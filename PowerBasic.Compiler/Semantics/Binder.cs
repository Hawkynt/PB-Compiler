using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Semantics;

/// <summary>
/// Binds a parsed compilation unit: resolves every name to a symbol, types every
/// expression, lays out UDTs, folds equates, classifies call-or-index expressions
/// and validates labels. Errors are collected, not thrown (the driver decides).
/// </summary>
public sealed class Binder {

  private readonly SemanticModel _model;
  private readonly CompilationUnit _unit;
  private readonly Dialect _dialect;
  private readonly Dictionary<char, PbType> _defaultTypes = [];
  private readonly HashSet<string> _redimmedArrays = new(StringComparer.OrdinalIgnoreCase);
  private ConstantFolder _folder;
  private bool _dynamicMode;
  private bool _optionSigned;
  private bool _checkedArithmetic;
  private bool _warnedAsm30;
  private int _optionBase;

  // PB 3.6 nested procedures: outer proc -> (nested name -> lifted top-level proc)
  private readonly Dictionary<ProcedureSymbol, Dictionary<string, ProcedureSymbol>> _nestedProcs = new(ReferenceEqualityComparer.Instance);
  // lifted nested proc -> the outer proc it is nested in, and its body to bind later
  private readonly List<(ProcedureSymbol Lifted, ProcedureSymbol Outer, bool IsFunction)> _nestedToBind = [];
  // lifted nested proc -> the outer locals it captures (in BYREF-param order)
  private readonly Dictionary<ProcedureSymbol, List<VariableSymbol>> _nestedCaptures = new(ReferenceEqualityComparer.Instance);
  // lifted nested proc -> the call sites (CallStmt / CallOrIndexExpr) to append capture args to
  private readonly Dictionary<ProcedureSymbol, List<(object Call, IReadOnlyList<Expression> Args)>> _nestedCallSites = new(ReferenceEqualityComparer.Instance);
  // lifted nested proc -> its original (unmangled) name, for the function-result variable alias
  private readonly Dictionary<ProcedureSymbol, string> _nestedOriginalName = new(ReferenceEqualityComparer.Instance);

  private Binder(CompilationUnit unit, Dialect dialect) {
    this._unit = unit;
    this._dialect = dialect;
    this._model = new() { FileName = unit.FileName, Dialect = dialect };
    this._folder = new(this._model.Equates, this._model.EnumMembers);
  }

  public static SemanticModel Bind(CompilationUnit unit, Dialect dialect = Dialect.Pb35) {
    var binder = new Binder(unit, dialect);
    binder.SeedInternalVariables();
    binder.CollectRedims(unit.Statements);
    binder.ScanModule();
    binder.BindAllBodies();
    binder.SpliceDimInitializers();
    return binder._model;
  }

  /// <summary>
  /// PB 3.6: inserts each DIM-initializer's lowered assignment(s) right after its
  /// DIM in the executable stream (main body and every procedure body, recursing
  /// nested blocks). Codegen and the optimizer passes then see a normal
  /// declaration-then-assignment pair - no hidden writes.
  /// </summary>
  private void SpliceDimInitializers() {
    if (this._model.DimInitializers.Count == 0)
      return;

    var spliced = this.SpliceBody(this._model.MainBody);
    this._model.MainBody.Clear();
    this._model.MainBody.AddRange(spliced);

    foreach (var proc in this._model.ProcedureList.Where(p => !p.IsExternal))
      proc.Body = this.SpliceBody(proc.Body!);
  }

  private List<Statement> SpliceBody(IReadOnlyList<Statement> statements) {
    var result = new List<Statement>(statements.Count);
    foreach (var statement in statements) {
      var rewritten = RewriteChildBlocks(statement, b => this.SpliceBody(b));
      result.Add(rewritten);
      if (statement is DimStmt dim && this._model.DimInitializers.TryGetValue(dim, out var inits))
        result.AddRange(inits);
    }
    return result;
  }

  /// <summary>
  /// Rebuilds a block-bearing statement with each child block replaced by
  /// <paramref name="map"/>(block); non-block statements are returned unchanged.
  /// The mirror of <see cref="ChildBlocks"/> for rewriting.
  /// </summary>
  private static Statement RewriteChildBlocks(Statement s, Func<IReadOnlyList<Statement>, List<Statement>> map) => s switch {
    IfStmt i => i with {
      Then = map(i.Then),
      ElseIfs = [.. i.ElseIfs.Select(e => (e.Condition, (IReadOnlyList<Statement>)map(e.Body)))],
      Else = i.Else == null ? null : map(i.Else),
    },
    SelectStmt sel => sel with {
      Arms = [.. sel.Arms.Select(a => a with { Body = map(a.Body) })],
    },
    ForStmt f => f with { Body = map(f.Body) },
    DoLoopStmt d => d with { Body = map(d.Body) },
    _ => s,
  };

  /// <summary>
  /// PB internal variables (pbvScrnCols, pbvScrnRows, pbvDefSeg, ...) resolve
  /// like SHARED module variables; codegen maps them onto runtime data cells.
  /// </summary>
  private void SeedInternalVariables() {
    foreach (var (name, info) in Runtime.DosRuntime.InternalVariables)
      this._model.ModuleVariables[name] = new(name, info.Size == 1 ? PbType.Byte : PbType.Word, VariableStorage.Global) { IsShared = true };
  }

  /// <summary>Adds a dialect-gate error when <paramref name="feature"/> is unavailable; true when usable.</summary>
  private bool Require(LanguageFeature feature, SourcePosition position) {
    if (DialectFacts.IsAvailable(feature, this._dialect))
      return true;
    this.Error(position, DialectFacts.RequirementMessage(feature, this._dialect));
    return false;
  }

  /// <summary>
  /// PB treats every array that appears in a REDIM anywhere as $DYNAMIC, even when
  /// its DIM has constant bounds - collect those names before declaring anything.
  /// </summary>
  private void CollectRedims(IReadOnlyList<Statement> statements) {
    foreach (var statement in statements)
      switch (statement) {
        case RedimStmt redim:
          foreach (var v in redim.Variables)
            this._redimmedArrays.Add(VariableKey(v.Name, v.Suffix, isArray: true));
          break;
        case SubDecl s:
          this.CollectRedims(s.Body);
          break;
        case FunctionDecl f:
          this.CollectRedims(f.Body);
          break;
        default:
          foreach (var block in ChildBlocks(statement))
            this.CollectRedims(block);
          break;
      }
  }

  private void Error(SourcePosition position, string message) => this._model.Errors.Add(new(position, message));
  private void Warn(SourcePosition position, string message) => this._model.Warnings.Add(new(position, message));

  #region pass 1 - module scan

  private void ScanModule() {
    foreach (var statement in this._unit.Statements)
      switch (statement) {
        case EquateStmt e:
          this.DefineEquate(e);
          break;

        case EnumDecl e:
          this.DefineEnum(e);
          break;

        case TypeDecl t:
          this.DefineUdt(t.Name, t.Fields, isUnion: false, t.Position);
          break;

        case UnionDecl u:
          this.DefineUdt(u.Name, u.Fields, isUnion: true, u.Position);
          break;

        case DefTypeStmt d:
          foreach (var (from, to) in d.Ranges)
            for (var c = char.ToUpperInvariant(from); c <= char.ToUpperInvariant(to); ++c)
              this._defaultTypes[c] = MapBuiltin(d.Type) ?? PbType.Integer;
          break;

        case DeclareStmt d:
          this.DeclareProcedure(d);
          break;

        case SubDecl s:
          this.DefineProcedure(s.Name, isFunction: false, TypeSuffix.None, null, s.Parameters, s.IsStatic, s.Body, s.Position, s.Cdecl);
          break;

        case FunctionDecl f:
          this.DefineProcedure(f.Name, isFunction: true, f.Suffix, f.ReturnType, f.Parameters, f.IsStatic, f.Body, f.Position, f.Cdecl);
          break;

        case DefFnDecl fn: {
          var proc = this.DefineProcedure(fn.Name, isFunction: true, fn.Suffix, null, fn.Parameters, isStatic: false,
            fn.BlockBody ?? [new AssignStmt(fn.Position, new NameExpr(fn.Position, fn.Name, fn.Suffix), fn.Body!)], fn.Position);
          proc.IsStatic = false;
          break;
        }

        case MetaStmt m:
          this._model.MetaStatements.Add(m);
          switch (m.Command) {
            case "DYNAMIC": this._dynamicMode = true; break;
            case "STATIC": this._dynamicMode = false; break;
            case "ERROR" when m.Arguments.Count >= 2
                && m.Arguments[0].Text.ToUpperInvariant() is "NUMERIC" or "OVERFLOW" or "ALL"
                && m.Arguments[^1].Text.Equals("ON", StringComparison.OrdinalIgnoreCase):
              // checked arithmetic disables the FPU promotion: genuine PBC
              // raises error 6 at the integral boundary instead
              this._checkedArithmetic = true;
              break;
            case "OPTION" when m.Arguments is [{ } opt, ..] && opt.Text.Equals("SIGNED", StringComparison.OrdinalIgnoreCase):
              // $OPTION SIGNED: the *PTR/*SEG functions return signed INTEGER
              this._optionSigned = m.Arguments is [_] || !m.Arguments[^1].Text.Equals("OFF", StringComparison.OrdinalIgnoreCase);
              break;
          }
          this._model.MainBody.Add(m);
          break;

        case DimStmt dim:
          this.DeclareModuleVariables(dim);
          this._model.MainBody.Add(dim); // dynamic arrays allocate at run time
          break;

        case CommandStmt { Keyword: "OPTION BASE" } ob when ob.Arguments is [IntegerLiteralExpr { Value: 0 or 1 } b]:
          this._optionBase = (int)b.Value;
          break;

        default:
          this._model.MainBody.Add(statement);
          break;
      }
  }

  /// <summary>
  /// PB 3.6 ENUM: registers each member as a bare integer constant (auto-incrementing
  /// from 0, or last+1, with explicit <c>= expr</c> values folded), and the enum name
  /// as an alias for its underlying integer type (INTEGER by default).
  /// </summary>
  private void DefineEnum(EnumDecl e) {
    var underlying = e.UnderlyingType != null ? this.ResolveTypeName(e.UnderlyingType) ?? PbType.Integer : PbType.Integer;
    this._model.EnumTypes[e.Name] = underlying;
    long next = 0;
    foreach (var (name, value) in e.Members) {
      if (value != null) {
        if (this._folder.TryFold(value) is { Integer: { } v })
          next = v;
        else
          this.Error(e.Position, $"ENUM member {name} value is not a compile-time constant");
      }
      this._model.EnumMembers[name] = next;
      this._model.EnumMemberTypes[name] = underlying;
      ++next;
    }
  }

  private void DefineEquate(EquateStmt e) {
    if (this._folder.TryFold(this.ApplyEquateFoldingQuirk(e.Value)) is not { } value) {
      this.Error(e.Position, $"equate %{e.Name} is not a compile-time constant");
      return;
    }
    if (this._model.Equates.TryGetValue(e.Name, out var existing) && existing != value) {
      this.Error(e.Position, $"equate %{e.Name} redefined with a different value");
      return;
    }
    this._model.Equates[e.Name] = value;
  }

  /// <summary>
  /// QUIRK 2.26 (FAQ, PB 3.0-3.2): equate constant folding mis-binds a LEADING
  /// unary minus to the whole additive chain - <c>%k = -20-4</c> yields -16
  /// (= -(20-4)) instead of -24. Replicated under those dialects and verified
  /// byte-identical against a genuine PBC 3.0c (tests/diff/pb30/QUIRK30.BAS).
  /// </summary>
  private Expression ApplyEquateFoldingQuirk(Expression e) {
    if (this._dialect is < Dialect.Pb30 or > Dialect.Pb32)
      return e;
    return TryStripLeadingNegate(e, out var stripped)
      ? new UnaryExpr(e.Position, UnaryOp.Negate, stripped)
      : e;
  }

  private static bool TryStripLeadingNegate(Expression e, out Expression stripped) {
    switch (e) {
      case UnaryExpr { Op: UnaryOp.Negate } u:
        stripped = u.Operand;
        return true;
      case BinaryExpr { Op: BinaryOp.Add or BinaryOp.Subtract } b when TryStripLeadingNegate(b.Left, out var left):
        stripped = b with { Left = left };
        return true;
      default:
        stripped = e;
        return false;
    }
  }

  private void DefineUdt(string name, IReadOnlyList<TypeField> fields, bool isUnion, SourcePosition position) {
    if (this._model.Udts.ContainsKey(name)) {
      this.Error(position, $"TYPE {name} already defined");
      return;
    }

    var resolved = new List<UdtField>();
    var offset = 0;
    foreach (var field in fields) {
      var fieldType = this.ResolveTypeName(field.Type);
      if (fieldType == null) {
        this.Error(field.Position, $"unknown type in field {name}.{field.Name}");
        continue;
      }

      var count = 1;
      if (field.ArrayBounds != null)
        foreach (var (lowerExpr, upperExpr) in field.ArrayBounds) {
          var lower = lowerExpr == null ? this._optionBase : this._folder.TryFold(lowerExpr)?.Integer;
          var upper = this._folder.TryFold(upperExpr)?.Integer;
          if (lower != null && upper != null)
            count *= (int)(upper.Value - lower.Value + 1);
          else
            this.Error(field.Position, $"field array bound of {name}.{field.Name} is not constant");
        }

      resolved.Add(new(field.Name, fieldType, isUnion ? 0 : offset, count));
      if (!isUnion)
        offset += fieldType.Size * count;
    }

    this._model.Udts[name] = new(name, resolved, isUnion);
  }

  private void DeclareProcedure(DeclareStmt d) {
    var proc = new ProcedureSymbol(d.Name, d.IsFunction) { Position = d.Position };
    if (d.IsFunction)
      proc.ReturnType = this.ResolveReturnType(d.Name, d.Suffix, d.ReturnType);
    if (d.Parameters != null)
      foreach (var p in d.Parameters)
        proc.Parameters.Add(this.BindParameter(p));

    // an identical prototype (or a definition) already on record: first one wins
    if (this._model.Overloads.TryGetValue(d.Name, out var set) && set.Any(e => SameSignature(e, proc)))
      return;
    this.RegisterProcedure(proc);
  }

  private ProcedureSymbol DefineProcedure(string name, bool isFunction, TypeSuffix suffix, TypeName? returnType, IReadOnlyList<Parameter> parameters, bool isStatic, IReadOnlyList<Statement> body, SourcePosition position, bool isCdecl = false) {
    var proc = new ProcedureSymbol(name, isFunction) { IsStatic = isStatic, Body = body, Position = position, IsCdecl = isCdecl };
    if (isFunction)
      proc.ReturnType = this.ResolveReturnType(name, suffix, returnType);
    foreach (var p in parameters)
      proc.Parameters.Add(this.BindParameter(p));

    if (this._model.Overloads.TryGetValue(name, out var set)) {
      var sameSig = set.FirstOrDefault(e => SameSignature(e, proc));
      if (sameSig is { IsExternal: false }) {
        this.Error(position, $"{(isFunction ? "FUNCTION" : "SUB")} {name} already defined");
        return sameSig;
      }
      if (sameSig is { IsExternal: true }) {
        this.ReplaceProcedure(sameSig, proc); // a DECLARE prototype: the definition supplies its body
        return proc;
      }
      // a new signature for an existing name = overloading (PB 3.6 only)
      this.Require(LanguageFeature.SubFunctionOverloading, position);
    }
    this.RegisterProcedure(proc);
    return proc;
  }

  /// <summary>Two procedures share a signature when their parameter lists have equal length and element types.</summary>
  private static bool SameSignature(ProcedureSymbol a, ProcedureSymbol b) {
    if (a.Parameters.Count != b.Parameters.Count)
      return false;
    for (var i = 0; i < a.Parameters.Count; ++i)
      if (!Equals(a.Parameters[i].Type, b.Parameters[i].Type))
        return false;
    return true;
  }

  private void RegisterProcedure(ProcedureSymbol proc) {
    this._model.ProcedureList.Add(proc);
    if (!this._model.Overloads.TryGetValue(proc.Name, out var list))
      this._model.Overloads[proc.Name] = list = [];
    proc.OverloadIndex = list.Count;
    list.Add(proc);
    this._model.Procedures.TryAdd(proc.Name, proc); // first of a name is the primary
  }

  /// <summary>Replaces a DECLARE prototype with its definition, keeping its overload slot.</summary>
  private void ReplaceProcedure(ProcedureSymbol old, ProcedureSymbol definition) {
    definition.OverloadIndex = old.OverloadIndex;
    var listIndex = this._model.ProcedureList.IndexOf(old);
    if (listIndex >= 0)
      this._model.ProcedureList[listIndex] = definition;
    else
      this._model.ProcedureList.Add(definition);
    var overloadList = this._model.Overloads[old.Name];
    var slot = overloadList.IndexOf(old);
    if (slot >= 0)
      overloadList[slot] = definition;
    if (this._model.Procedures.TryGetValue(old.Name, out var primary) && ReferenceEquals(primary, old))
      this._model.Procedures[old.Name] = definition;
  }

  /// <summary>
  /// Resolves a call to the best-matching overload of <paramref name="name"/>:
  /// the single definition when not overloaded, else the unique arity match, else
  /// the arity match with the most exact argument-type matches (PB 3.6). The
  /// arguments must already be bound (their types drive the tie-break).
  /// </summary>
  private ProcedureSymbol? ResolveOverload(string name, IReadOnlyList<Expression> args) {
    if (!this._model.Overloads.TryGetValue(name, out var set) || set.Count == 0)
      return null;
    if (set.Count == 1)
      return set[0];

    var byArity = set.Where(p => args.Count >= p.RequiredParameters && args.Count <= p.Parameters.Count).ToList();
    if (byArity.Count == 0)
      return set[0]; // no arity fits - let the argument-count diagnostic fire on the primary
    if (byArity.Count == 1)
      return byArity[0];

    ProcedureSymbol best = byArity[0];
    var bestScore = -1;
    foreach (var candidate in byArity) {
      var score = 0;
      for (var i = 0; i < args.Count && i < candidate.Parameters.Count; ++i)
        if (Equals(this._model.ExpressionTypes.GetValueOrDefault(args[i]), candidate.Parameters[i].Type))
          ++score;
      if (score > bestScore) {
        best = candidate;
        bestScore = score;
      }
    }
    return best;
  }

  private VariableSymbol BindParameter(Parameter p) {
    var type = p.Type != null
      ? this.ResolveTypeName(p.Type) ?? PbType.Integer
      : this.TypeFromSuffixOrDefault(p.Name, p.Suffix);
    if (p.IsArray)
      type = new ArrayType(type, null, Rank: 1); // array parameters arrive as descriptors

    return new(p.Name, type, VariableStorage.Parameter) { ByVal = p.ByVal, Seg = p.Seg, Optional = p.Optional, DefaultValue = p.DefaultValue };
  }

  private void DeclareModuleVariables(DimStmt dim) {
    foreach (var v in dim.Variables) {
      if (v.Initializer != null)
        continue; // DIM-with-initializer is declared in pass 2 with its inferred type
      var symbol = this.CreateVariable(v, VariableStorage.Global, dim.Position, dim.Class);
      if (symbol == null)
        continue;
      symbol.IsShared = dim.SharedFlag || dim.Storage is StorageClass.Shared or StorageClass.Public or StorageClass.Common;
      var key = VariableKey(v.Name, v.Suffix, v.ArrayBounds != null);
      if (this._model.ModuleVariables.TryGetValue(key, out var existing)) {
        // PB tolerates re-DIM of dynamic arrays and bare array mentions
        // (SHARED a$() before/after the bounds-carrying DIM); only genuine
        // element-type changes are errors
        var compatible = Equals(existing.Type, symbol.Type)
          || existing.Type is ArrayType { IsDynamic: true }
          || (existing.Type is ArrayType ea && symbol.Type is ArrayType { IsDynamic: true } na && Equals(ea.Element, na.Element));
        if (!compatible)
          this.Error(dim.Position, $"variable {v.Name} already declared with a different type");
        existing.IsShared |= symbol.IsShared;
        continue;
      }
      this._model.ModuleVariables[key] = symbol;
    }
  }

  private VariableSymbol? CreateVariable(VariableDecl v, VariableStorage storage, SourcePosition position, ArrayClass arrayClass = ArrayClass.Default) {
    var elementType = v.Type != null
      ? this.ResolveTypeName(v.Type)
      : this.TypeFromSuffixOrDefault(v.Name, v.Suffix);
    if (elementType == null) {
      this.Error(position, $"unknown type for variable {v.Name}");
      return null;
    }

    if (v.ArrayBounds == null)
      return new(v.Name, elementType, storage);

    // bare array mention (SHARED a$()) - shape comes from a DIM/REDIM elsewhere
    if (v.ArrayBounds.Count == 0)
      return new(v.Name, new ArrayType(elementType, null, Rank: 1), storage) { ArrayClass = arrayClass };

    // try static bounds; any non-constant bound, $DYNAMIC mode, an explicit
    // dynamic class (DYNAMIC/HUGE/VIRTUAL), or a REDIM anywhere makes the array dynamic
    var bounds = new List<(int, int)>();
    var isStatic = !this._dynamicMode
      && arrayClass is not (ArrayClass.Dynamic or ArrayClass.Huge or ArrayClass.Virtual or ArrayClass.Absolute or ArrayClass.Ems or ArrayClass.Xms)
      && !this._redimmedArrays.Contains(VariableKey(v.Name, v.Suffix, isArray: true));
    foreach (var (lowerExpr, upperExpr) in v.ArrayBounds) {
      var lower = lowerExpr == null ? this._optionBase : (int?)(this._folder.TryFold(lowerExpr)?.Integer);
      var upper = (int?)(this._folder.TryFold(upperExpr)?.Integer);
      if (lower == null || upper == null) {
        isStatic = false;
        break;
      }
      bounds.Add((lower.Value, upper.Value));
    }

    var arrayType = new ArrayType(elementType, isStatic ? bounds : null, v.ArrayBounds.Count);
    return new(v.Name, arrayType, storage) { ArrayClass = arrayClass };
  }

  #endregion

  #region types

  private static PbType? MapBuiltin(BuiltinType b) => b switch {
    BuiltinType.Byte => PbType.Byte,
    BuiltinType.Word => PbType.Word,
    BuiltinType.Dword => PbType.Dword,
    BuiltinType.Integer => PbType.Integer,
    BuiltinType.Long => PbType.Long,
    BuiltinType.Quad => PbType.Quad,
    BuiltinType.Single => PbType.Single,
    BuiltinType.Double => PbType.Double,
    BuiltinType.Ext => PbType.Ext,
    BuiltinType.Fix => PbType.Fix,
    BuiltinType.Bcd => PbType.Bcd,
    BuiltinType.String => PbType.String,
    BuiltinType.Flex => new FlexType(),
    BuiltinType.Any => PbType.Any,
    _ => null,
  };

  private PbType? ResolveTypeName(TypeName t) {
    if (t.IsPointer) {
      var target = this.ResolveTypeName(t.PointerTarget!);
      if (target == null) {
        this.Error(t.Position, "unknown pointer target type");
        return new PointerType(PbType.Integer);
      }
      return new PointerType(target);
    }

    if (t.Builtin is BuiltinType.FixedString or BuiltinType.Asciiz) {
      if (this._folder.TryFold(t.FixedLength!) is { Integer: { } n } && n is > 0 and <= 32767)
        return t.Builtin == BuiltinType.Asciiz ? new AsciizType((int)n) : new FixedStringType((int)n);
      this.Error(t.Position, $"{(t.Builtin == BuiltinType.Asciiz ? "ASCIIZ" : "fixed string")} length must be a constant in 1..32767");
      return t.Builtin == BuiltinType.Asciiz ? new AsciizType(1) : new FixedStringType(1);
    }

    if (t.IsUserDefined) {
      if (this._model.Udts.TryGetValue(t.UserTypeName!, out var udt))
        return udt;
      if (this._model.EnumTypes.TryGetValue(t.UserTypeName!, out var enumType)) // PB 3.6: an ENUM name aliases its integer type
        return enumType;
      return null;
    }

    return MapBuiltin(t.Builtin);
  }

  private PbType TypeFromSuffixOrDefault(string name, TypeSuffix suffix) => suffix switch {
    TypeSuffix.Byte => PbType.Byte,
    TypeSuffix.Word => PbType.Word,
    TypeSuffix.Dword => PbType.Dword,
    TypeSuffix.Integer => PbType.Integer,
    TypeSuffix.Long => PbType.Long,
    TypeSuffix.Quad => PbType.Quad,
    TypeSuffix.Single => PbType.Single,
    TypeSuffix.Double => PbType.Double,
    TypeSuffix.Ext => PbType.Ext,
    TypeSuffix.Fix => PbType.Fix,
    TypeSuffix.Bcd => PbType.Bcd,
    TypeSuffix.String => PbType.String,
    TypeSuffix.Flex => new FlexType(),
    _ => this._defaultTypes.TryGetValue(char.ToUpperInvariant(name[0]), out var def) ? def : PbType.Single,
  };

  private PbType ResolveReturnType(string name, TypeSuffix suffix, TypeName? declared) {
    if (declared != null)
      return this.ResolveTypeName(declared) ?? PbType.Integer;
    return this.TypeFromSuffixOrDefault(name, suffix);
  }

  /// <summary>
  /// Variable table key. Arrays live in their own namespace (BASIC keeps the
  /// scalar <c>A$</c> and the array <c>A$()</c> distinct), marked by a "()" tail.
  /// </summary>
  private static string VariableKey(string name, TypeSuffix suffix, bool isArray = false)
    => name + suffix.KeyText() + (isArray ? "()" : "");

  #endregion

  #region pass 2 - bodies

  /// <summary>Per-procedure (or main) binding context.</summary>
  private sealed class Scope(ProcedureSymbol? proc, ProcedureSymbol? captureFrom = null) {
    public ProcedureSymbol? Proc => proc;
    /// <summary>PB 3.6 nested procedure: the enclosing proc whose locals this one may capture (BYREF).</summary>
    public ProcedureSymbol? CaptureFrom => captureFrom;
    public string LabelKey => proc?.Name ?? "";
    public List<(string Target, SourcePosition Position)> PendingLabelRefs { get; } = [];
  }

  private void BindAllBodies() {
    this._folder = new(this._model.Equates, this._model.EnumMembers);
    this.PreScanNestedProcedures();

    var main = new Scope(null);

    // PB 3.6 default parameter values are constant/global expressions evaluated at the
    // call site; bind them once in the module scope so codegen has their types.
    foreach (var proc in this._model.ProcedureList)
      foreach (var p in proc.Parameters)
        if (p.DefaultValue is { } d)
          this.BindExpression(d, main);

    this.CollectLabels(this._model.MainBody, main);
    foreach (var statement in this._model.MainBody)
      this.BindStatement(statement, main);
    this.CheckLabelRefs(main);

    foreach (var proc in this._model.ProcedureList.Where(p => !p.IsExternal && !p.IsNested)) {
      var scope = new Scope(proc);

      foreach (var p in proc.Parameters)
        proc.Variables[VariableKey(p.Name, TypeSuffix.None, p.Type is ArrayType)] = p;

      if (proc.IsFunction) // the function name acts as the result variable
        proc.Variables.TryAdd(proc.Name, new(proc.Name, proc.ReturnType!, VariableStorage.Local));

      this.CollectLabels(proc.Body!, scope);
      foreach (var statement in proc.Body!)
        this.BindStatement(statement, scope);
      this.CheckLabelRefs(scope);
    }

    this.BindNestedProcedures();
  }

  #region PB 3.6 nested procedures (stack capture)

  /// <summary>Finds nested SUB/FUNCTION declarations in every top-level proc body and pre-registers each as a lifted top-level proc (so calls resolve before its captures are known).</summary>
  private void PreScanNestedProcedures() {
    foreach (var outer in this._model.ProcedureList.Where(p => !p.IsExternal && !p.IsNested).ToList())
      this.ScanNestedIn(outer, outer.Body!);
  }

  private void ScanNestedIn(ProcedureSymbol outer, IReadOnlyList<Statement> body) {
    foreach (var s in body)
      switch (s) {
        case SubDecl sub:
          this.RegisterNested(outer, sub.Name, isFunction: false, TypeSuffix.None, null, sub.Parameters, sub.Body, sub.Position);
          break;
        case FunctionDecl fn:
          this.RegisterNested(outer, fn.Name, isFunction: true, fn.Suffix, fn.ReturnType, fn.Parameters, fn.Body, fn.Position);
          break;
        default:
          foreach (var block in ChildBlocks(s))
            this.ScanNestedIn(outer, block);
          break;
      }
  }

  private void RegisterNested(ProcedureSymbol outer, string name, bool isFunction, TypeSuffix suffix, TypeName? returnType, IReadOnlyList<Parameter> parameters, IReadOnlyList<Statement> body, SourcePosition position) {
    if (!this.Require(LanguageFeature.NestedProcedures, position))
      return;
    var lifted = new ProcedureSymbol($"{outer.Name}${name}", isFunction) { IsNested = true, Body = body, Position = position };
    if (isFunction)
      lifted.ReturnType = this.ResolveReturnType(name, suffix, returnType);
    foreach (var p in parameters)
      lifted.Parameters.Add(this.BindParameter(p));
    this._model.ProcedureList.Add(lifted);
    if (!this._nestedProcs.TryGetValue(outer, out var map))
      this._nestedProcs[outer] = map = new(StringComparer.OrdinalIgnoreCase);
    map[name] = lifted;
    this._nestedOriginalName[lifted] = name;
    this._nestedToBind.Add((lifted, outer, isFunction));
  }

  /// <summary>Binds each lifted nested proc's body in a scope that may capture the outer proc's locals (added as BYREF parameters), then appends those captures to its call sites.</summary>
  private void BindNestedProcedures() {
    foreach (var (lifted, outer, isFunction) in this._nestedToBind) {
      this._nestedCaptures[lifted] = [];
      var scope = new Scope(lifted, captureFrom: outer);
      foreach (var p in lifted.Parameters)
        lifted.Variables[VariableKey(p.Name, TypeSuffix.None, p.Type is ArrayType)] = p;
      if (isFunction) {
        var result = new VariableSymbol(lifted.Name, lifted.ReturnType!, VariableStorage.Local);
        lifted.Variables.TryAdd(lifted.Name, result);                            // for codegen + FUNCTION = expr
        lifted.Variables.TryAdd(this._nestedOriginalName[lifted], result);       // for OriginalName = expr
      }
      this.CollectLabels(lifted.Body!, scope);
      foreach (var statement in lifted.Body!)
        this.BindStatement(statement, scope);
      this.CheckLabelRefs(scope);
    }

    // now that captures (extra BYREF params) are known, append them to every call
    // site as already-bound references to the outer locals (passed BYREF by codegen)
    foreach (var (lifted, captures) in this._nestedCaptures)
      if (captures.Count > 0 && this._nestedCallSites.TryGetValue(lifted, out var sites))
        foreach (var (call, args) in sites) {
          var full = new List<Expression>(args);
          foreach (var captured in captures) {
            var captureArg = new NameExpr(default, captured.Name, TypeSuffix.None);
            this._model.VariableBindings[captureArg] = captured;
            this._model.ExpressionTypes[captureArg] = captured.Type;
            full.Add(captureArg);
          }
          this._model.ReorderedArguments[call] = full;
        }
  }

  #endregion

  private void CollectLabels(IReadOnlyList<Statement> body, Scope scope) {
    var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    void Walk(IReadOnlyList<Statement> statements) {
      foreach (var s in statements) {
        if (s is LabelStmt l && !labels.Add(l.Name))
          this.Error(l.Position, $"duplicate label {l.Name}");
        foreach (var child in ChildBlocks(s))
          Walk(child);
      }
    }
    Walk(body);
    this._model.Labels[scope.LabelKey] = labels;
  }

  private static IEnumerable<IReadOnlyList<Statement>> ChildBlocks(Statement s) {
    switch (s) {
      case IfStmt i:
        yield return i.Then;
        foreach (var (_, body) in i.ElseIfs)
          yield return body;
        if (i.Else != null)
          yield return i.Else;
        break;
      case SelectStmt sel:
        foreach (var arm in sel.Arms)
          yield return arm.Body;
        break;
      case ForStmt f:
        yield return f.Body;
        break;
      case DoLoopStmt d:
        yield return d.Body;
        break;
    }
  }

  private void CheckLabelRefs(Scope scope) {
    var labels = this._model.Labels[scope.LabelKey];
    foreach (var (target, position) in scope.PendingLabelRefs)
      if (target != "0" && !labels.Contains(target))
        this.Error(position, $"undefined label {target}");
  }

  #endregion

  #region statement binding

  private void BindStatement(Statement statement, Scope scope) {
    switch (statement) {
      case AssignStmt a: {
        var targetType = this.BindAssignTarget(a.Target, scope);
        var valueType = this.BindExpression(a.Value, scope);
        this.CheckAssignable(targetType, valueType, a.Position);
        break;
      }

      case CallStmt c:
        this.BindCallStatement(c, scope);
        break;

      case CallPtrStmt cp:
        this.BindExpression(cp.Pointer, scope);
        foreach (var argument in cp.Arguments)
          this.BindExpression(argument, scope);
        break;

      case DimStmt dim:
        this.BindDimInScope(dim, scope);
        break;

      case RedimStmt redim:
        foreach (var v in redim.Variables) {
          foreach (var (lower, upper) in v.ArrayBounds ?? []) {
            if (lower != null)
              this.BindExpression(lower, scope);
            this.BindExpression(upper, scope);
          }
          var symbol = this.LookupArrayVariable(v.Name, v.Suffix, scope);
          if (symbol == null) {
            // REDIM can introduce a dynamic array
            var created = this.CreateVariable(v with { ArrayBounds = v.ArrayBounds }, scope.Proc == null ? VariableStorage.Global : VariableStorage.Local, redim.Position);
            if (created != null) {
              created.Type = created.Type is ArrayType at ? at with { StaticBounds = null } : new ArrayType(created.Type, null, v.ArrayBounds?.Count ?? 1);
              this.Register(created, v, scope);
              this._model.RedimBindings[v] = created;
            }
          } else if (symbol.Type is ArrayType { IsDynamic: false })
            this.Error(redim.Position, $"REDIM on static array {v.Name} (use $DYNAMIC)");
          else
            this._model.RedimBindings[v] = symbol;
        }
        break;

      case EraseStmt erase:
        foreach (var array in erase.Arrays) {
          var arraySymbol = this.LookupArrayVariable(array.Name, array.Suffix, scope);
          if (arraySymbol == null) {
            this.Error(array.Position, $"{array.Name} is not an array");
            continue;
          }
          this._model.VariableBindings[array] = arraySymbol;
          this._model.ExpressionTypes[array] = arraySymbol.Type;
        }
        break;

      case IfStmt i:
        this.BindExpression(i.Condition, scope);
        this.BindBlock(i.Then, scope);
        foreach (var (condition, body) in i.ElseIfs) {
          this.BindExpression(condition, scope);
          this.BindBlock(body, scope);
        }
        if (i.Else != null)
          this.BindBlock(i.Else, scope);
        break;

      case SelectStmt sel:
        this.BindExpression(sel.Subject, scope);
        foreach (var arm in sel.Arms) {
          foreach (var selector in arm.Selectors) {
            if (selector.Value != null)
              this.BindExpression(selector.Value, scope);
            if (selector.RangeUpper != null)
              this.BindExpression(selector.RangeUpper, scope);
          }
          this.BindBlock(arm.Body, scope);
        }
        break;

      case ForStmt f: {
        var counter = this.BindExpression(f.Variable, scope);
        if (counter is not ScalarType)
          this.Error(f.Position, "FOR counter must be numeric");
        this.BindExpression(f.From, scope);
        this.BindExpression(f.To, scope);
        if (f.Step != null)
          this.BindExpression(f.Step, scope);
        this.BindBlock(f.Body, scope);
        break;
      }

      case DoLoopStmt d:
        if (d.PreCondition != null)
          this.BindExpression(d.PreCondition, scope);
        if (d.PostCondition != null)
          this.BindExpression(d.PostCondition, scope);
        this.BindBlock(d.Body, scope);
        break;

      case GotoStmt g:
        scope.PendingLabelRefs.Add((g.Target, g.Position));
        break;

      case GosubStmt g:
        scope.PendingLabelRefs.Add((g.Target, g.Position));
        break;

      case GotoPtrStmt gp:
        this.BindExpression(gp.Pointer, scope);
        break;

      case GosubPtrStmt gsp:
        this.BindExpression(gsp.Pointer, scope);
        break;

      case ReturnStmt r when r.Target != null:
        scope.PendingLabelRefs.Add((r.Target, r.Position));
        break;

      case OnGotoStmt og:
        this.BindExpression(og.Selector, scope);
        foreach (var target in og.Targets)
          scope.PendingLabelRefs.Add((target, og.Position));
        break;

      case OnErrorStmt oe when oe.Target is { } target:
        scope.PendingLabelRefs.Add((target, oe.Position));
        break;

      case ResumeStmt rs when rs.Target != null:
        scope.PendingLabelRefs.Add((rs.Target, rs.Position));
        break;

      case OnEventStmt ev:
        if (ev.Index != null)
          this.BindExpression(ev.Index, scope);
        scope.PendingLabelRefs.Add((ev.Target, ev.Position));
        break;

      case EventControlStmt ec when ec.Index != null:
        this.BindExpression(ec.Index, scope);
        break;

      case IncrDecrStmt id:
        this.BindAssignTarget(id.Target, scope);
        if (id.Amount != null)
          this.BindExpression(id.Amount, scope);
        break;

      case SwapStmt sw:
        this.BindAssignTarget(sw.Left, scope);
        this.BindAssignTarget(sw.Right, scope);
        break;

      case ReplaceStmt replace: {
        this.BindExpression(replace.Find, scope);
        this.BindExpression(replace.With, scope);
        if (this.BindAssignTarget(replace.Target, scope) is not (StringType or FlexType))
          this.Error(replace.Position, "REPLACE needs a dynamic string target");
        break;
      }

      case BitStmt bit: {
        var targetType = this.BindAssignTarget(bit.Target, scope);
        if (targetType is not ScalarType { IsFloat: false })
          this.Error(bit.Position, "BIT statement needs an integral variable");
        this.BindExpression(bit.Bit, scope);
        break;
      }

      case ExitFarStmt ef when ef.AtLabel != null:
        scope.PendingLabelRefs.Add((ef.AtLabel, ef.Position));
        break;

      case ExitFarStmt:
        break;

      case ArraySortStmt sort:
        this.BindArrayStatement(sort.Array, sort.Count, sort.FromPos, sort.ToPos, sort.Collate, scope);
        if (sort.TagArray != null)
          this.BindExpression(sort.TagArray, scope);
        break;

      case ArrayScanStmt scan:
        this.BindArrayStatement(scan.Array, scan.Count, scan.FromPos, scan.ToPos, scan.Collate, scope);
        this.BindExpression(scan.Match, scope);
        this.BindAssignTarget(scan.Target, scope);
        break;

      case MidAssignStmt mid:
        this.BindAssignTarget(mid.Target, scope);
        this.BindExpression(mid.Start, scope);
        if (mid.Length != null)
          this.BindExpression(mid.Length, scope);
        this.BindExpression(mid.Value, scope);
        break;

      case AscAssignStmt asc: {
        var targetType = this.BindAssignTarget(asc.Target, scope);
        if (targetType is not (StringType or FixedStringType or AsciizType or FlexType))
          this.Error(asc.Position, "ASC statement needs a string target");
        if (asc.Index != null)
          this.BindExpression(asc.Index, scope);
        this.BindExpression(asc.Value, scope);
        break;
      }

      case StdOutStmt so:
        if (so.Value != null)
          this.BindExpression(so.Value, scope);
        break;

      case StdInStmt si:
        if (si.Count != null)
          this.BindExpression(si.Count, scope);
        if (this.BindAssignTarget(si.Target, scope) is not (StringType or FlexType))
          this.Error(si.Position, "STDIN needs a dynamic string target");
        break;

      case LsetRsetStmt ls:
        this.BindAssignTarget(ls.Target, scope);
        this.BindExpression(ls.Value, scope);
        break;

      case PrintStmt p:
        if (p.FileNumber != null)
          this.BindExpression(p.FileNumber, scope);
        if (p.UsingFormat != null)
          this.BindExpression(p.UsingFormat, scope);
        foreach (var item in p.Items)
          if (item.Value != null)
            this.BindExpression(item.Value, scope);
        break;

      case WriteStmt write:
        if (write.FileNumber != null)
          this.BindExpression(write.FileNumber, scope);
        foreach (var item in write.Items)
          this.BindExpression(item, scope);
        break;

      case IterateStmt:
        break;

      case InputStmt input:
        if (input.FileNumber != null)
          this.BindExpression(input.FileNumber, scope);
        foreach (var target in input.Targets)
          this.BindAssignTarget(target, scope);
        break;

      case OpenStmt open:
        this.BindExpression(open.FileName, scope);
        this.BindExpression(open.FileNumber, scope);
        if (open.RecordLength != null)
          this.BindExpression(open.RecordLength, scope);
        break;

      case CloseStmt close:
        foreach (var n in close.FileNumbers)
          this.BindExpression(n, scope);
        break;

      case GetPutFileStmt gp:
        this.BindExpression(gp.FileNumber, scope);
        if (gp.RecordNumber != null)
          this.BindExpression(gp.RecordNumber, scope);
        if (gp.Variable != null)
          this.BindExpression(gp.Variable, scope);
        break;

      case SeekStmt seek:
        this.BindExpression(seek.FileNumber, scope);
        this.BindExpression(seek.Target, scope);
        break;

      case FieldStmt field:
        this.BindExpression(field.FileNumber, scope);
        foreach (var (width, target) in field.Fields) {
          this.BindExpression(width, scope);
          this.BindAssignTarget(target, scope);
        }
        break;

      case ReadStmt read:
        foreach (var target in read.Targets)
          this.BindAssignTarget(target, scope);
        break;

      case RestoreStmt restore when restore.Target != null:
        scope.PendingLabelRefs.Add((restore.Target, restore.Position));
        break;

      case ChainStmt chain:
        if (this.BindExpression(chain.Target, scope) is not (StringType or FixedStringType or FlexType or AsciizType))
          this.Error(chain.Position, "CHAIN/RUN needs a file-name string");
        break;

      case ErrorStmt err:
        this.BindExpression(err.Code, scope);
        break;

      case EndStmt end when end.ExitCode != null:
        this.BindExpression(end.ExitCode, scope);
        break;

      case DefSegStmt seg:
        if (seg.Segment != null)
          this.BindExpression(seg.Segment, scope);
        break;

      case CommandStmt cmd:
        foreach (var argument in cmd.Arguments)
          if (argument != null)
            this.BindExpression(argument, scope);
        break;

      case LineStmt line:
        foreach (var e in new[] { line.From?.X, line.From?.Y, line.To.X, line.To.Y, line.Color, line.Style })
          if (e != null)
            this.BindExpression(e, scope);
        break;

      case CircleStmt circle:
        foreach (var e in new[] { circle.Center.X, circle.Center.Y, circle.Radius, circle.Color, circle.Start, circle.End, circle.Aspect })
          if (e != null)
            this.BindExpression(e, scope);
        break;

      case PsetStmt pset:
        this.BindExpression(pset.Point.X, scope);
        this.BindExpression(pset.Point.Y, scope);
        if (pset.Color != null)
          this.BindExpression(pset.Color, scope);
        break;

      case GetPutGraphicsStmt gg:
        this.BindExpression(gg.From.X, scope);
        this.BindExpression(gg.From.Y, scope);
        if (gg.To != null) {
          this.BindExpression(gg.To.Value.X, scope);
          this.BindExpression(gg.To.Value.Y, scope);
        }
        this.BindExpression(gg.Array, scope);
        break;

      case InlineAsmStmt asmStmt:
        // QUIRK 2.21 (FAQ): 3.0 resolved inline-asm variable operands differently
        // from 3.1+; PB-Compiler always applies 3.1+ semantics - surface that
        // once when compiling under --dialect pb30 (oracle verification pending)
        if (this._dialect == Dialect.Pb30 && !this._warnedAsm30) {
          this._warnedAsm30 = true;
          this.Warn(asmStmt.Position, "PB 3.0 inline-asm operand semantics (FAQ 2.21) are not replicated; 3.1+ semantics apply");
        }
        break;

      // declarations already handled in pass 1 (module) or harmless here; labels collected upfront
      case LabelStmt or DataStmt or MetaStmt or EquateStmt or DefTypeStmt
        or ExitStmt or ReturnStmt or ResumeStmt or OnErrorStmt or EndStmt or RestoreStmt or EventControlStmt:
        break;

      // PB 3.6: a nested SUB/FUNCTION inside a proc is lifted + bound separately (see
      // PreScanNestedProcedures); here it is a no-op when the feature is available.
      case SubDecl or FunctionDecl when scope.Proc != null:
        if (!DialectFacts.IsAvailable(LanguageFeature.NestedProcedures, this._dialect))
          this.Error(statement.Position, "declaration not allowed inside SUB/FUNCTION");
        break;

      case TypeDecl or UnionDecl or DeclareStmt or SubDecl or FunctionDecl or DefFnDecl:
        if (scope.Proc != null)
          this.Error(statement.Position, "declaration not allowed inside SUB/FUNCTION");
        break;

      default:
        this.Warn(statement.Position, $"statement {statement.GetType().Name} not yet semantically checked");
        break;
    }
  }

  private void BindBlock(IReadOnlyList<Statement> body, Scope scope) {
    foreach (var statement in body)
      this.BindStatement(statement, scope);
  }

  /// <summary>Binds the common parts of ARRAY SORT/SCAN; the target must be an array.</summary>
  private void BindArrayStatement(CallOrIndexExpr array, Expression? count, Expression? fromPos, Expression? toPos, Expression? collate, Scope scope) {
    var symbol = this.LookupArrayVariable(array.Name, array.Suffix, scope);
    if (symbol is { Type: ArrayType arrayType }) {
      this._model.VariableBindings[array] = symbol;
      this._model.ExpressionTypes[array] = arrayType;
    } else
      this.Error(array.Position, $"{array.Name} is not an array");
    foreach (var e in new[] { count, fromPos, toPos, collate }.OfType<Expression>())
      this.BindExpression(e, scope);
    foreach (var start in array.Arguments)
      this.BindExpression(start, scope);
  }

  private void BindDimInScope(DimStmt dim, Scope scope) {
    // PB 3.6 fused declare-and-initialize: bind the initializer, infer/resolve the
    // type, declare the variable, and record a real assignment that the post-bind
    // splice pass inserts after this DIM (so codegen/optimizer see the write).
    foreach (var v in dim.Variables.Where(v => v.Initializer != null))
      this.BindDimInitializer(dim, v, scope);

    if (scope.Proc == null) {
      // module DIMs were declared in pass 1 - but dynamic bounds are runtime
      // expressions that still need binding (DIM a(n) with a variable bound)
      foreach (var v in dim.Variables)
        foreach (var (lower, upper) in v.ArrayBounds ?? []) {
          if (lower != null)
            this.BindExpression(lower, scope);
          this.BindExpression(upper, scope);
        }
      return;
    }

    foreach (var v in dim.Variables) {
      if (v.Initializer != null)
        continue; // already declared + bound by BindDimInitializer above
      foreach (var (lower, upper) in v.ArrayBounds ?? []) {
        if (lower != null)
          this.BindExpression(lower, scope);
        this.BindExpression(upper, scope);
      }

      var key = VariableKey(v.Name, v.Suffix, v.ArrayBounds != null);
      // DIM x AS SHARED/STATIC type inside a procedure overrides the storage class
      var storageClass = dim.SharedFlag ? StorageClass.Shared
        : dim.StaticFlag ? StorageClass.Static
        : dim.Storage;
      switch (storageClass) {
        case StorageClass.Shared: {
          // SHARED inside a proc aliases the module-level variable
          if (!this._model.ModuleVariables.TryGetValue(key, out var moduleVar)) {
            var created = this.CreateVariable(v, VariableStorage.Global, dim.Position);
            if (created == null)
              continue;
            created.IsShared = true;
            this._model.ModuleVariables[key] = moduleVar = created;
          }
          moduleVar.IsShared = true;
          scope.Proc.Variables[key] = moduleVar;
          break;
        }

        case StorageClass.Static: {
          var symbol = this.CreateVariable(v, VariableStorage.Static, dim.Position);
          if (symbol != null)
            scope.Proc.Variables[key] = symbol;
          break;
        }

        default: {
          var storage = scope.Proc.IsStatic ? VariableStorage.Static : VariableStorage.Local;
          var symbol = this.CreateVariable(v, storage, dim.Position);
          if (symbol == null)
            continue;
          if (scope.Proc.Variables.TryGetValue(key, out var existing) && !Equals(existing.Type, symbol.Type))
            this.Error(dim.Position, $"variable {v.Name} already declared with a different type");
          else
            scope.Proc.Variables[key] = symbol;
          break;
        }
      }
    }
  }

  private void Register(VariableSymbol symbol, VariableDecl v, Scope scope) {
    var key = VariableKey(v.Name, v.Suffix, v.ArrayBounds != null);
    if (scope.Proc != null)
      scope.Proc.Variables[key] = symbol;
    else
      this._model.ModuleVariables[key] = symbol;
  }

  /// <summary>
  /// Declares a PB 3.6 <c>DIM x [AS type] = value</c>: binds the initializer (so its
  /// type is known), creates the variable with the explicit type or the inferred
  /// initializer type, and records a bound assignment for the splice pass to insert
  /// after the DIM. The variable lives where its storage class dictates (a proc local,
  /// or a module global at main level).
  /// </summary>
  private void BindDimInitializer(DimStmt dim, VariableDecl v, Scope scope) {
    // PB 3.6 object initializer: DIM p = NEW Udt { .field = value, ... } lowers to a
    // UDT declaration plus one assignment per listed field (unlisted fields keep
    // their zero value). The NEW node is consumed here and never reaches codegen.
    if (v.Initializer is NewExpr nu) {
      this.BindNewInitializer(dim, v, nu, scope);
      return;
    }
    if (v.Initializer is ArrayLiteralExpr lit) {
      this.BindArrayInitializer(dim, v, lit, scope);
      return;
    }

    var valueType = this.BindExpression(v.Initializer!, scope);
    var declaredType = v.Type != null ? this.ResolveTypeName(v.Type) : valueType;
    if (declaredType == null) {
      this.Error(v.Position, $"unknown type for variable {v.Name}");
      return;
    }

    this.DeclareInitializedVariable(dim, v, declaredType, scope);

    // lower the initializer to a real assignment (bound here, spliced after the DIM)
    var assign = new AssignStmt(v.Position, new NameExpr(v.Position, v.Name, v.Suffix), v.Initializer!);
    this.BindStatement(assign, scope);
    this.InitListOf(dim).Add(assign);
  }

  /// <summary>
  /// PB 3.6 object initializer: <c>DIM p [AS Udt] = NEW Udt { .f = v, ... }</c> declares
  /// <c>p</c> as the UDT and lowers to one <c>p.field = value</c> assignment per listed
  /// field (unlisted fields keep their zero-initialized value).
  /// </summary>
  /// <summary>
  /// PB 3.6 array initializer: <c>DIM a(...) = { v, lo..hi, ..arr }</c> (or <c>a()</c> to
  /// auto-size). Expands ranges and static-array spreads into a flat value list, declares
  /// the (static) array sized to the literal, and lowers to one element assignment each.
  /// </summary>
  private void BindArrayInitializer(DimStmt dim, VariableDecl v, ArrayLiteralExpr lit, Scope scope) {
    var element = v.Type != null ? this.ResolveTypeName(v.Type) : this.TypeFromSuffixOrDefault(v.Name, v.Suffix);
    if (element is null or ArrayType) {
      this.Error(v.Position, $"unknown element type for array {v.Name}");
      return;
    }

    var values = new List<Expression>();
    foreach (var el in lit.Elements)
      switch (el) {
        case ValueElement ve:
          this.BindExpression(ve.Value, scope);
          values.Add(ve.Value);
          break;
        case RangeElement re:
          if (this._folder.TryFold(re.Lo)?.Integer is not { } lo || this._folder.TryFold(re.Hi)?.Integer is not { } hi) {
            this.Error(re.Position, "array-literal range bounds must be compile-time constants");
            return;
          }
          for (var k = lo; lo <= hi ? k <= hi : k >= hi; k += lo <= hi ? 1 : -1)
            values.Add(new IntegerLiteralExpr(re.Position, k, TypeSuffix.None));
          break;
        case SpreadElement se:
          if (se.Source is not NameExpr src
              || this.LookupArrayVariable(src.Name, src.Suffix, scope) is not { Type: ArrayType { StaticBounds: [var dimBound] } }) {
            this.Error(se.Position, "spread (..arr) requires a 1-D static array");
            return;
          }
          for (var j = dimBound.Item1; j <= dimBound.Item2; ++j) {
            var read = new CallOrIndexExpr(se.Position, src.Name, src.Suffix, [new IntegerLiteralExpr(se.Position, j, TypeSuffix.None)]);
            this.BindExpression(read, scope);
            values.Add(read);
          }
          break;
      }

    // explicit DIM size (a(n)) or auto-size (a()) from the element count
    var lower = this._optionBase;
    int upper;
    if (v.ArrayBounds is [var (lowerExpr, upperExpr)] && this._folder.TryFold(upperExpr)?.Integer is { } u) {
      if (lowerExpr != null && this._folder.TryFold(lowerExpr)?.Integer is { } l)
        lower = (int)l;
      upper = (int)u;
    } else {
      upper = lower + values.Count - 1;
    }

    var arrayType = new ArrayType(element, [(lower, upper)], 1);
    var storage = scope.Proc == null ? VariableStorage.Global
      : scope.Proc.IsStatic || dim.StaticFlag ? VariableStorage.Static
      : VariableStorage.Local;
    var symbol = new VariableSymbol(v.Name, arrayType, storage);
    var key = VariableKey(v.Name, v.Suffix, isArray: true);
    if (scope.Proc != null)
      scope.Proc.Variables[key] = symbol;
    else
      this._model.ModuleVariables[key] = symbol;

    var list = this.InitListOf(dim);
    for (var k = 0; k < values.Count && lower + k <= upper; ++k) {
      var target = new CallOrIndexExpr(v.Position, v.Name, v.Suffix, [new IntegerLiteralExpr(v.Position, lower + k, TypeSuffix.None)]);
      var assign = new AssignStmt(v.Position, target, values[k]);
      this.BindStatement(assign, scope);
      list.Add(assign);
    }
  }

  private void BindNewInitializer(DimStmt dim, VariableDecl v, NewExpr nu, Scope scope) {
    if (!this._model.Udts.TryGetValue(nu.TypeName, out var udt)) {
      this.Error(nu.Position, $"unknown type {nu.TypeName}");
      return;
    }
    var declaredType = v.Type != null ? this.ResolveTypeName(v.Type) ?? udt : udt;
    this.DeclareInitializedVariable(dim, v, declaredType, scope);

    var list = this.InitListOf(dim);
    foreach (var (field, value) in nu.Fields) {
      var target = new MemberExpr(v.Position, new NameExpr(v.Position, v.Name, v.Suffix), field, TypeSuffix.None);
      var assign = new AssignStmt(v.Position, target, value);
      this.BindStatement(assign, scope);
      list.Add(assign);
    }
  }

  /// <summary>Creates and registers a DIM-initialized variable in the right scope (with a redeclaration check).</summary>
  private VariableSymbol DeclareInitializedVariable(DimStmt dim, VariableDecl v, PbType type, Scope scope) {
    var key = VariableKey(v.Name, v.Suffix);
    var storage = scope.Proc == null ? VariableStorage.Global
      : scope.Proc.IsStatic || dim.StaticFlag ? VariableStorage.Static
      : VariableStorage.Local;
    var symbol = new VariableSymbol(v.Name, type, storage);
    if (scope.Proc != null) {
      if (scope.Proc.Variables.ContainsKey(key))
        this.Error(v.Position, $"variable {v.Name} already declared");
      scope.Proc.Variables[key] = symbol;
    } else {
      if (this._model.ModuleVariables.ContainsKey(key))
        this.Error(v.Position, $"variable {v.Name} already declared");
      this._model.ModuleVariables[key] = symbol;
    }
    return symbol;
  }

  private List<AssignStmt> InitListOf(DimStmt dim)
    => this._model.DimInitializers.TryGetValue(dim, out var list) ? list : this._model.DimInitializers[dim] = [];

  private void BindCallStatement(CallStmt c, Scope scope) {
    // bind arguments first so their types can pick the overload (PB 3.6)
    foreach (var argument in c.Arguments)
      this.BindExpression(argument, scope);

    // PB 3.6 nested procedure call (scoped to the enclosing proc); captures appended later
    if (this.ResolveNestedCall(c.Name, scope, c, c.Arguments) is { } nested) {
      this._model.CallBindings[c] = nested;
      return;
    }

    // PB allows CALL on a FUNCTION too - the result is discarded
    if (this.ResolveOverload(c.Name, c.Arguments) is { } proc) {
      this._model.CallBindings[c] = proc;
      if (c.Arguments.Any(a => a is NamedArgExpr))
        this.ReorderNamedArguments(c, proc, c.Arguments, c.Position);
      else if ((c.Arguments.Count < proc.RequiredParameters || c.Arguments.Count > proc.Parameters.Count) && !proc.Parameters.Any(p => Equals(p.Type, PbType.Any)))
        this.Error(c.Position, $"{(proc.IsFunction ? "FUNCTION" : "SUB")} {c.Name} expects {proc.Parameters.Count} argument(s), got {c.Arguments.Count}");
      return;
    }

    this.Error(c.Position, $"unknown SUB {c.Name}");
  }

  /// <summary>Resolves a call to a nested procedure of the enclosing proc (PB 3.6), recording the site so captures can be appended once known; null when there is no such nested proc.</summary>
  private ProcedureSymbol? ResolveNestedCall(string name, Scope scope, object callKey, IReadOnlyList<Expression> args) {
    if (scope.Proc == null || !this._nestedProcs.TryGetValue(scope.Proc, out var map) || !map.TryGetValue(name, out var lifted))
      return null;
    if (!this._nestedCallSites.TryGetValue(lifted, out var sites))
      this._nestedCallSites[lifted] = sites = [];
    sites.Add((callKey, args));
    return lifted;
  }

  /// <summary>
  /// PB 3.6 named arguments: reorders a call's arguments into positional order, placing
  /// each <c>name := value</c> by parameter name and filling omitted parameters with
  /// their defaults; records the result for codegen/IPCP. Errors on an unknown name,
  /// a duplicate, a positional argument after a named one, or a missing argument.
  /// </summary>
  private void ReorderNamedArguments(object callKey, ProcedureSymbol proc, IReadOnlyList<Expression> args, SourcePosition position) {
    var slots = new Expression?[proc.Parameters.Count];
    var seenNamed = false;
    for (var i = 0; i < args.Count; ++i) {
      if (args[i] is NamedArgExpr named) {
        seenNamed = true;
        var pi = ParamIndex(proc, named.Name);
        if (pi < 0) {
          this.Error(named.Position, $"{proc.Name} has no parameter named {named.Name}");
          return;
        }
        if (slots[pi] != null) {
          this.Error(named.Position, $"argument {named.Name} specified more than once");
          return;
        }
        slots[pi] = named.Value;
      } else if (seenNamed) {
        this.Error(args[i].Position, "a positional argument cannot follow a named argument");
        return;
      } else if (i < slots.Length) {
        slots[i] = args[i];
      } else {
        this.Error(args[i].Position, $"too many arguments to {proc.Name}");
        return;
      }
    }

    var positional = new List<Expression>(proc.Parameters.Count);
    for (var i = 0; i < proc.Parameters.Count; ++i) {
      if (slots[i] is { } provided)
        positional.Add(provided);
      else if (proc.Parameters[i].DefaultValue is { } d)
        positional.Add(d);
      else {
        this.Error(position, $"missing argument for parameter {proc.Parameters[i].Name} of {proc.Name}");
        return;
      }
    }
    this._model.ReorderedArguments[callKey] = positional;
  }

  private static int ParamIndex(ProcedureSymbol proc, string name) {
    for (var i = 0; i < proc.Parameters.Count; ++i)
      if (proc.Parameters[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        return i;
    return -1;
  }

  private void CheckAssignable(PbType target, PbType value, SourcePosition position) {
    var targetIsString = target is StringType or FixedStringType or FlexType or AsciizType;
    var valueIsString = value is StringType or FixedStringType or FlexType or AsciizType;
    if (targetIsString != valueIsString)
      this.Error(position, "type mismatch: cannot mix string and numeric");
    else if (target is UdtType tu && value is UdtType vu && !tu.Name.Equals(vu.Name, StringComparison.OrdinalIgnoreCase))
      this.Error(position, $"type mismatch: {vu.Name} cannot be assigned to {tu.Name}");
  }

  #endregion

  #region expression binding

  private PbType BindExpression(Expression expression, Scope scope) {
    var type = this.BindExpressionCore(expression, scope);
    this._model.ExpressionTypes[expression] = type;
    return type;
  }

  private PbType BindAssignTarget(Expression target, Scope scope) {
    // plain name targets always bind as variables - never as parameterless
    // function calls (Foo& = 0 inside FUNCTION Foo??? creates a LONG variable)
    if (target is NameExpr n) {
      var symbol = this.ResolveVariable(n.Name, n.Suffix, scope, create: true, n.Position)!;
      this._model.VariableBindings[n] = symbol;
      var type = symbol.Type is ArrayType arr ? arr : symbol.Type;
      this._model.ExpressionTypes[n] = type;
      return type;
    }

    if (target is not (CallOrIndexExpr or MemberExpr or PtrDerefExpr)) {
      this.Error(target.Position, "expression is not assignable");
      return PbType.Integer;
    }
    return this.BindExpression(target, scope);
  }

  private PbType BindExpressionCore(Expression expression, Scope scope) {
    switch (expression) {
      case IntegerLiteralExpr i:
        return i.Suffix switch {
          TypeSuffix.Byte => PbType.Byte,
          TypeSuffix.Word => PbType.Word,
          TypeSuffix.Dword => PbType.Dword,
          TypeSuffix.Integer => PbType.Integer,
          TypeSuffix.Long => PbType.Long,
          TypeSuffix.Quad => PbType.Quad,
          TypeSuffix.Fix => PbType.Fix,
          TypeSuffix.Bcd => PbType.Bcd,
          _ => i.Value switch {
            >= short.MinValue and <= short.MaxValue => PbType.Integer,
            >= int.MinValue and <= int.MaxValue => PbType.Long,
            _ => this._dialect.IsTurboBasic() ? PbType.Double : PbType.Quad,
          },
        };

      case FloatLiteralExpr f:
        return f.Suffix switch {
          TypeSuffix.Single => PbType.Single,
          TypeSuffix.Double => PbType.Double,
          TypeSuffix.Ext => PbType.Ext,
          TypeSuffix.Fix => PbType.Fix,
          TypeSuffix.Bcd => PbType.Bcd,
          // the parser infers SINGLE/DOUBLE for bare literals from the source
          // digit count and exponent marker (InferFloatSuffix)
          _ => PbType.Single,
        };

      case StringLiteralExpr:
        return PbType.String;

      case NamedConstantExpr c: {
        if (!this._model.Equates.TryGetValue(c.Name, out var value)) {
          this.Error(c.Position, $"undefined equate %{c.Name}");
          return PbType.Integer;
        }
        return value.Text != null ? PbType.String
          : value.Integer is >= short.MinValue and <= short.MaxValue ? PbType.Integer
          : value.Integer is >= int.MinValue and <= int.MaxValue ? PbType.Long
          : value.Integer != null ? PbType.Quad
          : PbType.Ext;
      }

      case NameExpr n: {
        var symbol = this.ResolveVariable(n.Name, n.Suffix, scope, create: false, n.Position);

        // a bare name with no matching variable may be a PB 3.6 ENUM member (its own
        // namespace - a real variable of the same name wins, hence the symbol check first)
        if (symbol == null && n.Suffix == TypeSuffix.None && this._model.EnumMembers.TryGetValue(n.Name, out var enumValue)) {
          this._model.ResolvedConstants[n] = enumValue;
          return this._model.EnumMemberTypes.GetValueOrDefault(n.Name, PbType.Integer);
        }

        // a bare name may be a parameterless FUNCTION call (PB allows omitting "()")
        if (symbol == null && this._model.Procedures.TryGetValue(n.Name, out var fn) && fn.IsFunction) {
          this._model.CallBindings[n] = fn;
          return fn.ReturnType ?? PbType.Integer;
        }

        // ... or a parameterless intrinsic (FREEFILE, TIMER, ERR, INKEY$, ...)
        if (symbol == null) {
          var intrinsicName = n.Suffix == TypeSuffix.String ? n.Name + "$" : n.Name;
          if ((Intrinsics.TryGet(intrinsicName, out var intrinsic) || Intrinsics.TryGet(n.Name, out intrinsic)) && intrinsic.MinArgs == 0) {
            this._model.IntrinsicBindings[n] = intrinsic;
            if (DialectFacts.IntrinsicGate(intrinsic.Name) is { } gate)
              this.Require(gate, n.Position);
            return this.ReturnTypeOf(intrinsic, null);
          }
        }

        symbol ??= this.ResolveVariable(n.Name, n.Suffix, scope, create: true, n.Position);
        this._model.VariableBindings[n] = symbol!;
        return symbol!.Type is ArrayType arr ? arr : symbol.Type;
      }

      case FileNumberExpr fn:
        this.BindExpression(fn.Number, scope);
        return PbType.Integer;

      case MemberExpr m: {
        // QB-style dotted variable names: when the chain root is not a UDT-typed
        // variable, the whole dotted chain is one flat variable name (Max.X)
        if (this.TryBindDottedVariable(m, scope) is { } flatType)
          return flatType;

        var targetType = this.BindExpression(m.Target, scope);
        if (targetType is not UdtType udt) {
          this.Error(m.Position, "member access on non-TYPE value");
          return PbType.Integer;
        }
        var field = udt.FindField(m.Member);
        if (field == null) {
          this.Error(m.Position, $"TYPE {udt.Name} has no field {m.Member}");
          return PbType.Integer;
        }
        return field.Type;
      }

      case AnyMatchExpr any: {
        var inner = this.BindExpression(any.Value, scope);
        if (inner is not (StringType or FixedStringType or FlexType or AsciizType))
          this.Error(any.Position, "ANY needs a string match set");
        return PbType.String;
      }

      case IndexExpr ix: {
        // indexing a member-access result: a UDT array field selects one element
        // of the field's element type, so the target's bound type carries through
        var targetType = this.BindExpression(ix.Target, scope);
        foreach (var index in ix.Arguments)
          this.BindExpression(index, scope);
        return targetType is ArrayType arr ? arr.Element : targetType;
      }

      case PtrDerefExpr deref: {
        var pointerType = this.BindExpression(deref.Pointer, scope);
        if (deref.Index != null)
          this.BindExpression(deref.Index, scope);
        if (pointerType is not PointerType ptr) {
          this.Error(deref.Position, "'@' dereference needs a variable declared AS ... PTR");
          return PbType.Integer;
        }
        return ptr.Target;
      }

      case ByValArgExpr byVal:
        return this.BindExpression(byVal.Value, scope);

      case UnaryExpr u: {
        var operand = this.BindExpression(u.Operand, scope);
        if (operand is BcdType)
          operand = PbType.Ext; // FIX/BCD compute as EXT on the x87 stack
        if (operand is not ScalarType)
          this.Error(u.Position, "unary operator needs a numeric operand");
        if (u.Op == UnaryOp.Not)
          return IntegralOf(operand);
        // PB computes integral negation in floating point too: with N% = -32768,
        // PRINT -N% shows 32768 (oracle-verified); TB and QB wrap in 16 bits
        if (u.Op == UnaryOp.Negate && this._dialect.IsPbAtLeast(Dialect.Pb20) && !this._checkedArithmetic
            && operand is ScalarType { IsFloat: false, ByteSize: <= 4 })
          return operand.Size <= 2 && u.Operand is not IntegerLiteralExpr { Value: < short.MinValue or > short.MaxValue }
            ? PbType.Single
            : PbType.Double;
        return operand;
      }

      case BinaryExpr b:
        return this.BindBinary(b, scope);

      case IfExpr ternary:
        return this.BindTernaryIf(ternary, scope);

      case NewExpr neu:
        // a NEW initializer is consumed by BindNewInitializer; reaching here means
        // it was used somewhere other than a DIM initializer.
        return this.ErrorType(neu.Position, "NEW { ... } object initializer is only allowed as a DIM initializer");

      case NamedArgExpr named: // name := value (reordering happens in the call binder)
        return this.BindExpression(named.Value, scope);

      case FromEndExpr fromEnd: // arr(^n) is consumed in the array path; here it is misused
        this.BindExpression(fromEnd.Index, scope);
        return this.ErrorType(fromEnd.Position, "'^' from-end index is only valid as an array subscript");

      case ArrayLiteralExpr lit: // { ... } is consumed by BindArrayInitializer; here it is misused
        return this.ErrorType(lit.Position, "an array initializer '{ ... }' is only allowed as a DIM array initializer");

      case CallOrIndexExpr call:
        return this.BindCallOrIndex(call, scope);

      default:
        this.Error(expression.Position, $"expression {expression.GetType().Name} not yet supported");
        return PbType.Integer;
    }
  }

  /// <summary>
  /// Attempts to bind a member chain as one flat QB-style dotted variable
  /// (<c>Max.X</c>, <c>TL.Char</c>). Succeeds when every link is a plain name,
  /// and either the flat name is a declared variable or the chain root is not
  /// a UDT-typed variable (then the flat variable is created implicitly).
  /// </summary>
  private PbType? TryBindDottedVariable(MemberExpr m, Scope scope) {
    var parts = new List<string> { m.Member };
    var target = m.Target;
    while (target is MemberExpr { Suffix: TypeSuffix.None } inner) {
      parts.Add(inner.Member);
      target = inner.Target;
    }
    if (target is not NameExpr { Suffix: TypeSuffix.None } root)
      return null;
    parts.Add(root.Name);
    parts.Reverse();
    var flatName = string.Join(".", parts);

    var declared = this.LookupVariable(VariableKey(flatName, m.Suffix), scope)
      ?? this.LookupVariable(VariableKey(flatName, m.Suffix, isArray: true), scope);
    if (declared == null) {
      // root resolving to a UDT-typed variable means real member access
      var rootSymbol = this.LookupVariable(VariableKey(root.Name, TypeSuffix.None), scope);
      if (rootSymbol?.Type is UdtType)
        return null;
      if (rootSymbol == null && this._model.Procedures.TryGetValue(root.Name, out var fn) && fn is { IsFunction: true, ReturnType: UdtType })
        return null;
    }

    var symbol = declared ?? this.ResolveVariable(flatName, m.Suffix, scope, create: true, m.Position)!;
    this._model.VariableBindings[m] = symbol;
    return symbol.Type is ArrayType arr ? arr : symbol.Type;
  }

  /// <summary>
  /// PB 3.6 ternary <c>IF(condition, whenTrue, whenFalse)</c>: the result type is the
  /// common type of the two branches (both numeric -&gt; the wider; both string -&gt;
  /// STRING). Mixing a string and a numeric branch is a type error.
  /// </summary>
  private PbType BindTernaryIf(IfExpr t, Scope scope) {
    var conditionType = this.BindExpression(t.Condition, scope);
    if (IsStringLike(conditionType))
      this.Error(t.Condition.Position, "ternary IF() condition must be numeric");

    var whenTrue = this.BindExpression(t.WhenTrue, scope);
    var whenFalse = this.BindExpression(t.WhenFalse, scope);
    if (IsStringLike(whenTrue) && IsStringLike(whenFalse))
      return PbType.String;
    if (IsStringLike(whenTrue) || IsStringLike(whenFalse))
      return this.ErrorType(t.Position, "ternary IF() branches must be both numeric or both string");
    return Widest(whenTrue, whenFalse);
  }

  private static bool IsStringLike(PbType t) => t is StringType or FixedStringType or FlexType or AsciizType;

  /// <summary>
  /// PB 3.6 scaled pointer arithmetic: <c>ptr +* i</c> / <c>ptr -* i</c> add/subtract
  /// <c>i</c> scaled by the pointer's target size. The left operand must be a pointer
  /// and the result is that pointer type; the codegen does offset-only real-mode
  /// arithmetic (the same scaling <c>@p[i]</c> uses), so raw <c>ptr + n</c> keeps its
  /// unscaled meaning and the pb35 superset holds.
  /// </summary>
  private PbType BindPointerArith(BinaryExpr b, PbType left, PbType right) {
    if (left is not PointerType ptr)
      return this.ErrorType(b.Position, "the left operand of '+*' / '-*' must be a pointer");
    if (right is not ScalarType { IsFloat: false })
      this.Error(b.Position, "the index of '+*' / '-*' must be an integer");
    return ptr; // the operator's result is the pointer type (so chaining and assignment to a pointer work)
  }

  private PbType BindBinary(BinaryExpr b, Scope scope) {
    var left = this.BindExpression(b.Left, scope);
    var right = this.BindExpression(b.Right, scope);

    // PB 3.6 scaled pointer arithmetic: ptr +* i / ptr -* i
    if (b.Op is BinaryOp.PointerAdd or BinaryOp.PointerSub)
      return this.BindPointerArith(b, left, right);

    // whole-value TYPE/UNION comparison (PB 3.1): memcmp semantics for = and <>
    if (left is UdtType || right is UdtType) {
      if (left is not UdtType lu || right is not UdtType ru || !lu.Name.Equals(ru.Name, StringComparison.OrdinalIgnoreCase))
        return this.ErrorType(b.Position, "TYPE/UNION values compare only against the same TYPE");
      if (b.Op is not (BinaryOp.Equal or BinaryOp.NotEqual))
        return this.ErrorType(b.Position, "TYPE/UNION values support only = and <> comparison");
      this.Require(LanguageFeature.UdtComparison, b.Position);
      return PbType.Integer;
    }

    // FIX/BCD operands compute as EXT on the x87 stack
    if (left is BcdType)
      left = PbType.Ext;
    if (right is BcdType)
      right = PbType.Ext;

    // pointers participate in arithmetic/comparison as raw 32-bit values
    if (left is PointerType)
      left = PbType.Dword;
    if (right is PointerType)
      right = PbType.Dword;

    var leftString = left is StringType or FixedStringType or FlexType or AsciizType;
    var rightString = right is StringType or FixedStringType or FlexType or AsciizType;

    if (leftString || rightString) {
      if (!leftString || !rightString) {
        this.Error(b.Position, "type mismatch: cannot mix string and numeric");
        return PbType.Integer;
      }
      return b.Op switch {
        BinaryOp.Add or BinaryOp.Concat => PbType.String,
        BinaryOp.Equal or BinaryOp.NotEqual or BinaryOp.Less or BinaryOp.Greater or BinaryOp.LessEqual or BinaryOp.GreaterEqual => PbType.Integer,
        _ => this.ErrorType(b.Position, "operator not defined for strings"),
      };
    }

    if (b.Op == BinaryOp.Concat)
      return this.ErrorType(b.Position, "'&' concatenation needs string operands");

    return b.Op switch {
      BinaryOp.Divide => this.DivideResultType(b, left, right),
      BinaryOp.Power => this._dialect.IsTurboBasic() ? PbType.Double
        : this._dialect.Family() == DialectFamily.Microsoft ? this.DivideResultType(b, left, right)
        : PbType.Ext,
      // a DWORD operand makes \ and MOD divide UNSIGNED on genuine PBC
      // (4000000000 \ 4 = 1000000000, oracle-verified) - even when the other
      // operand is a small signed literal that Widest would promote to LONG
      BinaryOp.IntegerDivide or BinaryOp.Modulo
        when left is ScalarType { IsFloat: false, ByteSize: 4, Signed: false }
          || right is ScalarType { IsFloat: false, ByteSize: 4, Signed: false } => PbType.Dword,
      BinaryOp.IntegerDivide or BinaryOp.Modulo => PromoteUnsigned(IntegralOf(Widest(left, right))),
      BinaryOp.And or BinaryOp.Or or BinaryOp.Xor or BinaryOp.Eqv or BinaryOp.Imp => IntegralOf(Widest(left, right)),
      // PB 3.6 shift/rotate: the result is the (integral) type of the left operand;
      // its width sets the shift/rotate width, the right operand is the count
      BinaryOp.ShiftLeft or BinaryOp.ShiftRightArith or BinaryOp.ShiftRightLogical or BinaryOp.RotateLeft or BinaryOp.RotateRight => IntegralOf(left),
      BinaryOp.Equal or BinaryOp.NotEqual or BinaryOp.Less or BinaryOp.Greater or BinaryOp.LessEqual or BinaryOp.GreaterEqual => PbType.Integer,
      _ => this.ArithmeticResultType(b, left, right),
    };
  }

  /// <summary>
  /// PB 3.5 carries unsigned arithmetic in the next wider signed type: BYTE
  /// results widen to INTEGER, WORD results to LONG (the SVGA corpus relies on
  /// WORD*WORD pixel offsets &gt; 65535). DWORD stays DWORD.
  /// </summary>
  private static PbType PromoteUnsigned(PbType t) => t is ScalarType { IsFloat: false, Signed: false } u
    ? u.Size switch { 1 => PbType.Integer, 2 => PbType.Long, _ => t }
    : t;

  /// <summary>
  /// PBC 3.50 division typing (oracle-probed): a float operand wins at its own
  /// precision; otherwise 16-bit-or-smaller integrals divide in SINGLE and
  /// anything wider (LONG/DWORD/QUAD) in DOUBLE. Integral literals participate
  /// with their value-minimal width because the genuine constant folder
  /// re-types small suffixed constants (1&amp;/3 is SINGLE while A&amp;/3 is DOUBLE).
  /// </summary>
  private PbType DivideResultType(BinaryExpr b, PbType left, PbType right) {
    // Turbo Basic computes every division in its 16-digit double runtime
    if (this._dialect.IsTurboBasic())
      return PbType.Double;
    var leftFloat = left is ScalarType { IsFloat: true };
    var rightFloat = right is ScalarType { IsFloat: true };
    if (leftFloat || rightFloat) {
      // the integral operand adopts the float operand's precision (D!/3 is
      // SINGLE, E#/3 is DOUBLE); two floats divide at the wider precision
      if (!leftFloat)
        return right;
      if (!rightFloat)
        return left;
      return ((ScalarType)left).Size >= ((ScalarType)right).Size ? left : right;
    }
    return Math.Max(EffectiveDivideWidth(b.Left, left), EffectiveDivideWidth(b.Right, right)) <= 2
      ? PbType.Single
      : PbType.Double;
  }

  private static int EffectiveDivideWidth(Expression operand, PbType type)
    => operand is IntegerLiteralExpr { Value: >= short.MinValue and <= short.MaxValue } ? 2 : type.Size;

  /// <summary>
  /// PB 2.0+ computes +, - and * of integral operands in floating point: the
  /// result displays as SINGLE for 16-bit operands (PRINT 32767 + 1 = 32768,
  /// A% * B% shows 9E+8) and DOUBLE for LONG/DWORD ones (D&amp; * 2 beyond LONG
  /// stays exact), while the full FPU precision survives into typed stores
  /// (L&amp; = A% * B% keeps 1073676289). Wrapping happens only when storing
  /// into a narrower integral. TB and the Microsoft family wrap in-place.
  /// </summary>
  private PbType ArithmeticResultType(BinaryExpr b, PbType left, PbType right) {
    if (this._dialect.IsPbAtLeast(Dialect.Pb20) && !this._checkedArithmetic
        && left is ScalarType { IsFloat: false, ByteSize: <= 4 }
        && right is ScalarType { IsFloat: false, ByteSize: <= 4 })
      return Math.Max(EffectiveDivideWidth(b.Left, left), EffectiveDivideWidth(b.Right, right)) <= 2
        ? PbType.Single
        : PbType.Double;
    return PromoteUnsigned(Widest(left, right));
  }

  private PbType ErrorType(SourcePosition position, string message) {
    this.Error(position, message);
    return PbType.Integer;
  }

  private static PbType Widest(PbType a, PbType b) {
    if (a is not ScalarType sa || b is not ScalarType sb)
      return PbType.Integer;
    if (sa.IsFloat || sb.IsFloat) {
      var size = Math.Max(sa.IsFloat ? sa.Size : 8, sb.IsFloat ? sb.Size : 8);
      return size switch { <= 4 => PbType.Single, <= 8 => PbType.Double, _ => PbType.Ext };
    }
    // integral widening; mixing signedness widens to the next signed size
    if (sa.Signed == sb.Signed)
      return sa.Size >= sb.Size ? sa : sb;
    var unsigned = sa.Signed ? sb : sa;
    var signed = sa.Signed ? sa : sb;
    return unsigned.Size >= signed.Size ? PbType.Long : signed;
  }

  private static PbType IntegralOf(PbType t) => t is ScalarType { IsFloat: false } s ? s : PbType.Long;

  /// <summary>
  /// Array lookup honoring suffix aliasing: <c>Prj$(i)</c> hits an array
  /// declared <c>DIM Prj(...) AS STRING</c> when the suffix matches the element type.
  /// </summary>
  private VariableSymbol? LookupArrayVariable(string name, TypeSuffix suffix, Scope scope) {
    var symbol = this.LookupVariable(VariableKey(name, suffix, isArray: true), scope);
    if (symbol != null || suffix == TypeSuffix.None)
      return symbol;
    var bare = this.LookupVariable(VariableKey(name, TypeSuffix.None, isArray: true), scope);
    return bare is { Type: ArrayType bareArray } && Equals(bareArray.Element, this.TypeFromSuffixOrDefault(name, suffix))
      ? bare
      : null;
  }

  /// <summary>
  /// PB 3.6 from-end index <c>arr(^n)</c>: rewrites it to <c>arr(UBOUND(arr[,dim]) - n + 1)</c>
  /// so <c>^1</c> is the last element. UBOUND folds to a constant for a static array and
  /// reads the descriptor for a dynamic one. The bound rewrite is recorded for codegen.
  /// </summary>
  private void BindFromEndIndex(CallOrIndexExpr arrayCall, ArrayType array, FromEndExpr fromEnd, int dim, Scope scope) {
    var pos = fromEnd.Position;
    var arrayName = new NameExpr(pos, arrayCall.Name, arrayCall.Suffix);
    IReadOnlyList<Expression> uboundArgs = array.Rank > 1
      ? [arrayName, new IntegerLiteralExpr(pos, dim + 1, TypeSuffix.None)]
      : [arrayName];
    var ubound = new CallOrIndexExpr(pos, "UBOUND", TypeSuffix.None, uboundArgs);
    var rewritten = new BinaryExpr(pos, BinaryOp.Add,
      new BinaryExpr(pos, BinaryOp.Subtract, ubound, fromEnd.Index),
      new IntegerLiteralExpr(pos, 1, TypeSuffix.None));
    this.BindExpression(rewritten, scope);
    this._model.ExpressionTypes[fromEnd] = PbType.Integer;
    this._model.RewrittenIndex[fromEnd] = rewritten;
  }

  private PbType BindCallOrIndex(CallOrIndexExpr call, Scope scope) {
    // 1. array indexing (or a whole-array reference like `arr()` in argument lists)
    var symbol = this.LookupArrayVariable(call.Name, call.Suffix, scope);
    if (symbol is { Type: ArrayType array }) {
      this._model.VariableBindings[call] = symbol;
      if (call.Arguments.Count == 0)
        return array;
      if (array.Rank != call.Arguments.Count && array.StaticBounds != null)
        this.Error(call.Position, $"array {call.Name} has rank {array.Rank}, got {call.Arguments.Count} index(es)");
      for (var d = 0; d < call.Arguments.Count; ++d)
        if (call.Arguments[d] is FromEndExpr fromEnd)
          this.BindFromEndIndex(call, array, fromEnd, d, scope);
        else
          this.BindExpression(call.Arguments[d], scope);
      return array.Element;
    }

    // 2. intrinsic
    var lookupName = call.Suffix == TypeSuffix.String ? call.Name + "$" : call.Name;
    if (Intrinsics.TryGet(lookupName, out var intrinsic) || Intrinsics.TryGet(call.Name, out intrinsic)) {
      this._model.IntrinsicBindings[call] = intrinsic;
      if (call.Arguments.Count < intrinsic.MinArgs || call.Arguments.Count > intrinsic.MaxArgs)
        this.Error(call.Position, $"{intrinsic.Name} expects {intrinsic.MinArgs}..{intrinsic.MaxArgs} argument(s), got {call.Arguments.Count}");

      if (DialectFacts.IntrinsicGate(intrinsic.Name) is { } gate)
        this.Require(gate, call.Position);

      // forms whose arity arrived only in 3.5
      if (intrinsic.Name == "RND" && call.Arguments.Count == 2)
        this.Require(LanguageFeature.RndRange, call.Position);
      if (intrinsic.Name is "CVI" or "CVL" or "CVS" or "CVD" or "CVE" or "CVWRD" or "CVDWD" or "CVBYT" && call.Arguments.Count == 2)
        this.Require(LanguageFeature.CvStartOffset, call.Position);

      // SIZEOF: storage size, compile-time (2 for dynamic strings - the handle)
      if (intrinsic.Name == "SIZEOF" && call.Arguments.Count == 1) {
        this.BindExpression(call.Arguments[0], scope);
        return PbType.Long;
      }

      // UBOUND/LBOUND take a bare array name (the array namespace, not the scalar's)
      if (intrinsic.Name is "UBOUND" or "LBOUND" && call.Arguments.Count >= 1 && call.Arguments[0] is NameExpr boundArray) {
        var arraySymbol = this.LookupArrayVariable(boundArray.Name, boundArray.Suffix, scope);
        if (arraySymbol == null)
          this.Error(boundArray.Position, $"{boundArray.Name} is not an array");
        else {
          this._model.VariableBindings[boundArray] = arraySymbol;
          this._model.ExpressionTypes[boundArray] = arraySymbol.Type;
        }
        for (var i = 1; i < call.Arguments.Count; ++i)
          this.BindExpression(call.Arguments[i], scope);
        return PbType.Long;
      }

      // CODEPTR-family takes a SUB/FUNCTION (or label) name and yields its code address
      if (intrinsic.Name is "CODEPTR" or "CODESEG" or "CODEPTR32" && call.Arguments is [NameExpr procRef]) {
        var ptrType = intrinsic.Name == "CODEPTR32" ? PbType.Dword
          : this._optionSigned ? PbType.Integer
          : PbType.Word;
        if (this._model.Procedures.TryGetValue(procRef.Name, out var target)) {
          this._model.CallBindings[procRef] = target;
          this._model.ExpressionTypes[procRef] = ptrType;
          return ptrType;
        }
        if (this._model.Labels.TryGetValue(scope.LabelKey, out var labels) && labels.Contains(procRef.Name)) {
          this._model.LabelBindings[procRef] = procRef.Name;
          this._model.ExpressionTypes[procRef] = ptrType;
          return ptrType;
        }
      }

      PbType? firstArg = null;
      foreach (var argument in call.Arguments) {
        var t = this.BindExpression(argument, scope);
        firstArg ??= t;
      }
      if (this._optionSigned && intrinsic.Name is "VARPTR" or "VARSEG" or "STRPTR" or "STRSEG" or "CODEPTR" or "CODESEG")
        return PbType.Integer;   // $OPTION SIGNED
      return intrinsic.Name == "RND" && call.Arguments.Count == 2
        ? PbType.Long // RND(a, z) -> LONG in [a, z]
        : this.ReturnTypeOf(intrinsic, firstArg);
    }

    // 3a. PB 3.6 nested function of the enclosing proc (scoped); captures appended later
    if (scope.Proc != null && this._nestedProcs.TryGetValue(scope.Proc, out var nestedMap) && nestedMap.ContainsKey(call.Name)) {
      foreach (var argument in call.Arguments)
        this.BindExpression(argument, scope);
      var nestedFn = this.ResolveNestedCall(call.Name, scope, call, call.Arguments)!;
      if (!nestedFn.IsFunction)
        return this.ErrorType(call.Position, $"SUB {call.Name} used as a function");
      this._model.CallBindings[call] = nestedFn;
      return nestedFn.ReturnType ?? PbType.Integer;
    }

    // 3. user function - bind arguments first so their types can select the overload
    if (this._model.Overloads.ContainsKey(call.Name)) {
      foreach (var argument in call.Arguments)
        this.BindExpression(argument, scope);
      var proc = this.ResolveOverload(call.Name, call.Arguments)!;
      if (!proc.IsFunction) {
        this.Error(call.Position, $"SUB {call.Name} used as a function");
        return PbType.Integer;
      }
      this._model.CallBindings[call] = proc;
      if (call.Arguments.Any(a => a is NamedArgExpr))
        this.ReorderNamedArguments(call, proc, call.Arguments, call.Position);
      else if (call.Arguments.Count < proc.RequiredParameters || call.Arguments.Count > proc.Parameters.Count)
        this.Error(call.Position, $"FUNCTION {call.Name} expects {proc.Parameters.Count} argument(s), got {call.Arguments.Count}");
      return proc.ReturnType ?? PbType.Integer;
    }

    foreach (var argument in call.Arguments)
      this.BindExpression(argument, scope);
    return this.ErrorType(call.Position, $"unknown array or function {call.Name}");
  }

  /// <summary>FPU math intrinsics whose QB result precision follows the argument.</summary>
  private static readonly HashSet<string> _argTypedMath = new(StringComparer.OrdinalIgnoreCase) {
    "SQR", "SIN", "COS", "TAN", "ATN", "EXP", "LOG",
  };

  private PbType ReturnTypeOf(IntrinsicInfo intrinsic, PbType? firstArg) => intrinsic.Returns switch {
    // QB math functions return their argument's precision: SQR(2) prints the
    // SINGLE "1.414214", LOG(e#) the DOUBLE-rounded " 1 " (oracle-verified)
    IntrinsicReturn.Ext when this._dialect.Family() == DialectFamily.Microsoft && _argTypedMath.Contains(intrinsic.Name)
      => firstArg is ScalarType { IsFloat: true, ByteSize: >= 8 } ? PbType.Double : PbType.Single,
    IntrinsicReturn.Integer => PbType.Integer,
    IntrinsicReturn.Quad => PbType.Quad,
    IntrinsicReturn.Fix => PbType.Fix,
    IntrinsicReturn.Bcd => PbType.Bcd,
    IntrinsicReturn.Word => PbType.Word,
    IntrinsicReturn.Dword => PbType.Dword,
    IntrinsicReturn.Long => PbType.Long,
    IntrinsicReturn.Single => PbType.Single,
    IntrinsicReturn.Double => PbType.Double,
    IntrinsicReturn.Ext => PbType.Ext,
    IntrinsicReturn.String => PbType.String,
    _ => firstArg as ScalarType ?? PbType.Ext,
  };

  private VariableSymbol? LookupVariable(string key, Scope scope) {
    if (scope.Proc != null) {
      if (scope.Proc.Variables.TryGetValue(key, out var local))
        return local;
      if (this._model.ModuleVariables.TryGetValue(key, out var shared) && shared.IsShared)
        return shared;
      return null;
    }
    return this._model.ModuleVariables.GetValueOrDefault(key);
  }

  private VariableSymbol? ResolveVariable(string name, TypeSuffix suffix, Scope scope, bool create, SourcePosition position = default) {
    // FUNCTION = expr assigns the result of the enclosing FUNCTION
    if (scope.Proc is { IsFunction: true } fnProc && name.Equals("FUNCTION", StringComparison.OrdinalIgnoreCase))
      return fnProc.Variables.GetValueOrDefault(fnProc.Name);

    var key = VariableKey(name, suffix);
    var found = this.LookupVariable(key, scope);

    // a suffixed reference may also hit an AS-declared variable of the matching type
    if (found == null && suffix != TypeSuffix.None) {
      var bare = this.LookupVariable(name, scope);
      if (bare != null && Equals(bare.Type, this.TypeFromSuffixOrDefault(name, suffix)))
        found = bare;
    }

    // PB 3.6 nested procedure: a name resolving to the enclosing proc's scalar local
    // is captured - added as a BYREF parameter the call site fills with its address
    // (stack capture). First reference adds the param; later ones find it locally.
    if (found == null && scope is { CaptureFrom: { } outer, Proc: { } nested }
        && (outer.Variables.GetValueOrDefault(key) ?? (suffix == TypeSuffix.None ? null : outer.Variables.GetValueOrDefault(name))) is { Storage: VariableStorage.Local or VariableStorage.Static, IsArray: false } captured) {
      var param = new VariableSymbol(name, captured.Type, VariableStorage.Parameter) { ByVal = false };
      nested.Parameters.Add(param);
      nested.Variables[key] = param;
      this._nestedCaptures[nested].Add(captured);
      return param;
    }

    if (found != null || !create)
      return found;

    // BASIC implicit creation on first use
    var type = this.TypeFromSuffixOrDefault(name, suffix);
    var symbol = scope.Proc == null
      ? new VariableSymbol(name, type, VariableStorage.Global)
      : new VariableSymbol(name, type, scope.Proc.IsStatic ? VariableStorage.Static : VariableStorage.Local);
    if (scope.Proc != null)
      scope.Proc.Variables[key] = symbol;
    else
      this._model.ModuleVariables[key] = symbol;
    return symbol;
  }

  #endregion
}
