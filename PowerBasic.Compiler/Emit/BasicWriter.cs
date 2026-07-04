using System.Text;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Emit;

/// <summary>
/// Renders a bound program back to readable, PB 3.5-compatible PowerBASIC source - a "back-emitter"
/// that turns any dialect back into source the pb35 front end accepts and that runs identically.
/// <para>
/// Declarations and procedure signatures are taken from the original <see cref="CompilationUnit"/>
/// (faithful and complete - the binder routes TYPE/DECLARE/SUB out of the executable body); the
/// executable statements come from the bound <see cref="SemanticModel"/>, which is the post-splice
/// surface tree carrying the binder's own pb36-to-pb35 lowering in its side-tables
/// (<see cref="SemanticModel.Desugared"/>, <see cref="SemanticModel.DesugaredStatements"/>,
/// <see cref="SemanticModel.RewrittenIndex"/>, <see cref="SemanticModel.ResolvedConstants"/>,
/// <see cref="SemanticModel.ReorderedArguments"/>). Consulting those maps emits the desugared core
/// form, so a pb36 program is rendered in constructs pb35 understands. Any node not yet modelled
/// degrades to a <c>' [unsupported: ...]</c> comment rather than being dropped, so the output is
/// always complete.
/// </para>
/// </summary>
public sealed class BasicWriter {

  private readonly StringBuilder _sb = new();
  private readonly SemanticModel _model;
  private readonly bool _singleFloatRuntime;
  private int _indent;
  private IReadOnlyDictionary<string, VariableSymbol>? _scopeVars;   // current scope's locals (a proc) for inferred-type lookup
  private Dictionary<string, string>? _nameRemap;                    // in-scope name rewrites (a renamed overload's result variable)

  /// <summary>The bound variable symbol for a name in the current scope (proc locals first, then module vars); arrays are keyed with a "()" suffix, so both keys are probed.</summary>
  private VariableSymbol? ScopeSymbol(string name)
    => this._scopeVars is { } v && (v.TryGetValue(name, out var s) || v.TryGetValue(name + "()", out s)) ? s
      : this._model.ModuleVariables.TryGetValue(name, out var m) || this._model.ModuleVariables.TryGetValue(name + "()", out m) ? m : null;

  /// <summary>
  /// Maps the compiler's internal name characters ($ namespace, @ generic, . member-mangle) to a
  /// pb35-valid identifier (letters/digits/underscore, letter-leading). A trailing $/@ is a
  /// type-suffix-style marker that is part of the name (STR$, HEX$, a string variable Foo$), so it is
  /// preserved - only $/@/. used as a prefix or infix (the synthesized names) are rewritten.
  /// </summary>
  private static string Id(string name) {
    var tail = name.Length > 0 && name[^1] is '$' or '@' ? name[^1].ToString() : "";
    var core = name[..(name.Length - tail.Length)];
    if (core.IndexOfAny(['$', '@', '.']) < 0)
      return name;
    var s = core.Replace('$', '_').Replace('@', '_').Replace('.', '_');
    return (char.IsLetter(s[0]) ? s : "S" + s) + tail;   // pb35 identifiers must start with a letter
  }

  // O25 pure-function folding: a foldable constant-argument call mapped to its computed result. When
  // supplied (the optimized decompilation), the call is emitted as the folded literal, so the source
  // shows what the optimizer yields (PRINT Cube(4) -> PRINT 64).
  private readonly IReadOnlyDictionary<CallOrIndexExpr, ConstantValue>? _folds;

  private BasicWriter(SemanticModel model, IReadOnlyDictionary<CallOrIndexExpr, ConstantValue>? folds) {
    this._model = model;
    this._folds = folds;
    // The QB/PDS/TB families evaluate SINGLE-typed float expressions in single precision throughout;
    // pb35 keeps double/extended intermediates. When transpiling those dialects, narrow SINGLE-typed
    // observable values (CSNG) so the pb35 recompile prints the same precision. The PB family matches
    // pb35 already, so it gets no coercion (keeping pb35/pb36 round-trips exact).
    this._singleFloatRuntime = model.Dialect.Family() == DialectFamily.Microsoft || model.Dialect.IsTurboBasic();
  }

  /// <summary>
  /// Un-parses the whole program (declarations, main body, procedures) to PB 3.5 source. Pass
  /// <paramref name="folds"/> (from <c>OptPureFold.Analyze</c>) to show pure-function folding -
  /// foldable constant-argument calls are emitted as their computed literal.
  /// </summary>
  public static string Render(SemanticModel model, CompilationUnit unit, IReadOnlyDictionary<CallOrIndexExpr, ConstantValue>? folds = null) {
    var writer = new BasicWriter(model, folds);
    writer.EmitProgram(unit);
    return writer._sb.ToString();
  }

  private void EmitProgram(CompilationUnit unit) {
    // Emit a $COMPAT directive for every dialect whose runtime differs from pb35 (everything but the
    // pb35 family itself), so the pb35 recompile replicates that dialect's PRINT float formatting,
    // float-to-integer rounding, ^Z-on-close, 16-bit integer arithmetic and VAL radix wrapping - and
    // the executed output stays byte-identical. pb35/pb36 are the identity target, so they emit nothing.
    if (this._model.Dialect is not (Dialect.Pb35 or Dialect.Pb36))
      this.Line($"$COMPAT {this._model.Dialect.CanonicalName()}");

    // Declarations come from the surface unit (faithful), executable code from the bound model.
    // A name may have several definitions (pb36 overloading), so the decls are queued per name and
    // dequeued in source order - each overload's emitted body gets its own faithful signature.
    var procDecls = new Dictionary<string, Queue<Statement>>(StringComparer.OrdinalIgnoreCase);
    void Record(string name, Statement decl) => (procDecls.TryGetValue(name, out var q) ? q : procDecls[name] = new()).Enqueue(decl);
    var emittedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var statement in unit.Statements)
      switch (statement) {
        case TypeDecl { TypeParameters.Count: > 0 }: break;   // generic template - the monomorphized instances are emitted from model.Udts below
        // A layout-transformed TYPE (bit-fields, ALIGN, SIZE, explicit AT) does not match its surface
        // fields under pb35: emit the resolved layout (packed $bits words, padding for the offsets)
        // so member access binds and LEN() agrees. Falls back to the surface form if it cannot be
        // expressed sequentially (overlapping AT offsets need union semantics).
        case TypeDecl t when IsLayoutTransformed(t) && this._model.Udts.TryGetValue(t.Name, out var rt) && this.WriteUdtTypeWithLayout(rt): emittedTypes.Add(t.Name); break;
        case TypeDecl t: this.WriteTypeDecl(t); emittedTypes.Add(t.Name); break;
        case UnionDecl u: this.WriteUnionDecl(u); emittedTypes.Add(u.Name); break;
        case EnumDecl e: this.WriteEnumDecl(e); break;
        case EquateStmt eq: this.WriteEquate(eq); break;   // %equates: folded out of MainBody, re-emit here
        case DefTypeStmt dt: this.WriteDefType(dt); break;                              // DEFINT/DEFQUD/...: consumed by the binder, re-emit here (affects default typing)
        case DeclareStmt d: this.WriteDeclare(d); break;
        case SubDecl s: Record(s.Name, s); break;
        case FunctionDecl f: Record(f.Name, f); break;
        case DefFnDecl df: Record(df.Name, df); break;
        default: break;   // executable / DIM / meta come from model.MainBody below
      }

    // Types the binder synthesized (monomorphized generics, coroutine enumerators, nullable/tuple
    // UDTs) have no surface declaration; emit them from the resolved type table so DIMs and lifted
    // bodies that reference them compile. Their fields include compiler backing fields ($state, ...).
    foreach (var (name, udt) in this._model.Udts)
      if (emittedTypes.Add(name))
        this.WriteUdtType(udt);

    // Synthesized UDT-typed locals (a FOR EACH enumerator $foreach1 : Squares) are created implicitly
    // by the binder with no surface DIM; declare them so member accesses bind to a real instance.
    this.EmitSynthesizedDims(this._model.ModuleVariables);

    foreach (var statement in this._model.MainBody)
      this.WriteStatement(statement);

    foreach (var proc in this._model.ProcedureList) {
      if (proc.IsExternal)
        continue;
      var decl = procDecls.TryGetValue(proc.Name, out var q) && q.Count > 0 ? q.Dequeue() : null;
      if (decl is DefFnDecl df) {
        this.WriteDefFn(df);
        continue;
      }
      this.WriteProcedure(proc, decl);
    }

    // Delegate thunks discovered while emitting the bodies (a function called through a code pointer
    // is wrapped in a SUB with a trailing BYREF result, so it goes through pb35's CALL DWORD).
    for (var i = 0; i < this._thunkTargets.Count; i++)
      this.WriteThunk(this._thunkTargets[i]);
  }

  /// <summary>Emits a thunk SUB that adapts a function to the BYREF-result calling convention CALL DWORD uses.</summary>
  private void WriteThunk(ProcedureSymbol target) {
    var thunk = this._thunks[target.Name];
    var ps = target.Parameters.Take(target.VisibleParameterCount).ToList();
    var pars = ps.Select((p, i) => $"Sp{i} AS {TypeText(p.Type)}").ToList();
    var args = string.Join(", ", ps.Select((_, i) => $"Sp{i}"));
    this._sb.Append('\n');
    if (target.ReturnType is { } ret) {
      pars.Add($"Sresult AS {TypeText(ret)}");
      this.Line($"SUB {thunk}({string.Join(", ", pars)})");
      ++this._indent;
      this.Line($"Sresult = {Id(target.Name)}({args})");
      --this._indent;
    } else {
      this.Line($"SUB {thunk}({string.Join(", ", pars)})");
      ++this._indent;
      this.Line(args.Length > 0 ? $"{Id(target.Name)} {args}" : Id(target.Name));
      --this._indent;
    }
    this.Line("END SUB");
  }

  // ---- declarations -----------------------------------------------------------------------------

  private void WriteTypeDecl(TypeDecl t) {
    this._sb.Append('\n');
    this.Line($"TYPE {t.Name}");
    ++this._indent;
    foreach (var f in t.Fields)
      this.Line(this.FormatTypeField(f));
    --this._indent;
    this.Line("END TYPE");
  }

  /// <summary>Declares synthesized (compiler-named) UDT-typed locals that have no surface DIM, so they bind to a real instance under pb35.</summary>
  private void EmitSynthesizedDims(IEnumerable<KeyValuePair<string, VariableSymbol>> vars) {
    foreach (var (name, sym) in vars)
      if (name.IndexOfAny(['$', '@', '.']) >= 0 && sym.Storage != VariableStorage.Parameter && sym.Type is UdtType u)
        this.Line($"DIM {Id(name)} AS {Id(u.Name)}");
  }

  /// <summary>Emits a resolved (synthesized or monomorphized) UDT as a pb35 TYPE block, with sanitized field names.</summary>
  private void WriteUdtType(UdtType u) {
    this._sb.Append('\n');
    this.Line($"{(u.IsUnion ? "UNION" : "TYPE")} {Id(u.Name)}");
    ++this._indent;
    foreach (var fld in u.Fields)
      this.Line($"{Id(fld.Name)}{(fld.ElementCount > 1 ? $"({fld.ElementCount - 1})" : "")} AS {TypeText(fld.Type)}");
    --this._indent;
    this.Line(u.IsUnion ? "END UNION" : "END TYPE");
  }

  /// <summary>A TYPE whose pb35 layout differs from its surface fields: bit-fields, ALIGN/SIZE padding, or explicit AT offsets.</summary>
  private static bool IsLayoutTransformed(TypeDecl t)
    => t.Alignment > 0 || t.ExplicitSize != null || t.Fields.Any(f => f.BitWidth > 0 || f.ExplicitOffset != null);

  // AT-overlay member paths: (udtName, member) -> the rewritten access path through the union view
  // (v.lo -> v.Sv1.lo), filled when an overlapping layout is emitted as a UNION of view TYPEs.
  private readonly Dictionary<(string Udt, string Member), string> _memberPaths = new();

  /// <summary>
  /// Emits a layout-transformed TYPE as its RESOLVED layout. Non-overlapping fields are placed by
  /// their resolved byte offset with explicit <c>STRING * n</c> padding for the gaps (bit-field
  /// containers, ALIGN padding, non-overlapping AT). OVERLAPPING fields (an AT-overlay view) emit a
  /// UNION whose branches are helper view TYPEs - each branch a maximal non-overlapping run, padded
  /// to its offsets - and member accesses are rewritten through the view (<c>v.lo</c> becomes
  /// <c>v.Sv1.lo</c>), reproducing the exact byte layout under pb35.
  /// </summary>
  private bool WriteUdtTypeWithLayout(UdtType u) {
    var fields = u.Fields.OrderBy(f => f.Offset).ToList();
    var overlaps = false;
    for (var i = 1; i < fields.Count && !overlaps; ++i)
      overlaps = fields[i].Offset < fields[i - 1].Offset + fields[i - 1].TotalSize;
    if (!overlaps) {
      this.EmitSequentialLayout(Id(u.Name), fields, u.Size);
      return true;
    }

    // partition the fields into non-overlapping branches (greedy: first branch that has room)
    var branches = new List<(List<UdtField> Fields, int End)>();
    foreach (var fld in fields) {
      var idx = branches.FindIndex(b => fld.Offset >= b.End);
      if (idx < 0) {
        branches.Add(([fld], fld.Offset + fld.TotalSize));
      } else {
        branches[idx].Fields.Add(fld);
        branches[idx] = (branches[idx].Fields, fld.Offset + fld.TotalSize);
      }
    }

    // each branch becomes a view TYPE; the union then holds one member per view
    var unionMembers = new List<string>();
    for (var b = 0; b < branches.Count; ++b) {
      var view = $"{Id(u.Name)}__v{b + 1}";
      var member = $"Sv{b + 1}";
      this.EmitSequentialLayout(view, branches[b].Fields, branches[b].End);
      unionMembers.Add($"{member} AS {view}");
      foreach (var fld in branches[b].Fields)
        this._memberPaths[(u.Name, fld.Name)] = $"{member}.{Id(fld.Name)}";
    }

    this._sb.Append('\n');
    this.Line($"UNION {Id(u.Name)}");
    ++this._indent;
    foreach (var member in unionMembers)
      this.Line(member);
    --this._indent;
    this.Line("END UNION");
    return true;
  }

  /// <summary>Emits one sequential TYPE with STRING*n padding so each field lands at its resolved offset.</summary>
  private void EmitSequentialLayout(string name, IReadOnlyList<UdtField> fields, int totalSize) {
    var cursor = 0; var pad = 0;
    var lines = new List<string>();
    foreach (var fld in fields) {
      if (fld.Offset > cursor)
        lines.Add($"Spad{++pad} AS STRING * {fld.Offset - cursor}");
      lines.Add($"{Id(fld.Name)}{(fld.ElementCount > 1 ? $"({fld.ElementCount - 1})" : "")} AS {TypeText(fld.Type)}");
      cursor = fld.Offset + fld.TotalSize;
    }
    if (totalSize > cursor)
      lines.Add($"Spad{++pad} AS STRING * {totalSize - cursor}");

    this._sb.Append('\n');
    this.Line($"TYPE {name}");
    ++this._indent;
    foreach (var line in lines)
      this.Line(line);
    --this._indent;
    this.Line("END TYPE");
  }

  private void WriteUnionDecl(UnionDecl u) {
    this._sb.Append('\n');
    this.Line($"UNION {u.Name}");
    ++this._indent;
    foreach (var f in u.Fields)
      this.Line(this.FormatTypeField(f));
    --this._indent;
    this.Line("END UNION");
  }

  private string FormatTypeField(TypeField f) {
    var bounds = f.ArrayBounds is { Count: > 0 } ? "(" + this.FormatBounds(f.ArrayBounds) + ")" : "";
    return $"{f.Name}{bounds} AS {this.TypeNameText(f.Type)}";
  }

  // ENUM has no pb35 equivalent; the binder folds member references to literals (ResolvedConstants)
  // and the enum name to an integer type (EnumTypes), so the equates below are documentary only. They
  // are prefixed with the enum name (%Color_Red, not %Red) so two enums that share a member name do
  // not collide as duplicate equates under pb35.
  private void WriteEnumDecl(EnumDecl e) {
    this._sb.Append('\n');
    this.Line($"' ENUM {e.Name} (folded to integer constants below)");
    foreach (var (name, _) in e.Members)
      if (this._model.EnumMembers.TryGetValue(name, out var value))
        this.Line($"%{e.Name}_{name} = {value}");
  }

  private void WriteEquate(EquateStmt eq) {
    // Emit the binder's FOLDED value rather than re-parsing the expression: it is the constant the
    // source dialect actually used, so it reproduces dialect-specific constant-folding quirks (PB 3.0
    // folds %K = -20-4 to -16, not -24) and is exact under the pb35 recompile.
    if (this._model.Equates.TryGetValue(eq.Name, out var value)) {
      var text = value.Text is { } s ? "\"" + s.Replace("\"", "\"\"") + "\""
        : value.Float is { } f ? FormatFloat(f)
        : value.Integer is { } i ? i.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : this.Expr(eq.Value);
      this.Line($"%{eq.Name} = {text}");
      return;
    }
    this.Line($"%{eq.Name} = {this.Expr(eq.Value)}");
  }

  private void WriteDeclare(DeclareStmt d) {
    var ret = d.ReturnType is { } rt ? $" AS {this.TypeNameText(rt)}" : "";
    var pars = d.Parameters is { } ps ? "(" + string.Join(", ", ps.Select(this.FormatParam)) + ")" : "";
    this.Line($"DECLARE {(d.IsFunction ? "FUNCTION" : "SUB")} {d.Name}{Suffix(d.Suffix)}{pars}{ret}");
  }

  private void WriteDefFn(DefFnDecl df) {
    var pars = df.Parameters is { Count: > 0 } ? "(" + string.Join(", ", df.Parameters.Select(this.FormatParam)) + ")" : "";
    var name = df.Name.StartsWith("FN", StringComparison.OrdinalIgnoreCase) ? df.Name : "FN" + df.Name;   // the FN prefix is part of the name
    if (df.Body is { } expr) {
      this.Line($"DEF {name}{pars} = {this.Expr(expr)}");
      return;
    }
    this._sb.Append('\n');
    this.Line($"DEF {name}{pars}");
    ++this._indent;
    foreach (var s in df.BlockBody ?? [])
      this.WriteStatement(s);
    --this._indent;
    this.Line("END DEF");
  }

  private void WriteProcedure(ProcedureSymbol proc, Statement? decl) {
    this._sb.Append('\n');
    var name = this.OverloadName(proc);   // overloaded definitions get distinct pb35 names

    // A function returning a UDT/tuple by value is lowered to a SUB taking the result buffer as a
    // trailing BYREF parameter (pb35 has no by-value UDT return); the call site already passes the
    // buffer. The body assigns the result through the function name, so remap that to the buffer.
    string? sretRemap = null;
    string kind, header;
    if (proc.HasSretParam) {
      var sret = proc.Parameters[^1];
      sretRemap = Id(sret.Name);
      kind = "SUB";
      header = $"SUB {name}({string.Join(", ", proc.Parameters.Select(this.FormatParamSymbol))})";
    } else {
      kind = proc.IsFunction ? "FUNCTION" : "SUB";
      header = decl switch {
        SubDecl s => $"SUB {name}({string.Join(", ", s.Parameters.Select(this.FormatParam))})",
        FunctionDecl f => $"FUNCTION {name}{Suffix(f.Suffix)}({string.Join(", ", f.Parameters.Select(this.FormatParam))})" + (f.ReturnType is { } rt ? $" AS {this.TypeNameText(rt)}" : ""),
        _ => this.HeaderFromSymbol(proc),   // synthesized procs (lambdas, generics, lifted members) have no unit decl
      };
    }
    this.Line(header);
    ++this._indent;
    var savedScope = this._scopeVars;
    var savedRemap = this._nameRemap;
    this._scopeVars = proc.Variables;
    this.EmitSynthesizedDims(proc.Variables);   // synthesized UDT-typed locals need an explicit pb35 DIM
    // The function result is assigned in the body through the function name (or, for a lifted TYPE
    // member, through its ResultName - e.g. a PROPERTY GET assigns "Size"; or, for an sret function,
    // through the result buffer). When the emitted name differs (sanitized "." names, renamed
    // overloads, the sret buffer), remap those result references so the store hits the right target.
    this._nameRemap = null;
    Dictionary<string, string> remap = new(StringComparer.OrdinalIgnoreCase);
    var resultTarget = sretRemap ?? name;
    if (resultTarget != proc.Name) remap[proc.Name] = resultTarget;
    if (proc.ResultName is { } rn && rn != resultTarget) remap[rn] = resultTarget;
    if (remap.Count > 0 && (proc.IsFunction || proc.HasSretParam)) this._nameRemap = remap;
    var savedOnError = this._currentOnError;
    this._currentOnError = "0";   // error handlers are per-procedure - a module-level trap is not in scope here
    foreach (var statement in proc.Body!)
      this.WriteStatement(statement);
    this._currentOnError = savedOnError;
    this._nameRemap = savedRemap;
    this._scopeVars = savedScope;
    --this._indent;
    this.Line($"END {kind}");
  }

  private string HeaderFromSymbol(ProcedureSymbol proc) {
    var visible = proc.VisibleParameterCount;
    var pars = string.Join(", ", proc.Parameters.Take(visible).Select(this.FormatParamSymbol));
    var ret = proc.IsFunction && proc.ReturnType is { } rt ? $" AS {TypeText(rt)}" : "";
    return $"{(proc.IsFunction ? "FUNCTION" : "SUB")} {Id(proc.Name)}({pars}){ret}";   // lifted members / lambdas have $/. names
  }

  // Passing mode is always written explicitly (BYVAL / BYREF / SEG) so the emitted code is
  // self-documenting - the reader never has to remember that the default is by-reference.
  private string FormatParam(Parameter p) {
    var prefix = p.ByVal ? "BYVAL " : p.Seg ? "SEG " : "BYREF ";
    var arr = p.IsArray ? "()" : "";
    return p.Type is { } t
      ? $"{prefix}{p.Name}{arr} AS {this.TypeNameText(t)}"
      : $"{prefix}{p.Name}{Suffix(p.Suffix)}{arr}";
  }

  private string FormatParamSymbol(VariableSymbol p) {
    var prefix = p.ByVal ? "BYVAL " : p.Seg ? "SEG " : "BYREF ";
    return p.Type is ArrayType a
      ? $"{prefix}{Id(p.Name)}() AS {TypeText(a.Element)}"
      : $"{prefix}{Id(p.Name)} AS {TypeText(p.Type)}";
  }

  // ---- statements -------------------------------------------------------------------------------

  private readonly Dictionary<Expression, string> _exprSubst = new(ReferenceEqualityComparer.Instance);
  private int _tempCounter;
  // the lexically current ON ERROR target ("0" = none): TRY reconstructions restore THIS handler on
  // their exit edges (mirroring the codegen, which saves and re-arms the handler active at TRY entry)
  // so a re-raise after FINALLY still reaches a previously armed trap instead of turning fatal.
  private string _currentOnError = "0";
  // code-pointer / delegate thunks: a function called through a pointer is wrapped in a SUB that takes
  // its arguments and a trailing BYREF result, so the call can go through pb35's CALL DWORD (which has
  // no function-result form). Keyed by target procedure name; emitted after the procedures.
  private readonly Dictionary<string, string> _thunks = new(StringComparer.OrdinalIgnoreCase);
  private readonly List<ProcedureSymbol> _thunkTargets = [];

  private void WriteStatement(Statement statement) {
    // statement-level desugar (member-call statement, property-set assignment): emit the core form
    if (this._model.DesugaredStatements.TryGetValue(statement, out var lowered)) {
      this.WriteStatement(lowered);
      return;
    }
    // Value-position constructs with no pb35 expression form (a ternary IfExpr, a function called
    // through a code pointer) are hoisted to a temp computed before this statement, then substituted
    // at their use site.
    var hoisted = this.HoistComplex(statement);
    if (hoisted is { Count: > 0 }) {
      this.WriteStatementCore(statement);
      foreach (var h in hoisted)
        this._exprSubst.Remove(h);
      return;
    }
    this.WriteStatementCore(statement);
  }

  /// <summary>Hoists value-position ternaries and code-pointer calls out of a statement's own expressions; returns the substituted nodes (to clear afterward).</summary>
  private List<Expression>? HoistComplex(Statement statement) {
    List<Expression>? top = null;
    foreach (var e in DirectExpressions(statement))
      this.CollectHoistable(e, ref top);
    if (top is null)
      return null;
    foreach (var node in top) {
      var temp = "pbtmp" + (++this._tempCounter);
      if (IsSegmentedPeek(node)) {
        // PEEK(seg:offset) has no inline pb35 form: set DEF SEG = seg before the statement and render
        // the read as a plain PEEK(offset). Substitutes in place (no result temp needed).
        var peek = (CallOrIndexExpr)node;
        this.Line($"DEF SEG = {this.Expr(peek.Arguments[0])}");
        this._exprSubst[node] = $"{peek.Name}{Suffix(peek.Suffix)}({this.Expr(peek.Arguments[1])})";
        continue;
      }
      if (node is IfExpr ife) {
        this.Line($"DIM {temp} AS {TypeText(this._model.TypeOf(ife))}");
        this.Line($"IF {this.Expr(ife.Condition)} THEN");
        ++this._indent; this.Line($"{temp} = {this.Expr(ife.WhenTrue)}"); --this._indent;
        this.Line("ELSE");
        ++this._indent; this.Line($"{temp} = {this.Expr(ife.WhenFalse)}"); --this._indent;
        this.Line("END IF");
      } else {   // a code-pointer call: materialize args into BYREF temps and a result temp, CALL DWORD
        var call = (CallOrIndexExpr)node;
        var sig = this._model.ProcPtrCalls[call];
        var argTemps = new List<string>();
        for (var i = 0; i < call.Arguments.Count; i++) {
          var at = "pbtmp" + (++this._tempCounter);
          this.Line($"DIM {at} AS {TypeText(i < sig.ParameterTypes.Count ? sig.ParameterTypes[i] : PbType.Long)}");
          this.Line($"{at} = {this.Expr(call.Arguments[i])}");
          argTemps.Add(at);
        }
        this.Line($"DIM {temp} AS {TypeText(sig.ReturnType ?? PbType.Long)}");
        argTemps.Add(temp);
        this.Line($"CALL DWORD ({Id(call.Name)})({string.Join(", ", argTemps)})");   // result written into the trailing BYREF temp
      }
      this._exprSubst[node] = temp;
    }
    return top;
  }

  /// <summary>Collects the topmost hoistable nodes (a ternary IfExpr, a code-pointer call) - not descending into a ternary's branches (preserving short-circuit).</summary>
  private void CollectHoistable(Expression e, ref List<Expression>? acc) {
    var r = this._model.Desugared.TryGetValue(e, out var d) ? d
      : this._model.RewrittenIndex.TryGetValue(e, out var w) ? w : e;
    if (r is IfExpr || (r is CallOrIndexExpr && this._model.ProcPtrCalls.ContainsKey(r)) || IsSegmentedPeek(r)) {
      (acc ??= []).Add(r);
      return;   // a leaf to hoist; its branches/args are emitted at hoist time
    }
    foreach (var c in AstQuery.Subexpressions(r))
      this.CollectHoistable(c, ref acc);
  }

  /// <summary>A segmented PEEK[I|L](seg, offset) call, which the back-emitter hoists to DEF SEG = seg + PEEK(offset).</summary>
  private static bool IsSegmentedPeek(Expression e)
    => e is CallOrIndexExpr { Arguments.Count: 2 } c && c.Name.ToUpperInvariant() is "PEEK" or "PEEKI" or "PEEKL";

  /// <summary>Returns the thunk SUB name wrapping a procedure as a BYREF-result delegate target, creating it on first use.</summary>
  private string GetThunk(ProcedureSymbol target) {
    if (this._thunks.TryGetValue(target.Name, out var existing))
      return existing;
    var name = $"Sthunk{this._thunks.Count + 1}";
    this._thunks[target.Name] = name;
    this._thunkTargets.Add(target);
    return name;
  }

  /// <summary>The user function a CODEPTR32(...) delegate value points at (so it can be wrapped in a thunk), else null.</summary>
  private ProcedureSymbol? DelegateTarget(CallOrIndexExpr codeptr) {
    if (codeptr.Arguments is not [var arg])
      return null;
    var name = arg switch { NameExpr n => n.Name, CallOrIndexExpr c => c.Name, _ => null };
    return name is not null && this._model.Procedures.TryGetValue(name, out var p) && p.IsFunction ? p : null;
  }

  /// <summary>The expressions a statement evaluates at its own level (not inside nested blocks).</summary>
  private static IEnumerable<Expression> DirectExpressions(Statement s) => s switch {
    AssignStmt a => [a.Value],
    PrintStmt p => p.Items.Where(i => i.Value is not null).Select(i => i.Value!),
    WriteStmt w => w.Items,
    CallStmt c => c.Arguments,
    IncrDecrStmt d when d.Amount is { } amt => [amt],
    ReturnStmt or GotoStmt or LabelStmt => [],
    _ => [],
  };

  private void WriteStatementCore(Statement statement) {
    switch (statement) {
      case AssignStmt s: this.Line($"{this.Expr(s.Target)} = {this.Expr(s.Value)}"); break;
      case IncrDecrStmt s: this.Line($"{(s.Increment ? "INCR" : "DECR")} {this.Expr(s.Target)}{(s.Amount is { } a ? ", " + this.Expr(a) : "")}"); break;
      case DimStmt s: this.WriteDim(s); break;
      case RedimStmt s: this.Line($"REDIM {(s.Preserve ? "PRESERVE " : "")}{string.Join(", ", s.Variables.Select(this.FormatVarDecl))}"); break;
      case EraseStmt s: this.Line($"ERASE {string.Join(", ", s.Arrays.Select(a => this.Expr(a)))}"); break;
      case EquateStmt: break;   // emitted from the unit's declaration pass (folded equates aren't in MainBody)
      case CallStmt s: this.WriteCall(s); break;
      case MemberCallStmt s: this.Line($"{this.Expr(s.Receiver)}.{s.Member}({this.JoinArgs(s.Arguments)})"); break;
      case CallPtrStmt s: this.Line($"CALL DWORD ({this.Expr(s.Pointer)}){(s.Convention is { } c ? " " + c : "")}({this.JoinArgs(s.Arguments)})"); break;   // parenthesize the pointer so (ptr)(args) parses
      case PrintStmt s: this.WritePrint(s); break;
      case WriteStmt s: this.Line($"WRITE {FilesPrefix(s.FileNumber, this)}{string.Join(", ", s.Items.Select(this.CoerceFloat))}"); break;
      case InputStmt s: this.WriteInput(s); break;
      case OpenStmt s: this.WriteOpen(s); break;
      case CloseStmt s: this.Line(s.FileNumbers.Count == 0 ? "CLOSE" : $"CLOSE {string.Join(", ", s.FileNumbers.Select(this.FileRef))}"); break;
      case GetPutFileStmt s: this.Line($"{(s.IsGet ? "GET" : "PUT")} {this.FileRef(s.FileNumber)}{(s.RecordNumber is { } gr ? ", " + this.Expr(gr) : "")}{(s.Variable is { } gv ? ", " + this.Expr(gv) : "")}"); break;
      case SeekStmt s: this.Line($"SEEK {this.FileRef(s.FileNumber)}, {this.Expr(s.Target)}"); break;
      case FieldStmt s: this.Line($"FIELD {this.FileRef(s.FileNumber)}, {string.Join(", ", s.Fields.Select(f => $"{this.Expr(f.Width)} AS {this.Expr(f.Target)}"))}"); break;
      case SwapStmt s: this.Line($"SWAP {this.Expr(s.Left)}, {this.Expr(s.Right)}"); break;
      case LsetRsetStmt s: this.Line($"{(s.IsLeft ? "LSET" : "RSET")} {this.Expr(s.Target)} = {this.Expr(s.Value)}"); break;
      case MidAssignStmt s: this.Line($"MID$({this.Expr(s.Target)}, {this.Expr(s.Start)}{(s.Length is { } l ? ", " + this.Expr(l) : "")}) = {this.Expr(s.Value)}"); break;
      case AscAssignStmt s: this.Line($"ASC({this.Expr(s.Target)}{(s.Index is { } ai ? ", " + this.Expr(ai) : "")}) = {this.Expr(s.Value)}"); break;
      case ReplaceStmt s: this.Line($"REPLACE {this.Expr(s.Find)} WITH {this.Expr(s.With)} IN {this.Expr(s.Target)}"); break;
      case BitStmt s: this.Line($"BIT {s.Op.ToString().ToUpperInvariant()} {this.Expr(s.Target)}, {this.Expr(s.Bit)}"); break;
      case StdOutStmt s: this.Line($"STDOUT{(s.Value is { } ov ? " " + this.Expr(ov) : "")}{(s.NoNewline ? ";" : "")}"); break;
      case StdInStmt s: this.Line($"STDIN {(s.Line ? "LINE" : this.Expr(s.Count!))}, {this.Expr(s.Target)}"); break;
      case ArraySortStmt s: this.WriteArraySort(s); break;
      case ArrayScanStmt s: this.WriteArrayScan(s); break;
      case LabelStmt s: this.LineNoIndent($"{Id(s.Name)}:"); break;
      case GotoStmt s: this.Line($"GOTO {Id(s.Target)}"); break;
      case GosubStmt s: this.Line($"GOSUB {Id(s.Target)}"); break;
      case GotoPtrStmt s: this.Line($"GOTO DWORD {this.Expr(s.Pointer)}"); break;
      case GosubPtrStmt s: this.Line($"GOSUB DWORD {this.Expr(s.Pointer)}"); break;
      case ReturnStmt s: this.Line(s.Target is { } rt ? $"RETURN {Id(rt)}" : "RETURN"); break;
      case OnGotoStmt s: this.Line($"ON {this.Expr(s.Selector)} {(s.IsGosub ? "GOSUB" : "GOTO")} {string.Join(", ", s.Targets.Select(Id))}"); break;
      case ChainStmt s: this.Line($"{(s.IsRun ? "RUN" : "CHAIN")} {this.Expr(s.Target)}"); break;
      case ExitStmt s: this.Line($"EXIT {s.Kind.ToString().ToUpperInvariant()}"); break;
      case ExitFarStmt s: this.Line($"EXIT FAR{(s.AtLabel is { } xl ? " AT " + xl : "")}"); break;
      // bare ITERATE (Kind = Loop, innermost loop of ANY kind) must stay bare: "ITERATE LOOP"
      // would re-parse as the DO form and rebind to the wrong loop inside a FOR
      case IterateStmt s: this.Line(s.Kind switch { ExitKind.For => "ITERATE FOR", ExitKind.Do => "ITERATE DO", _ => "ITERATE" }); break;
      case EndStmt s: this.Line(s.ExitCode is { } ec ? $"END {this.Expr(ec)}" : "END"); break;
      case YieldStmt s: this.Line($"YIELD {this.Expr(s.Value)}"); break;
      case DataStmt s: this.Line($"DATA {string.Join(", ", s.Items)}"); break;
      case ReadStmt s: this.Line($"READ {string.Join(", ", s.Targets.Select(t => this.Expr(t)))}"); break;
      case RestoreStmt s: this.Line(s.Target is { } t ? $"RESTORE {t}" : "RESTORE"); break;
      case OnErrorStmt s:
        this.Line(s.ResumeNext ? "ON ERROR RESUME NEXT" : $"ON ERROR GOTO {s.Target ?? "0"}");   // null target = disable (GOTO 0)
        if (!s.ResumeNext)
          this._currentOnError = s.Target ?? "0";
        break;
      case ResumeStmt s: this.Line("RESUME" + (s.Kind switch { ResumeKind.Next => " NEXT", ResumeKind.Label => " " + s.Target, _ => "" })); break;
      case ErrorStmt s: this.Line($"ERROR {this.Expr(s.Code)}"); break;
      case OnEventStmt s: this.Line($"ON {s.EventKind}{(s.Index is { } oi ? $"({this.Expr(oi)})" : "")} GOSUB {s.Target}"); break;
      case EventControlStmt s: this.Line($"{s.EventKind}{(s.Index is { } vi ? $"({this.Expr(vi)})" : "")} {s.Mode}"); break;
      case DefSegStmt s: this.Line(s.Segment is { } seg ? $"DEF SEG = {this.Expr(seg)}" : "DEF SEG"); break;
      case InlineAsmStmt s: this.Line($"! {s.Text}"); break;
      case CommandStmt s: this.WriteCommand(s); break;
      case LineStmt s: this.WriteLine(s); break;
      case CircleStmt s: this.WriteCircle(s); break;
      case PsetStmt s: this.Line($"{(s.IsPreset ? "PRESET" : "PSET")} ({this.Expr(s.Point.X)}, {this.Expr(s.Point.Y)}){(s.Color is { } pc ? ", " + this.Expr(pc) : "")}"); break;
      case GetPutGraphicsStmt s: this.WriteGetPutGraphics(s); break;
      case IfStmt s: this.WriteIf(s); break;
      case ForStmt s: this.WriteFor(s); break;
      case ForEachStmt s: this.WriteForEach(s); break;
      case DoLoopStmt s: this.WriteDoLoop(s); break;
      case SelectStmt s: this.WriteSelect(s); break;
      case TryStmt s: this.WriteTry(s); break;
      case DestructureStmt s: this.Line($"{string.Join(", ", s.Targets.Select(t => this.Expr(t)))} = {this.Expr(s.Value)}"); break;
      case DeferStmt s: this.Line("' DEFER:"); this.WriteStatement(s.Deferred); break;
      case MetaStmt s: this.WriteMeta(s); break;
      case StaticAssertStmt: break;   // checked at bind time - nothing to emit, pb35 never sees it
      case RequireStmt rq:
        // pb36 contract: checked builds raise error 5 (message printed first); the codegen
        // compiles the check out under $OPTIMIZE SPEED, the decompile always shows it
        this.Line(rq.Message is { Length: > 0 } cmsg
          ? $"IF NOT ({this.Expr(rq.Condition)}) THEN PRINT {Quote(cmsg)} : ERROR 5"
          : $"IF NOT ({this.Expr(rq.Condition)}) THEN ERROR 5");
        break;
      case ResourceStmt res: this.WriteResource(res); break;
      case TypeDecl or UnionDecl or TypeAliasDecl or EnumDecl or DeclareStmt or SubDecl or FunctionDecl or DefFnDecl or DefTypeStmt: break; // emitted from the unit declaration pass (a type alias is resolved away entirely)
      case HandlerSaveStmt or HandlerRestoreStmt or HandlerArmStmt or HandlerReraiseStmt: break;            // synthesized coroutine plumbing
      default: this.Line($"' [unsupported: {statement.GetType().Name}]"); break;
    }
  }

  private void WriteCall(CallStmt s) {
    // a first-class delegate invocation in statement position lowers like the expression form:
    // BYVAL arg temps + CALL DWORD through the pointer (plus a discarded result temp for a FUNCTION
    // delegate, whose thunk takes the trailing BYREF result).
    if (this._model.ProcPtrStatementCalls.TryGetValue(s, out var invoke)) {
      var sig = this._model.ProcPtrCalls[invoke];
      var argTemps = new List<string>();
      for (var i = 0; i < invoke.Arguments.Count; i++) {
        var at = "pbtmp" + (++this._tempCounter);
        this.Line($"DIM {at} AS {TypeText(i < sig.ParameterTypes.Count ? sig.ParameterTypes[i] : PbType.Long)}");
        this.Line($"{at} = {this.Expr(invoke.Arguments[i])}");
        argTemps.Add(at);
      }
      if (sig.ReturnType is { } ret) {
        var rt = "pbtmp" + (++this._tempCounter);
        this.Line($"DIM {rt} AS {TypeText(ret)}");
        argTemps.Add(rt);
      }
      this.Line($"CALL DWORD ({Id(s.Name)})({string.Join(", ", argTemps)})");
      return;
    }

    var args = this.CallArguments(s, s.Arguments);
    var name = this.CallName(s, s.Name);
    this.Line(s.UsedCallKeyword ? $"CALL {name}({this.JoinExprs(args)})" : args.Count == 0 ? name : $"{name} {this.JoinExprs(args)}");
  }

  /// <summary>True when several SUB/FUNCTION definitions share <paramref name="name"/> (pb36 overloading).</summary>
  private bool IsOverloaded(string name) => this._model.Overloads.TryGetValue(name, out var set) && set.Count > 1;

  /// <summary>The distinct pb35 name for an overload: the primary keeps its name, later overloads get a __N suffix.</summary>
  private string OverloadName(ProcedureSymbol p) => p.OverloadIndex > 0 && this.IsOverloaded(p.Name) ? $"{Id(p.Name)}__{p.OverloadIndex}" : Id(p.Name);

  /// <summary>
  /// The name to emit at a call site. When the call resolves to a procedure whose emitted name differs
  /// from the surface name - a renamed overload, a sanitized lifted member (Counter.Bump), or a nested
  /// SUB called unqualified (Bump -> Outer_Bump) - use the resolved name so it matches the definition.
  /// Otherwise keep the (sanitized) surface name with its type suffix (intrinsics, arrays, plain calls).
  /// </summary>
  private string CallName(object callSite, string bareName, TypeSuffix suffix = TypeSuffix.None) {
    if (this._model.CallBindings.TryGetValue(callSite, out var p) && this.OverloadName(p) is var resolved && resolved != bareName)
      return resolved;
    return Id(bareName) + Suffix(suffix);
  }

  private void WriteDim(DimStmt s) {
    var keyword = s.Storage switch {
      StorageClass.Local => "LOCAL", StorageClass.Static => "STATIC", StorageClass.Public => "PUBLIC",
      StorageClass.Common => s.CommonBlock is { } b ? $"COMMON /{b}/" : "COMMON", StorageClass.Shared => "SHARED", _ => "DIM",
    };
    var shared = s.Storage == StorageClass.Dim && s.SharedFlag ? " SHARED" : "";
    var cls = s.Class switch { ArrayClass.Dynamic => " DYNAMIC", ArrayClass.Huge => " HUGE", ArrayClass.Virtual => " VIRTUAL", _ => "" };
    this.Line($"{keyword}{shared}{cls} {string.Join(", ", s.Variables.Select(this.FormatVarDecl))}");
  }

  private string FormatVarDecl(VariableDecl v) {
    // A pb36 DIM-with-initializer (DIM x = v / DIM x AS T = v / NEW object / { array } literal) lowers
    // to a plain declaration plus the binder's spliced assignment(s), which follow this DIM in the
    // stream. Emit just the declaration (with the inferred type from the bound symbol when none was
    // written, and array bounds from the symbol), so the result is compilable PB 3.5.
    if (v.Initializer is not null) {
      if (this.ScopeSymbol(v.Name)?.Type is ArrayType arr && arr.StaticBounds is { } sb)
        return $"{v.Name}{Suffix(v.Suffix)}({string.Join(", ", sb.Select(b => b.Lower == 0 ? b.Upper.ToString() : $"{b.Lower} TO {b.Upper}"))}) AS {TypeText(arr.Element)}";
      // an array-initializer literal ({ v1, v2, lo TO hi }) is spliced to per-element stores at indices
      // 0..N-1, so size the array to its element count.
      if (v.Initializer is ArrayLiteralExpr lit && this.CountLiteralElements(lit) is { } n) {
        var et = v.Type is { } at ? $" AS {this.TypeNameText(at)}" : "";
        return $"{v.Name}{Suffix(v.Suffix)}({n - 1}){et}";
      }
      var declared = v.Type is { } t ? this.TypeNameText(t) : this.ScopeSymbol(v.Name) is { } s ? TypeText(s.Type) : null;
      return $"{v.Name}{Suffix(v.Suffix)}{(declared is null ? "" : " AS " + declared)}";
    }
    var bounds = v.ArrayBounds is { Count: > 0 } ? "(" + this.FormatBounds(v.ArrayBounds) + ")" : "";
    // Prefer the bound symbol's resolved type so a generic use (Box OF LONG) names its monomorphized
    // type and a proc-pointer / named delegate becomes a DWORD - which the surface TypeName cannot.
    var type = this.ScopeSymbol(v.Name)?.Type switch {
      UdtType udt => $" AS {Id(udt.Name)}",
      ProcPtrType => " AS DWORD",
      _ => v.Type is { } t2 ? $" AS {this.TypeNameText(t2)}" : "",
    };
    return $"{v.Name}{Suffix(v.Suffix)}{bounds}{type}";
  }

  private string FormatBounds(IReadOnlyList<(Expression? Lower, Expression Upper)> bounds)
    => string.Join(", ", bounds.Select(b => b.Lower is { } lo ? $"{this.Expr(lo)} TO {this.Expr(b.Upper)}" : this.Expr(b.Upper)));

  /// <summary>Compile-time element count of an array-initializer literal (values count 1, an integer range hi-lo+1); null if a non-constant range or spread makes it unknowable.</summary>
  private int? CountLiteralElements(ArrayLiteralExpr lit) {
    var n = 0;
    foreach (var el in lit.Elements)
      switch (el) {
        case ValueElement: n++; break;
        case RangeElement r when this.ConstInt(r.Lo) is { } lo && this.ConstInt(r.Hi) is { } hi: n += (int)(hi - lo + 1); break;
        default: return null;
      }
    return n;
  }

  /// <summary>A compile-time integer value of an expression (a literal or a folded constant), else null.</summary>
  private long? ConstInt(Expression e)
    => this._model.ResolvedConstants.TryGetValue(e, out var c) ? c
      : e is IntegerLiteralExpr i ? i.Value
      : e is UnaryExpr { Op: UnaryOp.Negate, Operand: IntegerLiteralExpr n } ? -n.Value : null;

  private void WritePrint(PrintStmt s) {
    var sb = new StringBuilder((s.IsLPrint ? "LPRINT " : "PRINT ") + FilesPrefix(s.FileNumber, this));
    if (s.UsingFormat is { } u)
      sb.Append("USING ").Append(this.Expr(u)).Append("; ");
    foreach (var item in s.Items) {
      if (item.Value is { } v)
        sb.Append(this.CoerceFloat(v));
      sb.Append(item.Separator switch { PrintSeparator.Comma => ", ", PrintSeparator.Semicolon => "; ", _ => "" });
    }
    this.Line(sb.ToString().TrimEnd());
  }

  private void WriteInput(InputStmt s) {
    var kw = s.IsLineInput ? "LINE INPUT " : "INPUT ";
    var file = FilesPrefix(s.FileNumber, this);
    var prompt = s.Prompt is { } p ? $"\"{p}\"{(s.PromptSemicolon ? "; " : ", ")}" : "";
    this.Line($"{kw}{file}{prompt}{string.Join(", ", s.Targets.Select(t => this.Expr(t)))}");
  }

  private void WriteOpen(OpenStmt s) {
    var mode = s.Mode.ToString().ToUpperInvariant();
    var access = s.Access is { } a ? $" ACCESS {a}" : "";
    var lck = s.Lock is { } l ? $" {l}" : "";
    var len = s.RecordLength is { } r ? $" LEN = {this.Expr(r)}" : "";
    this.Line($"OPEN {this.Expr(s.FileName)} FOR {mode}{access}{lck} AS {this.FileRef(s.FileNumber)}{len}");
  }

  private void WriteArraySort(ArraySortStmt s) {
    var sb = new StringBuilder($"ARRAY SORT {this.Expr(s.Array)}");
    if (s.Count is { } c) sb.Append(" FOR ").Append(this.Expr(c));
    if (s.FromPos is { } f) sb.Append(", FROM ").Append(this.Expr(f)).Append(" TO ").Append(this.Expr(s.ToPos!));
    if (s.Collate is { } col) sb.Append(", COLLATE ").Append(this.Expr(col));
    sb.Append(s.Descend ? ", DESCEND" : "");
    if (s.TagArray is { } tag) sb.Append(", TAGARRAY ").Append(this.Expr(tag));
    this.Line(sb.ToString());
  }

  private void WriteArrayScan(ArrayScanStmt s) {
    var sb = new StringBuilder($"ARRAY SCAN {this.Expr(s.Array)}");
    if (s.Count is { } c) sb.Append(" FOR ").Append(this.Expr(c));
    if (s.FromPos is { } f) sb.Append(", FROM ").Append(this.Expr(f)).Append(" TO ").Append(this.Expr(s.ToPos!));
    if (s.Collate is { } col) sb.Append(", COLLATE ").Append(this.Expr(col));
    sb.Append(", ").Append(ComparisonText(s.Op)).Append(' ').Append(this.Expr(s.Match)).Append(", TO ").Append(this.Expr(s.Target));
    this.Line(sb.ToString());
  }

  private void WriteCommand(CommandStmt s) {
    // Segmented POKE (3-arg [seg, offset, value]) lowers to the classic DEF SEG = seg : POKE off, val
    // pair - exactly its runtime semantics - so it round-trips to pb35.
    if (s.Keyword is "POKE" or "POKEI" or "POKEL" && s.Arguments is [{ } seg, { } off, { } val]) {
      this.Line($"DEF SEG = {this.Expr(seg)}");
      this.Line($"{s.Keyword} {this.Expr(off)}, {this.Expr(val)}");
      return;
    }
    var args = s.Arguments.Select(a => a is null ? "" : this.Expr(a));
    var joined = string.Join(", ", args);
    this.Line(joined.Length == 0 ? s.Keyword : $"{s.Keyword} {joined}");
  }

  private void WriteLine(LineStmt s) {
    var from = s.From is { } f ? $"({this.Expr(f.X)}, {this.Expr(f.Y)})" : "";
    var box = s.Box ? (s.Fill ? ", BF" : ", B") : "";
    var color = s.Color is { } c ? $", {this.Expr(c)}" : (s.Box ? ", " : "");
    this.Line($"LINE {from}-({this.Expr(s.To.X)}, {this.Expr(s.To.Y)}){color}{box}");
  }

  private void WriteCircle(CircleStmt s) {
    var sb = new StringBuilder($"CIRCLE ({this.Expr(s.Center.X)}, {this.Expr(s.Center.Y)}), {this.Expr(s.Radius)}");
    if (s.Color is { } c) sb.Append(", ").Append(this.Expr(c));
    if (s.Start is { } st) sb.Append(", ").Append(this.Expr(st));
    if (s.End is { } en) sb.Append(", ").Append(this.Expr(en));
    if (s.Aspect is { } asp) sb.Append(", ").Append(this.Expr(asp));
    this.Line(sb.ToString());
  }

  private void WriteGetPutGraphics(GetPutGraphicsStmt s) {
    var from = $"({this.Expr(s.From.X)}, {this.Expr(s.From.Y)})";
    var to = s.To is { } t ? $"-({this.Expr(t.X)}, {this.Expr(t.Y)})" : "";
    var verb = s.Verb is { } v ? $", {v}" : "";
    this.Line($"{(s.IsGet ? "GET" : "PUT")} {from}{to}, {this.Expr(s.Array)}{verb}");
  }

  private void WriteMeta(MetaStmt s) {
    var args = string.Join(" ", s.Arguments.Select(t => t.Text));
    this.Line($"${s.Command}{(args.Length > 0 ? " " + args : "")}");
  }

  private void WriteDefType(DefTypeStmt s) {
    var kw = s.Type switch {
      BuiltinType.Integer => "DEFINT", BuiltinType.Long => "DEFLNG", BuiltinType.Single => "DEFSNG",
      BuiltinType.Double => "DEFDBL", BuiltinType.Ext => "DEFEXT", BuiltinType.String => "DEFSTR",
      BuiltinType.Quad => "DEFQUD", _ => "DEFINT",
    };
    this.Line($"{kw} {string.Join(", ", s.Ranges.Select(r => r.From == r.To ? r.From.ToString() : $"{r.From}-{r.To}"))}");
  }

  private void WriteIf(IfStmt s) {
    this.Line($"IF {this.Expr(s.Condition)} THEN");
    this.Block(s.Then);
    foreach (var (cond, body) in s.ElseIfs) {
      this.Line($"ELSEIF {this.Expr(cond)} THEN");
      this.Block(body);
    }
    if (s.Else is { } els) {
      this.Line("ELSE");
      this.Block(els);
    }
    this.Line("END IF");
  }

  private void WriteFor(ForStmt s) {
    var step = s.Step is { } st ? $" STEP {this.Expr(st)}" : "";
    this.Line($"FOR {this.Expr(s.Variable)} = {this.Expr(s.From)} TO {this.Expr(s.To)}{step}");
    this.Block(s.Body);
    this.Line("NEXT");
  }

  private void WriteForEach(ForEachStmt s) {
    this.Line($"FOR EACH {this.Expr(s.Variable)} IN {this.Expr(s.Collection)}");
    this.Block(s.Body);
    this.Line("NEXT");
  }

  private void WriteDoLoop(DoLoopStmt s) {
    this.Line("DO" + Test(s.PreTest, s.PreCondition));
    this.Block(s.Body);
    this.Line("LOOP" + Test(s.PostTest, s.PostCondition));

    string Test(LoopTestKind kind, Expression? cond) => kind switch {
      LoopTestKind.While => $" WHILE {this.Expr(cond!)}",
      LoopTestKind.Until => $" UNTIL {this.Expr(cond!)}",
      _ => "",
    };
  }

  private void WriteSelect(SelectStmt s) {
    this.Line($"SELECT CASE {this.Expr(s.Subject)}");
    ++this._indent;
    foreach (var arm in s.Arms) {
      this.Line(arm.Selectors.Count == 0 ? "CASE ELSE" : $"CASE {string.Join(", ", arm.Selectors.Select(this.FormatSelector))}");
      this.Block(arm.Body);
    }
    --this._indent;
    this.Line("END SELECT");
  }

  /// <summary>pb36 $RESOURCE: pb35 gets the same bytes as DIM + READ loop over labeled DATA lines.</summary>
  private void WriteResource(ResourceStmt res) {
    var key = res.Name + "()";
    if (!this._model.ModuleVariables.TryGetValue(key, out var symbol) || !this._model.ResourceData.TryGetValue(symbol, out var bytes))
      return;   // binder already reported the unreadable file
    var id = ++this._tempCounter;
    var dataLabel = $"Sres{id}";
    var counter = $"Sresi{id}%";
    this.Line($"DIM {res.Name}(0 TO {bytes.Length - 1}) AS BYTE");
    this.Line($"RESTORE {dataLabel}");
    this.Line($"FOR {counter} = 0 TO {bytes.Length - 1}");
    this.Line($"  READ {res.Name}({counter})");
    this.Line("NEXT");
    this.LineNoIndent($"{dataLabel}:");
    for (var i = 0; i < bytes.Length; i += 16)
      this.Line("DATA " + string.Join(", ", bytes.Skip(i).Take(16)));
  }

  private static string Quote(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";

  private void WriteTry(TryStmt s) {
    // Lower TRY/CATCH/FINALLY onto pb35 ON ERROR (the same machinery the binder uses, no RESUME):
    // a fault in the body jumps to the catch label with ERR set; FINALLY runs on both the normal and
    // the caught path. (A no-CATCH TRY runs body then finally; full fault-propagation of an uncaught
    // error is beyond what a source-level reconstruction expresses, so this covers the CATCH form.)
    var id = ++this._tempCounter;
    var catchLabel = $"Strycatch{id}";
    var finallyLabel = $"Stryfinally{id}";
    // The exit edges disarm with ON ERROR GOTO 0 (which leaves ERR readable - arming a real target
    // clears the error cell), and the handler lexically armed at TRY entry is re-armed once at the
    // shared exit, mirroring the codegen's restore of the entry handler. "0" when none - then the
    // re-arm is omitted and an uncaught re-raise is fatal exactly like the compiled form.
    var previous = this._currentOnError;
    if (s.Catch is { } c) {
      this.Line($"ON ERROR GOTO {catchLabel}");
      this._currentOnError = catchLabel;
      this.Block(s.Body);
      this.Line("ON ERROR GOTO 0");
      this.Line($"GOTO {finallyLabel}");
      this.LineNoIndent($"{catchLabel}:");
      this.Line("ON ERROR GOTO 0");   // disarm only - the catch body still reads the faulting ERR
      this._currentOnError = "0";
      this.Block(c);
      this.LineNoIndent($"{finallyLabel}:");
      if (s.Finally is { } f1)
        this.Block(f1);
      if (previous != "0")
        this.Line($"ON ERROR GOTO {previous}");
    } else if (s.Finally is { } fin) {
      // no CATCH but a FINALLY (the DEFER shape): the cleanup must run on the fault path too, so arm
      // a handler over the body. The FINALLY body is emitted ONCE and shared by both edges via GOTO;
      // the fault edge saves ERR into a variable FIRST (the FINALLY statements may raise/handle
      // errors of their own and change ERR) and a nonzero saved code is re-raised after the cleanup,
      // reaching the re-armed previous handler.
      var fid = ++this._tempCounter;
      var faultLabel = $"Stryfault{fid}";
      var finLabel = $"Stryfin{fid}";
      var errVar = $"Stryerr{fid}%";
      this.Line($"{errVar} = 0");
      this.Line($"ON ERROR GOTO {faultLabel}");
      this._currentOnError = faultLabel;
      this.Block(s.Body);
      this.Line("ON ERROR GOTO 0");
      this.Line($"GOTO {finLabel}");
      this.LineNoIndent($"{faultLabel}:");
      this.Line($"{errVar} = ERR");
      this.Line("ON ERROR GOTO 0");
      this._currentOnError = "0";
      this.LineNoIndent($"{finLabel}:");
      this.Block(fin);
      if (previous != "0")
        this.Line($"ON ERROR GOTO {previous}");
      this.Line($"IF {errVar} <> 0 THEN ERROR {errVar}");
    } else {
      this.Block(s.Body);
    }
    this._currentOnError = previous;
  }

  private string FormatSelector(CaseSelector c) {
    if (c.IsComparison is { } cmp)
      return $"IS {ComparisonText(cmp)} {this.Expr(c.Value!)}";
    if (c.RangeUpper is { } hi)
      return $"{this.Expr(c.Value!)} TO {this.Expr(hi)}";
    return this.Expr(c.Value!);
  }

  // ---- expressions ------------------------------------------------------------------------------

  private string Expr(Expression e, int parentPrec = 0) {
    // binder lowerings: emit the desugared / rewritten / constant-folded core form pb35 understands
    if (this._model.Desugared.TryGetValue(e, out var desugared))
      return this.Expr(desugared, parentPrec);
    if (this._model.RewrittenIndex.TryGetValue(e, out var rewritten))
      return this.Expr(rewritten, parentPrec);
    // a value-position ternary hoisted to a temp (see HoistConditionals): emit the temp's name
    if (this._exprSubst.TryGetValue(e, out var temp))
      return temp;
    if (this._model.ResolvedConstants.TryGetValue(e, out var constant))
      return constant.ToString(System.Globalization.CultureInfo.InvariantCulture);

    return e switch {
      IntegerLiteralExpr x => this.FormatInt(x),
      FloatLiteralExpr x => FormatFloat(x.Value) + Suffix(x.Suffix),
      StringLiteralExpr x => "\"" + x.Value.Replace("\"", "\"\"") + "\"",
      NamedConstantExpr x => "%" + x.Name,
      NameExpr x => (this._nameRemap is { } m && m.TryGetValue(x.Name, out var rn) ? rn : Id(x.Name)) + Suffix(x.Suffix),   // remap a renamed overload's result; Id() maps synthesized locals
      // O25 pure-function folding: a foldable constant-argument call becomes its computed literal
      CallOrIndexExpr x when this._folds is { } folds && folds.TryGetValue(x, out var folded) => FormatConstant(folded),
      // CODEPTR32(func) used as a delegate value points at a BYREF-result thunk wrapping the function
      CallOrIndexExpr x when x.Name.Equals("CODEPTR32", StringComparison.OrdinalIgnoreCase) && this.DelegateTarget(x) is { } tgt => $"CODEPTR32({this.GetThunk(tgt)})",
      CallOrIndexExpr x => $"{this.CallName(x, x.Name, x.Suffix)}({this.JoinExprs(this.CallArguments(x, x.Arguments))})",
      // an AT-overlay member routes through its union view (v.lo -> v.Sv1.lo)
      MemberExpr x when this.LoweredType(x.Target) is UdtType mu && this._memberPaths.TryGetValue((mu.Name, x.Member), out var path) => $"{this.Expr(x.Target, 99)}.{path}{Suffix(x.Suffix)}",
      MemberExpr x => $"{this.Expr(x.Target, 99)}.{Id(x.Member)}{Suffix(x.Suffix)}",   // synthesized backing fields ($Current/$state) -> pb35-valid
      IndexExpr x => $"{this.Expr(x.Target, 99)}({this.JoinArgs(x.Arguments)})",
      PtrDerefExpr x => $"@{this.Expr(x.Pointer, 99)}{(x.Index is { } i ? $"[{this.Expr(i)}]" : "")}",
      ByValArgExpr x => $"BYVAL {this.Expr(x.Value)}",
      AnyMatchExpr x => $"ANY {this.Expr(x.Value)}",
      FromEndExpr x => $"^{this.Expr(x.Index)}",
      FileNumberExpr x => this.Expr(x.Number),
      NothingExpr => "NOTHING",
      TupleExpr x => $"({this.JoinArgs(x.Elements)})",
      CoalesceExpr x => Paren(parentPrec, 0, $"{this.Expr(x.Value, 1)} ?? {this.Expr(x.Fallback, 0)}"),
      IfExpr x => $"IIF({this.Expr(x.Condition)}, {this.Expr(x.WhenTrue)}, {this.Expr(x.WhenFalse)})",
      NewExpr x => $"{x.TypeName}({string.Join(", ", x.Fields.Select(f => $"{f.Field} := {this.Expr(f.Value)}"))})",
      NamedArgExpr x => $"{x.Name} := {this.Expr(x.Value)}",
      // an inline lambda lifts to a top-level FUNCTION; its delegate value is a thunk's code pointer
      LambdaExpr x when this._model.LambdaProcs.TryGetValue(x, out var lifted) => $"CODEPTR32({this.GetThunk(lifted)})",
      LambdaExpr x => $"FUNCTION({string.Join(", ", x.Parameters.Select(this.FormatParam))})" + (x.ReturnType is { } rt ? $" AS {this.TypeNameText(rt)}" : "") + $" => {this.Expr(x.Body)}",
      ArrayLiteralExpr x => "{" + string.Join(", ", x.Elements.Select(this.FormatElement)) + "}",
      InterpolatedStringExpr x => this.FormatInterpolation(x),
      UnaryExpr x => this.Unary(x, parentPrec),
      BinaryExpr x => this.Binary(x, parentPrec),
      _ => $"/* {e.GetType().Name} */",
    };
  }

  private string FormatElement(CollectionElement el) => el switch {
    ValueElement v => this.Expr(v.Value),
    RangeElement r => $"{this.Expr(r.Lo)} TO {this.Expr(r.Hi)}",
    SpreadElement { IsSlice: true } s => $"..{this.Expr(s.Source)}({(s.SliceLo is { } lo ? this.Expr(lo) : "")} TO {(s.SliceHi is { } hi ? this.Expr(hi) : "")})",
    SpreadElement s => $"..{this.Expr(s.Source)}",
    _ => "",
  };

  private string FormatInterpolation(InterpolatedStringExpr x) {
    var sb = new StringBuilder("$\"");
    foreach (var p in x.Parts)
      if (p.Literal is { } lit)
        sb.Append(lit.Replace("\"", "\"\""));
      else
        sb.Append('{').Append(this.Expr(p.Hole!)).Append(p.Format is { } f ? ":" + f : "").Append('}');
    return sb.Append('"').ToString();
  }

  private string Unary(UnaryExpr x, int parentPrec) {
    var op = x.Op == UnaryOp.Negate ? "-" : "NOT ";
    var prec = x.Op == UnaryOp.Negate ? 9 : 2;
    return Paren(parentPrec, prec, op + this.Expr(x.Operand, prec));
  }

  private string Binary(BinaryExpr x, int parentPrec) {
    // Scaled pointer arithmetic ptr +* i / ptr -* i has no pb35 operator; lower it to the equivalent
    // unscaled byte arithmetic ptr +/- i * sizeof(target), the same scaling @p[i] uses.
    if (x.Op is BinaryOp.PointerAdd or BinaryOp.PointerSub && this.LoweredType(x.Left) is PointerType pt) {
      var op = x.Op == BinaryOp.PointerAdd ? "+" : "-";
      return Paren(parentPrec, 4, $"{this.Expr(x.Left, 4)} {op} {this.Expr(x.Right, 8)} * {pt.Target.Size}");
    }
    // A constant-amount logical shift has no pb35 operator usable in expression position (SHL/SHR
    // parse only inside PRINT), so lower it to the equivalent multiply/integer-divide by 2^k - exact
    // for the unsigned/masked values bit-fields use, and recompilable everywhere. Arithmetic shift
    // and rotates have no such equivalent and keep their operator (illustrative only).
    if (x.Op is BinaryOp.ShiftLeft or BinaryOp.ShiftRightLogical && this.ConstInt(x.Right) is { } k && k is >= 0 and < 31) {
      var factor = 1L << (int)k;
      return x.Op == BinaryOp.ShiftLeft
        ? Paren(parentPrec, 7, $"{this.Expr(x.Left, 7)} * {factor}")
        : Paren(parentPrec, 6, $"{this.Expr(x.Left, 6)} \\ {factor}");
    }
    var prec = Precedence(x.Op);
    // left-associative: the left child at the same precedence needs no parens, the right child does
    var text = $"{this.Expr(x.Left, prec)} {OperatorText(x.Op)} {this.Expr(x.Right, prec + 1)}";
    return Paren(parentPrec, prec, text);
  }

  private static string Paren(int parentPrec, int prec, string text) => prec < parentPrec ? $"({text})" : text;

  /// <summary>
  /// Narrows a SINGLE-typed expression to single precision (CSNG) when the source dialect computes
  /// floats in single throughout (the QB/PDS/TB families) but pb35 would keep a double/extended
  /// intermediate. This reproduces, at the observable point (PRINT/WRITE), the source dialect's
  /// single-precision result - the math intrinsics' SINGLE return type and single-precision arithmetic
  /// (e.g. <c>SIN(1)^2+COS(1)^2</c> = 1 in QB, .9999999999999999 as a pb35 double). The PB family
  /// keeps pb35's exact behavior (no coercion), so pb35/pb36 round-trips are untouched.
  /// </summary>
  private string CoerceFloat(Expression e) {
    var text = this.Expr(e);
    // The QB/PDS/TB families compute SINGLE-typed float expressions in single precision throughout;
    // pb35 keeps a double/extended intermediate. CSNG forces single at the observable point so the
    // pb35 recompile prints the same value (SQR(2) -> 1.414214, SIN(1)^2+COS(1)^2 -> 1). DOUBLE-typed
    // values narrow via the binder's $COMPAT intrinsic typing (EffectiveDialect), not here.
    if (this._singleFloatRuntime && this.LoweredType(e) is ScalarType { Kind: ScalarKind.Single })
      return $"CSNG({text})";
    return text;
  }

  /// <summary>Bound type of an expression, following the binder's desugar/rewrite substitution the writer also emits.</summary>
  private PbType LoweredType(Expression e) {
    if (this._model.Desugared.TryGetValue(e, out var d))
      return this.LoweredType(d);
    if (this._model.RewrittenIndex.TryGetValue(e, out var r))
      return this.LoweredType(r);
    return this._model.TypeOf(e);
  }

  /// <summary>
  /// Call arguments, reordered to positional form when the binder recorded named-argument reordering,
  /// and with omitted trailing default parameters filled in from the resolved procedure's signature -
  /// pb35 has no defaults, so the call site must pass every argument explicitly.
  /// </summary>
  private IReadOnlyList<Expression> CallArguments(object callSite, IReadOnlyList<Expression> original) {
    var args = this._model.ReorderedArguments.TryGetValue(callSite, out var reordered) ? reordered : original;
    if (this._model.CallBindings.TryGetValue(callSite, out var proc) && args.Count < proc.VisibleParameterCount) {
      var filled = args.ToList();
      for (var i = args.Count; i < proc.VisibleParameterCount; i++)
        if (proc.Parameters[i].DefaultValue is { } d)
          filled.Add(d);
      return filled;
    }
    return args;
  }

  // ---- helpers ----------------------------------------------------------------------------------

  private string JoinArgs(IReadOnlyList<Expression> args) => string.Join(", ", args.Select(a => this.Expr(a)));
  private string JoinExprs(IReadOnlyList<Expression> args) => string.Join(", ", args.Select(a => this.Expr(a)));

  private static string FilesPrefix(Expression? fileNumber, BasicWriter w) => fileNumber is { } f ? $"{w.FileRef(f)}, " : "";

  /// <summary>A file number with its <c>#</c> sigil (the canonical PB form). Expr() renders a FileNumberExpr as its bare number, so a single sigil is always correct.</summary>
  private string FileRef(Expression fileNumber) => "#" + this.Expr(fileNumber);

  private void Block(IReadOnlyList<Statement> body) {
    ++this._indent;
    foreach (var s in body)
      this.WriteStatement(s);
    --this._indent;
  }

  private void Line(string text) => this._sb.Append(new string(' ', this._indent * 2)).Append(text).Append('\n');
  private void LineNoIndent(string text) => this._sb.Append(text).Append('\n');

  /// <summary>
  /// Renders an integer literal so re-parsing yields the same value AND keeps it integral:
  /// a magnitude beyond LONG gets a <c>&amp;&amp;</c> (QUAD) suffix so it is not promoted to a float,
  /// and a negative value at its type's boundary (e.g. INTEGER -32768, which cannot be written as
  /// <c>-(32768)</c>) is emitted as a two's-complement <c>&amp;H</c> hex pattern - the only way PB can
  /// spell it.
  /// </summary>
  private string FormatInt(IntegerLiteralExpr x) {
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    var v = x.Value;
    var suf = Suffix(x.Suffix);
    var bits = x.Suffix switch {
      TypeSuffix.Byte => 8, TypeSuffix.Integer or TypeSuffix.Word => 16,
      TypeSuffix.Long or TypeSuffix.Dword => 32, TypeSuffix.Quad => 64,
      _ => this._model.TypeOf(x) is ScalarType st ? st.ByteSize * 8 : 32,
    };
    if (x.Suffix == TypeSuffix.None && (v > int.MaxValue || v < int.MinValue)) {   // beyond LONG: force QUAD, else it re-parses as a float
      suf = "&&";
      bits = 64;
    }
    if (v >= 0)
      return v.ToString(inv) + suf;

    var signedMax = bits switch { 8 => 127L, 16 => 32767L, 32 => 2147483647L, _ => long.MaxValue };
    if (-v <= signedMax)
      return "-" + (-v).ToString(inv) + suf;

    // boundary negative (type MinValue): spell the two's-complement bit pattern as hex
    return bits switch {
      16 => "&H" + ((ushort)v).ToString("X4") + suf,
      32 => "&H" + ((uint)v).ToString("X8") + suf,
      _ => "&H" + ((ulong)v).ToString("X16") + (suf.Length > 0 ? suf : "&&"),
    };
  }

  private static string FormatFloat(double v) {
    var s = v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    return s.Contains('.') || s.Contains('E') || s.Contains('e') ? s : s + ".0";   // keep it a float literal
  }

  /// <summary>Renders a folded constant value as a pb35 literal (the O25 pure-fold result).</summary>
  private static string FormatConstant(ConstantValue c)
    => c.Text is { } t ? "\"" + t.Replace("\"", "\"\"") + "\""
      : c.Integer is { } i ? i.ToString(System.Globalization.CultureInfo.InvariantCulture)
      : FormatFloat(c.Float ?? 0);

  private static string Suffix(TypeSuffix s) => s switch {
    TypeSuffix.Integer => "%", TypeSuffix.Long => "&", TypeSuffix.Quad => "&&",
    TypeSuffix.Single => "!", TypeSuffix.Double => "#", TypeSuffix.Ext => "##",
    TypeSuffix.String => "$", TypeSuffix.Flex => "$$",
    TypeSuffix.Byte => "?", TypeSuffix.Word => "??", TypeSuffix.Dword => "???",
    TypeSuffix.Fix => "@", TypeSuffix.Bcd => "@@",
    _ => "",
  };

  private static string OperatorText(BinaryOp op) => op switch {
    BinaryOp.Add => "+", BinaryOp.Subtract => "-", BinaryOp.Multiply => "*", BinaryOp.Divide => "/",
    BinaryOp.IntegerDivide => "\\", BinaryOp.Modulo => "MOD", BinaryOp.Power => "^",
    BinaryOp.Equal => "=", BinaryOp.NotEqual => "<>", BinaryOp.Less => "<", BinaryOp.Greater => ">",
    BinaryOp.LessEqual => "<=", BinaryOp.GreaterEqual => ">=",
    BinaryOp.And => "AND", BinaryOp.Or => "OR", BinaryOp.Xor => "XOR", BinaryOp.Eqv => "EQV", BinaryOp.Imp => "IMP",
    BinaryOp.Concat => "+",   // PB 3.5 string concatenation is '+' (and works for numbers too); '&' is a long-suffix token
    BinaryOp.ShiftLeft => "SHL", BinaryOp.ShiftRightArith => "SHR", BinaryOp.ShiftRightLogical => "SHR",
    BinaryOp.RotateLeft => "ROL", BinaryOp.RotateRight => "ROR",
    BinaryOp.PointerAdd => "+*", BinaryOp.PointerSub => "-*",
    _ => "?",
  };

  // higher binds tighter; used for minimal parenthesization
  private static int Precedence(BinaryOp op) => op switch {
    BinaryOp.Power => 8,
    BinaryOp.Multiply or BinaryOp.Divide => 7,
    BinaryOp.IntegerDivide => 6,
    BinaryOp.Modulo => 5,
    BinaryOp.Add or BinaryOp.Subtract or BinaryOp.Concat
      or BinaryOp.ShiftLeft or BinaryOp.ShiftRightArith or BinaryOp.ShiftRightLogical
      or BinaryOp.RotateLeft or BinaryOp.RotateRight or BinaryOp.PointerAdd or BinaryOp.PointerSub => 4,
    BinaryOp.Equal or BinaryOp.NotEqual or BinaryOp.Less or BinaryOp.Greater
      or BinaryOp.LessEqual or BinaryOp.GreaterEqual => 3,
    BinaryOp.And => 2,
    BinaryOp.Or or BinaryOp.Xor => 1,
    _ => 0,   // Eqv, Imp
  };

  private static string ComparisonText(CaseComparison c) => c switch {
    CaseComparison.Equal => "=", CaseComparison.NotEqual => "<>", CaseComparison.Less => "<",
    CaseComparison.LessEqual => "<=", CaseComparison.Greater => ">", _ => ">=",
  };

  /// <summary>Renders an <c>AS</c>-clause type from the syntax tree (a <see cref="TypeName"/>).</summary>
  private string TypeNameText(TypeName t) {
    if (t.IsProcPtr)   // a typed procedure pointer / delegate is a 32-bit code pointer in pb35
      return "DWORD";
    if (t.IsPointer)
      return $"{this.TypeNameText(t.PointerTarget!)} PTR";
    if (t.UserTypeName is { } udt) {
      if (this._model.TypeAliases.TryGetValue(udt, out var aliased))
        return this.TypeNameText(aliased);   // pb36 type alias: substitute the underlying type (chains resolve recursively)
      return this._model.EnumTypes.ContainsKey(udt) ? "INTEGER" : udt;   // ENUM names alias an integer in pb35
    }
    if (t.Builtin == BuiltinType.FixedString && t.FixedLength is { } len)
      return $"STRING * {this.Expr(len)}";
    if (t.Builtin == BuiltinType.Asciiz && t.FixedLength is { } alen)
      return $"ASCIIZ * {this.Expr(alen)}";
    return BuiltinText(t.Builtin);
  }

  private static string BuiltinText(BuiltinType b) => b switch {
    BuiltinType.SByte => "INTEGER", BuiltinType.QWord => "QUAD",   // map pb36-only widths to nearest pb35 type
    BuiltinType.Int128 or BuiltinType.Int256 or BuiltinType.Int512 => "QUAD",
    BuiltinType.UInt128 or BuiltinType.UInt256 or BuiltinType.UInt512 => "QUAD",
    _ => b.ToString().ToUpperInvariant(),
  };

  private static string TypeText(PbType type) => type switch {
    ScalarType { Kind: ScalarKind.Integer } => "INTEGER", ScalarType { Kind: ScalarKind.Long } => "LONG",
    ScalarType { Kind: ScalarKind.Quad } => "QUAD", ScalarType { Kind: ScalarKind.Byte } => "BYTE",
    ScalarType { Kind: ScalarKind.Word } => "WORD", ScalarType { Kind: ScalarKind.Dword } => "DWORD",
    ScalarType { Kind: ScalarKind.Single } => "SINGLE", ScalarType { Kind: ScalarKind.Double } => "DOUBLE",
    ScalarType { Kind: ScalarKind.Ext } => "EXT", ScalarType { Kind: ScalarKind.SByte } => "INTEGER",
    ScalarType { Kind: ScalarKind.QWord } => "QUAD",
    StringType or FlexType => "STRING", FixedStringType f => $"STRING * {f.Length}",
    AsciizType a => $"ASCIIZ * {a.Length}",
    WideIntType => "QUAD",
    PointerType p => $"{TypeText(p.Target)} PTR",
    ProcPtrType => "DWORD",    // a code pointer / fat delegate is a 32-bit value in pb35
    UdtType u => Id(u.Name),   // monomorphized generics carry @ in the name (Box@Long)
    _ => "INTEGER",
  };
}
