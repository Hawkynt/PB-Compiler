using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.Semantics;

[TestFixture]
public sealed class ConstantFolderTests {

  private static readonly SourcePosition _pos = new("TEST.BAS", 1, 1);

  private static Expression Int(long v) => new IntegerLiteralExpr(_pos, v, TypeSuffix.None);
  private static Expression Flt(double v) => new FloatLiteralExpr(_pos, v, TypeSuffix.None);
  private static Expression Str(string v) => new StringLiteralExpr(_pos, v);
  private static Expression Bin(BinaryOp op, Expression l, Expression r) => new BinaryExpr(_pos, op, l, r);

  private static readonly ConstantFolder _folder = new();

  [TestCase(BinaryOp.Add, 7L, 3L, 10L)]
  [TestCase(BinaryOp.Subtract, 7L, 3L, 4L)]
  [TestCase(BinaryOp.Multiply, 7L, 3L, 21L)]
  [TestCase(BinaryOp.IntegerDivide, 7L, 2L, 3L)]
  [TestCase(BinaryOp.Modulo, 7L, 3L, 1L)]
  [TestCase(BinaryOp.And, 6L, 3L, 2L)]
  [TestCase(BinaryOp.Or, 6L, 3L, 7L)]
  [TestCase(BinaryOp.Xor, 6L, 3L, 5L)]
  public void TryFold_GivenIntegralBinary_WhenFolded_ThenIntegralResult(BinaryOp op, long l, long r, long expected) {
    var v = _folder.TryFold(Bin(op, Int(l), Int(r)));
    Assert.That(v?.Integer, Is.EqualTo(expected));
  }

  [Test]
  public void TryFold_GivenIntegerSlashDivision_WhenFolded_ThenFloatResult() {
    var v = _folder.TryFold(Bin(BinaryOp.Divide, Int(10), Int(4)));
    Assert.That(v?.Float, Is.EqualTo(2.5));
  }

  [Test]
  public void TryFold_GivenPower_WhenFolded_ThenFloatResult() {
    var v = _folder.TryFold(Bin(BinaryOp.Power, Int(2), Int(10)));
    Assert.That(v?.AsFloat, Is.EqualTo(1024));
  }

  [Test]
  public void TryFold_GivenComparison_WhenTrue_ThenMinusOne() {
    var v = _folder.TryFold(Bin(BinaryOp.Less, Int(1), Int(2)));
    Assert.That(v?.Integer, Is.EqualTo(-1));
  }

  [Test]
  public void TryFold_GivenDivisionByZero_WhenFolded_ThenNotConstant() {
    Assert.That(_folder.TryFold(Bin(BinaryOp.IntegerDivide, Int(1), Int(0))), Is.Null);
  }

  [Test]
  public void TryFold_GivenNegation_WhenFolded_ThenNegated() {
    var v = _folder.TryFold(new UnaryExpr(_pos, UnaryOp.Negate, Int(5)));
    Assert.That(v?.Integer, Is.EqualTo(-5));
  }

  [Test]
  public void TryFold_GivenNot_WhenFolded_ThenBitwiseComplement() {
    var v = _folder.TryFold(new UnaryExpr(_pos, UnaryOp.Not, Int(0)));
    Assert.That(v?.Integer, Is.EqualTo(-1));
  }

  [Test]
  public void TryFold_GivenStringConcat_WhenFolded_ThenJoined() {
    var v = _folder.TryFold(Bin(BinaryOp.Add, Str("AB"), Str("CD")));
    Assert.That(v?.Text, Is.EqualTo("ABCD"));
  }

  [Test]
  public void TryFold_GivenKnownEquate_WhenFolded_ThenTableValue() {
    var folder = new ConstantFolder(new Dictionary<string, ConstantValue> { ["MAX"] = ConstantValue.Of(99L) });
    var v = folder.TryFold(new NamedConstantExpr(_pos, "MAX"));
    Assert.That(v?.Integer, Is.EqualTo(99));
  }

  [Test]
  public void TryFold_GivenUnknownEquate_WhenFolded_ThenNotConstant() {
    Assert.That(_folder.TryFold(new NamedConstantExpr(_pos, "NOPE")), Is.Null);
  }

  [Test]
  public void TryFold_GivenVariableReference_WhenFolded_ThenNotConstant() {
    Assert.That(_folder.TryFold(new NameExpr(_pos, "x", TypeSuffix.None)), Is.Null);
  }

  [Test]
  public void TryFold_GivenMixedIntFloat_WhenFolded_ThenFloatArithmetic() {
    var v = _folder.TryFold(Bin(BinaryOp.Add, Int(1), Flt(0.5)));
    Assert.That(v?.Float, Is.EqualTo(1.5));
  }
}
