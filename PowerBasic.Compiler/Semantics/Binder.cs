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
  private CompilationUnit _unit;
  private readonly Dialect _dialect;

  /// <summary>
  /// The dialect that governs runtime-observable numeric SEMANTICS (integer-arithmetic width and
  /// promotion, VAL radix wrapping): the <c>$COMPAT</c> override when set, else the compile dialect.
  /// Distinct from <see cref="_dialect"/>, which keeps gating syntax/feature availability - a
  /// transpiled-to-pb35 program is written in pb35 syntax but must compute like its source dialect.
  /// </summary>
  private Dialect EffectiveDialect => this._model.CompatDialect ?? this._dialect;
  private readonly Dictionary<char, PbType> _defaultTypes = [];
  private readonly HashSet<string> _redimmedArrays = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>pb36 generators (SUB/FUNCTION with YIELD): names whose call constructs an enumerator instance rather than calling a procedure.</summary>
  private readonly HashSet<string> _generatorNames = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>Each generator's parameter names (in order), so a construction call seeds the enumerator's captured-parameter fields.</summary>
  private readonly Dictionary<string, IReadOnlyList<string>> _generatorParams = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>Inside a generator, a FOR EACH over another generator that itself contains a YIELD: the enumerator field ($fe&lt;n&gt;) in THIS that holds the inner iterator (so its state persists across the outer YIELDs), and the inner generator's name. Keyed by AST node identity, populated before MoveNext lowering.</summary>
  private readonly Dictionary<ForEachStmt, (string Field, string GenName)> _foreachEnumField = new(ReferenceEqualityComparer.Instance);

  /// <summary>pb36 TYPEs with a constructor (a member SUB named like the TYPE): <c>p = Type(args)</c> calls it with the target as the BYREF THIS, after zeroing the instance.</summary>
  private readonly HashSet<string> _typeConstructors = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>pb36 READONLY TYPEs: their fields may be written only inside the type's own constructor.</summary>
  private readonly HashSet<string> _readonlyTypes = new(StringComparer.OrdinalIgnoreCase);
  // pb36 type aliases (TYPE Name AS type): resolved lazily by ResolveTypeName; the stack detects cycles
  private readonly Dictionary<string, TypeName> _typeAliases = new(StringComparer.OrdinalIgnoreCase);
  private int _sliceCounter;
  private readonly HashSet<string> _aliasResolutionStack = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>pb36 bit-fields: "Udt.field" (case-insensitive) -> the hidden storage word it lives in, its bit offset and width. Member access desugars to shift/mask on the storage word.</summary>
  private readonly Dictionary<string, (string Storage, int Offset, int Width, int ContainerBits)> _bitFields = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>pb36 generic procedures: name -> the template declaration (SUB/FUNCTION ... OF T). A call infers the type arguments and instantiates a concrete procedure named with the mangle.</summary>
  private readonly Dictionary<string, Statement> _genericProcs = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>pb36 events: name -> the backing handler-array/count module-variable names and the delegate's parameters (types + BYVAL flags, used to pass RAISE arguments correctly). <c>+=</c>/<c>-=</c>/RAISE desugar against these.</summary>
  private readonly Dictionary<string, (string Array, string Count, IReadOnlyList<VariableSymbol> Params)> _events = new(StringComparer.OrdinalIgnoreCase);
  /// <summary>Fixed handler capacity per event (a resizable list is a later increment).</summary>
  private const int EventCapacity = 32;
  private int _eventTemp;
  /// <summary>pb36 generic procedure instantiations already created (mangled name -> symbol), to instantiate each once.</summary>
  private readonly Dictionary<string, ProcedureSymbol> _genericProcInstances = new(StringComparer.OrdinalIgnoreCase);
  /// <summary>pb36 generic procedure instances whose bodies still need binding (drained after the main body-binding pass, so adding them never mutates the loop being iterated).</summary>
  private readonly Queue<ProcedureSymbol> _genericBindQueue = new();
  /// <summary>Generic-procedure instance symbols (bound only via the drain, skipped by the main proc-body loop).</summary>
  private readonly HashSet<ProcedureSymbol> _genericInstanceProcs = new(ReferenceEqualityComparer.Instance);
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
  // PB 3.6 inline lambdas lifted to anonymous procs, to bind (capture-checked) after the main bodies
  private readonly List<(ProcedureSymbol Lifted, ProcedureSymbol? Enclosing, SourcePosition Position)> _pendingLambdas = [];
  private int _lambdaCounter;
  // PB 3.6: the delegate type an expression is being bound against (assignment/DIM target), so a lambda can infer omitted parameter and result types from it
  private ProcPtrType? _expectedSignature;

  private Binder(CompilationUnit unit, Dialect dialect) {
    this._unit = unit;
    this._dialect = dialect;
    this._model = new() { FileName = unit.FileName, Dialect = dialect };
    this._folder = new(this._model.Equates, this._model.EnumMembers, this.FoldDesugared);
  }

  public static SemanticModel Bind(CompilationUnit unit, Dialect dialect = Dialect.Pb35) {
    var binder = new Binder(unit, dialect);
    binder.ExpandGenerics();
    binder.SeedInternalVariables();
    binder.CollectRedims(binder._unit.Statements);
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
    if (this._model.DimInitializers.Count == 0 && this._model.DesugaredStatements.Count == 0)
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
      // pb36: a statement-level desugar (member call/property set, generator construct, FOR EACH)
      // is spliced into the real AST here so every analysis (reachability, dead-globals, ...) sees
      // the lowered form, not the opaque surface statement
      if (this._model.DesugaredStatements.TryGetValue(statement, out var desugared)) {
        result.AddRange(this.SpliceBody([desugared]));
        continue;
      }
      // a synthesized statement group flattens inline - codegen and the back-emitter see plain statements
      if (statement is GroupStmt group) {
        result.AddRange(this.SpliceBody(group.Body));
        continue;
      }
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

  /// <summary>pb36 generics: each monomorphized TYPE instance mapped to its (template name, concrete type arguments), for inferring a generic procedure's type parameters from a generic-typed argument (s AS Stack OF T).</summary>
  private IReadOnlyDictionary<string, (string Template, IReadOnlyList<TypeName> Args)> _instanceArgs = new Dictionary<string, (string, IReadOnlyList<TypeName>)>();

  /// <summary>pb36 generics: vivify every generic instantiation into a concrete TYPE before binding, so the binder only ever sees concrete types (see <see cref="Monomorphizer"/>).</summary>
  private void ExpandGenerics() {
    var (statements, any, instances) = Monomorphizer.Expand(this._unit, this.Error);
    this._instanceArgs = instances;
    if (any)
      this._unit = this._unit with { Statements = statements };
  }

  private void Error(SourcePosition position, string message) => this._model.Errors.Add(new(position, message));
  private void Warn(SourcePosition position, string message) => this._model.Warnings.Add(new(position, message));

  /// <summary>
  /// Commands this runtime parses and binds in full and then emits nothing for.
  ///
  /// They are not errors: the genuine compiler accepts every one of them, and refusing them would
  /// turn programs that compile today into programs that do not - the sibling graphics corpus alone
  /// uses PALETTE and WIDTH. But accepting them in silence is its own bug, because the program then
  /// runs with its colours, screen width and viewport quietly not applied and nothing anywhere says
  /// so. A warning is the honest middle: no bytes change, nothing is rejected, and the gap is on the
  /// record at the point it matters.
  ///
  /// Strike a name from here when it gains a code generator - the test over this set insists on it.
  /// </summary>
  public static readonly IReadOnlySet<string> CommandsWithNoEffect = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
    "COLOR", "WIDTH", "KEY", "VIEW", "VIEW TEXT", "VIEW PRINT", "VIEW SCREEN",
    "WINDOW", "PALETTE", "PALETTE USING", "PLAY",
  };

  /// <summary>
  /// Checks a constant PLAY or DRAW string against its macro grammar.
  ///
  /// Both languages are otherwise read byte by byte at run time, where a typo is a runtime error at
  /// best. PLAY is the worse of the two today: it binds, warns that it does nothing, and compiles,
  /// so a malformed tune reaches the executable with nothing said about it at all. When the string
  /// is a constant the grammar is knowable here, and there is no reason to wait for a run that will
  /// not happen.
  ///
  /// A computed string is left alone - there is nothing to check - and so is one the folder cannot
  /// reduce whole. It is a warning rather than an error because the genuine compiler accepts these
  /// strings and finds out later, and refusing a program it takes would be the larger bug.
  /// </summary>
  private void CheckMacroString(CommandStmt cmd, Scope scope) {
    if (cmd.Keyword is not ("PLAY" or "DRAW") || cmd.Arguments is not [{ } argument])
      return;
    if (this._folder.TryFold(argument) is not { Text: { } text })
      return;

    var complaint = cmd.Keyword == "PLAY"
      ? MacroStringValidator.ValidatePlay(text)
      : MacroStringValidator.ValidateDraw(text);
    if (complaint is not null)
      this.Warn(cmd.Position, $"{cmd.Keyword} string: {complaint}");
  }

  /// <summary>Reports a command that binds but does nothing, and the one that binds in the wrong place.</summary>
  private void NoteCommandWithNoEffect(CommandStmt cmd) {
    // A module-level OPTION BASE never reaches here - the pre-pass consumes the valid spellings and
    // refuses the rest - so one that does is inside a procedure, where it would be read after the
    // arrays it is meant to govern have already been bound. Silently ignoring it there would undo
    // the point of implementing it at all.
    if (cmd.Keyword == "OPTION BASE") {
      this.Error(cmd.Position, "OPTION BASE must appear at module level, before the arrays it affects");
      return;
    }

    if (CommandsWithNoEffect.Contains(cmd.Keyword))
      this.Warn(cmd.Position, $"{cmd.Keyword} is accepted but has no effect: this runtime does not implement it");
  }

  /// <summary>Folder hook: folds what the surface tree alone cannot - bind-time desugars recorded in the model (e.g. compile-time reflection calls already replaced by literals).</summary>
  private ConstantValue? FoldDesugared(Expression e)
    => this._model.Desugared.TryGetValue(e, out var d) ? this._folder.TryFold(d) : null;

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

        case TypeDecl t: {
          // pb36 bit-fields: pack runs of AS BIT * n fields into hidden $bits<k> WORD storage, recording
          // each field's (storage, offset, width) so member access desugars to shift/mask on the word
          var packed = this.PackBitFields(t);
          // pb36: each property gets a hidden backing field ($Prop) so FIELD / auto accessors have storage
          var backing = PropertyBackingFields(t);
          var fields = backing.Count == 0 ? packed
            : [.. packed, .. backing.Select(b => new TypeField(t.Position, b.Field, b.Type, null))];
          if (t.IsReadonly)
            this._readonlyTypes.Add(t.Name);
          this.DefineUdt(t.Name, fields, isUnion: false, t.Position, t.Alignment, t.ExplicitSize);
          this.DefineTypeMembers(t, backing.ToDictionary(b => b.Prop, b => b, StringComparer.OrdinalIgnoreCase));
          break;
        }

        case ResourceStmt res: {
          // $RESOURCE name, "file": read the bytes now (relative to the source file) and declare
          // the static BYTE array; codegen bakes the bytes into the array's data slot
          var dir = System.IO.Path.GetDirectoryName(this._unit.FileName) ?? "";
          var path = System.IO.Path.Combine(dir, res.FileName);
          byte[] bytes;
          try {
            bytes = System.IO.File.ReadAllBytes(path);
          } catch (System.IO.IOException) {
            this.Error(res.Position, $"$RESOURCE: cannot read '{res.FileName}'");
            break;
          } catch (UnauthorizedAccessException) {
            this.Error(res.Position, $"$RESOURCE: cannot read '{res.FileName}'");
            break;
          }
          if (bytes.Length == 0) {
            this.Error(res.Position, $"$RESOURCE: '{res.FileName}' is empty");
            break;
          }
          var resKey = VariableKey(res.Name, TypeSuffix.None, isArray: true);
          if (this._model.ModuleVariables.ContainsKey(resKey)) {
            this.Error(res.Position, $"duplicate variable {res.Name}");
            break;
          }
          var resSymbol = new VariableSymbol(res.Name, new ArrayType(PbType.Byte, [(0, bytes.Length - 1)], 1), VariableStorage.Global);
          this._model.ModuleVariables[resKey] = resSymbol;
          this._model.ResourceData[resSymbol] = bytes;
          this._model.MainBody.Add(res);   // codegen skips it (data-only); the back-emitter renders the DATA reconstruction
          break;
        }

        case TypeAliasDecl a:
          if (this._model.Udts.ContainsKey(a.Name) || this._model.EnumTypes.ContainsKey(a.Name) || !this._typeAliases.TryAdd(a.Name, a.Target))
            this.Error(a.Position, $"duplicate type name '{a.Name}'");
          else
            this._model.TypeAliases[a.Name] = a.Target;
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

        case SubDecl { TypeParameters.Count: > 0 } gs:
          this._genericProcs[gs.Name] = gs;   // pb36 generics: a template, instantiated per call (not a callable proc)
          break;

        case SubDecl s:
          this.DefineProcedure(s.Name, isFunction: false, TypeSuffix.None, null, s.Parameters, s.IsStatic, s.Body, s.Position, s.Convention, s.NoInline);
          break;

        case FunctionDecl { TypeParameters.Count: > 0 } gf:
          this._genericProcs[gf.Name] = gf;   // pb36 generics: a template, instantiated per call
          break;

        case FunctionDecl f:
          if (ContainsYield(f.Body))
            this.SynthesizeGenerator(f);   // pb36 coroutine: lower to an enumerator TYPE, not a callable function
          else
            this.DefineProcedure(f.Name, isFunction: true, f.Suffix, f.ReturnType, f.Parameters, f.IsStatic, f.Body, f.Position, f.Convention, f.NoInline);
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
            case "OPTION" when m.Arguments is [{ } opt, ..] && opt.Text.Equals("VIDEO", StringComparison.OrdinalIgnoreCase):
              this._model.FastVideo = true;   // R1: direct-video console PRINT
              break;
            case "COMPAT" when m.Arguments is [{ } compat, ..] && DialectFacts.TryParse(compat.Text, out var fmt):
              // $COMPAT <dialect>: replicate that dialect's runtime quirks (numeric PRINT formatting,
              // float-to-integer rounding) - emitted by the back-emitter so a transpiled-to-pb35
              // program prints and rounds like its source dialect
              this._model.CompatDialect = fmt;
              break;
          }
          this._model.MainBody.Add(m);
          break;

        case DimStmt dim:
          this.DeclareModuleVariables(dim);
          this._model.MainBody.Add(dim); // dynamic arrays allocate at run time
          break;

        case EventDeclStmt ev:
          this.SynthesizeEvent(ev);
          break;

        case CommandStmt { Keyword: "OPTION BASE" } ob when ob.Arguments is [IntegerLiteralExpr { Value: 0 or 1 } b]:
          this._optionBase = (int)b.Value;
          break;

        // Anything else is not a base the language has. Letting it through silently would be the
        // worst of the three answers: OPTION BASE decides the implicit lower bound of every array
        // declared after it, so a value that does not take effect shifts an entire program's
        // subscripts with nothing to show for it.
        case CommandStmt { Keyword: "OPTION BASE" } bad:
          this.Error(bad.Position, "OPTION BASE takes a literal 0 or 1");
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

  private void DefineUdt(string name, IReadOnlyList<TypeField> fields, bool isUnion, SourcePosition position, int alignment = 0, Expression? explicitSize = null) {
    if (this._model.Udts.ContainsKey(name)) {
      this.Error(position, $"TYPE {name} already defined");
      return;
    }

    var resolved = new List<UdtField>();
    var offset = 0;       // running pack offset (TYPE only)
    var naturalEnd = 0;   // highest field end - the natural total size
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

      var size = fieldType.Size * count;
      int fieldOffset;
      if (isUnion) {
        fieldOffset = 0;
      } else if (field.ExplicitOffset is { } offExpr) {
        // pb36 layout control: field AS T AT offset - place at an explicit byte offset (gaps/overlap allowed)
        if (this._folder.TryFold(offExpr)?.Integer is not { } at || at < 0) {
          this.Error(field.Position, $"field offset of {name}.{field.Name} must be a non-negative constant");
          at = offset;
        }
        fieldOffset = (int)at;
        offset = fieldOffset + size;
      } else {
        // pb36 ALIGN n: round the running offset up to the field's natural alignment (capped at n)
        if (alignment > 1)
          offset = RoundUpTo(offset, Math.Min(alignment, NaturalAlignment(fieldType)));
        fieldOffset = offset;
        offset += size;
      }

      resolved.Add(new(field.Name, fieldType, fieldOffset, count));
      naturalEnd = Math.Max(naturalEnd, fieldOffset + size);
    }

    // pb36 layout control: ALIGN rounds the whole type up to a multiple of n; SIZE n fixes the total
    var totalSize = 0;
    if (!isUnion && alignment > 1)
      totalSize = RoundUpTo(naturalEnd, alignment);
    if (explicitSize != null) {
      if (this._folder.TryFold(explicitSize)?.Integer is not { } sz || sz < naturalEnd)
        this.Error(position, $"TYPE {name} SIZE must be a constant of at least its natural size ({naturalEnd} bytes)");
      else
        totalSize = (int)sz;
    }

    this._model.Udts[name] = new(name, resolved, isUnion, totalSize);
  }

  /// <summary>Natural alignment of a field type: a scalar aligns to its byte size, an array to its element's, an aggregate to its widest member; everything else to 1.</summary>
  private static int NaturalAlignment(PbType type) => type switch {
    ScalarType s => s.ByteSize,
    ArrayType a => NaturalAlignment(a.Element),
    UdtType u => u.Fields.Count == 0 ? 1 : u.Fields.Max(f => NaturalAlignment(f.Type)),
    _ => 1,
  };

  private static int RoundUpTo(int value, int alignment) => alignment <= 1 ? value : (value + alignment - 1) / alignment * alignment;

  private void DeclareProcedure(DeclareStmt d) {
    var proc = new ProcedureSymbol(d.Name, d.IsFunction) { Position = d.Position, CallConv = d.Convention, Alias = d.Alias };
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

  private ProcedureSymbol DefineProcedure(string name, bool isFunction, TypeSuffix suffix, TypeName? returnType, IReadOnlyList<Parameter> parameters, bool isStatic, IReadOnlyList<Statement> body, SourcePosition position, CallConvention convention = CallConvention.Basic, bool noInline = false) {
    var proc = new ProcedureSymbol(name, isFunction) { IsStatic = isStatic, NoInline = noInline, Body = body, Position = position, CallConv = convention, IsGenerator = ContainsYield(body) };
    if (isFunction)
      proc.ReturnType = this.ResolveReturnType(name, suffix, returnType);
    foreach (var p in parameters)
      proc.Parameters.Add(this.BindParameter(p));

    // pb36: a FUNCTION returning a UDT by value uses the struct-return convention - a hidden trailing
    // BYREF result-buffer parameter the body writes through; the result variable aliases it
    if (isFunction && proc.ReturnType is UdtType sretType) {
      proc.Parameters.Add(new VariableSymbol(GeneratedPrefix + "sret", sretType, VariableStorage.Parameter) { ByVal = false });
      proc.HasSretParam = true;
    }

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

  /// <summary>
  /// pb36 generic procedures: a call to a generic SUB/FUNCTION template infers its type arguments from
  /// the (already-bound) argument types - each type parameter is read off the first parameter declared
  /// as that bare type - then monomorphizes a concrete instance (named with the mangle) and returns its
  /// symbol. Null when the name is not a generic template; a clear error when a parameter cannot be
  /// inferred. The instance body is queued, bound after the main pass (so the proc list is not mutated mid-iteration).
  /// </summary>
  private ProcedureSymbol? ResolveGenericCall(string name, IReadOnlyList<Expression> args, SourcePosition position, Scope scope, IReadOnlyList<TypeName>? explicitTypeArgs = null) {
    if (!this._genericProcs.TryGetValue(name, out var template))
      return null;
    foreach (var argument in args)   // bind the arguments so their types drive type-parameter inference
      this.BindExpression(argument, scope);
    var (typeParams, parameters, isFunction, suffix, returnType, body) = template switch {
      FunctionDecl f => (f.TypeParameters, f.Parameters, true, f.Suffix, f.ReturnType, f.Body),
      SubDecl s => (s.TypeParameters, s.Parameters, false, TypeSuffix.None, (TypeName?)null, s.Body),
      _ => ([], [], false, TypeSuffix.None, null, (IReadOnlyList<Statement>)[]),
    };

    var map = new Dictionary<string, TypeName>(StringComparer.OrdinalIgnoreCase);
    if (explicitTypeArgs is { Count: > 0 }) {
      // explicit type arguments: Name OF (T1, T2)(args) - bind each type parameter directly
      if (explicitTypeArgs.Count != typeParams.Count) {
        this.Error(position, $"generic {(isFunction ? "FUNCTION" : "SUB")} {name} takes {typeParams.Count} type argument(s), got {explicitTypeArgs.Count}");
        return null;
      }
      for (var i = 0; i < typeParams.Count; ++i)
        map[typeParams[i]] = explicitTypeArgs[i];
    } else {
      // infer each type parameter from the first parameter declared as the bare parameter type
      var typeParamSet = new HashSet<string>(typeParams, StringComparer.OrdinalIgnoreCase);
      for (var i = 0; i < parameters.Count && i < args.Count; ++i)
        if (parameters[i].Type is { } pt && this.ArgTypeName(this._model.TypeOf(args[i]), position) is { } at)
          this.UnifyTypeName(pt, at, typeParamSet, map);   // bare T, or nested e.g. Stack OF T against Stack@Long
      foreach (var tp in typeParams)
        if (!map.ContainsKey(tp)) {
          this.Error(position, $"cannot infer type parameter '{tp}' of generic {(isFunction ? "FUNCTION" : "SUB")} {name} from the arguments (give explicit type arguments: {name} OF <type>)");
          return null;
        }
    }

    var mangled = Monomorphizer.MangleName(name, typeParams.Select(tp => map[tp]).ToList());
    if (this._genericProcInstances.TryGetValue(mangled, out var existing))
      return existing;

    // monomorphize: clone the template's parameters and body with the type parameters substituted
    var concreteParams = parameters.Select(p => (Parameter)Monomorphizer.SubstituteClone(p, map)!).ToList();
    var concreteBody = body.Select(s => (Statement)Monomorphizer.SubstituteClone(s, map)!).ToList();
    var concreteReturn = returnType is { } rt ? (TypeName)Monomorphizer.SubstituteClone(rt, map)! : null;
    var proc = this.DefineProcedure(mangled, isFunction, suffix, concreteReturn, concreteParams, isStatic: false, concreteBody, position);
    if (isFunction)
      proc.ResultName = name;   // the cloned body assigns the template's simple name (Max = ...) as its result
    this._genericProcInstances[mangled] = proc;
    this._genericInstanceProcs.Add(proc);
    this._genericBindQueue.Enqueue(proc);
    return proc;
  }

  /// <summary>The AST type name corresponding to a bound type (for generic type-argument inference / substitution); null for a type that cannot be a type argument.</summary>
  private TypeName? TypeNameOf(PbType type, SourcePosition pos) => type switch {
    ScalarType => LocalFieldTypeName(pos, type),
    StringType => new TypeName(pos, BuiltinType.String),
    UdtType u => new TypeName(pos, BuiltinType.None, u.Name),
    _ => null,
  };

  /// <summary>The argument's type as a type name for generic inference: a monomorphized instance (Stack@Long) is reconstructed as the generic use it came from (Stack OF LONG), so a nested pattern can unify against it.</summary>
  private TypeName? ArgTypeName(PbType type, SourcePosition pos) {
    if (type is UdtType u && this._instanceArgs.TryGetValue(u.Name, out var origin))
      return new TypeName(pos, BuiltinType.None, origin.Template) { TypeArguments = origin.Args };
    return TypeNameOf(type, pos);
  }

  /// <summary>Unifies a generic parameter type pattern against a concrete argument type name, binding each type parameter: a bare T binds to the whole concrete type; a generic-use pattern (Stack OF T) unifies position-by-position against a matching concrete instance (Stack OF LONG).</summary>
  private void UnifyTypeName(TypeName pattern, TypeName concrete, HashSet<string> typeParams, Dictionary<string, TypeName> map) {
    if (pattern is { IsUserDefined: true, IsGenericUse: false, UserTypeName: { } pn } && typeParams.Contains(pn)) {
      map.TryAdd(pn, concrete);
      return;
    }
    if (pattern.IsGenericUse && concrete.IsGenericUse
        && string.Equals(pattern.UserTypeName, concrete.UserTypeName, StringComparison.OrdinalIgnoreCase)
        && pattern.TypeArguments!.Count == concrete.TypeArguments!.Count)
      for (var i = 0; i < pattern.TypeArguments.Count; ++i)
        this.UnifyTypeName(pattern.TypeArguments[i], concrete.TypeArguments[i], typeParams, map);
    if (pattern.IsPointer && concrete.IsPointer)
      this.UnifyTypeName(pattern.PointerTarget!, concrete.PointerTarget!, typeParams, map);
  }

  /// <summary>
  /// pb36: lifts each TYPE member to an ordinary procedure that takes the instance
  /// BYREF as an implicit first parameter named THIS. The mangled name embeds a '.'
  /// (which a user identifier cannot), so member procs never collide with user names
  /// and call resolution just rebuilds the name from the receiver's static type.
  /// </summary>
  private void DefineTypeMembers(TypeDecl t, Dictionary<string, (string Prop, string Field, TypeName Type)> backing) {
    foreach (var m in t.Members) {
      var thisParam = new Parameter(m.Position, "THIS", TypeSuffix.None, new TypeName(m.Position, BuiltinType.None, UserTypeName: t.Name), ByVal: false, Seg: false, IsArray: false);
      var parameters = new List<Parameter>(m.Parameters.Count + 1) { thisParam };
      var isFunction = m.Kind is TypeMemberKind.Function or TypeMemberKind.PropertyGet or TypeMemberKind.Operator;
      var isProperty = m.Kind is TypeMemberKind.PropertyGet or TypeMemberKind.PropertySet;
      if (m.Kind == TypeMemberKind.Sub && m.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase))
        this._typeConstructors.Add(t.Name);   // a SUB named like the TYPE is its constructor
      backing.TryGetValue(m.Name, out var bk);   // bk is default (Field == null) when there is no backing field
      var hasBacking = isProperty && bk.Field != null;
      var body = m.Body;
      string? valueParam = null;

      if (m.Kind == TypeMemberKind.PropertySet) {
        // the incoming value: the explicit first parameter, or an injected VALUE of the property type
        if (m.Parameters.Count > 0) {
          parameters.AddRange(m.Parameters);
          valueParam = m.Parameters[0].Name;
        } else {
          valueParam = "VALUE";
          parameters.Add(new Parameter(m.Position, "VALUE", TypeSuffix.None, hasBacking ? bk.Type : null, ByVal: true, Seg: false, IsArray: false));
        }
        // an auto setter just stores the value into its backing field (a trivial body the optimizer inlines)
        if (m.IsAuto && hasBacking)
          body = [new AssignStmt(m.Position, ThisField(m.Position, bk.Field), new NameExpr(m.Position, valueParam, TypeSuffix.None))];
      } else {
        parameters.AddRange(m.Parameters);
        // an auto getter just yields its backing field (a trivial body the optimizer inlines)
        if (m.Kind == TypeMemberKind.PropertyGet && m.IsAuto && hasBacking)
          body = [new AssignStmt(m.Position, new NameExpr(m.Position, m.Name, m.Suffix), ThisField(m.Position, bk.Field))];
      }

      var returnType = m.Kind == TypeMemberKind.PropertySet ? null : m.ReturnType;
      var proc = this.DefineProcedure(MemberProcName(t.Name, m), isFunction, m.Suffix, returnType, parameters, isStatic: false, body, m.Position);
      if (isFunction)
        // an operator body assigns its result via the RESULT keyword; other members use the member name
        proc.ResultName = m.Kind == TypeMemberKind.Operator ? "RESULT" : m.Name;
      if (hasBacking)
        proc.BackingField = bk.Field;   // the FIELD keyword resolves to THIS.<backing> in this accessor
      proc.ValueParamName = valueParam; // the VALUE keyword aliases the set value parameter
    }
  }

  private static MemberExpr ThisField(SourcePosition pos, string field)
    => new(pos, new NameExpr(pos, "THIS", TypeSuffix.None), field, TypeSuffix.None);

  /// <summary>
  /// pb36 bit-fields: replaces each run of consecutive <c>AS BIT * n</c> fields with hidden storage
  /// fields (<c>$bits0</c>, ...) and records each bit-field's (storage, bit offset, width, container
  /// bits) in <see cref="_bitFields"/>. Packing is DENSE: fields fill a container greedily (a field
  /// never straddles containers), and each container takes the smallest size holding its bits - up to
  /// 8 used bits a BYTE, else a WORD. So one flag costs 1 byte, 17 bits cost a WORD plus a BYTE.
  /// Ordinary fields pass through unchanged and break the current run.
  /// </summary>
  private IReadOnlyList<TypeField> PackBitFields(TypeDecl t) {
    if (!t.Fields.Any(f => f.BitWidth > 0))
      return t.Fields;
    var result = new List<TypeField>();
    var run = new List<TypeField>();
    var storageIndex = 0;

    void FlushRun() {
      if (run.Count == 0)
        return;
      // assign fields to containers greedily (capacity 16 bits, no straddling)
      var containers = new List<List<TypeField>>();
      var bit = 17;   // force a new container for the first field
      foreach (var f in run) {
        if (bit + f.BitWidth > 16) {
          containers.Add([]);
          bit = 0;
        }
        containers[^1].Add(f);
        bit += f.BitWidth;
      }
      // size each container by its used bits and record every member's placement
      foreach (var container in containers) {
        var used = container.Sum(f => f.BitWidth);
        var containerBits = used <= 8 ? 8 : 16;
        var storage = GeneratedPrefix + "bits" + storageIndex++;
        result.Add(new TypeField(container[0].Position, storage, new TypeName(container[0].Position, containerBits == 8 ? BuiltinType.Byte : BuiltinType.Word), null));
        var offset = 0;
        foreach (var f in container) {
          this._bitFields[t.Name + "." + f.Name] = (storage, offset, f.BitWidth, containerBits);
          offset += f.BitWidth;
        }
      }
      run.Clear();
    }

    foreach (var f in t.Fields)
      if (f.BitWidth > 0) {
        run.Add(f);
      } else {
        FlushRun();
        result.Add(f);
      }
    FlushRun();
    return result;
  }

  /// <summary>The hidden storage cell / bit offset / width / container size of a UDT bit-field <c>o.field</c>, or null when it is not a bit-field.</summary>
  private (string Storage, int Offset, int Width, int ContainerBits)? BitFieldOf(PbType udt, string member)
    => udt is UdtType u && this._bitFields.TryGetValue(u.Name + "." + member, out var info) ? info : null;

  /// <summary>
  /// Builds the MoveNext state machine for a generator body of TOP-LEVEL yields. Each YIELD k
  /// stores its value into the Current backing field, records resume state k, returns true and
  /// EXITs; a SELECT CASE on $state dispatches to the label after each yield on the next call,
  /// and the tail returns false. (Yields inside loops / nested control flow are a later wave.)
  /// </summary>
  private IReadOnlyList<Statement> BuildMoveNextBody(SourcePosition pos, IReadOnlyList<Statement> genBody, string currentField) {
    Expression Int(long v) => new IntegerLiteralExpr(pos, v, TypeSuffix.None);
    var ctx = new GenLower(pos, currentField);
    // structured TRY / CATCH / FINALLY is the only error model a generator supports: the inline
    // ON ERROR / RESUME handler arms a stack frame a YIELD's EXIT would unwind, so reject it
    if (ContainsOnError(genBody))
      this.Error(pos, "ON ERROR / RESUME is not supported in a generator (a YIELD unwinds the handler frame) - use TRY / CATCH / FINALLY instead");
    // a YIELD inside a TRY needs the saved-handler enumerator fields and per-frame re-arming
    var hasTry = ContainsYieldingTry(genBody);
    if (hasTry) {
      ctx.HOnerr = ThisField(pos, GeneratedPrefix + "gonerr");
      ctx.HBp = ThisField(pos, GeneratedPrefix + "gbp");
      ctx.HSp = ThisField(pos, GeneratedPrefix + "gsp");
    }
    this.LowerGenBody(genBody, ctx);
    if (ContainsYield(ctx.Body))   // a YIELD that could not be lowered (only a TRY nested in a TRY now)
      this.Error(pos, "a YIELD here is not yet supported in a generator (it sits in a construct - e.g. a TRY nested in another TRY - the state machine cannot yet re-enter)");

    var state = ThisField(pos, GeneratedPrefix + "state");
    var arms = new List<CaseArm> { new(pos, [new CaseSelector(pos, Int(0), null, null)], [new GotoStmt(pos, "$start")]) };
    arms.AddRange(ctx.Dispatch);
    arms.Add(new CaseArm(pos, [], [new GotoStmt(pos, "$done")]));   // exhausted / past the last yield

    var body = new List<Statement>();
    // snapshot the caller's ON ERROR handler this invocation, so a YIELD inside a TRY can restore it
    if (hasTry)
      body.Add(new HandlerSaveStmt(pos, ctx.HOnerr!, ctx.HBp!, ctx.HSp!));
    body.Add(new SelectStmt(pos, state, arms));
    body.Add(new LabelStmt(pos, "$start"));
    body.AddRange(ctx.Body);
    body.Add(new LabelStmt(pos, "$done"));
    body.Add(new AssignStmt(pos, state, Int(-1)));
    body.Add(new AssignStmt(pos, new NameExpr(pos, "MoveNext", TypeSuffix.None), Int(0)));
    return body;
  }

  /// <summary>True when a body contains an inline ON ERROR / RESUME handler (which a generator cannot host across a YIELD), recursing nested blocks but not nested procedures.</summary>
  private static bool ContainsOnError(IReadOnlyList<Statement> body) {
    foreach (var s in body) {
      if (s is OnErrorStmt or ResumeStmt)
        return true;
      if (s is SubDecl or FunctionDecl or DefFnDecl)
        continue;
      foreach (var block in ChildBlocks(s))
        if (ContainsOnError(block))
          return true;
    }
    return false;
  }

  /// <summary>True when a generator body contains a TRY whose protected region (or catch/finally) yields - it needs the saved-handler fields and per-frame re-arming.</summary>
  private static bool ContainsYieldingTry(IReadOnlyList<Statement> body) {
    foreach (var s in body) {
      if (s is TryStmt && ContainsYield([s]))
        return true;
      if (s is SubDecl or FunctionDecl or DefFnDecl)
        continue;
      foreach (var block in ChildBlocks(s))
        if (ContainsYieldingTry(block))
          return true;
    }
    return false;
  }

  /// <summary>Mutable state threaded through the generator-body flattening: the linearized output, the SELECT dispatch arms, and the running yield/label counters.</summary>
  private sealed class GenLower(SourcePosition pos, string currentField) {
    public readonly SourcePosition Pos = pos;
    public readonly string CurrentField = currentField;
    public readonly List<Statement> Body = [];
    public readonly List<CaseArm> Dispatch = [];
    public int State;
    public int Label;
    // generator-in-TRY: the enumerator fields that save the ON ERROR handler triple (null when the
    // generator has no yielding TRY), and the catch label of the TRY whose body is currently lowering
    // (non-null only while inside that body, so a YIELD there disarms before EXIT and re-arms on resume).
    public MemberExpr? HOnerr, HBp, HSp;
    public string? TryCatchLabel;
    public HandlerRestoreStmt Restore() => new(this.Pos, this.HOnerr!, this.HBp!, this.HSp!);
  }

  /// <summary>
  /// Flattens a generator body into the MoveNext state machine: each YIELD becomes set-current /
  /// record-state / return-true / EXIT / resume-label (and a dispatch arm); FOR / WHILE-DO / IF that
  /// contain a YIELD are lowered to label+GOTO form so the resume can re-enter mid-block; constructs
  /// with no YIELD (and any other statement) pass through unchanged (their variable references are
  /// captured to enumerator fields when the body is bound).
  /// </summary>
  private void LowerGenBody(IReadOnlyList<Statement> body, GenLower g) {
    var pos = g.Pos;
    Expression Int(long v) => new IntegerLiteralExpr(pos, v, TypeSuffix.None);
    string NewLabel() => GeneratedPrefix + "L" + ++g.Label;

    foreach (var s in body)
      switch (s) {
        case YieldStmt y: {
          var k = ++g.State;
          var resume = GeneratedPrefix + "r" + k;
          g.Dispatch.Add(new CaseArm(pos, [new CaseSelector(pos, Int(k), null, null)], [new GotoStmt(pos, resume)]));
          g.Body.Add(new AssignStmt(pos, ThisField(pos, g.CurrentField), y.Value));
          g.Body.Add(new AssignStmt(pos, ThisField(pos, GeneratedPrefix + "state"), Int(k)));
          if (g.TryCatchLabel is not null)
            g.Body.Add(g.Restore());                          // disarm our dispatcher before the consumer runs
          g.Body.Add(new AssignStmt(pos, new NameExpr(pos, "MoveNext", TypeSuffix.None), Int(-1)));
          g.Body.Add(new ExitStmt(pos, ExitKind.Function));
          g.Body.Add(new LabelStmt(pos, resume));
          if (g.TryCatchLabel is { } rearm)
            g.Body.Add(new HandlerArmStmt(pos, rearm));        // re-arm for this fresh MoveNext frame
          break;
        }

        case ForStmt f when ContainsYield([f]): {
          var ascending = f.Step switch { null => true, IntegerLiteralExpr { Value: var v } => v >= 0, _ => (bool?)null };
          if (ascending is null) {
            this.Error(pos, "a generator FOR loop with a non-constant STEP and a YIELD is not yet supported");
            ascending = true;
          }
          var top = NewLabel();
          var end = NewLabel();
          g.Body.Add(new AssignStmt(pos, f.Variable, f.From));
          g.Body.Add(new LabelStmt(pos, top));
          g.Body.Add(new IfStmt(pos, new BinaryExpr(pos, ascending.Value ? BinaryOp.Greater : BinaryOp.Less, f.Variable, f.To), [new GotoStmt(pos, end)], [], null));
          this.LowerGenBody(f.Body, g);
          g.Body.Add(new AssignStmt(pos, f.Variable, new BinaryExpr(pos, BinaryOp.Add, f.Variable, f.Step ?? Int(1))));
          g.Body.Add(new GotoStmt(pos, top));
          g.Body.Add(new LabelStmt(pos, end));
          break;
        }

        case DoLoopStmt d when ContainsYield([d]): {
          var top = NewLabel();
          var end = NewLabel();
          g.Body.Add(new LabelStmt(pos, top));
          if (d.PreTest != LoopTestKind.None)
            g.Body.Add(new IfStmt(pos, LoopExitCondition(d.PreTest, d.PreCondition!), [new GotoStmt(pos, end)], [], null));
          this.LowerGenBody(d.Body, g);
          if (d.PostTest != LoopTestKind.None)
            g.Body.Add(new IfStmt(pos, LoopExitCondition(d.PostTest, d.PostCondition!), [new GotoStmt(pos, end)], [], null));
          g.Body.Add(new GotoStmt(pos, top));
          g.Body.Add(new LabelStmt(pos, end));
          break;
        }

        case IfStmt i when ContainsYield([i]): {
          var end = NewLabel();
          var arms = new List<(Expression Condition, IReadOnlyList<Statement> Body)> { (i.Condition, i.Then) };
          arms.AddRange(i.ElseIfs);
          var labels = arms.Select(_ => NewLabel()).ToList();
          for (var k = 0; k < arms.Count; ++k)
            g.Body.Add(new IfStmt(pos, arms[k].Condition, [new GotoStmt(pos, labels[k])], [], null));
          if (i.Else != null)
            this.LowerGenBody(i.Else, g);
          g.Body.Add(new GotoStmt(pos, end));
          for (var k = 0; k < arms.Count; ++k) {
            g.Body.Add(new LabelStmt(pos, labels[k]));
            this.LowerGenBody(arms[k].Body, g);
            g.Body.Add(new GotoStmt(pos, end));
          }
          g.Body.Add(new LabelStmt(pos, end));
          break;
        }

        case SelectStmt sel when ContainsYield([sel]): {
          // SELECT CASE with a YIELD: fan out to per-arm labels (first match wins, CASE ELSE last),
          // like IF. The subject is compared once per arm, so it must be side-effect-free to repeat;
          // a plain variable / field / literal is idempotent, anything else is rejected.
          if (sel.Subject is not (NameExpr or MemberExpr or IntegerLiteralExpr or FloatLiteralExpr or StringLiteralExpr)) {
            this.Error(pos, "a YIELD inside SELECT CASE needs a simple subject (a variable, field, or literal)");
            g.Body.Add(s);
            break;
          }
          var valueArms = sel.Arms.Where(a => a.Selectors.Count > 0).ToList();
          var elseArm = sel.Arms.FirstOrDefault(a => a.Selectors.Count == 0);
          var end = NewLabel();
          var labels = valueArms.Select(_ => NewLabel()).ToList();
          for (var k = 0; k < valueArms.Count; ++k)
            g.Body.Add(new IfStmt(pos, CaseArmCondition(sel.Subject, valueArms[k].Selectors), [new GotoStmt(pos, labels[k])], [], null));
          if (elseArm != null)
            this.LowerGenBody(elseArm.Body, g);
          g.Body.Add(new GotoStmt(pos, end));
          for (var k = 0; k < valueArms.Count; ++k) {
            g.Body.Add(new LabelStmt(pos, labels[k]));
            this.LowerGenBody(valueArms[k].Body, g);
            g.Body.Add(new GotoStmt(pos, end));
          }
          g.Body.Add(new LabelStmt(pos, end));
          break;
        }

        case ForEachStmt fe when this._foreachEnumField.TryGetValue(fe, out var feInfo): {
          // iterate an inner generator while yielding: drive its persistent enumerator field
          // (THIS.$feN) by hand so the outer resume re-enters the inner iteration mid-stream
          var enumField = ThisField(pos, feInfo.Field);
          var call = (CallOrIndexExpr)fe.Collection;
          // construct: reset the inner resume state and seed its captured parameters from the args
          g.Body.Add(new AssignStmt(pos, new MemberExpr(pos, enumField, GeneratedPrefix + "state", TypeSuffix.None), Int(0)));
          if (this._generatorParams.TryGetValue(feInfo.GenName, out var pnames))
            for (var k = 0; k < pnames.Count && k < call.Arguments.Count; ++k)
              g.Body.Add(new AssignStmt(pos, new MemberExpr(pos, enumField, GeneratedPrefix + pnames[k], TypeSuffix.None), call.Arguments[k]));
          var top = NewLabel();
          var end = NewLabel();
          g.Body.Add(new LabelStmt(pos, top));
          g.Body.Add(new IfStmt(pos, new UnaryExpr(pos, UnaryOp.Not, new MemberExpr(pos, enumField, "MoveNext", TypeSuffix.None)), [new GotoStmt(pos, end)], [], null));
          g.Body.Add(new AssignStmt(pos, fe.Variable, new MemberExpr(pos, enumField, "Current", TypeSuffix.None)));
          this.LowerGenBody(fe.Body, g);
          g.Body.Add(new GotoStmt(pos, top));
          g.Body.Add(new LabelStmt(pos, end));
          break;
        }

        case TryStmt tr when ContainsYield([tr]): {
          // a YIELD inside a TRY: flatten the protected body but keep the ON ERROR handler correct
          // across the suspension. Arm our dispatcher on entry and on each resume; disarm (restore the
          // caller's handler) before every YIELD's EXIT and on normal/caught completion.
          if (g.TryCatchLabel is not null) {
            this.Error(pos, "a YIELD inside a TRY that is nested in another TRY is not yet supported in a generator");
            g.Body.Add(s);
            break;
          }
          var catchL = NewLabel();
          var afterL = NewLabel();
          g.Body.Add(new HandlerArmStmt(pos, catchL));
          g.TryCatchLabel = catchL;
          this.LowerGenBody(tr.Body, g);                 // protected body (its YIELDs disarm / re-arm)
          g.TryCatchLabel = null;
          // normal completion: disarm, run FINALLY, skip the catch
          g.Body.Add(g.Restore());
          if (tr.Finally != null)
            this.LowerGenBody(tr.Finally, g);
          g.Body.Add(new GotoStmt(pos, afterL));
          // fault path: rt_raise restored SP/BP to the armed frame and jumped here
          g.Body.Add(new LabelStmt(pos, catchL));
          g.Body.Add(g.Restore());                       // disarm so a fault in CATCH reaches the outer handler
          if (tr.Catch != null)
            this.LowerGenBody(tr.Catch, g);
          if (tr.Finally != null)
            this.LowerGenBody(tr.Finally, g);
          if (tr.Catch == null)
            g.Body.Add(new HandlerReraiseStmt(pos));      // TRY ... FINALLY (no CATCH): re-propagate ERR
          g.Body.Add(new LabelStmt(pos, afterL));
          break;
        }

        default:
          g.Body.Add(s);
          break;
      }
  }

  /// <summary>The condition under which a loop EXITs: WHILE c exits on NOT c, UNTIL c exits on c.</summary>
  private static Expression LoopExitCondition(LoopTestKind kind, Expression condition)
    => kind == LoopTestKind.While ? new UnaryExpr(condition.Position, UnaryOp.Not, condition) : condition;

  /// <summary>The boolean condition for a CASE arm: the OR of its selectors against the subject (value -> equal, x TO y -> in range, IS &lt;rel&gt; v -> the relation).</summary>
  private static Expression CaseArmCondition(Expression subject, IReadOnlyList<CaseSelector> selectors) {
    Expression? result = null;
    foreach (var sel in selectors) {
      var pos = sel.Position;
      Expression one = sel switch {
        { IsComparison: { } cmp } => new BinaryExpr(pos, cmp switch {
          CaseComparison.Equal => BinaryOp.Equal,
          CaseComparison.NotEqual => BinaryOp.NotEqual,
          CaseComparison.Less => BinaryOp.Less,
          CaseComparison.LessEqual => BinaryOp.LessEqual,
          CaseComparison.Greater => BinaryOp.Greater,
          _ => BinaryOp.GreaterEqual,
        }, subject, sel.Value!),
        { RangeUpper: { } upper } => new BinaryExpr(pos, BinaryOp.And,
          new BinaryExpr(pos, BinaryOp.GreaterEqual, subject, sel.Value!),
          new BinaryExpr(pos, BinaryOp.LessEqual, subject, upper)),
        _ => new BinaryExpr(pos, BinaryOp.Equal, subject, sel.Value!),
      };
      result = result == null ? one : new BinaryExpr(pos, BinaryOp.Or, result, one);
    }
    return result ?? new IntegerLiteralExpr(subject.Position, 0, TypeSuffix.None);
  }

  /// <summary>
  /// pb36 coroutine: lowers a generator FUNCTION (a body containing YIELD) to a first-class
  /// enumerator TYPE named after the generator. The enumerator holds the resume state ($state)
  /// and the captured parameters ($param), exposes a Current auto-property (its $Current backing
  /// holds the last yielded value), and a MoveNext / Reset built on the TYPE-member machinery.
  /// Calling the generator (e = Gen(args)) constructs an instance (see the AssignStmt path); the
  /// MoveNext body is the YIELD state machine (currently a stub - empty sequence - pending the
  /// state-machine transform).
  /// </summary>
  private void SynthesizeGenerator(FunctionDecl f) {
    this._generatorNames.Add(f.Name);
    var pos = f.Position;
    var intType = new TypeName(pos, BuiltinType.Integer);
    var elementType = f.ReturnType ?? intType;

    var paramNames = new HashSet<string>(f.Parameters.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
    var captures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var seededParams = new List<string>();
    var fields = new List<TypeField> { new(pos, GeneratedPrefix + "state", intType, null) };
    foreach (var p in f.Parameters) {
      // an explicit AS type, else the scalar/string type implied by the name suffix / DEFtype
      var paramType = p.Type ?? LocalFieldTypeName(pos, this.TypeFromSuffixOrDefault(p.Name, p.Suffix));
      if (paramType is null) {
        this.Error(pos, $"generator parameter '{p.Name}' has an unsupported type (only scalars and strings persist across YIELDs)");
        continue;
      }
      fields.Add(new(pos, GeneratedPrefix + p.Name, paramType, null));
      captures[p.Name] = GeneratedPrefix + p.Name;
      seededParams.Add(p.Name);
    }

    // locals assigned in the body persist across resumes as enumerator fields (type from suffix/DEFtype)
    var locals = new Dictionary<string, TypeSuffix>(StringComparer.OrdinalIgnoreCase);
    CollectAssignedNames(f.Body, locals);
    foreach (var (local, suffix) in locals) {
      if (paramNames.Contains(local) || captures.ContainsKey(local))
        continue;
      if (LocalFieldTypeName(pos, this.TypeFromSuffixOrDefault(local, suffix)) is not { } localType) {
        this.Error(pos, $"generator local '{local}' has an unsupported type (only scalars and strings persist across YIELDs)");
        continue;
      }
      fields.Add(new(pos, GeneratedPrefix + local, localType, null));
      captures[local] = GeneratedPrefix + local;
    }

    // a FOR EACH over another generator, when the body yields (so the outer resume must re-enter the
    // inner iteration), needs the inner enumerator to persist across the outer YIELDs - give it a
    // UDT-typed field in THIS, recorded by node identity so the MoveNext lowering reuses it
    var feIndex = 0;
    foreach (var fe in YieldingForEachOverGenerator(f.Body)) {
      var innerName = ((CallOrIndexExpr)fe.Collection).Name;
      var field = GeneratedPrefix + "fe" + ++feIndex;
      fields.Add(new(pos, field, new TypeName(pos, BuiltinType.None, UserTypeName: innerName), null));
      this._foreachEnumField[fe] = (field, innerName);
    }

    // a YIELD inside a TRY needs three WORD fields to hold the caller's ON ERROR handler triple
    // across suspensions (the stack-based save the normal TRY uses can't survive a YIELD's EXIT)
    if (ContainsYieldingTry(f.Body))
      foreach (var h in new[] { "gonerr", "gbp", "gsp" })
        fields.Add(new(pos, GeneratedPrefix + h, intType, null));

    var members = new List<TypeMember> {
      new(pos, TypeMemberKind.PropertyGet, "Current", f.Suffix, [], elementType, [], IsAuto: true),
      new(pos, TypeMemberKind.Function, "MoveNext", TypeSuffix.None, [], intType,
        BuildMoveNextBody(pos, f.Body, GeneratedPrefix + "Current")),
      new(pos, TypeMemberKind.Sub, "Reset", TypeSuffix.None, [], null,
        [new AssignStmt(pos, ThisField(pos, GeneratedPrefix + "state"), new IntegerLiteralExpr(pos, 0, TypeSuffix.None))]),
    };

    var decl = new TypeDecl(pos, f.Name, fields) { Members = members };
    var backing = PropertyBackingFields(decl);
    var allFields = backing.Count == 0
      ? (IReadOnlyList<TypeField>)fields
      : [.. fields, .. backing.Select(b => new TypeField(pos, b.Field, b.Type, null))];
    this.DefineUdt(f.Name, allFields, isUnion: false, pos);
    this.DefineTypeMembers(decl, backing.ToDictionary(b => b.Prop, b => b, StringComparer.OrdinalIgnoreCase));

    // MoveNext reads every captured parameter/local as THIS.$name so state persists across resumes;
    // a construction call seeds the parameter fields from its arguments (parameters only, in order)
    this._generatorParams[f.Name] = seededParams;
    if (this._model.Procedures.TryGetValue(f.Name + ".MoveNext", out var moveNext))
      moveNext.CoroutineCaptures = captures;
  }

  /// <summary>
  /// Prefix for every compiler-synthesized name that shares the user's variable / field
  /// namespace (property backing fields, coroutine state-machine fields, ...). A user
  /// identifier must START with an ASCII letter (see <c>Lexer.IsIdentifierStart</c>), so a
  /// leading <c>$</c> can never be typed in source - these names cannot clash with user code.
  /// Procedure names use a different guard (an embedded '.', also non-typeable).
  /// </summary>
  internal const string GeneratedPrefix = "$";

  /// <summary>The hidden backing field synthesized for each property: name (<c>$Prop</c>) and type (GET result / SET value type).</summary>
  private static List<(string Prop, string Field, TypeName Type)> PropertyBackingFields(TypeDecl t) {
    var result = new List<(string, string, TypeName)>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var m in t.Members) {
      if (m.Kind is not (TypeMemberKind.PropertyGet or TypeMemberKind.PropertySet) || !seen.Add(m.Name))
        continue;
      if (PropertyType(t, m.Name) is { } type)
        result.Add((m.Name, GeneratedPrefix + m.Name, type));
    }
    return result;
  }

  /// <summary>The declared type of a property: a GET result type, a SET value type (AS), or a SET first parameter type.</summary>
  private static TypeName? PropertyType(TypeDecl t, string prop) {
    foreach (var m in t.Members.Where(m => m.Name.Equals(prop, StringComparison.OrdinalIgnoreCase))) {
      if (m.ReturnType is { } rt)
        return rt;
      if (m.Kind == TypeMemberKind.PropertySet && m.Parameters is [{ Type: { } pt }, ..])
        return pt;
    }
    return null;
  }

  /// <summary>The lifted-procedure name of a TYPE member: <c>Type.M</c>, <c>Type.get_P</c>, <c>Type.set_P</c>.</summary>
  private static string MemberProcName(string typeName, TypeMember m) => m.Kind switch {
    TypeMemberKind.PropertyGet => $"{typeName}.get_{m.Name}",
    TypeMemberKind.PropertySet => $"{typeName}.set_{m.Name}",
    _ => $"{typeName}.{m.Name}",
  };

  /// <summary>Whether a lifted member procedure of the given mangled name exists.</summary>
  private bool HasMemberProc(string name)
    => this._model.Procedures.ContainsKey(name) || this._model.Overloads.ContainsKey(name);

  /// <summary>The lifted method name of an overloadable binary operator (op_Add, op_Eq, ...), or null when the operator cannot be overloaded.</summary>
  private static string? OperatorMethodName(BinaryOp op) => op switch {
    BinaryOp.Add => "op_Add", BinaryOp.Subtract => "op_Sub", BinaryOp.Multiply => "op_Mul",
    BinaryOp.Divide => "op_Div", BinaryOp.IntegerDivide => "op_IDiv", BinaryOp.Power => "op_Pow",
    BinaryOp.Modulo => "op_Mod", BinaryOp.Equal => "op_Eq", BinaryOp.NotEqual => "op_Ne",
    BinaryOp.Less => "op_Lt", BinaryOp.Greater => "op_Gt", BinaryOp.LessEqual => "op_Le", BinaryOp.GreaterEqual => "op_Ge",
    BinaryOp.And => "op_And", BinaryOp.Or => "op_Or", BinaryOp.Xor => "op_Xor",
    _ => null,
  };

  /// <summary>The lifted operator-method name for a UDT left operand of <paramref name="op"/> when such an operator is defined; null otherwise.</summary>
  private string? UdtOperatorProc(PbType left, BinaryOp op)
    => left is UdtType lu && OperatorMethodName(op) is { } n && this.HasMemberProc($"{lu.Name}.{n}") ? $"{lu.Name}.{n}" : null;

  /// <summary>
  /// pb36 method call: <c>o.M(args)</c> when <c>o</c> is a UDT whose member <c>M</c> is a
  /// lifted method (not an array field). Desugars the node to <c>Type.M(o, args)</c> - the
  /// receiver is passed as the BYREF THIS first argument (a UDT lvalue passes by address
  /// naturally) - binds that call, and returns its type. Null when it is not a method call.
  /// </summary>
  private PbType? TryBindMemberCall(Expression node, MemberExpr member, IReadOnlyList<Expression> args, Scope scope) {
    if (this.BindExpression(member.Target, scope) is not UdtType udt)
      return null;
    if (udt.FindField(member.Member) != null)
      return null;                                  // an array-field index, not a method
    var procName = $"{udt.Name}.{member.Member}";
    if (!this.HasMemberProc(procName))
      return null;

    var callArgs = new List<Expression>(args.Count + 1) { member.Target };
    callArgs.AddRange(args);
    var call = new CallOrIndexExpr(node.Position, procName, TypeSuffix.None, callArgs);
    var type = this.BindExpression(call, scope);
    this._model.Desugared[node] = call;
    return type;
  }

  private int _foreachCounter;
  private int _destructureCounter;

  /// <summary>
  /// pb36 tuple destructuring <c>a, b = expr</c>: bind the source tuple (a tuple-returning call writes a
  /// hidden temp via struct return; a tuple value is read in place) and assign each element to its target.
  /// </summary>
  private void BindDestructure(DestructureStmt ds, Scope scope) {
    var pos = ds.Position;

    // pb36 parallel assignment: a, b = (b, a). Evaluate every right-hand value into a fresh temp first,
    // then assign each target - so a swap (and any aliasing case) is correct (simultaneous semantics).
    if (ds.Value is TupleExpr lit) {
      if (lit.Elements.Count != ds.Targets.Count) {
        this.Error(pos, $"parallel assignment has {ds.Targets.Count} targets but {lit.Elements.Count} values");
        return;
      }
      var parallel = new List<Statement>();
      var temps = new List<Expression>();
      foreach (var element in lit.Elements) {
        var temp = this.DeclareHidden(scope, pos, "destr" + ++this._destructureCounter, this.BindExpression(element, scope));
        parallel.Add(new AssignStmt(pos, temp, element));
        temps.Add(temp);
      }
      for (var i = 0; i < ds.Targets.Count; ++i)
        parallel.Add(new AssignStmt(pos, ds.Targets[i], temps[i]));
      var litGroup = new GroupStmt(pos, parallel);
      this.BindStatement(litGroup, scope);
      this._model.DesugaredStatements[ds] = litGroup;
      return;
    }

    var pre = new List<Statement>();
    Expression source;
    UdtType udt;

    if (ds.Value is CallOrIndexExpr vc
        && (this._genericProcs.ContainsKey(vc.Name)
              ? this.ResolveGenericCall(vc.Name, vc.Arguments, pos, scope, vc.TypeArguments)
              : this._model.Overloads.TryGetValue(vc.Name, out var os) ? os.FirstOrDefault(p => p.IsFunction) : null)
            is { HasSretParam: true, ReturnType: UdtType callUdt }) {
      // a tuple-returning function writes its result straight into a hidden temp (no copy)
      udt = callUdt;
      source = this.DeclareHidden(scope, pos, "destr" + ++this._destructureCounter, udt);
      pre.Add(new CallStmt(pos, vc.Name, [.. vc.Arguments, source], UsedCallKeyword: false, vc.TypeArguments));
    } else if (this.BindExpression(ds.Value, scope) is UdtType valueUdt) {
      udt = valueUdt;
      source = ds.Value;   // a tuple variable / field - read its elements in place
    } else {
      this.Error(pos, "the right-hand side of a destructuring assignment must be a tuple");
      return;
    }

    if (udt.Fields.Count < ds.Targets.Count)
      this.Error(pos, $"destructuring expects {ds.Targets.Count} elements but the tuple has {udt.Fields.Count}");
    var stmts = new List<Statement>(pre);
    for (var i = 0; i < ds.Targets.Count; ++i)
      stmts.Add(new AssignStmt(pos, ds.Targets[i], new MemberExpr(pos, source, "Item" + (i + 1), TypeSuffix.None)));
    var group = new GroupStmt(pos, stmts);
    this.BindStatement(group, scope);
    this._model.DesugaredStatements[ds] = group;
  }

  /// <summary>
  /// Declares a hidden ($-prefixed, untypeable) compiler variable directly in the current scope
  /// (local in a procedure, otherwise a module global) and returns a NameExpr referring to it.
  /// Used for FOR EACH temporaries; registering it directly avoids the create-on-use path, which
  /// derives a type from the leading letter a $-name does not have.
  /// </summary>
  private NameExpr DeclareHidden(Scope scope, SourcePosition pos, string baseName, PbType type) {
    var name = GeneratedPrefix + baseName;
    var key = VariableKey(name, TypeSuffix.None);
    if (scope.Proc is { } proc)
      proc.Variables[key] = new VariableSymbol(name, type, VariableStorage.Local);
    else
      this._model.ModuleVariables[key] = new VariableSymbol(name, type, VariableStorage.Global);
    return new NameExpr(pos, name, TypeSuffix.None);
  }

  /// <summary>
  /// pb36 FOR EACH: lowers per the collection's kind. A generator call becomes the iterator
  /// loop (DIM $e AS Gen : $e = Gen() : WHILE $e.MoveNext : v = $e.Current : body : WEND, wrapped
  /// in an always-true IF so it is a single desugared statement); an array/array-call becomes the
  /// counted loop over LBOUND..UBOUND.
  /// </summary>
  private void BindForEach(ForEachStmt fe, Scope scope) {
    var pos = fe.Position;
    Statement lowered;
    if (fe.Collection is CallOrIndexExpr gen && this._generatorNames.Contains(gen.Name)
        && this._model.Udts.TryGetValue(gen.Name, out var enumType)) {
      // a hidden enumerator variable holds the iterator; register it directly (a synthesized DIM
      // would miss the pre-pass, so the construct could not see it as a UDT-typed variable)
      var enumVar = this.DeclareHidden(scope, pos, "foreach" + ++this._foreachCounter, enumType);
      var construct = new AssignStmt(pos, enumVar, gen);
      var loopBody = new List<Statement> { new AssignStmt(pos, fe.Variable, new MemberExpr(pos, enumVar, "Current", TypeSuffix.None)) };
      loopBody.AddRange(fe.Body);
      var loop = new DoLoopStmt(pos, LoopTestKind.While, new MemberExpr(pos, enumVar, "MoveNext", TypeSuffix.None), LoopTestKind.None, null, loopBody);
      lowered = new GroupStmt(pos, [construct, loop]);
    } else if (fe.Collection is CallOrIndexExpr { Arguments: [RangeArgExpr feRange] } feSrc
        && this.LookupArrayVariable(feSrc.Name, feSrc.Suffix, scope) is { Type: ArrayType { Rank: 1 } }) {
      // pb36 slice iteration: FOR EACH v IN a(lo TO hi) - a counted loop over the slice
      var n = ++this._sliceCounter;
      var srcRef = new NameExpr(pos, feSrc.Name, feSrc.Suffix);
      Expression Bound(Expression? bound, bool isLower) {
        var ubound = new CallOrIndexExpr(pos, "UBOUND", TypeSuffix.None, [srcRef]);
        return bound switch {
          null => isLower ? new CallOrIndexExpr(pos, "LBOUND", TypeSuffix.None, [srcRef]) : ubound,
          FromEndExpr fe2 => new BinaryExpr(pos, BinaryOp.Subtract, ubound,
            new BinaryExpr(pos, BinaryOp.Subtract, fe2.Index, new IntegerLiteralExpr(pos, 1, TypeSuffix.None))),
          _ => bound,
        };
      }
      var index = this.DeclareHidden(scope, pos, "foreach" + ++this._foreachCounter, PbType.Long);
      var body = new List<Statement> { new AssignStmt(pos, fe.Variable, new CallOrIndexExpr(pos, feSrc.Name, feSrc.Suffix, [index])) };
      body.AddRange(fe.Body);
      lowered = new ForStmt(pos, index, Bound(feRange.Lo, isLower: true), Bound(feRange.Hi, isLower: false), null, body);
    } else {
      var (name, suffix) = fe.Collection switch {
        NameExpr n => (n.Name, n.Suffix),
        CallOrIndexExpr { Arguments.Count: 0 } c => (c.Name, c.Suffix),
        _ => ((string?)null, TypeSuffix.None),
      };
      if (name is null) {
        this.Error(pos, "FOR EACH expects an array, a generator, or a '[lo..hi]' range");
        return;
      }
      var index = this.DeclareHidden(scope, pos, "foreach" + ++this._foreachCounter, PbType.Long);
      var arrayRef = new NameExpr(pos, name, suffix);
      var loopBody = new List<Statement> { new AssignStmt(pos, fe.Variable, new CallOrIndexExpr(pos, name, suffix, [index])) };
      loopBody.AddRange(fe.Body);
      lowered = new ForStmt(pos, index,
        new CallOrIndexExpr(pos, "LBOUND", TypeSuffix.None, [arrayRef]),
        new CallOrIndexExpr(pos, "UBOUND", TypeSuffix.None, [arrayRef]),
        null, loopBody);
    }
    this.BindStatement(lowered, scope);
    this._model.DesugaredStatements[fe] = lowered;
  }

  /// <summary>pb36 statement-form method call <c>o.M(args)</c>: desugars to <c>Type.M(o, args)</c>.</summary>
  private void BindMemberCallStatement(MemberCallStmt mc, Scope scope) {
    if (this.BindExpression(mc.Receiver, scope) is not UdtType udt) {
      this.Error(mc.Position, "member call on a non-TYPE value");
      return;
    }
    var procName = $"{udt.Name}.{mc.Member}";
    if (!this.HasMemberProc(procName)) {
      this.Error(mc.Position, $"TYPE {udt.Name} has no method {mc.Member}");
      return;
    }
    var args = new List<Expression>(mc.Arguments.Count + 1) { mc.Receiver };
    args.AddRange(mc.Arguments);
    var call = new CallStmt(mc.Position, procName, args, UsedCallKeyword: false);
    this.BindCallStatement(call, scope);
    this._model.DesugaredStatements[mc] = call;
  }

  /// <summary>
  /// pb36 property set <c>o.P = x</c>: when <c>P</c> is not a field but a PROPERTY SET exists,
  /// desugars the assignment to <c>Type.set_P(o, x)</c>. Returns false for a plain field store.
  /// </summary>
  private bool TryBindPropertySet(AssignStmt a, MemberExpr m, Scope scope) {
    if (this.BindExpression(m.Target, scope) is not UdtType udt)
      return false;
    if (udt.FindField(m.Member) != null)
      return false;
    var setter = $"{udt.Name}.set_{m.Member}";
    if (!this.HasMemberProc(setter))
      return false;
    // READONLY: a property may be set only while constructing (the setter mutates a write-once field)
    if (this._readonlyTypes.Contains(udt.Name) && !IsInConstructorOf(udt.Name, scope))
      this.Error(m.Position, $"property '{m.Member}' of READONLY TYPE {udt.Name} can be set only in its constructor");
    var call = new CallStmt(a.Position, setter, [m.Target, a.Value], UsedCallKeyword: false);
    this.BindCallStatement(call, scope);
    this._model.DesugaredStatements[a] = call;
    return true;
  }

  /// <summary>True when the current scope is the constructor of <paramref name="udtName"/> (its lifted <c>Type.Type</c> proc).</summary>
  private static bool IsInConstructorOf(string udtName, Scope scope)
    => scope.Proc?.Name.Equals(udtName + "." + udtName, StringComparison.OrdinalIgnoreCase) == true;

  /// <summary>
  /// pb36 READONLY enforcement (compile-time): a write to a field of a READONLY TYPE is allowed only
  /// inside that type's constructor, or inside one of its property setters (which are themselves
  /// callable only during construction - see <see cref="TryBindPropertySet"/>). A write anywhere else
  /// is rejected.
  /// </summary>
  private void CheckReadonlyFieldWrite(MemberExpr m, Scope scope) {
    if (this.BindExpression(m.Target, scope) is not UdtType udt || !this._readonlyTypes.Contains(udt.Name))
      return;
    if (udt.FindField(m.Member) == null)
      return;                                    // not a real field (e.g. a method name) - leave to normal binding
    if (IsInConstructorOf(udt.Name, scope) || scope.Proc?.Name.StartsWith(udt.Name + ".set_", StringComparison.OrdinalIgnoreCase) == true)
      return;                                    // the constructor, or a property setter (gated to ctor scope at its call sites)
    var shown = m.Member.StartsWith(GeneratedPrefix, StringComparison.Ordinal) ? m.Member[GeneratedPrefix.Length..] : m.Member;
    this.Error(m.Position, $"field '{shown}' of READONLY TYPE {udt.Name} can be set only in its constructor");
  }

  /// <summary>True when a YIELD appears inside a nested block (loop / IF / SELECT) rather than at the top level of the body.</summary>
  private static bool ContainsNestedYield(IReadOnlyList<Statement> body) {
    foreach (var s in body)
      if (s is not YieldStmt and not (SubDecl or FunctionDecl or DefFnDecl))
        foreach (var block in ChildBlocks(s))
          if (ContainsYield(block))
            return true;
    return false;
  }

  /// <summary>Collects the scalar variables assigned anywhere in a body (assignment / INCR-DECR target, FOR counter) with their suffixes, recursing nested blocks but not nested procedures.</summary>
  private static void CollectAssignedNames(IReadOnlyList<Statement> body, Dictionary<string, TypeSuffix> names) {
    foreach (var s in body) {
      switch (s) {
        case AssignStmt { Target: NameExpr a }: names.TryAdd(a.Name, a.Suffix); break;
        case IncrDecrStmt { Target: NameExpr d }: names.TryAdd(d.Name, d.Suffix); break;
        case ForStmt { Variable: NameExpr c }: names.TryAdd(c.Name, c.Suffix); break;
        case ForEachStmt { Variable: NameExpr v }: names.TryAdd(v.Name, v.Suffix); break;
      }
      if (s is not (SubDecl or FunctionDecl or DefFnDecl))
        foreach (var block in ChildBlocks(s))
          CollectAssignedNames(block, names);
    }
  }

  /// <summary>Every FOR EACH over a (yield-bearing) generator that itself contains a YIELD, anywhere in a generator body (recursing nested blocks but not nested procedures) - these need a persistent inner-enumerator field.</summary>
  private IEnumerable<ForEachStmt> YieldingForEachOverGenerator(IReadOnlyList<Statement> body) {
    foreach (var s in body) {
      if (s is ForEachStmt fe && fe.Collection is CallOrIndexExpr gen && this._generatorNames.Contains(gen.Name) && ContainsYield([fe]))
        yield return fe;
      if (s is SubDecl or FunctionDecl or DefFnDecl)
        continue;
      foreach (var block in ChildBlocks(s))
        foreach (var inner in this.YieldingForEachOverGenerator(block))
          yield return inner;
    }
  }

  /// <summary>The AST type name for a captured generator local of resolved scalar/string type (null = unsupported, e.g. a UDT/array local).</summary>
  private static TypeName? LocalFieldTypeName(SourcePosition pos, PbType type) => type switch {
    ScalarType s => new TypeName(pos, s.Kind switch {
      ScalarKind.Byte => BuiltinType.Byte,
      ScalarKind.Word => BuiltinType.Word,
      ScalarKind.Dword => BuiltinType.Dword,
      ScalarKind.Long => BuiltinType.Long,
      ScalarKind.Quad => BuiltinType.Quad,
      ScalarKind.Single => BuiltinType.Single,
      ScalarKind.Double => BuiltinType.Double,
      ScalarKind.Ext => BuiltinType.Ext,
      ScalarKind.SByte => BuiltinType.SByte,
      ScalarKind.QWord => BuiltinType.QWord,
      _ => BuiltinType.Integer,
    }),
    StringType => new TypeName(pos, BuiltinType.String),
    _ => null,
  };

  /// <summary>True when a procedure body contains a YIELD (making it a generator); nested SUB/FUNCTION bodies are their own scope and do not count.</summary>
  private static bool ContainsYield(IReadOnlyList<Statement> body) {
    foreach (var s in body) {
      if (s is YieldStmt)
        return true;
      if (s is SubDecl or FunctionDecl or DefFnDecl)
        continue;
      foreach (var block in ChildBlocks(s))
        if (ContainsYield(block))
          return true;
    }
    return false;
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

  /// <summary>
  /// pb36 event: synthesizes the backing storage for <c>EVENT name AS delegate</c> - a fixed-capacity
  /// DWORD handler array and an INTEGER count, declared as module variables. <c>+=</c>/<c>-=</c> and
  /// RAISE desugar against these (see <see cref="BindEventAddRemove"/> / <see cref="BindRaise"/>).
  /// </summary>
  private void SynthesizeEvent(EventDeclStmt ev) {
    if (!this.Require(LanguageFeature.Events, ev.Position))
      return;
    var pos = ev.Position;
    var arr = ev.Name + "__evh";
    var cnt = ev.Name + "__evn";
    var arrDim = new DimStmt(pos, StorageClass.Dim, false, [
      new VariableDecl(pos, arr, TypeSuffix.None, [(null, new IntegerLiteralExpr(pos, EventCapacity - 1, TypeSuffix.None))], new TypeName(pos, BuiltinType.Dword)),
    ]);
    var cntDim = new DimStmt(pos, StorageClass.Dim, false, [
      new VariableDecl(pos, cnt, TypeSuffix.None, null, new TypeName(pos, BuiltinType.Integer)),
    ]);
    this.DeclareModuleVariables(arrDim);
    this.DeclareModuleVariables(cntDim);
    this._model.MainBody.Add(arrDim);
    this._model.MainBody.Add(cntDim);
    // the delegate's parameters (types + BYVAL flags) drive how RAISE passes each argument
    var dparams = ev.Delegate.UserTypeName is { } dn && this._model.Procedures.TryGetValue(dn, out var dproc)
      ? dproc.Parameters.ToList()
      : new List<VariableSymbol>();
    this._events[ev.Name] = (arr, cnt, dparams);
  }

  /// <summary>
  /// pb36 first-class procedures: where a code pointer is expected (an event <c>+=</c>/<c>-=</c>
  /// handler, a delegate-typed assignment), a bare procedure name stands for its address - the
  /// explicit <c>CODEPTR32(name)</c> may be omitted. Returns the wrapped form when
  /// <paramref name="value"/> is a name that refers to a procedure (and not to a variable, which
  /// would already hold a pointer value); else null.
  /// </summary>
  private Expression? TryImplicitCodePtr(Expression value, Scope scope) {
    if (value is not NameExpr n)
      return null;
    if (this.ResolveVariable(n.Name, n.Suffix, scope, create: false) is not null)
      return null;   // a variable (DWORD/delegate) already IS a pointer value
    if (!this._model.Procedures.ContainsKey(n.Name))
      return null;
    return new CallOrIndexExpr(n.Position, "CODEPTR32", TypeSuffix.None, [value]);
  }

  /// <summary>The conversion intrinsic (CINT/CLNG/...) that forces a value to a scalar type, so a RAISE argument is pushed at the delegate parameter's width; null for non-scalar / string (passed as-is).</summary>
  private static string? ConversionIntrinsic(PbType t) => t switch {
    ScalarType { Kind: ScalarKind.Integer } => "CINT", ScalarType { Kind: ScalarKind.Long } => "CLNG",
    ScalarType { Kind: ScalarKind.Single } => "CSNG", ScalarType { Kind: ScalarKind.Double } => "CDBL",
    ScalarType { Kind: ScalarKind.Ext } => "CEXT", ScalarType { Kind: ScalarKind.Byte } => "CBYT",
    ScalarType { Kind: ScalarKind.Word } => "CWRD", ScalarType { Kind: ScalarKind.Dword } => "CDWD",
    ScalarType { Kind: ScalarKind.Quad } => "CQUD",
    _ => null,
  };

  /// <summary>Desugars <c>event += handler</c> / <c>event -= handler</c> into a handler-list append / compacting removal.</summary>
  private void BindEventAddRemove(AssignStmt a, string name, (string Array, string Count, IReadOnlyList<VariableSymbol> Params) ev, Scope scope) {
    // the parser lowered 'event += h' to 'event = event + h'; recover the handler and the direction
    if (a.Value is not BinaryExpr { Op: BinaryOp.Add or BinaryOp.Subtract } be) {
      this.Error(a.Position, $"event '{name}' supports only '+= handler' and '-= handler'");
      return;
    }
    var pos = a.Position;
    // first-class procedures: 'event += HandlerName' takes the handler's address implicitly
    var handler = this.TryImplicitCodePtr(be.Right, scope) ?? be.Right;
    Expression Cnt() => new NameExpr(pos, ev.Count, TypeSuffix.None);
    Expression Elem(Expression i) => new CallOrIndexExpr(pos, ev.Array, TypeSuffix.None, [i]);
    Expression Int(long v) => new IntegerLiteralExpr(pos, v, TypeSuffix.None);

    List<Statement> body;
    if (be.Op == BinaryOp.Add) {
      // handlers(count) = handler : count = count + 1
      body = [
        new AssignStmt(pos, Elem(Cnt()), handler),
        new AssignStmt(pos, Cnt(), new BinaryExpr(pos, BinaryOp.Add, Cnt(), Int(1))),
      ];
    } else {
      // the handler pointer is computed ONCE into a DWORD temp (self-declaring ??? suffix), not
      // re-evaluated per comparison iteration
      var n = ++this._eventTemp;
      var h = new NameExpr(pos, $"{ev.Array}__h{n}", TypeSuffix.Dword);
      // FOR i = 0 TO count-1 : IF handlers(i) = h THEN {shift tail down; count--; EXIT FOR} : NEXT
      var i = new NameExpr(pos, $"{ev.Array}__i{n}", TypeSuffix.Integer);
      var j = new NameExpr(pos, $"{ev.Array}__j{n}", TypeSuffix.Integer);
      var shift = new ForStmt(pos, j, i, new BinaryExpr(pos, BinaryOp.Subtract, Cnt(), Int(2)), null, [
        new AssignStmt(pos, Elem(j), Elem(new BinaryExpr(pos, BinaryOp.Add, j, Int(1)))),
      ]);
      var found = new List<Statement> {
        shift,
        new AssignStmt(pos, Cnt(), new BinaryExpr(pos, BinaryOp.Subtract, Cnt(), Int(1))),
        new ExitStmt(pos, ExitKind.For),
      };
      var loopBody = new List<Statement> {
        new IfStmt(pos, new BinaryExpr(pos, BinaryOp.Equal, Elem(i), h), found, [], null),
      };
      body = [
        new AssignStmt(pos, h, handler),
        new ForStmt(pos, i, Int(0), new BinaryExpr(pos, BinaryOp.Subtract, Cnt(), Int(1)), null, loopBody),
      ];
    }
    var group = new GroupStmt(pos, body);
    this.BindStatement(group, scope);
    this._model.DesugaredStatements[a] = group;
  }

  /// <summary>The self-declaring type suffix matching a scalar type, so a synthesized temp needs no DIM; null when the type has no suffix (pass unbuffered).</summary>
  private static TypeSuffix? SuffixOf(PbType t) => t switch {
    ScalarType { Kind: ScalarKind.Integer } => TypeSuffix.Integer, ScalarType { Kind: ScalarKind.Long } => TypeSuffix.Long,
    ScalarType { Kind: ScalarKind.Single } => TypeSuffix.Single, ScalarType { Kind: ScalarKind.Double } => TypeSuffix.Double,
    ScalarType { Kind: ScalarKind.Byte } => TypeSuffix.Byte, ScalarType { Kind: ScalarKind.Word } => TypeSuffix.Word,
    ScalarType { Kind: ScalarKind.Dword } => TypeSuffix.Dword, ScalarType { Kind: ScalarKind.Quad } => TypeSuffix.Quad,
    ScalarType { Kind: ScalarKind.Ext } => TypeSuffix.Ext, StringType => TypeSuffix.String,
    _ => null,
  };

  /// <summary>
  /// Builds and binds the raise-invocation group for an event: each argument is evaluated ONCE into a
  /// typed temp (coerced to the delegate parameter's type - a bare INTEGER 42 into a LONG parameter
  /// would otherwise be pushed at the wrong width), then a loop invokes every handler through its
  /// code pointer. Raising is the first-class call form: <c>name(args)</c> / <c>name args</c> /
  /// <c>CALL name(args)</c>.
  /// </summary>
  private Statement BuildRaiseGroup((string Array, string Count, IReadOnlyList<VariableSymbol> Params) ev, IReadOnlyList<Expression> arguments, SourcePosition pos, Scope scope) {
    var n = ++this._eventTemp;
    var body = new List<Statement>();
    // hoist each argument into a self-declaring suffixed temp - evaluated once, at the right width,
    // instead of re-evaluating (and re-coercing) per handler iteration
    var args = new List<Expression>();
    for (var idx = 0; idx < arguments.Count; ++idx) {
      var p = idx < ev.Params.Count ? ev.Params[idx] : null;
      if (p is { ByVal: true } && SuffixOf(p.Type) is { } suffix) {
        var temp = new NameExpr(pos, $"{ev.Array}__a{n}_{idx}", suffix);
        body.Add(new AssignStmt(pos, temp, arguments[idx]));
        args.Add(new ByValArgExpr(pos, temp));
      } else if (p is { ByVal: true }) {
        args.Add(new ByValArgExpr(pos, arguments[idx]));
      } else {
        args.Add(arguments[idx]);   // BYREF (or extra) argument: pass its address as-is
      }
    }
    // FOR i = 0 TO count-1 : CALL DWORD (handlers(i))(args) : NEXT
    var i = new NameExpr(pos, $"{ev.Array}__r{n}", TypeSuffix.Integer);
    body.Add(new ForStmt(pos, i, new IntegerLiteralExpr(pos, 0, TypeSuffix.None),
      new BinaryExpr(pos, BinaryOp.Subtract, new NameExpr(pos, ev.Count, TypeSuffix.None), new IntegerLiteralExpr(pos, 1, TypeSuffix.None)), null,
      [new CallPtrStmt(pos, new CallOrIndexExpr(pos, ev.Array, TypeSuffix.None, [i]), null, args)]));
    var group = new GroupStmt(pos, body);
    this.BindStatement(group, scope);
    return group;
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
    BuiltinType.Int128 => new WideIntType(16, Signed: true),
    BuiltinType.Int256 => new WideIntType(32, Signed: true),
    BuiltinType.Int512 => new WideIntType(64, Signed: true),
    BuiltinType.UInt128 => new WideIntType(16, Signed: false),
    BuiltinType.UInt256 => new WideIntType(32, Signed: false),
    BuiltinType.UInt512 => new WideIntType(64, Signed: false),
    BuiltinType.SByte => PbType.SByte,
    BuiltinType.QWord => PbType.QWord,
    _ => null,
  };

  private PbType? ResolveTypeName(TypeName t) {
    if (t.IsProcPtr) // PB 3.6 typed procedure pointer
      return new ProcPtrType(
        [.. (t.ProcParameterTypes ?? []).Select(p => this.ResolveTypeName(p) ?? PbType.Integer)],
        t.ProcReturnType != null ? this.ResolveTypeName(t.ProcReturnType) ?? PbType.Long : null);

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

    // pb36 nullable type T?: a UDT carrying the value plus a presence flag, synthesized on first use
    if (t.IsNullable)
      return this.ResolveNullableType(t);

    // pb36 tuple type (T1, T2): an anonymous UDT with fields Item1..ItemN, synthesized on first use
    if (t.IsTuple)
      return this.ResolveTupleType(t);

    if (t.IsUserDefined) {
      // pb36 generics: a generic use (Stack OF LONG) resolves to its monomorphized concrete TYPE (Stack@LONG)
      var name = t.IsGenericUse ? Monomorphizer.Mangle(t) : t.UserTypeName!;
      if (this._model.Udts.TryGetValue(name, out var udt))
        return udt;
      if (this._model.EnumTypes.TryGetValue(t.UserTypeName!, out var enumType)) // PB 3.6: an ENUM name aliases its integer type
        return enumType;
      if (this._typeAliases.TryGetValue(t.UserTypeName!, out var aliasTarget)) { // pb36 type alias: fully transparent at bind time
        if (!this._aliasResolutionStack.Add(t.UserTypeName!)) {
          this.Error(t.Position, $"type alias '{t.UserTypeName}' is circular");
          return PbType.Integer;
        }
        var aliased = this.ResolveTypeName(aliasTarget);
        this._aliasResolutionStack.Remove(t.UserTypeName!);
        if (aliased == null)
          this.Error(aliasTarget.Position, $"type alias '{t.UserTypeName}' names unknown type");
        return aliased ?? PbType.Integer;
      }
      if (this._model.Overloads.TryGetValue(t.UserTypeName!, out var overloads) && overloads.Count > 0) // PB 3.6: a DECLAREd SUB/FUNCTION name doubles as a named delegate type
        return this.NamedDelegateType(t, overloads);
      return null;
    }

    return this.AsMbfIfInterpreter(MapBuiltin(t.Builtin));
  }

  /// <summary>pb36 nullable type <c>T?</c>: synthesizes (once) a UDT with a <c>Value</c> field of T and an INTEGER <c>HasValue</c> presence flag, recorded in the model's nullable set, and returns it.</summary>
  private PbType ResolveNullableType(TypeName t) {
    var inner = t with { IsNullable = false };
    var valueType = this.ResolveTypeName(inner);
    if (valueType == null) {
      this.Error(t.Position, "unknown nullable value type");
      valueType = PbType.Integer;
    }
    var name = "$nul@" + TupleElementMangle(inner);
    if (!this._model.Udts.ContainsKey(name)) {
      var fields = new List<TypeField> {
        new(t.Position, "Value", inner, null),
        new(t.Position, "HasValue", new TypeName(t.Position, BuiltinType.Integer), null),
      };
      this.DefineUdt(name, fields, isUnion: false, t.Position);
    }
    this._model.NullableUnderlying[name] = valueType;
    return this._model.Udts[name];
  }

  /// <summary>True when <paramref name="type"/> is a synthesized nullable UDT (<c>T?</c>).</summary>
  private bool IsNullableType(PbType? type) => type is UdtType u && this._model.NullableUnderlying.ContainsKey(u.Name);

  /// <summary>
  /// Desugars a null-conditional access <c>target?.member</c> / <c>target?[i]</c> to
  /// <c>IF(target.HasValue, target.Value.member, fallback)</c> - the fallback is the <c>??</c> default
  /// when present, else a type-matched zero (0 / "").
  /// </summary>
  private Expression DesugarNullConditional(NullConditionalExpr nc, Expression? fallback, Scope scope) {
    var value = new MemberExpr(nc.Position, nc.Target, "Value", TypeSuffix.None);
    Expression access = nc.Member is { } m
      ? new MemberExpr(nc.Position, value, m, TypeSuffix.None)
      : new IndexExpr(nc.Position, value, [nc.Index!]);
    var hasValue = new MemberExpr(nc.Position, nc.Target, "HasValue", TypeSuffix.None);
    var elseExpr = fallback ?? (this.BindExpression(access, scope) is StringType or FlexType
      ? new StringLiteralExpr(nc.Position, "")
      : new IntegerLiteralExpr(nc.Position, 0, TypeSuffix.None));
    return new IfExpr(nc.Position, hasValue, access, elseExpr);
  }

  /// <summary>pb36 tuple type: synthesizes (once) an anonymous UDT with fields Item1..ItemN for <c>(T1, T2, ...)</c>, named with an untypeable mangle so identical tuples share the type, and returns it.</summary>
  private PbType ResolveTupleType(TypeName t) {
    var name = "$tup" + string.Concat(t.TupleElements!.Select(e => "@" + TupleElementMangle(e)));
    if (this._model.Udts.TryGetValue(name, out var existing))
      return existing;
    var fields = new List<TypeField>();
    for (var i = 0; i < t.TupleElements.Count; ++i)
      fields.Add(new TypeField(t.Position, "Item" + (i + 1), t.TupleElements[i], null));
    this.DefineUdt(name, fields, isUnion: false, t.Position);
    return this._model.Udts[name];
  }

  /// <summary>The <c>AS</c>-clause spelling of a bound scalar/string type (for synthesizing anonymous-type fields); null when the type has no simple spelling.</summary>
  private TypeName? PbTypeToTypeName(PbType t, SourcePosition pos) => t switch {
    ScalarType s => new TypeName(pos, s.Kind switch {
      ScalarKind.Byte => BuiltinType.Byte, ScalarKind.Word => BuiltinType.Word, ScalarKind.Dword => BuiltinType.Dword,
      ScalarKind.Integer => BuiltinType.Integer, ScalarKind.Long => BuiltinType.Long, ScalarKind.Quad => BuiltinType.Quad,
      ScalarKind.Single => BuiltinType.Single, ScalarKind.Double => BuiltinType.Double, ScalarKind.Ext => BuiltinType.Ext,
      ScalarKind.SByte => BuiltinType.SByte, _ => BuiltinType.QWord,
    }),
    StringType => new TypeName(pos, BuiltinType.String),
    UdtType u => new TypeName(pos, BuiltinType.None, u.Name),
    _ => null,
  };

  /// <summary>A stable mangle fragment for a tuple element type (so identical tuples map to the same synthesized UDT).</summary>
  private static string TupleElementMangle(TypeName e) =>
    e.IsTuple ? "tup" + string.Concat(e.TupleElements!.Select(x => "_" + TupleElementMangle(x)))
    : e.IsGenericUse ? Monomorphizer.Mangle(e)
    : e.IsPointer ? TupleElementMangle(e.PointerTarget!) + "Ptr"
    : e.IsUserDefined ? e.UserTypeName!
    : e.Builtin.ToString();

  /// <summary>
  /// BASICA / GW-BASIC store SINGLE in Microsoft Binary Format, so map the IEEE
  /// single scalar to <see cref="MbfType"/> in those dialects (QBasic and the rest
  /// keep IEEE). DOUBLE (MBF 8-byte, 55-bit mantissa) is a later increment - it
  /// stays IEEE for now.
  /// </summary>
  private PbType? AsMbfIfInterpreter(PbType? type)
    => this._dialect.IsGwBasica() && type is ScalarType { Kind: ScalarKind.Single }
      ? new MbfType(IsDouble: false)
      : type;

  /// <summary>
  /// PB 3.6: a DECLAREd (or defined) SUB/FUNCTION name used in a type position
  /// (<c>DIM f AS Comparator</c>, <c>... AS Comparator</c>) names a typed procedure
  /// pointer carrying that prototype's signature - a statically-checked delegate.
  /// Requires a single (non-overloaded) signature.
  /// </summary>
  private PbType NamedDelegateType(TypeName t, List<ProcedureSymbol> overloads) {
    if (!this.Require(LanguageFeature.NamedDelegates, t.Position))
      return PbType.Long;
    if (overloads.Count > 1) {
      this.Error(t.Position, $"{t.UserTypeName} is overloaded; a delegate type needs a single signature");
      return PbType.Long;
    }
    var sig = overloads[0];
    return new ProcPtrType([.. sig.Parameters.Select(p => p.Type)], sig.IsFunction ? sig.ReturnType : null);
  }

  private PbType TypeFromSuffixOrDefault(string name, TypeSuffix suffix) => this.AsMbfIfInterpreter(suffix switch {
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
  })!;

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
  private sealed class Scope(ProcedureSymbol? proc, ProcedureSymbol? captureFrom = null, bool capturesByEnv = false) {
    public ProcedureSymbol? Proc => proc;
    /// <summary>PB 3.6 nested procedure: the enclosing proc whose locals this one may capture (BYREF).</summary>
    public ProcedureSymbol? CaptureFrom => captureFrom;
    /// <summary>PB 3.6 lambda: captures become closure-environment entries (reached through the env pointer), not appended BYREF parameters (a lambda is called indirectly, so call sites cannot pass extra arguments).</summary>
    public bool CapturesByEnv => capturesByEnv;
    public string LabelKey => proc?.Name ?? "";
    public List<(string Target, SourcePosition Position)> PendingLabelRefs { get; } = [];
  }

  private void BindAllBodies() {
    this._folder = new(this._model.Equates, this._model.EnumMembers, this.FoldDesugared);
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

    // snapshot: binding a body may instantiate a generic procedure (appended to the proc list); those
    // are skipped here and bound by the drain below, so the list is never mutated mid-iteration
    foreach (var proc in this._model.ProcedureList.Where(p => !p.IsExternal && !p.IsNested && !this._genericInstanceProcs.Contains(p)).ToList())
      this.BindOneProcBody(proc);

    this.BindNestedProcedures();
    this.BindLambdaBodies();

    // pb36 generics: bind every monomorphized procedure body (each may itself call further generics)
    while (this._genericBindQueue.Count > 0)
      this.BindOneProcBody(this._genericBindQueue.Dequeue());
  }

  /// <summary>Binds one procedure body: seeds its parameters / result / VALUE alias as locals, then binds the statements.</summary>
  private void BindOneProcBody(ProcedureSymbol proc) {
    var scope = new Scope(proc);

    foreach (var p in proc.Parameters)
      proc.Variables[VariableKey(p.Name, TypeSuffix.None, p.Type is ArrayType)] = p;

    if (proc.IsFunction) { // the function name acts as the result variable
      // a UDT-returning FUNCTION writes its result through the hidden BYREF buffer; the result name
      // aliases that parameter (so F = ... / F.field = ... store into the caller's target). Otherwise
      // the result is an ordinary local.
      var resultVar = proc.HasSretParam ? proc.Parameters[^1]
        : new VariableSymbol(proc.Name, proc.ReturnType!, VariableStorage.Local);
      proc.Variables.TryAdd(proc.Name, resultVar);
      if (proc.ResultName is { } resultAlias) // a lifted member assigns the simple name (Pop = ...)
        proc.Variables.TryAdd(resultAlias, resultVar);
    }

    if (proc.ValueParamName is { } valueName // a PROPERTY SET: VALUE aliases the incoming value parameter
        && proc.Variables.TryGetValue(VariableKey(valueName, TypeSuffix.None, false), out var valueVar))
      proc.Variables.TryAdd(VariableKey("VALUE", TypeSuffix.None, false), valueVar);

    this.CollectLabels(proc.Body!, scope);
    foreach (var statement in proc.Body!)
      this.BindStatement(statement, scope);
    this.CheckLabelRefs(scope);
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
      case ForEachStmt fe:
        yield return fe.Body;
        break;
      case TryStmt t:
        yield return t.Body;
        if (t.Catch != null)
          yield return t.Catch;
        if (t.Finally != null)
          yield return t.Finally;
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
      case GroupStmt group:   // a synthesized multi-statement desugar; flattened inline by the splice pass
        foreach (var member in group.Body)
          this.BindStatement(member, scope);
        break;

      case MemberCallStmt mc:
        this.BindMemberCallStatement(mc, scope);
        break;

      case ForEachStmt fe:
        this.BindForEach(fe, scope);
        break;

      case DestructureStmt ds:
        this.BindDestructure(ds, scope);
        break;

      // pb36 generator-in-TRY (synthesized): bind the handler-save field operands; arm/reraise carry none
      case HandlerSaveStmt hs:
        this.BindExpression(hs.OnerrField, scope);
        this.BindExpression(hs.BpField, scope);
        this.BindExpression(hs.SpField, scope);
        break;
      case HandlerRestoreStmt hr:
        this.BindExpression(hr.OnerrField, scope);
        this.BindExpression(hr.BpField, scope);
        this.BindExpression(hr.SpField, scope);
        break;
      case HandlerArmStmt or HandlerReraiseStmt:
        break;

      case AssignStmt a when a.Target is NameExpr et && this._events.TryGetValue(et.Name, out var evInfo):
        this.BindEventAddRemove(a, et.Name, evInfo, scope);
        break;

      case AssignStmt a: {
        // pb36 nullable assignment: x = value sets Value + HasValue=TRUE; x = NOTHING clears the flag;
        // x = otherNullable falls through to an ordinary UDT copy. Restricted to a plain NameExpr target
        // (binding it is side-effect-free, unlike a property/bit-field member) and a simple-lvalue value
        // probe (so a lambda/tuple/NEW value is never bound out of context). Nullable struct fields and
        // array elements get normal handling - assign their .Value/.HasValue explicitly.
        if (a.Target is NameExpr) {
          var nullTargetType = this.BindExpression(a.Target, scope);
          bool ValueIsNullableLvalue() => a.Value is NameExpr or MemberExpr or IndexExpr && this.IsNullableType(this.BindExpression(a.Value, scope));
          if (this.IsNullableType(nullTargetType)) {
            if (a.Value is NothingExpr) {
              var clear = new AssignStmt(a.Position, new MemberExpr(a.Position, a.Target, "HasValue", TypeSuffix.None), new IntegerLiteralExpr(a.Position, 0, TypeSuffix.None));
              this.BindStatement(clear, scope);
              this._model.DesugaredStatements[a] = clear;
              break;
            }
            if (!ValueIsNullableLvalue()) {
              var set = new List<Statement> {
                new AssignStmt(a.Position, new MemberExpr(a.Position, a.Target, "Value", TypeSuffix.None), a.Value),
                new AssignStmt(a.Position, new MemberExpr(a.Position, a.Target, "HasValue", TypeSuffix.None), new IntegerLiteralExpr(a.Position, -1, TypeSuffix.None)),
              };
              var group = new GroupStmt(a.Position, set);
              this.BindStatement(group, scope);
              this._model.DesugaredStatements[a] = group;
              break;
            }
            // value is also a nullable -> a plain struct copy of both fields (handled by the normal path)
          } else if (ValueIsNullableLvalue()) {
            // pb36 auto-unwrap: assigning a nullable to a plain target reads its .Value
            var unwrap = new AssignStmt(a.Position, a.Target, new MemberExpr(a.Position, a.Value, "Value", TypeSuffix.None));
            this.BindStatement(unwrap, scope);
            this._model.DesugaredStatements[a] = unwrap;
            break;
          }
        }
        // pb36 array slice copy: b() = a(lo TO hi) -> REDIM b(0 TO hi-lo) + element copy loop.
        // Bounds may be omitted (the source's LBOUND/UBOUND) or from-end (^n); they are hoisted
        // into LONG temps so runtime expressions evaluate once.
        if (a.Value is CallOrIndexExpr { Arguments: [RangeArgExpr sliceRange] } sliceSrc
            && this.LookupArrayVariable(sliceSrc.Name, sliceSrc.Suffix, scope) is { Type: ArrayType { Rank: 1 } } srcArray) {
          var (targetName, targetSuffix) = a.Target switch {
            NameExpr tn => (tn.Name, tn.Suffix),
            CallOrIndexExpr { Arguments.Count: 0 } tc => (tc.Name, tc.Suffix),
            _ => (null, TypeSuffix.None),
          };
          if (targetName is null
              || this.LookupArrayVariable(targetName, targetSuffix, scope) is not { Type: ArrayType { IsDynamic: true, Rank: 1 } }) {
            this.Error(a.Position, "a slice copies into a dynamic rank-1 array (b() = a(lo TO hi))");
            break;
          }
          var pos = a.Position;
          var n = ++this._sliceCounter;
          var srcRef = new NameExpr(pos, sliceSrc.Name, sliceSrc.Suffix);
          Expression Bound(Expression? bound, bool isLower) {
            var ubound = new CallOrIndexExpr(pos, "UBOUND", TypeSuffix.None, [srcRef]);
            return bound switch {
              null => isLower ? new CallOrIndexExpr(pos, "LBOUND", TypeSuffix.None, [srcRef]) : ubound,
              FromEndExpr fe => new BinaryExpr(pos, BinaryOp.Subtract, ubound,
                new BinaryExpr(pos, BinaryOp.Subtract, fe.Index, new IntegerLiteralExpr(pos, 1, TypeSuffix.None))),
              _ => bound,
            };
          }
          var lo = new NameExpr(pos, $"slice{n}_lo", TypeSuffix.Long);
          var hi = new NameExpr(pos, $"slice{n}_hi", TypeSuffix.Long);
          var i = new NameExpr(pos, $"slice{n}_i", TypeSuffix.Long);
          var group = new GroupStmt(pos, [
            new AssignStmt(pos, lo, Bound(sliceRange.Lo, isLower: true)),
            new AssignStmt(pos, hi, Bound(sliceRange.Hi, isLower: false)),
            new RedimStmt(pos, [new VariableDecl(pos, targetName, targetSuffix,
              [(new IntegerLiteralExpr(pos, 0, TypeSuffix.None), new BinaryExpr(pos, BinaryOp.Subtract, hi, lo))], null)]),
            new ForStmt(pos, i, lo, hi, null, [
              new AssignStmt(pos,
                new CallOrIndexExpr(pos, targetName, targetSuffix, [new BinaryExpr(pos, BinaryOp.Subtract, i, lo)]),
                new CallOrIndexExpr(pos, sliceSrc.Name, sliceSrc.Suffix, [i])),
            ]),
          ]);
          this.BindStatement(group, scope);
          this._model.DesugaredStatements[a] = group;
          break;
        }

        // pb36 bit-field write: o.bf = v -> o.$storage = (o.$storage AND clearMask) OR ((v AND mask) << offset),
        // minimized: a constant v folds its masked/shifted value to one literal; a field covering the
        // whole container skips the read-modify-write (the member store truncates); a fold to 0 drops the OR.
        if (a.Target is MemberExpr bfTarget && this.BindExpression(bfTarget.Target, scope) is UdtType bfUdt
            && this.BitFieldOf(bfUdt, bfTarget.Member) is { } wbf) {
          var word = new MemberExpr(a.Position, bfTarget.Target, wbf.Storage, TypeSuffix.None);
          var fieldMask = (1L << wbf.Width) - 1;
          var clearMask = ~(fieldMask << wbf.Offset) & ((1L << wbf.ContainerBits) - 1);   // mask to the container's size (BYTE or WORD)

          Expression rhs;
          if (clearMask == 0) {
            // the field IS the container: plain store (the BYTE/WORD member truncates the value itself)
            rhs = a.Value;
          } else if (this._folder.TryFold(a.Value)?.Integer is { } constVal) {
            // constant value: (v AND mask) << offset folds to a single literal; 0 drops the OR entirely
            var folded = (constVal & fieldMask) << wbf.Offset;
            Expression cleared = new BinaryExpr(a.Position, BinaryOp.And, word, new IntegerLiteralExpr(a.Position, clearMask, TypeSuffix.None));
            rhs = folded == 0 ? cleared : new BinaryExpr(a.Position, BinaryOp.Or, cleared, new IntegerLiteralExpr(a.Position, folded, TypeSuffix.None));
          } else {
            Expression masked = new BinaryExpr(a.Position, BinaryOp.And, a.Value, new IntegerLiteralExpr(a.Position, fieldMask, TypeSuffix.None));
            if (wbf.Offset > 0)
              masked = new BinaryExpr(a.Position, BinaryOp.ShiftLeft, masked, new IntegerLiteralExpr(a.Position, wbf.Offset, TypeSuffix.None));
            var cleared = new BinaryExpr(a.Position, BinaryOp.And, word, new IntegerLiteralExpr(a.Position, clearMask, TypeSuffix.None));
            rhs = new BinaryExpr(a.Position, BinaryOp.Or, cleared, masked);
          }
          var store = new AssignStmt(a.Position, word, rhs);
          this.BindStatement(store, scope);
          this._model.DesugaredStatements[a] = store;
          break;
        }
        // pb36 tuple literal assigned to a tuple variable: t = (a, b) -> set each Item field (via temps,
        // so a self-referencing build like t = (t.Item2, t.Item1) is correct)
        if (a.Value is TupleExpr tupleLit && a.Target is NameExpr or MemberExpr or IndexExpr
            && this.BindExpression(a.Target, scope) is UdtType tupleTarget && tupleTarget.Name.StartsWith("$tup@", StringComparison.Ordinal)) {
          if (tupleLit.Elements.Count != tupleTarget.Fields.Count)
            this.Error(a.Position, $"tuple has {tupleTarget.Fields.Count} elements but {tupleLit.Elements.Count} values were given");
          var build = new List<Statement>();
          var fieldTemps = new List<Expression>();
          foreach (var element in tupleLit.Elements) {
            var temp = this.DeclareHidden(scope, a.Position, "tup" + ++this._destructureCounter, this.BindExpression(element, scope));
            build.Add(new AssignStmt(a.Position, temp, element));
            fieldTemps.Add(temp);
          }
          for (var i = 0; i < tupleLit.Elements.Count && i < tupleTarget.Fields.Count; ++i)
            build.Add(new AssignStmt(a.Position, new MemberExpr(a.Position, a.Target, "Item" + (i + 1), TypeSuffix.None), fieldTemps[i]));
          var buildGroup = new GroupStmt(a.Position, build);
          this.BindStatement(buildGroup, scope);
          this._model.DesugaredStatements[a] = buildGroup;
          break;
        }
        // pb36 coroutine: e = Gen(args) constructs the enumerator - reset its resume state and seed
        // the captured-parameter fields from the arguments (instead of calling a function)
        if (a is { Target: NameExpr enumTarget, Value: CallOrIndexExpr gen } && this._generatorNames.Contains(gen.Name)) {
          var inits = new List<Statement> {
            new AssignStmt(a.Position, new MemberExpr(a.Position, enumTarget, GeneratedPrefix + "state", TypeSuffix.None), new IntegerLiteralExpr(a.Position, 0, TypeSuffix.None)),
          };
          if (this._generatorParams.TryGetValue(gen.Name, out var paramNames))
            for (var i = 0; i < paramNames.Count && i < gen.Arguments.Count; ++i)
              inits.Add(new AssignStmt(a.Position, new MemberExpr(a.Position, enumTarget, GeneratedPrefix + paramNames[i], TypeSuffix.None), gen.Arguments[i]));
          var construct = new GroupStmt(a.Position, inits);
          this.BindStatement(construct, scope);
          this._model.DesugaredStatements[a] = construct;
          break;
        }
        // pb36 constructor: p = Type(args) runs the type's constructor with the target as BYREF THIS
        if (a.Value is CallOrIndexExpr ctor && this._typeConstructors.Contains(ctor.Name)) {
          var callArgs = new List<Expression>(ctor.Arguments.Count + 1) { a.Target };
          callArgs.AddRange(ctor.Arguments);
          var call = new CallStmt(a.Position, ctor.Name + "." + ctor.Name, callArgs, UsedCallKeyword: false);
          this.BindCallStatement(call, scope);
          this._model.DesugaredStatements[a] = call;
          break;
        }
        // pb36 struct return: q = F(args) where F returns a UDT by value passes q as the hidden result
        // buffer, so F writes its result straight into q (no copy) - lowered to CALL F(args, q). Covers a
        // generic function too (resolve the instance first - it carries the hidden buffer iff UDT-returning).
        if (a.Value is CallOrIndexExpr sretCall) {
          var returnsUdt = this._genericProcs.ContainsKey(sretCall.Name)
            ? this.ResolveGenericCall(sretCall.Name, sretCall.Arguments, sretCall.Position, scope, sretCall.TypeArguments) is { HasSretParam: true }
            : this._model.Overloads.TryGetValue(sretCall.Name, out var sretSet) && sretSet.Any(p => p is { IsFunction: true, HasSretParam: true });
          if (returnsUdt) {
            var callArgs = new List<Expression>(sretCall.Arguments) { a.Target };
            var call = new CallStmt(a.Position, sretCall.Name, callArgs, UsedCallKeyword: false, sretCall.TypeArguments);
            this.BindCallStatement(call, scope);
            this._model.DesugaredStatements[a] = call;
            break;
          }
        }
        // pb36 operator overloading returning a TYPE: c = a OP b -> CALL Type.op_X(a, b, c) (struct return)
        if (a.Value is BinaryExpr opBin && this.UdtOperatorProc(this.BindExpression(opBin.Left, scope), opBin.Op) is { } opCall
            && this._model.Overloads.TryGetValue(opCall, out var opCallSet) && opCallSet.Any(p => p.HasSretParam)) {
          var call = new CallStmt(a.Position, opCall, [opBin.Left, opBin.Right, a.Target], UsedCallKeyword: false);
          this.BindCallStatement(call, scope);
          this._model.DesugaredStatements[a] = call;
          break;
        }
        // pb36 coroutine: inside MoveNext, a write to a captured generator parameter/local -> THIS.$name
        if (a.Target is NameExpr capTarget && scope.Proc?.CoroutineCaptures is { } caps && caps.TryGetValue(capTarget.Name, out var capWrite)) {
          var lowered = new AssignStmt(a.Position, ThisField(a.Position, capWrite), a.Value);
          this.BindStatement(lowered, scope);
          this._model.DesugaredStatements[a] = lowered;
          break;
        }
        // pb36 property accessor: FIELD = expr writes the backing field (THIS.$Prop = expr)
        if (a.Target is NameExpr { Suffix: TypeSuffix.None } fieldTarget && scope.Proc?.BackingField is { } backingWrite
            && fieldTarget.Name.Equals("FIELD", StringComparison.OrdinalIgnoreCase)) {
          var lowered = new AssignStmt(a.Position, ThisField(a.Position, backingWrite), a.Value);
          this.BindStatement(lowered, scope);
          this._model.DesugaredStatements[a] = lowered;
          break;
        }
        if (a.Target is MemberExpr propTarget && this.TryBindPropertySet(a, propTarget, scope))
          break;
        if (a.Target is MemberExpr writeTarget)
          this.CheckReadonlyFieldWrite(writeTarget, scope);
        var targetType = this.BindAssignTarget(a.Target, scope);
        // first-class procedures: assigning a bare procedure name to a delegate/DWORD-pointer target
        // takes its address implicitly - x = Handler == x = CODEPTR32(Handler)
        if (targetType is ProcPtrType or ScalarType { Kind: ScalarKind.Dword } && this.TryImplicitCodePtr(a.Value, scope) is { } implicitPtr) {
          var wrapped = new AssignStmt(a.Position, a.Target, implicitPtr);
          this.BindStatement(wrapped, scope);
          this._model.DesugaredStatements[a] = wrapped;
          break;
        }
        var valueType = this.BindWithExpected(a.Value, targetType as ProcPtrType, scope);
        this.CheckAssignable(targetType, valueType, a.Position);
        if (targetType is ProcPtrType pp && a.Value is LambdaExpr lam && this._model.LambdaProcs.TryGetValue(lam, out var lifted))
          this.CheckProcPtrCompatible(pp, lifted, a.Position);
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

      // PB 3.6 TRY/CATCH/FINALLY: bind each block in the same scope (the front-end
      // parser already enforced at least one of CATCH/FINALLY and the dialect gate).
      case TryStmt t:
        this.BindBlock(t.Body, scope);
        if (t.Catch != null)
          this.BindBlock(t.Catch, scope);
        if (t.Finally != null)
          this.BindBlock(t.Finally, scope);
        break;

      case ResumeStmt rs when rs.Target != null:
        scope.PendingLabelRefs.Add((rs.Target, rs.Position));
        break;

      // The event system is recorded but inert - there is no dispatch, so a handler is bound, its
      // label is resolved, and it is then never called. That is the quietest failure in the compiler:
      // ON TIMER(1) GOSUB Tick compiles without a word and simply never fires. Refusing it would be
      // wrong (the genuine compiler takes it), so it is warned about instead.
      case OnEventStmt ev:
        if (ev.Index != null)
          this.BindExpression(ev.Index, scope);
        scope.PendingLabelRefs.Add((ev.Target, ev.Position));
        this.Warn(ev.Position, $"ON {ev.EventKind} is accepted but has no effect: this runtime has no event dispatch");
        break;

      case EventControlStmt ec:
        if (ec.Index != null)
          this.BindExpression(ec.Index, scope);
        this.Warn(ec.Position, $"{ec.EventKind} {ec.Mode} is accepted but has no effect: this runtime has no event dispatch");
        break;

      case IncrDecrStmt id: {
        // pb36 coroutine: INCR/DECR of a captured generator parameter/local persists across resumes
        // as the enumerator field (THIS.$name = THIS.$name +/- amount), so lower it like a write.
        if (id.Target is NameExpr incrTarget && scope.Proc?.CoroutineCaptures is { } incrCaps && incrCaps.TryGetValue(incrTarget.Name, out var incrField)) {
          var amount = id.Amount ?? new IntegerLiteralExpr(id.Position, 1, TypeSuffix.None);
          var lowered = new AssignStmt(id.Position, ThisField(id.Position, incrField),
            new BinaryExpr(id.Position, id.Increment ? BinaryOp.Add : BinaryOp.Subtract, ThisField(id.Position, incrField), amount));
          this.BindStatement(lowered, scope);
          this._model.DesugaredStatements[id] = lowered;
          break;
        }
        this.BindAssignTarget(id.Target, scope);
        if (id.Amount != null)
          this.BindExpression(id.Amount, scope);
        break;
      }

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

      // PB 3.6 coroutines: type-check the surfaced value; suspend/resume codegen is future work.
      case YieldStmt y:
        this.BindExpression(y.Value, scope);
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
        this.NoteCommandWithNoEffect(cmd);
        this.CheckMacroString(cmd, scope);
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

      // pb36 contracts: the condition must be boolean-ish; checked at run time (debug), pruned in release
      case RequireStmt rq:
        this.BindExpression(rq.Condition, scope);
        break;

      case ResourceStmt:
        if (scope.Proc != null)
          this.Error(statement.Position, "$RESOURCE is module-level only");
        break;

      // pb36 $ASSERT: evaluated right here at bind time; emits no code at all
      case StaticAssertStmt sa: {
        this.BindExpression(sa.Condition, scope);   // resolves reflection subexpressions into foldable desugars
        if (this._folder.TryFold(sa.Condition)?.Integer is not { } truth)
          this.Error(sa.Position, "$ASSERT condition must be a compile-time constant");
        else if (truth == 0)
          this.Error(sa.Position, sa.Message is { } m ? $"$ASSERT failed: {m}" : "$ASSERT failed");
        break;
      }

      case TypeDecl or UnionDecl or TypeAliasDecl or DeclareStmt or SubDecl or FunctionDecl or DefFnDecl:
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
      if (dim.Class == ArrayClass.Stack)
        this.Error(dim.Position, "a STACK array lives in a procedure's stack frame - not at module level");
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
          if (dim.Class == ArrayClass.Stack)
            this.Error(dim.Position, $"STACK array {v.Name} cannot be STATIC");
          var symbol = this.CreateVariable(v, VariableStorage.Static, dim.Position);
          if (symbol != null)
            scope.Proc.Variables[key] = symbol;
          break;
        }

        default: {
          var storage = scope.Proc.IsStatic ? VariableStorage.Static : VariableStorage.Local;
          var symbol = this.CreateVariable(v, storage, dim.Position, dim.Class);
          if (symbol == null)
            continue;
          // pb36 STACK array: frame-resident, so it needs a real frame (Local, not STATIC)
          // and compile-time bounds (the frame is laid out before the first instruction)
          if (dim.Class == ArrayClass.Stack) {
            if (storage != VariableStorage.Local)
              this.Error(dim.Position, $"STACK array {v.Name} cannot live in a STATIC procedure's frame");
            else if (symbol.Type is not ArrayType { StaticBounds: not null })
              this.Error(dim.Position, $"STACK array {v.Name} needs compile-time bounds");
          }
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

    // resolve the declared type first, so a lambda initializer can infer its
    // omitted parameter/result types from a delegate target (DIM f AS Cmp = (a,b) => ...)
    var declaredType = v.Type != null ? this.ResolveTypeName(v.Type) : null;
    var valueType = this.BindWithExpected(v.Initializer!, declaredType as ProcPtrType, scope);
    // a lambda initializer with no declared type infers a full delegate type from the lifted proc's
    // signature (not a bare DWORD), so the variable is directly callable (x 15 / CALL x(15) / x(15))
    if (declaredType == null && v.Initializer is LambdaExpr dimLam && this._model.LambdaProcs.TryGetValue(dimLam, out var dimLifted))
      declaredType = new ProcPtrType([.. dimLifted.Parameters.Select(p => p.Type)], dimLifted.IsFunction ? dimLifted.ReturnType : null);
    declaredType ??= valueType;
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
        case SpreadElement se: {
          if (se.Source is not NameExpr src
              || this.LookupArrayVariable(src.Name, src.Suffix, scope) is not { Type: ArrayType { StaticBounds: [var dimBound] } }) {
            this.Error(se.Position, "spread (..arr) requires a 1-D static array");
            return;
          }
          // slice bounds: constant expressions, a from-end ^n (= UBOUND - n + 1), or omitted
          // (= the source's LBOUND/UBOUND) - so ..b(0 TO 2), ..b(TO ^5), ..c(^7 TO) all resolve
          // to a compile-time index window
          long? ResolveBound(Expression? bound, long fallback) {
            if (bound == null)
              return fallback;
            if (bound is FromEndExpr fe)
              return this._folder.TryFold(fe.Index)?.Integer is { } n ? dimBound.Item2 - n + 1 : null;
            return this._folder.TryFold(bound)?.Integer;
          }
          var sliceLo = ResolveBound(se.SliceLo, dimBound.Item1);
          var sliceHi = ResolveBound(se.SliceHi, dimBound.Item2);
          if (sliceLo is not { } lo2 || sliceHi is not { } hi2) {
            this.Error(se.Position, "slice bounds must be compile-time constants (a literal, %equate or ^n from-end)");
            return;
          }
          if (lo2 < dimBound.Item1 || hi2 > dimBound.Item2 || lo2 > hi2) {
            this.Error(se.Position, $"slice ({lo2} TO {hi2}) is outside {src.Name}({dimBound.Item1} TO {dimBound.Item2})");
            return;
          }
          for (var j = lo2; j <= hi2; ++j) {
            var read = new CallOrIndexExpr(se.Position, src.Name, src.Suffix, [new IntegerLiteralExpr(se.Position, j, TypeSuffix.None)]);
            this.BindExpression(read, scope);
            values.Add(read);
          }
          break;
        }
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
    // pb36 anonymous type: NEW { .field = value, ... } with no type name synthesizes a UDT from the
    // field names and their inferred value types; two literals with the same shape share the type.
    UdtType? udt;
    if (nu.TypeName.Length == 0) {
      var fields = new List<TypeField>();
      var mangle = new System.Text.StringBuilder("$anon");
      foreach (var (fieldName, value) in nu.Fields) {
        var valueType = this.BindExpression(value, scope);
        if (this.PbTypeToTypeName(valueType, nu.Position) is not { } tn) {
          this.Error(nu.Position, $"cannot infer a field type for .{fieldName} in an anonymous type");
          return;
        }
        fields.Add(new TypeField(nu.Position, fieldName, tn, null));
        mangle.Append('@').Append(fieldName).Append('_').Append(valueType is ScalarType sk ? sk.Kind.ToString() : valueType is UdtType uu ? uu.Name : "Str");
      }
      var anonName = mangle.ToString();
      if (!this._model.Udts.ContainsKey(anonName))
        this.DefineUdt(anonName, fields, isUnion: false, nu.Position);
      udt = this._model.Udts[anonName];
    } else if (!this._model.Udts.TryGetValue(nu.TypeName, out udt)) {
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
    // pb36 first-class events: raising is just invoking - OnClick(42) / OnClick 42 / CALL OnClick(42)
    // are all equivalent to RAISE OnClick(42)
    if (this._events.TryGetValue(c.Name, out var ev)) {
      this._model.DesugaredStatements[c] = this.BuildRaiseGroup(ev, c.Arguments, c.Position, scope);
      return;
    }

    // pb36 first-class delegates: a delegate-typed variable is invoked like a SUB - x 15 / CALL x(15)
    // / x(15). Routed through the typed pointer-call path (which loads the closure environment); a
    // FUNCTION delegate's result is discarded, like CALL of a FUNCTION.
    if (this.ResolveVariable(c.Name, TypeSuffix.None, scope, create: false) is { Type: ProcPtrType sig } ptrVar) {
      var invoke = new CallOrIndexExpr(c.Position, c.Name, TypeSuffix.None, c.Arguments);
      this._model.VariableBindings[invoke] = ptrVar;
      this._model.ProcPtrCalls[invoke] = sig;
      if (c.Arguments.Count != sig.ParameterTypes.Count)
        this.Error(c.Position, $"procedure pointer {c.Name} expects {sig.ParameterTypes.Count} argument(s), got {c.Arguments.Count}");
      foreach (var argument in c.Arguments)
        this.BindExpression(argument, scope);
      this._model.ProcPtrStatementCalls[c] = invoke;
      return;
    }

    // bind arguments first so their types can pick the overload (PB 3.6)
    foreach (var argument in c.Arguments)
      this.BindExpression(argument, scope);

    // PB 3.6 nested procedure call (scoped to the enclosing proc); captures appended later
    if (this.ResolveNestedCall(c.Name, scope, c, c.Arguments) is { } nested) {
      this._model.CallBindings[c] = nested;
      return;
    }

    // pb36 generic procedure: infer the type arguments and bind to the monomorphized instance
    if (this.ResolveGenericCall(c.Name, c.Arguments, c.Position, scope, c.TypeArguments) is { } instance) {
      this._model.CallBindings[c] = instance;
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

  /// <summary>
  /// PB 3.6 inline lambda: lifts <c>FUNCTION(params) =&gt; expr</c> to an anonymous
  /// top-level function, records the mapping, and types the expression as a code
  /// pointer (DWORD). The lifted body is bound later (capture-checked: a reference to
  /// an outer local is rejected - capturing lambdas need the closure-env stage).
  /// </summary>
  private PbType BindLambda(LambdaExpr lambda, Scope scope) {
    if (this._model.LambdaProcs.ContainsKey(lambda)) // already lifted (a DIM-initializer lambda is re-bound by its lowered assignment): no-op
      return PbType.Dword;

    // PB 3.6: when bound against a delegate (DIM/assignment target), a lambda infers
    // its omitted result and parameter types - and BYVAL - from that signature.
    // A statement-bodied SUB lambda (SUB(params) statement) lifts to an anonymous SUB instead.
    var expected = this._expectedSignature;
    var isSubLambda = lambda.StatementBody != null;
    var lifted = new ProcedureSymbol($"$lambda${++this._lambdaCounter}", isFunction: !isSubLambda) {
      IsNested = true, // skipped by the main body loop; bound in the lambda phase
      Position = lambda.Position,
      Body = isSubLambda
        ? [lambda.StatementBody!]
        : [new AssignStmt(lambda.Position, new NameExpr(lambda.Position, "FUNCTION", TypeSuffix.None), lambda.Body)],
      ReturnType = isSubLambda ? null
        : lambda.ReturnType != null ? this.ResolveTypeName(lambda.ReturnType) ?? PbType.Long : expected?.ReturnType ?? PbType.Long,
    };
    for (var i = 0; i < lambda.Parameters.Count; ++i) {
      var p = lambda.Parameters[i];
      if (expected != null && i < expected.ParameterTypes.Count && p.Type == null && p.Suffix == TypeSuffix.None) { // untyped param: infer the delegate's type, passed BYVAL
        lifted.Parameters.Add(new(p.Name, expected.ParameterTypes[i], VariableStorage.Parameter) { ByVal = true });
      } else {
        var bound = this.BindParameter(p);
        bound.ByVal = bound.Type is ScalarType;   // lambda parameters are BYVAL by default (a delegate's params are values; pb36-only, no legacy BYREF expectation)
        lifted.Parameters.Add(bound);
      }
    }
    this._model.LambdaProcs[lambda] = lifted;
    this._pendingLambdas.Add((lifted, scope.Proc, lambda.Position)); // added to ProcedureList + bound in BindLambdaBodies
    return PbType.Dword; // the lambda value is a (far) code pointer
  }

  /// <summary>
  /// Binds each lifted lambda body in its own scope. References to the enclosing
  /// proc's stack locals are captured into the closure environment (stage 1: a
  /// stack closure whose env is the enclosing frame, reached through the env pointer
  /// carried in the fat closure value). Adds the lifted procs to the emission list.
  /// </summary>
  private void BindLambdaBodies() {
    foreach (var (lifted, enclosing, _) in this._pendingLambdas) {
      this._model.ProcedureList.Add(lifted);
      var scope = new Scope(lifted, captureFrom: enclosing, capturesByEnv: true);
      foreach (var p in lifted.Parameters)
        lifted.Variables[VariableKey(p.Name, TypeSuffix.None, p.Type is ArrayType)] = p;
      if (lifted.IsFunction)   // a SUB lambda has no result variable
        lifted.Variables.TryAdd(lifted.Name, new(lifted.Name, lifted.ReturnType!, VariableStorage.Local));
      this.CollectLabels(lifted.Body!, scope);
      foreach (var statement in lifted.Body!)
        this.BindStatement(statement, scope);
      this.CheckLabelRefs(scope);
      if (lifted.ClosureEnvPtr is { } envPtr) // a capturing lambda saves the far env pointer in this hidden local at entry
        lifted.Variables[VariableKey(envPtr.Name, TypeSuffix.None, isArray: false)] = envPtr;
    }

    // Escape analysis (stage 2): a capturing lambda whose value can outlive the
    // enclosing frame needs a HEAP environment instead of the stack env. Computed
    // here, once captures are known, so codegen can lay out the heap env record.
    foreach (var (lambda, lifted) in this._model.LambdaProcs)
      if (lifted.Captures.Count > 0 && this.LambdaEscapes(lambda, lifted))
        this.MarkClosureEscaping(lifted);
  }

  /// <summary>
  /// PB 3.6 escaping-closure layout: lay out a by-value heap env record over the
  /// captured locals (each capture's Offset becomes its byte slot in the record) so
  /// the lambda reads them through the env far pointer at those slot offsets.
  /// </summary>
  private void MarkClosureEscaping(ProcedureSymbol lifted) {
    lifted.IsEscapingClosure = true;
    var offset = 0;
    foreach (var captured in lifted.Captures) {
      // the env-record slot offset is parked on the corresponding Captured symbol;
      // codegen rewrites EmitCapturedPlace to read at this offset for heap closures
      foreach (var sym in lifted.Variables.Values)
        if (sym.Storage == VariableStorage.Captured && ReferenceEquals(lifted.Captures[sym.Offset], captured))
          sym.EnvSlotOffset = offset;
      offset += Math.Max(2, (captured.Type.Size + 1) & ~1);
    }
    lifted.ClosureEnvSize = offset;
  }

  /// <summary>
  /// Conservative escape detection for a capturing lambda: it escapes when its
  /// closure value can outlive the enclosing frame - assigned (directly, or through a
  /// local that itself escapes) to the enclosing FUNCTION's result or to a
  /// SHARED/GLOBAL/STATIC variable. Passing a closure to another procedure as an
  /// argument does NOT escape (the env travels with the live frame - the stage-1
  /// stack closure already handles that). When no defining assignment is found, treat
  /// it as escaping (the safe over-approximation).
  /// </summary>
  private bool LambdaEscapes(Expression lambda, ProcedureSymbol lifted) {
    var enclosing = this._pendingLambdas.First(p => ReferenceEquals(p.Lifted, lifted)).Enclosing;
    var body = enclosing?.Body ?? this._model.MainBody;

    // find the assignment that defines the closure value
    AssignStmt? defining = null;
    foreach (var assign in AssignmentsIn(body))
      if (ReferenceEquals(assign.Value, lambda)) {
        defining = assign;
        break;
      }
    if (defining == null)
      return true; // cannot prove it stays local

    if (this.TargetEscapes(defining.Target, enclosing))
      return true;

    // one level of local indirection: a local holding the closure that is itself
    // copied into an escaping location
    if (defining.Target is NameExpr holderName
        && this._model.VariableBindings.TryGetValue(holderName, out var holder)
        && holder.Storage == VariableStorage.Local)
      foreach (var assign in AssignmentsIn(body))
        if (assign.Value is NameExpr src
            && this._model.VariableBindings.TryGetValue(src, out var srcSym)
            && ReferenceEquals(srcSym, holder)
            && this.TargetEscapes(assign.Target, enclosing))
          return true;

    return false;
  }

  /// <summary>True when storing into <paramref name="target"/> lets a value outlive the enclosing frame (FUNCTION result, or a SHARED/GLOBAL/STATIC location).</summary>
  private bool TargetEscapes(Expression target, ProcedureSymbol? enclosing) {
    if (target is not NameExpr name)
      return true; // member/index target: be conservative
    if (enclosing is { IsFunction: true } && name.Name.Equals("FUNCTION", StringComparison.OrdinalIgnoreCase))
      return true;
    if (this._model.VariableBindings.TryGetValue(name, out var symbol)) {
      if (enclosing is { IsFunction: true } && symbol.Name.Equals(enclosing.Name, StringComparison.OrdinalIgnoreCase))
        return true; // assigning the result by its function name
      return symbol.Storage is VariableStorage.Global or VariableStorage.Static;
    }
    return false;
  }

  /// <summary>Yields every AssignStmt in a body, recursing through nested statement blocks.</summary>
  private static IEnumerable<AssignStmt> AssignmentsIn(IEnumerable<Statement> body) {
    foreach (var statement in body) {
      if (statement is AssignStmt a)
        yield return a;
      foreach (var block in StatementBlocksOf(statement))
        foreach (var nested in AssignmentsIn(block))
          yield return nested;
    }
  }

  /// <summary>Child statement blocks of a control-flow statement (mirrors codegen's ChildStatementBlocks for escape scanning).</summary>
  private static IEnumerable<IReadOnlyList<Statement>> StatementBlocksOf(Statement s) {
    switch (s) {
      case IfStmt i:
        yield return i.Then;
        foreach (var (_, arm) in i.ElseIfs)
          yield return arm;
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
      case ForEachStmt fe:
        yield return fe.Body;
        break;
      case TryStmt t:
        yield return t.Body;
        if (t.Catch != null)
          yield return t.Catch;
        if (t.Finally != null)
          yield return t.Finally;
        break;
    }
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

  /// <summary>PB 3.6: a lambda bound to a typed procedure pointer must match its arity, parameter types and result type, and pass each parameter BYVAL (delegates pass by value over the far call).</summary>
  private void CheckProcPtrCompatible(ProcPtrType sig, ProcedureSymbol lambda, SourcePosition position) {
    if (lambda.Parameters.Count != sig.ParameterTypes.Count) {
      this.Error(position, $"lambda has {lambda.Parameters.Count} parameter(s) but the procedure pointer expects {sig.ParameterTypes.Count}");
      return;
    }
    for (var i = 0; i < sig.ParameterTypes.Count; ++i)
      if (!lambda.Parameters[i].ByVal)
        this.Error(position, $"procedure-pointer parameter {i + 1} must be declared BYVAL");
      else if (!Equals(lambda.Parameters[i].Type, sig.ParameterTypes[i]))
        this.Error(position, $"procedure-pointer parameter {i + 1} type does not match the pointer's signature");
    if (sig.ReturnType != null && lambda.IsFunction && !Equals(lambda.ReturnType, sig.ReturnType))
      this.Error(position, "lambda result type does not match the procedure pointer's return type");
  }

  #endregion

  #region expression binding

  private PbType BindExpression(Expression expression, Scope scope) {
    var type = this.BindExpressionCore(expression, scope);
    this._model.ExpressionTypes[expression] = type;
    return type;
  }

  /// <summary>Binds <paramref name="expression"/> with a contextual delegate type in scope, so a lambda value can infer its omitted parameter/result types from it (PB 3.6).</summary>
  private PbType BindWithExpected(Expression expression, ProcPtrType? expected, Scope scope) {
    var previous = this._expectedSignature;
    this._expectedSignature = expected;
    try {
      return this.BindExpression(expression, scope);
    } finally {
      this._expectedSignature = previous;
    }
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

    // IndexExpr targets an array-field element through a receiver (THIS.Items(i) = v); codegen's
    // EmitPlace addresses it. A method-call IndexExpr (o.M(args)) is not a real lvalue, but that is
    // caught downstream (no addressable place) rather than here.
    if (target is not (CallOrIndexExpr or MemberExpr or PtrDerefExpr or IndexExpr)) {
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
        // pb36 coroutine: inside MoveNext, a captured generator parameter reads as THIS.$param
        if (scope.Proc?.CoroutineCaptures is { } captures && captures.TryGetValue(n.Name, out var capRead)) {
          var capAccess = ThisField(n.Position, capRead);
          var capType = this.BindExpression(capAccess, scope);
          this._model.Desugared[n] = capAccess;
          return capType;
        }
        // pb36 property accessor: FIELD reads the compiler-generated backing field (THIS.$Prop)
        if (n.Suffix == TypeSuffix.None && scope.Proc?.BackingField is { } backingRead && n.Name.Equals("FIELD", StringComparison.OrdinalIgnoreCase)) {
          var access = ThisField(n.Position, backingRead);
          var fieldType = this.BindExpression(access, scope);
          this._model.Desugared[n] = access;
          return fieldType;
        }

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
        // pb36 bit-field read: o.bf -> (o.$storage >>> offset) AND ((1 << width) - 1), minimized: no
        // shift at offset 0, and no mask when the field reaches the container's top bit (the logical
        // shift of the unsigned container already discarded everything below).
        if (this.BitFieldOf(udt, m.Member) is { } bf) {
          Expression read = new MemberExpr(m.Position, m.Target, bf.Storage, TypeSuffix.None);
          if (bf.Offset > 0)
            read = new BinaryExpr(m.Position, BinaryOp.ShiftRightLogical, read, new IntegerLiteralExpr(m.Position, bf.Offset, TypeSuffix.None));
          if (bf.Offset + bf.Width < bf.ContainerBits)   // mask only when bits remain above the field
            read = new BinaryExpr(m.Position, BinaryOp.And, read, new IntegerLiteralExpr(m.Position, (1L << bf.Width) - 1, TypeSuffix.None));
          var bfType = this.BindExpression(read, scope);
          this._model.Desugared[m] = read;
          return bfType;
        }
        var field = udt.FindField(m.Member);
        if (field != null)
          return field.Type;
        // pb36: o.Prop with no such field but a PROPERTY GET -> Type.get_Prop(o)
        var getter = MemberProcName(udt.Name, new TypeMember(m.Position, TypeMemberKind.PropertyGet, m.Member, m.Suffix, [], null, []));
        // ... or a parameterless method called without parens (o.MoveNext) -> Type.MoveNext(o)
        var member = this.HasMemberProc(getter) ? getter : this.HasMemberProc($"{udt.Name}.{m.Member}") ? $"{udt.Name}.{m.Member}" : null;
        if (member != null) {
          var call = new CallOrIndexExpr(m.Position, member, TypeSuffix.None, [m.Target]);
          var type = this.BindExpression(call, scope);
          this._model.Desugared[m] = call;
          return type;
        }
        this.Error(m.Position, $"TYPE {udt.Name} has no field {m.Member}");
        return PbType.Integer;
      }

      case AnyMatchExpr any: {
        var inner = this.BindExpression(any.Value, scope);
        if (inner is not (StringType or FixedStringType or FlexType or AsciizType))
          this.Error(any.Position, "ANY needs a string match set");
        return PbType.String;
      }

      case IndexExpr ix: {
        // pb36: o.Method(args) parses as IndexExpr(MemberExpr(o,Method), args); when the
        // member is a lifted method (not an array field) it desugars to Type.Method(o, args)
        if (ix.Target is MemberExpr method && this.TryBindMemberCall(ix, method, ix.Arguments, scope) is { } methodType)
          return methodType;
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

      case TupleExpr tup:
        // a tuple literal is only meaningful as an assignment / destructuring right-hand side, which the
        // statement binder handles before reaching here; anywhere else has no place to store it
        foreach (var element in tup.Elements)
          this.BindExpression(element, scope);
        return this.ErrorType(tup.Position, "a tuple literal '(...)' is only valid as the right-hand side of an assignment (t = (a, b)) or a destructuring (a, b = (...))");

      case BinaryExpr b:
        return this.BindBinary(b, scope);

      case IfExpr ternary:
        return this.BindTernaryIf(ternary, scope);

      case NothingExpr nothing:
        // NOTHING is consumed by the AssignStmt binder when the target is nullable; anywhere else it has no type
        return this.ErrorType(nothing.Position, "NOTHING is only valid assigned to a nullable (T?) variable");

      case CoalesceExpr coalesce: {
        // pb36 null-coalescing: v ?? d  ->  IF(v.HasValue, v.Value, d). A null-conditional access on the
        // left (a?.m ?? d) is desugared together so the ?? default is the short-circuit fallback.
        if (coalesce.Value is NullConditionalExpr ncLeft) {
          var form = this.DesugarNullConditional(ncLeft, coalesce.Fallback, scope);
          var t = this.BindExpression(form, scope);
          this._model.Desugared[coalesce] = form;
          return t;
        }
        var valueType = this.BindExpression(coalesce.Value, scope);
        if (!this.IsNullableType(valueType))
          return this.ErrorType(coalesce.Position, "the left operand of '??' must be a nullable (T?) value");
        var ternaryForm = new IfExpr(coalesce.Position,
          new MemberExpr(coalesce.Position, coalesce.Value, "HasValue", TypeSuffix.None),
          new MemberExpr(coalesce.Position, coalesce.Value, "Value", TypeSuffix.None),
          coalesce.Fallback);
        var coalesceType = this.BindExpression(ternaryForm, scope);
        this._model.Desugared[coalesce] = ternaryForm;
        return coalesceType;
      }

      case NullConditionalExpr nc: {
        // pb36 null-conditional access standalone (no '??'): a?.m -> IF(a.HasValue, a.Value.m, 0).
        // The zero fallback matches the accessed member's type (numeric 0 or "").
        var probe = this.DesugarNullConditional(nc, null, scope);
        var t = this.BindExpression(probe, scope);
        this._model.Desugared[nc] = probe;
        return t;
      }

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

      case InterpolatedStringExpr interp:
        return this.BindInterpolatedString(interp, scope);

      case LambdaExpr lambda:
        return this.BindLambda(lambda, scope);

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

    // pb36 wide integers: ADD/SUBTRACT of two same-width wide values is the ADC/SBB-chain path; other
    // ops (compare, bitwise, shift, multiply, decimal print) and mixed widths are still follow-ups
    if (left is WideIntType lw && right is WideIntType rw) {
      if (b.Op is BinaryOp.Add or BinaryOp.Subtract && lw.ByteSize == rw.ByteSize)
        return lw;
      return this.ErrorType(b.Position, "wide-integer operation not yet supported (only + and - between same-width wide values)");
    }
    if (left is WideIntType || right is WideIntType)
      return this.ErrorType(b.Position, "mixing a wide integer with a narrower value in an expression is not yet supported (convert explicitly)");

    // pb36 nullable auto-unwrap: a nullable operand in arithmetic/comparison reads its .Value
    // (the whole binary is rewritten so the .Value member's target is the original operand - no recursion)
    if (this.IsNullableType(left) || this.IsNullableType(right)) {
      var unwrappedLeft = this.IsNullableType(left) ? new MemberExpr(b.Position, b.Left, "Value", TypeSuffix.None) : b.Left;
      var unwrappedRight = this.IsNullableType(right) ? new MemberExpr(b.Position, b.Right, "Value", TypeSuffix.None) : b.Right;
      var lowered = new BinaryExpr(b.Position, b.Op, unwrappedLeft, unwrappedRight);
      var loweredType = this.BindExpression(lowered, scope);
      this._model.Desugared[b] = lowered;
      return loweredType;
    }

    // PB 3.6 scaled pointer arithmetic: ptr +* i / ptr -* i
    if (b.Op is BinaryOp.PointerAdd or BinaryOp.PointerSub)
      return this.BindPointerArith(b, left, right);

    // pb36 operator overloading: a OP b where the left operand's TYPE defines OPERATOR <op>
    if (this.UdtOperatorProc(left, b.Op) is { } opProcName) {
      // an operator that returns a TYPE (struct return) is valid only as 'result = a OP b' (the
      // AssignStmt path rewrites it); here, in expression position, it cannot leave a value
      if (this._model.Overloads.TryGetValue(opProcName, out var opSet) && opSet.Any(p => p.HasSretParam))
        return this.ErrorType(b.Position, $"operator '{opProcName}' returns a TYPE; use it as 'result = a <op> b'");
      var call = new CallOrIndexExpr(b.Position, opProcName, TypeSuffix.None, [b.Left, b.Right]);
      var type = this.BindExpression(call, scope);
      this._model.Desugared[b] = call;
      return type;
    }

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
    // EffectiveDialect: the PB 2.0+ float-promotion of integer +/-/* (32767+1 = 32768, not a 16-bit
    // wrap to -32768) is a runtime semantic, so a $COMPAT override makes a transpiled-to-pb35 program
    // wrap in-place the way its source dialect (TB / Microsoft / pb20) did.
    if (this.EffectiveDialect.IsPbAtLeast(Dialect.Pb20)
        && left is ScalarType { IsFloat: false, ByteSize: <= 4 }
        && right is ScalarType { IsFloat: false, ByteSize: <= 4 }) {
      var wide = Math.Max(EffectiveDivideWidth(b.Left, left), EffectiveDivideWidth(b.Right, right)) <= 2
        ? PbType.Single
        : PbType.Double;
      // Genuine PBC promotes to float only where it keeps the result exact: a 2-byte +/-/*
      // sum fits Single, and a 4-byte product needs the FPU (>32 bits). But a 4-byte ADD or
      // SUBTRACT is done in the native 32-bit ALU and WRAPS - verified against PBC 3.50, which
      // prints 2147483000+1000 as -2147483296 (wrapped), not the x87 sentinel a Double store
      // would give. So Double-wide +/- stays integral; only the multiply promotes.
      if (!this._checkedArithmetic)
        return wide == PbType.Single || b.Op == BinaryOp.Multiply ? wide : PromoteUnsigned(Widest(left, right));
      var bothSigned = left is ScalarType { Signed: true } && right is ScalarType { Signed: true };
      if (b.Op == BinaryOp.Multiply && bothSigned && wide == PbType.Double)
        return wide;
    }
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
    // QWORD (8-byte unsigned) has no signed type holding its full range, so it dominates any
    // same-or-narrower signed operand, keeping 64-bit (unsigned) arithmetic
    if (unsigned.Size >= 8)
      return unsigned;
    // DWORD has no 32-bit signed type that holds its full range, so it dominates a
    // same-or-narrower signed operand (genuine PBC keeps the result DWORD:
    // 4000000000 + 5 = 4000000005, d??? > 100 compares unsigned) - BYTE/WORD instead
    // widen to the next signed size (INTEGER/LONG) via PromoteUnsigned upstream
    if (unsigned.Size == 4 && signed.Size <= 4)
      return unsigned;
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

  /// <summary>
  /// PB 3.6 interpolated string <c>$"text {expr} {expr:fmt}"</c>: desugars to a string
  /// concatenation reusing the existing runtime - literal parts become string literals, a
  /// STRING hole stays as-is, a numeric hole becomes <c>STR$(expr)</c>, and a
  /// <c>{expr:fmt}</c> hole becomes <c>USING$(fmt, expr)</c> (the PRINT USING formatter). The
  /// bound concatenation is recorded for codegen; a non-string/non-numeric hole is an error.
  /// </summary>
  private PbType BindInterpolatedString(InterpolatedStringExpr interp, Scope scope) {
    var pos = interp.Position;
    Expression? result = null;

    void Append(Expression piece) =>
      result = result == null ? piece : new BinaryExpr(pos, BinaryOp.Concat, result, piece);

    foreach (var part in interp.Parts) {
      if (part.Literal != null) {
        Append(new StringLiteralExpr(part.Position, part.Literal));
        continue;
      }

      var hole = part.Hole!;
      var holeType = this.BindExpression(hole, scope);
      Expression piece;
      if (part.Format != null) {
        // {expr:fmt} -> USING$(fmt, expr): the PRINT USING formatter (STRING or numeric)
        piece = new CallOrIndexExpr(part.Position, "USING$", TypeSuffix.None,
          [new StringLiteralExpr(part.Position, part.Format), hole]);
      } else if (IsStringType(holeType)) {
        piece = hole; // a STRING hole concatenates directly
      } else if (holeType is ScalarType or BcdType) {
        // a numeric hole -> STR$(expr): exactly the text PRINT/STR$ produces
        piece = new CallOrIndexExpr(part.Position, "STR$", TypeSuffix.None, [hole]);
      } else {
        this.Error(part.Position, "interpolated '{ }' hole must be a STRING or numeric expression");
        piece = new StringLiteralExpr(part.Position, "");
      }
      Append(piece);
    }

    result ??= new StringLiteralExpr(pos, ""); // $"" is the empty string
    var type = this.BindExpression(result, scope);
    this._model.Desugared[interp] = result;
    return type;
  }

  private static bool IsStringType(PbType type) => type is StringType or FixedStringType or FlexType or AsciizType;


  #region pb36 compile-time reflection

  private int _bindThunkCounter;

  /// <summary>
  /// pb36 partial application and composition, lowered to synthesized top-level thunk FUNCTIONs
  /// (delegates by CODEPTR32, exactly like lambdas): <c>BIND(f, c1, ...)</c> pre-fills f's leading
  /// parameters with compile-time constants and yields a delegate over the remaining ones;
  /// <c>COMPOSE(f, g)</c> yields <c>h</c> with <c>h(x) = g(f(x))</c> (apply f first). Both names
  /// stay available as ordinary identifiers - the forms only fire when no user procedure or
  /// variable shadows them.
  /// </summary>
  private PbType? TryBindPartialApplication(CallOrIndexExpr call, Scope scope) {
    if (call.Suffix != TypeSuffix.None || this._model.Procedures.ContainsKey(call.Name)
        || this.ResolveVariable(call.Name, call.Suffix, scope, create: false) != null)
      return null;
    var pos = call.Position;

    Parameter[]? ParamsOf(ProcedureSymbol target, int skip) {
      var result = new List<Parameter>();
      for (var k = skip; k < target.VisibleParameterCount; ++k) {
        var p = target.Parameters[k];
        var tn = this.TypeNameOf(p.Type, pos);
        if (tn == null)
          return null;
        result.Add(new Parameter(pos, p.Name, TypeSuffix.None, tn, ByVal: true, Seg: false, IsArray: false));
      }
      return [.. result];
    }

    PbType BuildThunk(string kind, ProcedureSymbol target, Parameter[] pars, Expression resultValue) {
      var thunkName = $"${kind}{++this._bindThunkCounter}@{target.Name}";
      var returnTn = target.ReturnType is { } rt ? this.TypeNameOf(rt, pos) : null;
      var body = new List<Statement> { new AssignStmt(pos, new NameExpr(pos, thunkName, TypeSuffix.None), resultValue) };
      var proc = this.DefineProcedure(thunkName, isFunction: true, TypeSuffix.None, returnTn, pars, isStatic: false, body, pos);
      this._genericBindQueue.Enqueue(proc);
      var pointer = new CallOrIndexExpr(pos, "CODEPTR32", TypeSuffix.None, [new NameExpr(pos, thunkName, TypeSuffix.None)]);
      this.BindExpression(pointer, scope);
      this._model.Desugared[call] = pointer;
      return new ProcPtrType([.. pars.Select(p => this.ResolveTypeName(p.Type!) ?? PbType.Integer)], target.ReturnType);
    }

    switch (call.Name.ToUpperInvariant()) {
      case "BIND" when call.Arguments.Count >= 2 && call.Arguments[0] is NameExpr bt
          && this._model.Procedures.TryGetValue(bt.Name, out var target) && target.IsFunction: {
        this.Require(LanguageFeature.PartialApplication, pos);
        var bound = call.Arguments.Skip(1).ToList();
        if (bound.Count >= target.VisibleParameterCount) {
          this.Error(pos, $"BIND pre-fills fewer arguments than {bt.Name} takes ({target.VisibleParameterCount})");
          return PbType.Dword;
        }
        var boundLiterals = new List<Expression>();
        foreach (var b in bound) {
          if (this._folder.TryFold(b) is not { } cv) {
            this.Error(b.Position, "BIND arguments must be compile-time constants (the delegate snapshots them)");
            return PbType.Dword;
          }
          boundLiterals.Add(cv.Text is { } txt
            ? new StringLiteralExpr(b.Position, txt)
            : cv.Integer is { } iv ? new IntegerLiteralExpr(b.Position, iv, TypeSuffix.None)
            : new FloatLiteralExpr(b.Position, cv.AsFloat, TypeSuffix.None));
        }
        var pars = ParamsOf(target, bound.Count);
        if (pars == null) {
          this.Error(pos, $"BIND cannot forward {bt.Name}'s parameter types");
          return PbType.Dword;
        }
        var forwarded = boundLiterals.Concat(pars.Select(Expression (p) => new NameExpr(pos, p.Name, TypeSuffix.None))).ToList();
        return BuildThunk("bind", target, pars, new CallOrIndexExpr(pos, bt.Name, TypeSuffix.None, forwarded));
      }

      case "COMPOSE" when call.Arguments is [NameExpr fe, NameExpr ge]
          && this._model.Procedures.TryGetValue(fe.Name, out var f) && f.IsFunction
          && this._model.Procedures.TryGetValue(ge.Name, out var g) && g.IsFunction: {
        this.Require(LanguageFeature.PartialApplication, pos);
        if (f.VisibleParameterCount != 1 || g.VisibleParameterCount != 1) {
          this.Error(pos, "COMPOSE takes two single-parameter FUNCTIONs");
          return PbType.Dword;
        }
        var pars = ParamsOf(f, 0);
        if (pars == null) {
          this.Error(pos, $"COMPOSE cannot forward {fe.Name}'s parameter type");
          return PbType.Dword;
        }
        var inner = new CallOrIndexExpr(pos, fe.Name, TypeSuffix.None, [new NameExpr(pos, pars[0].Name, TypeSuffix.None)]);
        return BuildThunk("compose", g, pars, new CallOrIndexExpr(pos, ge.Name, TypeSuffix.None, [inner]));
      }

      default:
        return null;
    }
  }

  /// <summary>
  /// Binds the compile-time reflection pseudo-functions. Every one of them folds to a literal at
  /// bind time (recorded in <see cref="SemanticModel.Desugared"/>): TYPEOF$(x) - the display name of
  /// x's type (x may be a TYPE name or any expression); SIZEOF(T) - storage size of a TYPE name (the
  /// variable form stays with the regular intrinsic); FIELDCOUNT(T); FIELDNAME$(T, i) (1-based);
  /// FIELDOFFSET(T, f) and FIELDSIZE(T, f) where f is a field name or a 1-based constant index.
  /// Returns null when the call is not a reflection form (fall through to intrinsics/procedures).
  /// </summary>
  private PbType? TryBindReflection(CallOrIndexExpr call, Scope scope) {
    var name = call.Suffix == TypeSuffix.String ? call.Name.ToUpperInvariant() + "$" : call.Name.ToUpperInvariant();
    switch (name) {
      case "TYPEOF$" when call.Arguments.Count == 1: {
        this.Require(LanguageFeature.CompileTimeReflection, call.Position);
        var t = this.ReflectionSubjectType(call.Arguments[0], scope);
        var nameLit = new StringLiteralExpr(call.Position, t is null ? "" : TypeDisplayName(t));
        this._model.Desugared[call] = nameLit;
        return this.BindExpression(nameLit, scope);
      }

      case "SIZEOF" when call.Arguments is [NameExpr tn] && this.ResolveReflectedTypeName(tn, scope) is { } ty: {
        this.Require(LanguageFeature.CompileTimeReflection, call.Position);
        // the literal is Long-suffixed AND bound: a 32-bit consumer (PRINT of a LONG) must
        // see a LONG-typed literal, else it reads a stale high word
        var sizeLit = new IntegerLiteralExpr(call.Position, ty.Size, TypeSuffix.Long);
        this._model.Desugared[call] = sizeLit;
        return this.BindExpression(sizeLit, scope);
      }

      case "FIELDCOUNT" when call.Arguments.Count == 1: {
        this.Require(LanguageFeature.CompileTimeReflection, call.Position);
        if (this.ReflectedUdt(call.Arguments[0], scope) is { } udt) {
          var countLit = new IntegerLiteralExpr(call.Position, udt.Fields.Count, TypeSuffix.None);
          this._model.Desugared[call] = countLit;
          this.BindExpression(countLit, scope);
        }
        return PbType.Integer;
      }

      case "FIELDNAME$" when call.Arguments.Count == 2: {
        this.Require(LanguageFeature.CompileTimeReflection, call.Position);
        if (this.ReflectedUdt(call.Arguments[0], scope) is { } udt && this.ReflectedField(udt, call.Arguments[1]) is { } f) {
          var fieldLit = new StringLiteralExpr(call.Position, f.Name);
          this._model.Desugared[call] = fieldLit;
          this.BindExpression(fieldLit, scope);
        }
        return PbType.String;
      }

      case "FIELDOFFSET" or "FIELDSIZE" when call.Arguments.Count == 2: {
        this.Require(LanguageFeature.CompileTimeReflection, call.Position);
        if (this.ReflectedUdt(call.Arguments[0], scope) is { } udt && this.ReflectedField(udt, call.Arguments[1]) is { } f) {
          var offLit = new IntegerLiteralExpr(call.Position, name == "FIELDOFFSET" ? f.Offset : f.TotalSize, TypeSuffix.None);
          this._model.Desugared[call] = offLit;
          this.BindExpression(offLit, scope);
        }
        return PbType.Integer;
      }

      default:
        return null;
    }
  }

  /// <summary>The type a reflection subject denotes: a TYPE/alias name directly, otherwise the expression's static type.</summary>
  private PbType? ReflectionSubjectType(Expression e, Scope scope)
    => e is NameExpr n && this.ResolveReflectedTypeName(n, scope) is { } named ? named : this.BindExpression(e, scope);

  /// <summary>Resolves a bare unsuffixed name to a TYPE/alias when it is NOT shadowed by a variable (variables win, so SIZEOF(x) stays the variable's storage size).</summary>
  private PbType? ResolveReflectedTypeName(NameExpr n, Scope scope) {
    if (n.Suffix != TypeSuffix.None || this.ResolveVariable(n.Name, n.Suffix, scope, create: false) != null)
      return null;
    return this.ResolveTypeName(new TypeName(n.Position, BuiltinType.None, n.Name));
  }

  private UdtType? ReflectedUdt(Expression e, Scope scope) {
    if (this.ReflectionSubjectType(e, scope) is UdtType udt)
      return udt;
    this.Error(e.Position, "the reflection subject must be a TYPE (or a variable of one)");
    return null;
  }

  /// <summary>A field selector: a bare field name, a string literal, or a 1-based constant index.</summary>
  private UdtField? ReflectedField(UdtType udt, Expression selector) {
    switch (selector) {
      case NameExpr n when udt.FindField(n.Name) is { } f:
        return f;
      case StringLiteralExpr s when udt.FindField(s.Value) is { } f:
        return f;
      default:
        if (this._folder.TryFold(selector)?.Integer is { } i && i >= 1 && i <= udt.Fields.Count)
          return udt.Fields[(int)(i - 1)];
        this.Error(selector.Position, $"'{udt.Name}' has no such field");
        return null;
    }
  }

  /// <summary>The PB spelling of a type, as TYPEOF$ reports it.</summary>
  private static string TypeDisplayName(PbType t) => t switch {
    ScalarType s => s.Kind switch {
      ScalarKind.SByte => "SBYTE", ScalarKind.QWord => "QWORD",
      _ => s.Kind.ToString().ToUpperInvariant(),
    },
    StringType => "STRING",
    FixedStringType f => $"STRING * {f.Length}",
    AsciizType a => $"ASCIIZ * {a.Length}",
    UdtType u => u.Name,
    ArrayType a => TypeDisplayName(a.Element) + "()",
    PointerType p => TypeDisplayName(p.Target) + " PTR",
    ProcPtrType => "DWORD",
    _ => t.GetType().Name.ToUpperInvariant(),
  };

  #endregion

  private PbType BindCallOrIndex(CallOrIndexExpr call, Scope scope) {
    // pb36 slices are handled by the assignment / FOR EACH desugars; anywhere else is an error
    if (call.Arguments.Any(arg => arg is RangeArgExpr)) {
      this.Error(call.Position, "a slice (lo TO hi) is only valid as a whole assignment source or a FOR EACH collection");
      return PbType.Integer;
    }
    // 0. pb36 generic function: a user-declared generic template shadows an intrinsic of the same name
    // (e.g. a generic Max), so resolve it before the array/intrinsic checks
    if (this._genericProcs.ContainsKey(call.Name) && this.ResolveGenericCall(call.Name, call.Arguments, call.Position, scope, call.TypeArguments) is { } genInstance0) {
      if (!genInstance0.IsFunction)
        return this.ErrorType(call.Position, $"SUB {call.Name} used as a function");
      this._model.CallBindings[call] = genInstance0;
      return genInstance0.ReturnType ?? PbType.Integer;
    }

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

    // 1b. PB 3.6 typed procedure-pointer call: f(args) where f is a FUNCTION(...) pointer
    if (this.ResolveVariable(call.Name, call.Suffix, scope, create: false) is { Type: ProcPtrType procPtr } ptrVar) {
      this._model.VariableBindings[call] = ptrVar;
      this._model.ProcPtrCalls[call] = procPtr;
      if (call.Arguments.Count != procPtr.ParameterTypes.Count)
        this.Error(call.Position, $"procedure pointer {call.Name} expects {procPtr.ParameterTypes.Count} argument(s), got {call.Arguments.Count}");
      foreach (var argument in call.Arguments)
        this.BindExpression(argument, scope);
      return procPtr.ReturnType ?? this.ErrorType(call.Position, $"SUB-pointer {call.Name} has no result; call it with CALL");
    }

    // pb36 compile-time reflection: TYPEOF$/FIELDCOUNT/FIELDNAME$/FIELDOFFSET/FIELDSIZE (and SIZEOF
    // of a TYPE name) fold to literals right here - zero runtime footprint, the decompilation and the
    // codegen only ever see the constant
    if (this.TryBindReflection(call, scope) is { } reflected)
      return reflected;

    // pb36 partial application / composition: BIND(f, consts...) / COMPOSE(f, g) synthesize a
    // delegate thunk FUNCTION (bound through the late-proc queue, like generic instances)
    if (this.TryBindPartialApplication(call, scope) is { } partial)
      return partial;

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
      // a UDT-returning FUNCTION returns through a hidden buffer (struct return); its call is valid only
      // as the right-hand side of an assignment (q = F(args)), which the AssignStmt path rewrites
      if (proc.HasSretParam) {
        this.Error(call.Position, $"FUNCTION {call.Name} returns a TYPE by value; assign its result to a variable (q = {call.Name}(...))");
        return proc.ReturnType ?? PbType.Integer;
      }
      this._model.CallBindings[call] = proc;
      if (call.Arguments.Any(a => a is NamedArgExpr))
        this.ReorderNamedArguments(call, proc, call.Arguments, call.Position);
      else if (call.Arguments.Count < proc.RequiredParameters || call.Arguments.Count > proc.VisibleParameterCount)
        this.Error(call.Position, $"FUNCTION {call.Name} expects {proc.VisibleParameterCount} argument(s), got {call.Arguments.Count}");
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
    // SINGLE "1.414214", LOG(e#) the DOUBLE-rounded " 1 " (oracle-verified). The DOUBLE/SINGLE
    // return type makes codegen NARROW the extended FYL2X result to that precision before PRINT
    // (pb35 keeps it extended, printing 1 ULP off at 16 digits). EffectiveDialect honours $COMPAT so
    // a transpiled-to-pb35 program narrows identically to its source dialect.
    IntrinsicReturn.Ext when this.EffectiveDialect.Family() == DialectFamily.Microsoft && _argTypedMath.Contains(intrinsic.Name)
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
        && (outer.Variables.GetValueOrDefault(key) ?? (suffix == TypeSuffix.None ? null : outer.Variables.GetValueOrDefault(name))) is { Storage: VariableStorage.Local or VariableStorage.Static or VariableStorage.Parameter, IsArray: false } captured) {
      // PB 3.6 lambda: the captured outer variable becomes a closure-environment entry
      // reached through the env pointer (a lambda is called indirectly, so the
      // BYREF-parameter capture used by nested procs cannot work). Stage 1 is
      // stack-based: the env is the enclosing frame, so its stack LOCALs and BYVAL
      // PARAMETERS (both BP-relative cells) are captured this way.
      if (scope.CapturesByEnv) {
        if (!(captured.Storage == VariableStorage.Local
              || (captured.Storage == VariableStorage.Parameter && captured.ByVal)))
          return null; // STATIC/shared lives in DS (reachable directly); a BYREF param holds a pointer, not a value
        captured.IsCaptured = true; // its address escapes into the closure: keep it in memory, never fold/elide
        var capturedSym = new VariableSymbol(name, captured.Type, VariableStorage.Captured) { Offset = nested.Captures.Count };
        nested.Captures.Add(captured);
        nested.Variables[key] = capturedSym;
        nested.ClosureEnvPtr ??= new($"{nested.Name}$env", PbType.Dword, VariableStorage.Local);
        return capturedSym;
      }
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
