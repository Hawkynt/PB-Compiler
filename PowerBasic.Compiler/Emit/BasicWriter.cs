using System.Text;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Emit;

/// <summary>
/// Renders a bound <see cref="SemanticModel"/> back to readable PowerBASIC source - a "back-emitter"
/// that un-parses the (post-weaving, optionally post-optimization) AST so the result of the front end
/// and the optimizer is visible as code. Statements and expressions it does not model yet are emitted
/// as a <c>' [unsupported: ...]</c> comment rather than dropped, so the output is always complete.
/// </summary>
public sealed class BasicWriter {

  private readonly StringBuilder _sb = new();
  private int _indent;

  /// <summary>Un-parses the whole program (main body then each in-module procedure) to PowerBASIC source.</summary>
  public static string Render(SemanticModel model) {
    var writer = new BasicWriter();
    foreach (var statement in model.MainBody)
      writer.WriteStatement(statement);
    foreach (var proc in model.ProcedureList)
      if (!proc.IsExternal)
        writer.WriteProcedure(proc);
    return writer._sb.ToString();
  }

  private void WriteProcedure(ProcedureSymbol proc) {
    this._sb.Append('\n');
    var kind = proc.IsFunction ? "FUNCTION" : "SUB";
    var visible = proc.Parameters.Count - (proc.HasSretParam ? 1 : 0);   // drop the hidden struct-return buffer
    var pars = string.Join(", ", proc.Parameters.Take(visible).Select(FormatParameter));
    var ret = proc.IsFunction && proc.ReturnType is { } rt ? $" AS {TypeText(rt)}" : "";
    this.Line($"{kind} {proc.Name}({pars}){ret}");
    ++this._indent;
    foreach (var statement in proc.Body!)
      this.WriteStatement(statement);
    --this._indent;
    this.Line($"END {kind}");
  }

  private static string FormatParameter(VariableSymbol p) {
    var prefix = p.ByVal ? "BYVAL " : "";
    return $"{prefix}{p.Name} AS {TypeText(p.Type)}";
  }

  // ---- statements -------------------------------------------------------------------------------

  private void WriteStatement(Statement statement) {
    switch (statement) {
      case AssignStmt s: this.Line($"{this.Expr(s.Target)} = {this.Expr(s.Value)}"); break;
      case IncrDecrStmt s: this.Line($"{(s.Increment ? "INCR" : "DECR")} {this.Expr(s.Target)}{(s.Amount is { } a ? ", " + this.Expr(a) : "")}"); break;
      case DimStmt s: this.WriteDim(s); break;
      case RedimStmt s: this.Line($"REDIM {(s.Preserve ? "PRESERVE " : "")}{string.Join(", ", s.Variables.Select(this.FormatVarDecl))}"); break;
      case EraseStmt s: this.Line($"ERASE {string.Join(", ", s.Arrays.Select(a => this.Expr(a)))}"); break;
      case EquateStmt s: this.Line($"{s.Name} = {this.Expr(s.Value)}"); break;
      case CallStmt s: this.Line(s.UsedCallKeyword ? $"CALL {s.Name}({Join(s.Arguments)})" : s.Arguments.Count == 0 ? s.Name : $"{s.Name} {Join(s.Arguments)}"); break;
      case PrintStmt s: this.WritePrint(s); break;
      case SwapStmt s: this.Line($"SWAP {this.Expr(s.Left)}, {this.Expr(s.Right)}"); break;
      case MidAssignStmt s: this.Line($"MID$({this.Expr(s.Target)}, {this.Expr(s.Start)}{(s.Length is { } l ? ", " + this.Expr(l) : "")}) = {this.Expr(s.Value)}"); break;
      case LabelStmt s: this.LineNoIndent($"{s.Name}:"); break;
      case GotoStmt s: this.Line($"GOTO {s.Target}"); break;
      case GosubStmt s: this.Line($"GOSUB {s.Target}"); break;
      case ReturnStmt s: this.Line(s.Target is { } rt ? $"RETURN {rt}" : "RETURN"); break;
      case OnGotoStmt s: this.Line($"ON {this.Expr(s.Selector)} {(s.IsGosub ? "GOSUB" : "GOTO")} {string.Join(", ", s.Targets)}"); break;
      case ExitStmt s: this.Line($"EXIT {s.Kind.ToString().ToUpperInvariant()}"); break;
      case IterateStmt s: this.Line($"ITERATE {s.Kind.ToString().ToUpperInvariant()}"); break;
      case EndStmt s: this.Line(s.ExitCode is { } c ? $"END {this.Expr(c)}" : "END"); break;
      case YieldStmt s: this.Line($"YIELD {this.Expr(s.Value)}"); break;
      case DataStmt s: this.Line($"DATA {string.Join(", ", s.Items)}"); break;
      case ReadStmt s: this.Line($"READ {string.Join(", ", s.Targets.Select(t => this.Expr(t)))}"); break;
      case RestoreStmt s: this.Line(s.Target is { } rst ? $"RESTORE {rst}" : "RESTORE"); break;
      case OnErrorStmt s: this.Line(s.ResumeNext ? "ON ERROR RESUME NEXT" : $"ON ERROR GOTO {s.Target}"); break;
      case ResumeStmt s: this.Line("RESUME" + (s.Kind switch { ResumeKind.Next => " NEXT", ResumeKind.Label => " " + s.Target, _ => "" })); break;
      case ErrorStmt s: this.Line($"ERROR {this.Expr(s.Code)}"); break;
      case DeclareStmt s: this.Line($"DECLARE {(s.IsFunction ? "FUNCTION" : "SUB")} {s.Name}"); break;
      case IfStmt s: this.WriteIf(s); break;
      case ForStmt s: this.WriteFor(s); break;
      case ForEachStmt s: this.WriteForEach(s); break;
      case DoLoopStmt s: this.WriteDoLoop(s); break;
      case SelectStmt s: this.WriteSelect(s); break;
      case MetaStmt or DefTypeStmt: this.Line($"' {statement.GetType().Name}"); break;
      default: this.Line($"' [unsupported: {statement.GetType().Name}]"); break;
    }
  }

  private void WriteDim(DimStmt s) {
    var scope = s.Storage switch {
      StorageClass.Local => "LOCAL", StorageClass.Static => "STATIC", StorageClass.Shared => "DIM SHARED",
      StorageClass.Public => "PUBLIC", StorageClass.Common => "COMMON", _ => "DIM",
    };
    this.Line($"{scope} {string.Join(", ", s.Variables.Select(this.FormatVarDecl))}");
  }

  private string FormatVarDecl(VariableDecl v) {
    var bounds = v.ArrayBounds is { Count: > 0 }
      ? "(" + string.Join(", ", v.ArrayBounds.Select(b => b.Lower is { } lo ? $"{this.Expr(lo)} TO {this.Expr(b.Upper)}" : this.Expr(b.Upper))) + ")"
      : "";
    var type = v.Type is { } t ? $" AS {this.TypeNameText(t)}" : "";
    var init = v.Initializer is { } i ? $" = {this.Expr(i)}" : "";
    return $"{v.Name}{Suffix(v.Suffix)}{bounds}{type}{init}";
  }

  /// <summary>Renders an <c>AS</c>-clause type from the syntax tree (a <see cref="TypeName"/>, not a resolved <see cref="PbType"/>).</summary>
  private string TypeNameText(TypeName t) {
    if (t.IsPointer)
      return $"{this.TypeNameText(t.PointerTarget!)} PTR";
    if (t.UserTypeName is { } udt)
      return udt;
    if (t.Builtin == BuiltinType.FixedString && t.FixedLength is { } len)
      return $"STRING * {this.Expr(len)}";
    if (t.Builtin == BuiltinType.Asciiz && t.FixedLength is { } alen)
      return $"ASCIIZ * {this.Expr(alen)}";
    return t.Builtin.ToString().ToUpperInvariant();
  }

  private void WritePrint(PrintStmt s) {
    var file = s.FileNumber is { } f ? $"#{this.Expr(f)}, " : "";
    var sb = new StringBuilder((s.IsLPrint ? "LPRINT " : "PRINT ") + file);
    if (s.UsingFormat is { } u)
      sb.Append("USING ").Append(this.Expr(u)).Append("; ");
    foreach (var item in s.Items) {
      if (item.Value is { } v)
        sb.Append(this.Expr(v));
      sb.Append(item.Separator switch { PrintSeparator.Comma => ", ", PrintSeparator.Semicolon => "; ", _ => "" });
    }
    this.Line(sb.ToString().TrimEnd());
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

  private string FormatSelector(CaseSelector c) {
    if (c.IsComparison is { } cmp)
      return $"IS {ComparisonText(cmp)} {this.Expr(c.Value!)}";
    if (c.RangeUpper is { } hi)
      return $"{this.Expr(c.Value!)} TO {this.Expr(hi)}";
    return this.Expr(c.Value!);
  }

  // ---- expressions ------------------------------------------------------------------------------

  private string Expr(Expression e, int parentPrec = 0) => e switch {
    IntegerLiteralExpr x => x.Value + Suffix(x.Suffix),
    FloatLiteralExpr x => x.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + Suffix(x.Suffix),
    StringLiteralExpr x => "\"" + x.Value.Replace("\"", "\"\"") + "\"",
    NamedConstantExpr x => x.Name,
    NameExpr x => x.Name + Suffix(x.Suffix),
    CallOrIndexExpr x => $"{x.Name}{Suffix(x.Suffix)}({Join(x.Arguments)})",
    MemberExpr x => $"{this.Expr(x.Target, 99)}.{x.Member}{Suffix(x.Suffix)}",
    IndexExpr x => $"{this.Expr(x.Target, 99)}({Join(x.Arguments)})",
    PtrDerefExpr x => $"@{this.Expr(x.Pointer, 99)}{(x.Index is { } i ? $"[{this.Expr(i)}]" : "")}",
    ByValArgExpr x => $"BYVAL {this.Expr(x.Value)}",
    NothingExpr => "NOTHING",
    TupleExpr x => $"({Join(x.Elements)})",
    CoalesceExpr x => Paren(parentPrec, 0, $"{this.Expr(x.Value, 1)} ?? {this.Expr(x.Fallback, 0)}"),
    IfExpr x => $"IIF({this.Expr(x.Condition)}, {this.Expr(x.WhenTrue)}, {this.Expr(x.WhenFalse)})",
    NewExpr x => $"{x.TypeName}({string.Join(", ", x.Fields.Select(f => $"{f.Field} := {this.Expr(f.Value)}"))})",
    NamedArgExpr x => $"{x.Name} := {this.Expr(x.Value)}",
    UnaryExpr x => this.Unary(x, parentPrec),
    BinaryExpr x => this.Binary(x, parentPrec),
    _ => $"/* {e.GetType().Name} */",
  };

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

  // ---- helpers ----------------------------------------------------------------------------------

  private string Join(IReadOnlyList<Expression> args) => string.Join(", ", args.Select(a => this.Expr(a)));

  private void Block(IReadOnlyList<Statement> body) {
    ++this._indent;
    foreach (var s in body)
      this.WriteStatement(s);
    --this._indent;
  }

  private void Line(string text) => this._sb.Append(new string(' ', this._indent * 2)).Append(text).Append('\n');
  private void LineNoIndent(string text) => this._sb.Append(text).Append('\n');

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
    BinaryOp.Concat => "&",
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

  private static string TypeText(PbType type) => type switch {
    ScalarType { Kind: ScalarKind.Integer } => "INTEGER", ScalarType { Kind: ScalarKind.Long } => "LONG",
    ScalarType { Kind: ScalarKind.Quad } => "QUAD", ScalarType { Kind: ScalarKind.Byte } => "BYTE",
    ScalarType { Kind: ScalarKind.Word } => "WORD", ScalarType { Kind: ScalarKind.Dword } => "DWORD",
    ScalarType { Kind: ScalarKind.Single } => "SINGLE", ScalarType { Kind: ScalarKind.Double } => "DOUBLE",
    ScalarType { Kind: ScalarKind.Ext } => "EXT", ScalarType { Kind: ScalarKind.SByte } => "SBYTE",
    ScalarType { Kind: ScalarKind.QWord } => "QWORD",
    StringType or FlexType => "STRING", FixedStringType f => $"STRING * {f.Length}",
    WideIntType w => (w.Signed ? "INT" : "UINT") + w.ByteSize * 8,
    UdtType u => u.Name,
    _ => type.ToString() ?? "ANY",
  };
}
