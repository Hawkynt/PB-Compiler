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
  private readonly Dictionary<char, PbType> _defaultTypes = [];
  private ConstantFolder _folder;
  private bool _dynamicMode;
  private int _optionBase;

  private Binder(CompilationUnit unit) {
    this._unit = unit;
    this._model = new() { FileName = unit.FileName };
    this._folder = new(this._model.Equates);
  }

  public static SemanticModel Bind(CompilationUnit unit) {
    var binder = new Binder(unit);
    binder.ScanModule();
    binder.BindAllBodies();
    return binder._model;
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
          this.DefineProcedure(s.Name, isFunction: false, TypeSuffix.None, null, s.Parameters, s.IsStatic, s.Body, s.Position);
          break;

        case FunctionDecl f:
          this.DefineProcedure(f.Name, isFunction: true, f.Suffix, f.ReturnType, f.Parameters, f.IsStatic, f.Body, f.Position);
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

  private void DefineEquate(EquateStmt e) {
    if (this._folder.TryFold(e.Value) is not { } value) {
      this.Error(e.Position, $"equate %{e.Name} is not a compile-time constant");
      return;
    }
    if (this._model.Equates.TryGetValue(e.Name, out var existing) && existing != value) {
      this.Error(e.Position, $"equate %{e.Name} redefined with a different value");
      return;
    }
    this._model.Equates[e.Name] = value;
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
    if (this._model.Procedures.ContainsKey(d.Name))
      return; // a definition or earlier DECLARE wins; signatures checked when bodies bind

    var proc = new ProcedureSymbol(d.Name, d.IsFunction) { Position = d.Position };
    if (d.IsFunction)
      proc.ReturnType = this.ResolveReturnType(d.Name, d.Suffix, d.ReturnType);
    if (d.Parameters != null)
      foreach (var p in d.Parameters)
        proc.Parameters.Add(this.BindParameter(p));
    this._model.Procedures[d.Name] = proc;
  }

  private ProcedureSymbol DefineProcedure(string name, bool isFunction, TypeSuffix suffix, TypeName? returnType, IReadOnlyList<Parameter> parameters, bool isStatic, IReadOnlyList<Statement> body, SourcePosition position) {
    if (this._model.Procedures.TryGetValue(name, out var existing) && !existing.IsExternal) {
      this.Error(position, $"{(isFunction ? "FUNCTION" : "SUB")} {name} already defined");
      return existing;
    }

    var proc = new ProcedureSymbol(name, isFunction) { IsStatic = isStatic, Body = body, Position = position };
    if (isFunction)
      proc.ReturnType = this.ResolveReturnType(name, suffix, returnType);
    foreach (var p in parameters)
      proc.Parameters.Add(this.BindParameter(p));

    this._model.Procedures[name] = proc;
    return proc;
  }

  private VariableSymbol BindParameter(Parameter p) {
    var type = p.Type != null
      ? this.ResolveTypeName(p.Type) ?? PbType.Integer
      : this.TypeFromSuffixOrDefault(p.Name, p.Suffix);
    if (p.IsArray)
      type = new ArrayType(type, null, Rank: 1); // array parameters arrive as descriptors

    return new(p.Name, type, VariableStorage.Parameter) { ByVal = p.ByVal, Seg = p.Seg };
  }

  private void DeclareModuleVariables(DimStmt dim) {
    foreach (var v in dim.Variables) {
      var symbol = this.CreateVariable(v, VariableStorage.Global, dim.Position);
      if (symbol == null)
        continue;
      symbol.IsShared = dim.SharedFlag || dim.Storage is StorageClass.Shared or StorageClass.Public or StorageClass.Common;
      var key = VariableKey(v.Name, v.Suffix);
      if (this._model.ModuleVariables.TryGetValue(key, out var existing)) {
        // PB tolerates re-DIM of dynamic arrays; complain only about type changes
        if (!Equals(existing.Type, symbol.Type) && existing.Type is not ArrayType { IsDynamic: true })
          this.Error(dim.Position, $"variable {v.Name} already declared with a different type");
        existing.IsShared |= symbol.IsShared;
        continue;
      }
      this._model.ModuleVariables[key] = symbol;
    }
  }

  private VariableSymbol? CreateVariable(VariableDecl v, VariableStorage storage, SourcePosition position) {
    var elementType = v.Type != null
      ? this.ResolveTypeName(v.Type)
      : this.TypeFromSuffixOrDefault(v.Name, v.Suffix);
    if (elementType == null) {
      this.Error(position, $"unknown type for variable {v.Name}");
      return null;
    }

    if (v.ArrayBounds == null)
      return new(v.Name, elementType, storage);

    // try static bounds; any non-constant bound (or $DYNAMIC mode) makes the array dynamic
    var bounds = new List<(int, int)>();
    var isStatic = !this._dynamicMode;
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
    return new(v.Name, arrayType, storage);
  }

  #endregion

  #region types

  private static PbType? MapBuiltin(BuiltinType b) => b switch {
    BuiltinType.Byte => PbType.Byte,
    BuiltinType.Word => PbType.Word,
    BuiltinType.Dword => PbType.Dword,
    BuiltinType.Integer => PbType.Integer,
    BuiltinType.Long => PbType.Long,
    BuiltinType.Quad => PbType.Long, // PB 3.5 has no QUAD; tolerate as LONG with a warning at use sites
    BuiltinType.Single => PbType.Single,
    BuiltinType.Double => PbType.Double,
    BuiltinType.Ext => PbType.Ext,
    BuiltinType.String => PbType.String,
    BuiltinType.Flex => new FlexType(),
    BuiltinType.Any => PbType.Any,
    _ => null,
  };

  private PbType? ResolveTypeName(TypeName t) {
    if (t.Builtin == BuiltinType.FixedString) {
      if (this._folder.TryFold(t.FixedLength!) is { Integer: { } n } && n is > 0 and <= 32767)
        return new FixedStringType((int)n);
      this.Error(t.Position, "fixed string length must be a constant in 1..32767");
      return new FixedStringType(1);
    }

    if (t.IsUserDefined)
      return this._model.Udts.TryGetValue(t.UserTypeName!, out var udt) ? udt : null;

    return MapBuiltin(t.Builtin);
  }

  private PbType TypeFromSuffixOrDefault(string name, TypeSuffix suffix) => suffix switch {
    TypeSuffix.Integer => PbType.Integer,
    TypeSuffix.Long => PbType.Long,
    TypeSuffix.Single => PbType.Single,
    TypeSuffix.Double => PbType.Double,
    TypeSuffix.Ext => PbType.Ext,
    TypeSuffix.String => PbType.String,
    _ => this._defaultTypes.TryGetValue(char.ToUpperInvariant(name[0]), out var def) ? def : PbType.Single,
  };

  private PbType ResolveReturnType(string name, TypeSuffix suffix, TypeName? declared) {
    if (declared != null)
      return this.ResolveTypeName(declared) ?? PbType.Integer;
    return this.TypeFromSuffixOrDefault(name, suffix);
  }

  private static string VariableKey(string name, TypeSuffix suffix) => suffix == TypeSuffix.None ? name : name + SuffixChar(suffix);

  private static char SuffixChar(TypeSuffix s) => s switch {
    TypeSuffix.Integer => '%',
    TypeSuffix.Long => '&',
    TypeSuffix.Single => '!',
    TypeSuffix.Double => '#',
    TypeSuffix.Ext => 'E',
    TypeSuffix.String => '$',
    _ => ' ',
  };

  #endregion

  #region pass 2 - bodies

  /// <summary>Per-procedure (or main) binding context.</summary>
  private sealed class Scope(ProcedureSymbol? proc) {
    public ProcedureSymbol? Proc => proc;
    public string LabelKey => proc?.Name ?? "";
    public List<(string Target, SourcePosition Position)> PendingLabelRefs { get; } = [];
  }

  private void BindAllBodies() {
    this._folder = new(this._model.Equates);

    var main = new Scope(null);
    this.CollectLabels(this._model.MainBody, main);
    foreach (var statement in this._model.MainBody)
      this.BindStatement(statement, main);
    this.CheckLabelRefs(main);

    foreach (var proc in this._model.Procedures.Values.Where(p => !p.IsExternal)) {
      var scope = new Scope(proc);

      foreach (var p in proc.Parameters)
        proc.Variables[VariableKey(p.Name, TypeSuffix.None)] = p;

      if (proc.IsFunction) // the function name acts as the result variable
        proc.Variables.TryAdd(proc.Name, new(proc.Name, proc.ReturnType!, VariableStorage.Local));

      this.CollectLabels(proc.Body!, scope);
      foreach (var statement in proc.Body!)
        this.BindStatement(statement, scope);
      this.CheckLabelRefs(scope);
    }
  }

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
          var symbol = this.ResolveVariable(v.Name, v.Suffix, scope, create: false);
          if (symbol == null) {
            // REDIM can introduce a dynamic array
            var created = this.CreateVariable(v with { ArrayBounds = v.ArrayBounds }, scope.Proc == null ? VariableStorage.Global : VariableStorage.Local, redim.Position);
            if (created != null) {
              created.Type = created.Type is ArrayType at ? at with { StaticBounds = null } : new ArrayType(created.Type, null, v.ArrayBounds?.Count ?? 1);
              this.Register(created, v, scope);
            }
          } else if (symbol.Type is ArrayType { IsDynamic: false })
            this.Error(redim.Position, $"REDIM on static array {v.Name} (use $DYNAMIC)");
        }
        break;

      case EraseStmt erase:
        foreach (var array in erase.Arrays)
          this.BindExpression(array, scope);
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

      case MidAssignStmt mid:
        this.BindAssignTarget(mid.Target, scope);
        this.BindExpression(mid.Start, scope);
        if (mid.Length != null)
          this.BindExpression(mid.Length, scope);
        this.BindExpression(mid.Value, scope);
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

      case ErrorStmt err:
        this.BindExpression(err.Code, scope);
        break;

      case EndStmt end when end.ExitCode != null:
        this.BindExpression(end.ExitCode, scope);
        break;

      case DefSegStmt seg when seg.Segment != null:
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

      // declarations already handled in pass 1 (module) or harmless here; labels collected upfront
      case LabelStmt or DataStmt or MetaStmt or InlineAsmStmt or EquateStmt or DefTypeStmt
        or ExitStmt or ReturnStmt or ResumeStmt or OnErrorStmt or EndStmt or RestoreStmt or EventControlStmt:
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

  private void BindDimInScope(DimStmt dim, Scope scope) {
    if (scope.Proc == null)
      return; // module DIMs were declared in pass 1

    foreach (var v in dim.Variables) {
      foreach (var (lower, upper) in v.ArrayBounds ?? []) {
        if (lower != null)
          this.BindExpression(lower, scope);
        this.BindExpression(upper, scope);
      }

      var key = VariableKey(v.Name, v.Suffix);
      switch (dim.Storage) {
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
    var key = VariableKey(v.Name, v.Suffix);
    if (scope.Proc != null)
      scope.Proc.Variables[key] = symbol;
    else
      this._model.ModuleVariables[key] = symbol;
  }

  private void BindCallStatement(CallStmt c, Scope scope) {
    if (this._model.Procedures.TryGetValue(c.Name, out var proc) && !proc.IsFunction) {
      this._model.CallBindings[c] = proc;
      if (c.Arguments.Count != proc.Parameters.Count && !proc.Parameters.Any(p => Equals(p.Type, PbType.Any)))
        this.Error(c.Position, $"SUB {c.Name} expects {proc.Parameters.Count} argument(s), got {c.Arguments.Count}");
      foreach (var argument in c.Arguments)
        this.BindExpression(argument, scope);
      return;
    }

    this.Error(c.Position, $"unknown SUB {c.Name}");
    foreach (var argument in c.Arguments)
      this.BindExpression(argument, scope);
  }

  private void CheckAssignable(PbType target, PbType value, SourcePosition position) {
    var targetIsString = target is StringType or FixedStringType or FlexType;
    var valueIsString = value is StringType or FixedStringType or FlexType;
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
    if (target is not (NameExpr or CallOrIndexExpr or MemberExpr)) {
      this.Error(target.Position, "expression is not assignable");
      return PbType.Integer;
    }
    return this.BindExpression(target, scope);
  }

  private PbType BindExpressionCore(Expression expression, Scope scope) {
    switch (expression) {
      case IntegerLiteralExpr i:
        return i.Suffix switch {
          TypeSuffix.Long => PbType.Long,
          TypeSuffix.Integer => PbType.Integer,
          _ => i.Value is >= short.MinValue and <= short.MaxValue ? PbType.Integer : PbType.Long,
        };

      case FloatLiteralExpr f:
        return f.Suffix switch {
          TypeSuffix.Double => PbType.Double,
          TypeSuffix.Ext => PbType.Ext,
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
          : value.Integer != null ? PbType.Long
          : PbType.Ext;
      }

      case NameExpr n: {
        var symbol = this.ResolveVariable(n.Name, n.Suffix, scope, create: true, n.Position);
        this._model.VariableBindings[n] = symbol!;
        return symbol!.Type is ArrayType arr ? arr : symbol.Type;
      }

      case FileNumberExpr fn:
        this.BindExpression(fn.Number, scope);
        return PbType.Integer;

      case MemberExpr m: {
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

      case UnaryExpr u: {
        var operand = this.BindExpression(u.Operand, scope);
        if (operand is not ScalarType)
          this.Error(u.Position, "unary operator needs a numeric operand");
        return u.Op == UnaryOp.Not ? IntegralOf(operand) : operand;
      }

      case BinaryExpr b:
        return this.BindBinary(b, scope);

      case CallOrIndexExpr call:
        return this.BindCallOrIndex(call, scope);

      default:
        this.Error(expression.Position, $"expression {expression.GetType().Name} not yet supported");
        return PbType.Integer;
    }
  }

  private PbType BindBinary(BinaryExpr b, Scope scope) {
    var left = this.BindExpression(b.Left, scope);
    var right = this.BindExpression(b.Right, scope);

    var leftString = left is StringType or FixedStringType or FlexType;
    var rightString = right is StringType or FixedStringType or FlexType;

    if (leftString || rightString) {
      if (!leftString || !rightString) {
        this.Error(b.Position, "type mismatch: cannot mix string and numeric");
        return PbType.Integer;
      }
      return b.Op switch {
        BinaryOp.Add => PbType.String,
        BinaryOp.Equal or BinaryOp.NotEqual or BinaryOp.Less or BinaryOp.Greater or BinaryOp.LessEqual or BinaryOp.GreaterEqual => PbType.Integer,
        _ => this.ErrorType(b.Position, "operator not defined for strings"),
      };
    }

    return b.Op switch {
      BinaryOp.Divide => Widest(left, right) is ScalarType { IsFloat: true } f ? f : PbType.Ext,
      BinaryOp.Power => PbType.Ext,
      BinaryOp.IntegerDivide or BinaryOp.Modulo => IntegralOf(Widest(left, right)),
      BinaryOp.And or BinaryOp.Or or BinaryOp.Xor or BinaryOp.Eqv or BinaryOp.Imp => IntegralOf(Widest(left, right)),
      BinaryOp.Equal or BinaryOp.NotEqual or BinaryOp.Less or BinaryOp.Greater or BinaryOp.LessEqual or BinaryOp.GreaterEqual => PbType.Integer,
      _ => Widest(left, right),
    };
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

  private PbType BindCallOrIndex(CallOrIndexExpr call, Scope scope) {
    var key = VariableKey(call.Name, call.Suffix);

    // 1. array indexing
    var symbol = this.LookupVariable(key, scope);
    if (symbol is { Type: ArrayType array }) {
      this._model.VariableBindings[call] = symbol;
      if (array.Rank != call.Arguments.Count && array.StaticBounds != null)
        this.Error(call.Position, $"array {call.Name} has rank {array.Rank}, got {call.Arguments.Count} index(es)");
      foreach (var index in call.Arguments)
        this.BindExpression(index, scope);
      return array.Element;
    }

    // 2. intrinsic
    var lookupName = call.Suffix == TypeSuffix.String ? call.Name + "$" : call.Name;
    if (Intrinsics.TryGet(lookupName, out var intrinsic) || Intrinsics.TryGet(call.Name, out intrinsic)) {
      this._model.IntrinsicBindings[call] = intrinsic;
      if (call.Arguments.Count < intrinsic.MinArgs || call.Arguments.Count > intrinsic.MaxArgs)
        this.Error(call.Position, $"{intrinsic.Name} expects {intrinsic.MinArgs}..{intrinsic.MaxArgs} argument(s), got {call.Arguments.Count}");
      PbType? firstArg = null;
      foreach (var argument in call.Arguments) {
        var t = this.BindExpression(argument, scope);
        firstArg ??= t;
      }
      return intrinsic.Returns switch {
        IntrinsicReturn.Integer => PbType.Integer,
        IntrinsicReturn.Word => PbType.Word,
        IntrinsicReturn.Dword => PbType.Dword,
        IntrinsicReturn.Long => PbType.Long,
        IntrinsicReturn.Single => PbType.Single,
        IntrinsicReturn.Double => PbType.Double,
        IntrinsicReturn.Ext => PbType.Ext,
        IntrinsicReturn.String => PbType.String,
        _ => firstArg as ScalarType ?? PbType.Ext,
      };
    }

    // 3. user function
    if (this._model.Procedures.TryGetValue(call.Name, out var proc)) {
      if (!proc.IsFunction) {
        this.Error(call.Position, $"SUB {call.Name} used as a function");
        return PbType.Integer;
      }
      this._model.CallBindings[call] = proc;
      if (call.Arguments.Count != proc.Parameters.Count)
        this.Error(call.Position, $"FUNCTION {call.Name} expects {proc.Parameters.Count} argument(s), got {call.Arguments.Count}");
      foreach (var argument in call.Arguments)
        this.BindExpression(argument, scope);
      return proc.ReturnType ?? PbType.Integer;
    }

    foreach (var argument in call.Arguments)
      this.BindExpression(argument, scope);
    return this.ErrorType(call.Position, $"unknown array or function {call.Name}");
  }

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
    var key = VariableKey(name, suffix);
    var found = this.LookupVariable(key, scope);

    // a suffixed reference may also hit an AS-declared variable of the matching type
    if (found == null && suffix != TypeSuffix.None) {
      var bare = this.LookupVariable(name, scope);
      if (bare != null && Equals(bare.Type, this.TypeFromSuffixOrDefault(name, suffix)))
        found = bare;
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
