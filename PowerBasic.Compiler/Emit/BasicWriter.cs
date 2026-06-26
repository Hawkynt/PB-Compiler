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
  private int _indent;

  private BasicWriter(SemanticModel model) => this._model = model;

  /// <summary>Un-parses the whole program (declarations, main body, procedures) to PB 3.5 source.</summary>
  public static string Render(SemanticModel model, CompilationUnit unit) {
    var writer = new BasicWriter(model);
    writer.EmitProgram(unit);
    return writer._sb.ToString();
  }

  private void EmitProgram(CompilationUnit unit) {
    // Declarations come from the surface unit (faithful), executable code from the bound model.
    var procDecls = new Dictionary<string, Statement>(StringComparer.OrdinalIgnoreCase);
    foreach (var statement in unit.Statements)
      switch (statement) {
        case TypeDecl t: this.WriteTypeDecl(t); break;
        case UnionDecl u: this.WriteUnionDecl(u); break;
        case EnumDecl e: this.WriteEnumDecl(e); break;
        case EquateStmt eq: this.Line($"%{eq.Name} = {this.Expr(eq.Value)}"); break;   // %equates: folded out of MainBody, re-emit here
        case DeclareStmt d: this.WriteDeclare(d); break;
        case SubDecl s: procDecls[s.Name] = s; break;
        case FunctionDecl f: procDecls[f.Name] = f; break;
        case DefFnDecl df: procDecls[df.Name] = df; break;
        default: break;   // executable / DIM / meta come from model.MainBody below
      }

    foreach (var statement in this._model.MainBody)
      this.WriteStatement(statement);

    foreach (var proc in this._model.ProcedureList) {
      if (proc.IsExternal)
        continue;
      if (procDecls.TryGetValue(proc.Name, out var decl) && decl is DefFnDecl df) {
        this.WriteDefFn(df);
        continue;
      }
      this.WriteProcedure(proc, procDecls.GetValueOrDefault(proc.Name));
    }
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

  // ENUM has no pb35 equivalent; the binder folds members to literals (ResolvedConstants) and the
  // enum name to an integer type (EnumTypes). Emit a comment banner so the source stays self-describing.
  private void WriteEnumDecl(EnumDecl e) {
    this._sb.Append('\n');
    this.Line($"' ENUM {e.Name} (folded to integer constants below)");
    foreach (var (name, _) in e.Members)
      if (this._model.EnumMembers.TryGetValue(name, out var value))
        this.Line($"%{name} = {value}");
  }

  private void WriteDeclare(DeclareStmt d) {
    var ret = d.ReturnType is { } rt ? $" AS {this.TypeNameText(rt)}" : "";
    var pars = d.Parameters is { } ps ? "(" + string.Join(", ", ps.Select(this.FormatParam)) + ")" : "";
    this.Line($"DECLARE {(d.IsFunction ? "FUNCTION" : "SUB")} {d.Name}{pars}{ret}");
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
    var kind = proc.IsFunction ? "FUNCTION" : "SUB";
    string header = decl switch {
      SubDecl s => $"SUB {s.Name}({string.Join(", ", s.Parameters.Select(this.FormatParam))})",
      FunctionDecl f => $"FUNCTION {f.Name}({string.Join(", ", f.Parameters.Select(this.FormatParam))})" + (f.ReturnType is { } rt ? $" AS {this.TypeNameText(rt)}" : ""),
      _ => this.HeaderFromSymbol(proc),   // synthesized procs (lambdas, generics, lifted members) have no unit decl
    };
    this.Line(header);
    ++this._indent;
    foreach (var statement in proc.Body!)
      this.WriteStatement(statement);
    --this._indent;
    this.Line($"END {kind}");
  }

  private string HeaderFromSymbol(ProcedureSymbol proc) {
    var visible = proc.VisibleParameterCount;
    var pars = string.Join(", ", proc.Parameters.Take(visible).Select(this.FormatParamSymbol));
    var ret = proc.IsFunction && proc.ReturnType is { } rt ? $" AS {TypeText(rt)}" : "";
    return $"{(proc.IsFunction ? "FUNCTION" : "SUB")} {proc.Name}({pars}){ret}";
  }

  private string FormatParam(Parameter p) {
    var prefix = p.ByVal ? "BYVAL " : p.Seg ? "SEG " : "";
    var arr = p.IsArray ? "()" : "";
    return p.Type is { } t
      ? $"{prefix}{p.Name}{arr} AS {this.TypeNameText(t)}"
      : $"{prefix}{p.Name}{Suffix(p.Suffix)}{arr}";
  }

  private string FormatParamSymbol(VariableSymbol p) {
    var prefix = p.ByVal ? "BYVAL " : "";
    return p.Type is ArrayType a
      ? $"{prefix}{p.Name}() AS {TypeText(a.Element)}"
      : $"{prefix}{p.Name} AS {TypeText(p.Type)}";
  }

  // ---- statements -------------------------------------------------------------------------------

  private void WriteStatement(Statement statement) {
    // statement-level desugar (member-call statement, property-set assignment): emit the core form
    if (this._model.DesugaredStatements.TryGetValue(statement, out var lowered)) {
      this.WriteStatement(lowered);
      return;
    }
    switch (statement) {
      case AssignStmt s: this.Line($"{this.Expr(s.Target)} = {this.Expr(s.Value)}"); break;
      case IncrDecrStmt s: this.Line($"{(s.Increment ? "INCR" : "DECR")} {this.Expr(s.Target)}{(s.Amount is { } a ? ", " + this.Expr(a) : "")}"); break;
      case DimStmt s: this.WriteDim(s); break;
      case RedimStmt s: this.Line($"REDIM {(s.Preserve ? "PRESERVE " : "")}{string.Join(", ", s.Variables.Select(this.FormatVarDecl))}"); break;
      case EraseStmt s: this.Line($"ERASE {string.Join(", ", s.Arrays.Select(a => this.Expr(a)))}"); break;
      case EquateStmt: break;   // emitted from the unit's declaration pass (folded equates aren't in MainBody)
      case CallStmt s: this.WriteCall(s); break;
      case MemberCallStmt s: this.Line($"{this.Expr(s.Receiver)}.{s.Member}({this.JoinArgs(s.Arguments)})"); break;
      case CallPtrStmt s: this.Line($"CALL DWORD {this.Expr(s.Pointer)}{(s.Convention is { } c ? " " + c : "")}({this.JoinArgs(s.Arguments)})"); break;
      case PrintStmt s: this.WritePrint(s); break;
      case WriteStmt s: this.Line($"WRITE {FilesPrefix(s.FileNumber, this)}{this.JoinArgs(s.Items)}"); break;
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
      case LabelStmt s: this.LineNoIndent($"{s.Name}:"); break;
      case GotoStmt s: this.Line($"GOTO {s.Target}"); break;
      case GosubStmt s: this.Line($"GOSUB {s.Target}"); break;
      case GotoPtrStmt s: this.Line($"GOTO DWORD {this.Expr(s.Pointer)}"); break;
      case GosubPtrStmt s: this.Line($"GOSUB DWORD {this.Expr(s.Pointer)}"); break;
      case ReturnStmt s: this.Line(s.Target is { } rt ? $"RETURN {rt}" : "RETURN"); break;
      case OnGotoStmt s: this.Line($"ON {this.Expr(s.Selector)} {(s.IsGosub ? "GOSUB" : "GOTO")} {string.Join(", ", s.Targets)}"); break;
      case ChainStmt s: this.Line($"{(s.IsRun ? "RUN" : "CHAIN")} {this.Expr(s.Target)}"); break;
      case ExitStmt s: this.Line($"EXIT {s.Kind.ToString().ToUpperInvariant()}"); break;
      case ExitFarStmt s: this.Line($"EXIT FAR{(s.AtLabel is { } xl ? " AT " + xl : "")}"); break;
      case IterateStmt s: this.Line($"ITERATE {s.Kind.ToString().ToUpperInvariant()}"); break;
      case EndStmt s: this.Line(s.ExitCode is { } ec ? $"END {this.Expr(ec)}" : "END"); break;
      case YieldStmt s: this.Line($"YIELD {this.Expr(s.Value)}"); break;
      case DataStmt s: this.Line($"DATA {string.Join(", ", s.Items)}"); break;
      case ReadStmt s: this.Line($"READ {string.Join(", ", s.Targets.Select(t => this.Expr(t)))}"); break;
      case RestoreStmt s: this.Line(s.Target is { } t ? $"RESTORE {t}" : "RESTORE"); break;
      case OnErrorStmt s: this.Line(s.ResumeNext ? "ON ERROR RESUME NEXT" : $"ON ERROR GOTO {s.Target ?? "0"}"); break;   // null target = disable (GOTO 0)
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
      case DefTypeStmt s: this.WriteDefType(s); break;
      case TypeDecl or UnionDecl or EnumDecl or DeclareStmt or SubDecl or FunctionDecl or DefFnDecl: break; // emitted from the unit
      case HandlerSaveStmt or HandlerRestoreStmt or HandlerArmStmt or HandlerReraiseStmt: break;            // synthesized coroutine plumbing
      default: this.Line($"' [unsupported: {statement.GetType().Name}]"); break;
    }
  }

  private void WriteCall(CallStmt s) {
    var args = this.CallArguments(s, s.Arguments);
    this.Line(s.UsedCallKeyword ? $"CALL {s.Name}({this.JoinExprs(args)})" : args.Count == 0 ? s.Name : $"{s.Name} {this.JoinExprs(args)}");
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
    var bounds = v.ArrayBounds is { Count: > 0 } ? "(" + this.FormatBounds(v.ArrayBounds) + ")" : "";
    var type = v.Type is { } t ? $" AS {this.TypeNameText(t)}" : "";
    var init = v.Initializer is { } i ? $" = {this.Expr(i)}" : "";
    return $"{v.Name}{Suffix(v.Suffix)}{bounds}{type}{init}";
  }

  private string FormatBounds(IReadOnlyList<(Expression? Lower, Expression Upper)> bounds)
    => string.Join(", ", bounds.Select(b => b.Lower is { } lo ? $"{this.Expr(lo)} TO {this.Expr(b.Upper)}" : this.Expr(b.Upper)));

  private void WritePrint(PrintStmt s) {
    var sb = new StringBuilder((s.IsLPrint ? "LPRINT " : "PRINT ") + FilesPrefix(s.FileNumber, this));
    if (s.UsingFormat is { } u)
      sb.Append("USING ").Append(this.Expr(u)).Append("; ");
    foreach (var item in s.Items) {
      if (item.Value is { } v)
        sb.Append(this.Expr(v));
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
    var kw = "DEF" + s.Type.ToString().ToUpperInvariant()[..3];
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

  private void WriteTry(TryStmt s) {
    this.Line("TRY");
    this.Block(s.Body);
    if (s.Catch is { } c) {
      this.Line("CATCH");
      this.Block(c);
    }
    if (s.Finally is { } f) {
      this.Line("FINALLY");
      this.Block(f);
    }
    this.Line("END TRY");
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
    if (this._model.ResolvedConstants.TryGetValue(e, out var constant))
      return constant.ToString(System.Globalization.CultureInfo.InvariantCulture);

    return e switch {
      IntegerLiteralExpr x => FormatInt(x) + Suffix(x.Suffix),
      FloatLiteralExpr x => FormatFloat(x.Value) + Suffix(x.Suffix),
      StringLiteralExpr x => "\"" + x.Value.Replace("\"", "\"\"") + "\"",
      NamedConstantExpr x => "%" + x.Name,
      NameExpr x => x.Name + Suffix(x.Suffix),
      CallOrIndexExpr x => $"{x.Name}{Suffix(x.Suffix)}({this.JoinExprs(this.CallArguments(x, x.Arguments))})",
      MemberExpr x => $"{this.Expr(x.Target, 99)}.{x.Member}{Suffix(x.Suffix)}",
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
    var prec = Precedence(x.Op);
    // left-associative: the left child at the same precedence needs no parens, the right child does
    var text = $"{this.Expr(x.Left, prec)} {OperatorText(x.Op)} {this.Expr(x.Right, prec + 1)}";
    return Paren(parentPrec, prec, text);
  }

  private static string Paren(int parentPrec, int prec, string text) => prec < parentPrec ? $"({text})" : text;

  /// <summary>Call arguments, reordered to positional form when the binder recorded named-argument reordering.</summary>
  private IReadOnlyList<Expression> CallArguments(object callSite, IReadOnlyList<Expression> original)
    => this._model.ReorderedArguments.TryGetValue(callSite, out var reordered) ? reordered : original;

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

  private static string FormatInt(IntegerLiteralExpr x) => x.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

  private static string FormatFloat(double v) {
    var s = v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    return s.Contains('.') || s.Contains('E') || s.Contains('e') ? s : s + ".0";   // keep it a float literal
  }

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
    if (t.IsPointer)
      return $"{this.TypeNameText(t.PointerTarget!)} PTR";
    if (t.UserTypeName is { } udt)
      return this._model.EnumTypes.ContainsKey(udt) ? "INTEGER" : udt;   // ENUM names alias an integer in pb35
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
    UdtType u => u.Name,
    _ => "INTEGER",
  };
}
