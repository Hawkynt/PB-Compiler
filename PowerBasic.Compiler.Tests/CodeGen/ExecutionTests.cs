using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// True end-to-end tests: AST -> binder -> code generator -> MZ EXE -> DOSBox.
/// Skipped when DOSBox is unavailable.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class ExecutionTests {

  private static readonly SourcePosition _pos = new("TEST.BAS", 1, 1);

  private static string Run(params Statement[] statements) {
    var model = Binder.Bind(new("TEST.BAS", statements));
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return DosBoxRunner.Normalize(DosBoxRunner.Run(exe));
  }

  private static PrintStmt Print(params Expression[] items)
    => new(_pos, null, false, null, items.Select(e => new PrintItem(e, PrintSeparator.Semicolon)).ToList() is var list && list.Count > 0
      ? list[..^1].Append(new(list[^1].Value, PrintSeparator.Newline)).ToList()
      : []);

  private static StringLiteralExpr Str(string s) => new(_pos, s);
  private static IntegerLiteralExpr Int(long v, TypeSuffix suffix = TypeSuffix.None) => new(_pos, v, suffix);
  private static FloatLiteralExpr Flt(double v) => new(_pos, v, TypeSuffix.None);
  private static NameExpr Name(string name, TypeSuffix suffix = TypeSuffix.Integer) => new(_pos, name, suffix);
  private static BinaryExpr Bin(BinaryOp op, Expression l, Expression r) => new(_pos, op, l, r);

  [Test]
  public void Execute_GivenHelloWorld_WhenRun_ThenPrintsText() {
    var output = Run(Print(Str("HELLO FROM PB-COMPILER")));
    Assert.That(output, Is.EqualTo("HELLO FROM PB-COMPILER\n"));
  }

  [Test]
  public void Execute_GivenIntegerArithmetic_WhenRun_ThenPbFormatting() {
    var output = Run(
      Print(Bin(BinaryOp.Add, Int(1), Int(2))),
      Print(Bin(BinaryOp.Multiply, Int(6), Int(7))),
      Print(Bin(BinaryOp.IntegerDivide, Int(7), Int(2))),
      Print(Bin(BinaryOp.Modulo, Int(7), Int(3))),
      Print(new UnaryExpr(_pos, UnaryOp.Negate, Int(5))));
    Assert.That(output, Is.EqualTo(" 3\n 42\n 3\n 1\n-5\n"));
  }

  [Test]
  public void Execute_GivenIntegerBoundaries_WhenRun_ThenExactValues() {
    var output = Run(
      Print(Int(32767)),
      Print(Int(-32768)),
      Print(Int(0)));
    Assert.That(output, Is.EqualTo(" 32767\n-32768\n 0\n"));
  }

  [Test]
  public void Execute_GivenLongArithmetic_WhenRun_ThenThirtyTwoBitPath() {
    var output = Run(
      Print(Int(100000, TypeSuffix.Long)),
      Print(Bin(BinaryOp.Multiply, Int(100000, TypeSuffix.Long), Int(3, TypeSuffix.Long))),
      Print(Bin(BinaryOp.IntegerDivide, Int(1000000, TypeSuffix.Long), Int(7, TypeSuffix.Long))),
      Print(new UnaryExpr(_pos, UnaryOp.Negate, Int(2000000000, TypeSuffix.Long))));
    Assert.That(output, Is.EqualTo(" 100000\n 300000\n 142857\n-2000000000\n"));
  }

  [Test]
  public void Execute_GivenVariablesAndAssignment_WhenRun_ThenStoredAndLoaded() {
    var output = Run(
      new AssignStmt(_pos, Name("a"), Int(11)),
      new AssignStmt(_pos, Name("b"), Bin(BinaryOp.Multiply, Name("a"), Int(3))),
      Print(Name("b")));
    Assert.That(output, Is.EqualTo(" 33\n"));
  }

  [Test]
  public void Execute_GivenIfElse_WhenRun_ThenCorrectBranch() {
    var output = Run(
      new AssignStmt(_pos, Name("i"), Int(10)),
      new IfStmt(_pos, Bin(BinaryOp.Greater, Name("i"), Int(5)),
        [Print(Str("GT"))], [], [Print(Str("LE"))]));
    Assert.That(output, Is.EqualTo("GT\n"));
  }

  [Test]
  public void Execute_GivenForLoop_WhenRun_ThenIterates() {
    var output = Run(
      new ForStmt(_pos, Name("i"), Int(1), Int(5), null, [
        new PrintStmt(_pos, null, false, null, [new(Str("I="), PrintSeparator.Semicolon), new(Name("i"), PrintSeparator.Newline)]),
      ]));
    Assert.That(output, Is.EqualTo("I= 1\nI= 2\nI= 3\nI= 4\nI= 5\n"));
  }

  [Test]
  public void Execute_GivenDoWhileLoop_WhenRun_ThenLoopsAndStops() {
    var output = Run(
      new AssignStmt(_pos, Name("i"), Int(0)),
      new DoLoopStmt(_pos, LoopTestKind.While, Bin(BinaryOp.Less, Name("i"), Int(3)), LoopTestKind.None, null, [
        new IncrDecrStmt(_pos, true, Name("i"), null),
      ]),
      Print(Name("i")));
    Assert.That(output, Is.EqualTo(" 3\n"));
  }

  [Test]
  public void Execute_GivenSelectCase_WhenRun_ThenRangeAndValueAndElse() {
    Statement Sel(int value) => new SelectStmt(_pos, Int(value), [
      new(_pos, [new(_pos, Int(1), Int(9), null)], [Print(Str("1-9"))]),
      new(_pos, [new(_pos, Int(10), null, null)], [Print(Str("TEN"))]),
      new(_pos, [], [Print(Str("OTHER"))]),
    ]);
    var output = Run(Sel(5), Sel(10), Sel(99));
    Assert.That(output, Is.EqualTo("1-9\nTEN\nOTHER\n"));
  }

  [Test]
  public void Execute_GivenGosubAndGoto_WhenRun_ThenControlTransfers() {
    var output = Run(
      new GosubStmt(_pos, "sr"),
      Print(Str("AFTER")),
      new GotoStmt(_pos, "fin"),
      new LabelStmt(_pos, "sr"),
      Print(Str("SUB")),
      new ReturnStmt(_pos, null),
      new LabelStmt(_pos, "fin"));
    Assert.That(output, Is.EqualTo("SUB\nAFTER\n"));
  }

  [Test]
  public void Execute_GivenFloatArithmetic_WhenRun_ThenFormattedLikePb() {
    var output = Run(
      Print(Bin(BinaryOp.Divide, Int(10), Int(4))),
      Print(Flt(1.5)),
      Print(Bin(BinaryOp.Power, Int(2), Int(10))),
      Print(Flt(0)));
    Assert.That(output, Is.EqualTo(" 2.5\n 1.5\n 1024\n 0\n"));
  }

  [Test]
  public void Execute_GivenNegativeFloat_WhenRun_ThenMinusSign() {
    var output = Run(Print(Flt(-2.25)));
    Assert.That(output, Is.EqualTo("-2.25\n"));
  }
}
