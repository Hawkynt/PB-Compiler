using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;
using static PowerBasic.Compiler.Tests.Syntax.ParserTestHelper;

namespace PowerBasic.Compiler.Tests.Syntax;

[TestFixture]
public sealed class ParserExpressionTests {

  #region literals & atoms

  [Test]
  public void Parse_GivenIntegerLiteral_WhenParsed_ThenValueAndSuffixAreKept() {
    var expr = ParseExpression<IntegerLiteralExpr>("42&");
    Assert.Multiple(() => {
      Assert.That(expr.Value, Is.EqualTo(42));
      Assert.That(expr.Suffix, Is.EqualTo(TypeSuffix.Long));
    });
  }

  [Test]
  public void Parse_GivenHexLiteral_WhenParsed_ThenValueIsDecoded() {
    var expr = ParseExpression<IntegerLiteralExpr>("&H4F05");
    Assert.That(expr.Value, Is.EqualTo(0x4F05));
  }

  [Test]
  public void Parse_GivenFloatLiteral_WhenParsed_ThenValueIsKept() {
    var expr = ParseExpression<FloatLiteralExpr>("1.5");
    Assert.That(expr.Value, Is.EqualTo(1.5));
  }

  [Test]
  public void Parse_GivenStringLiteral_WhenParsed_ThenValueIsKept() {
    var expr = ParseExpression<StringLiteralExpr>("\"hello\"");
    Assert.That(expr.Value, Is.EqualTo("hello"));
  }

  [Test]
  public void Parse_GivenNamedConstant_WhenParsed_ThenNameIsKept() {
    var expr = ParseExpression<NamedConstantExpr>("%SVGA_MODEX");
    Assert.That(expr.Name, Is.EqualTo("SVGA_MODEX"));
  }

  [Test]
  public void Parse_GivenSuffixedName_WhenParsed_ThenSuffixIsKept() {
    var expr = ParseExpression<NameExpr>("s$");
    Assert.Multiple(() => {
      Assert.That(expr.Name, Is.EqualTo("s"));
      Assert.That(expr.Suffix, Is.EqualTo(TypeSuffix.String));
    });
  }

  [Test]
  public void Parse_GivenCallWithArguments_WhenParsed_ThenArgumentsAreParsed() {
    var expr = ParseExpression<CallOrIndexExpr>("MID$(s$, 1, n + 2)");
    Assert.Multiple(() => {
      Assert.That(expr.Name, Is.EqualTo("MID"));
      Assert.That(expr.Suffix, Is.EqualTo(TypeSuffix.String));
      Assert.That(expr.Arguments, Has.Count.EqualTo(3));
      Assert.That(expr.Arguments[2], Is.InstanceOf<BinaryExpr>());
    });
  }

  [Test]
  public void Parse_GivenEmptyArgumentList_WhenParsed_ThenCallHasNoArguments() {
    var expr = ParseExpression<CallOrIndexExpr>("CGX()");
    Assert.That(expr.Arguments, Is.Empty);
  }

  #endregion

  #region member access

  [Test]
  public void Parse_GivenMemberChain_WhenParsed_ThenChainNestsLeft() {
    var expr = ParseExpression<MemberExpr>("ctx.Inner.Mode");
    Assert.Multiple(() => {
      Assert.That(expr.Member, Is.EqualTo("Mode"));
      var inner = (MemberExpr)expr.Target;
      Assert.That(inner.Member, Is.EqualTo("Inner"));
      Assert.That(((NameExpr)inner.Target).Name, Is.EqualTo("ctx"));
    });
  }

  [Test]
  public void Parse_GivenArrayElementMember_WhenParsed_ThenIndexThenMember() {
    var expr = ParseExpression<MemberExpr>("Sprites(i).X");
    Assert.Multiple(() => {
      Assert.That(expr.Member, Is.EqualTo("X"));
      Assert.That(expr.Target, Is.InstanceOf<CallOrIndexExpr>());
    });
  }

  [Test]
  public void Parse_GivenIndexedMemberThenMember_WhenParsed_ThenIndexExprIsUsed() {
    var expr = ParseExpression<MemberExpr>("ctx.NamedTimers(i).Active");
    var index = (IndexExpr)expr.Target;
    Assert.Multiple(() => {
      Assert.That(expr.Member, Is.EqualTo("Active"));
      Assert.That(index.Arguments, Has.Count.EqualTo(1));
      Assert.That(((MemberExpr)index.Target).Member, Is.EqualTo("NamedTimers"));
    });
  }

  #endregion

  #region precedence

  [Test]
  public void Parse_GivenAddAndMultiply_WhenParsed_ThenMultiplyBindsTighter() {
    var expr = ParseExpression<BinaryExpr>("1 + 2 * 3");
    Assert.Multiple(() => {
      Assert.That(expr.Op, Is.EqualTo(BinaryOp.Add));
      Assert.That(((BinaryExpr)expr.Right).Op, Is.EqualTo(BinaryOp.Multiply));
    });
  }

  [Test]
  public void Parse_GivenModAndIntegerDivide_WhenParsed_ThenIntegerDivideBindsTighter() {
    var expr = ParseExpression<BinaryExpr>("10 MOD 4 \\ 2");
    Assert.Multiple(() => {
      Assert.That(expr.Op, Is.EqualTo(BinaryOp.Modulo));
      Assert.That(((BinaryExpr)expr.Right).Op, Is.EqualTo(BinaryOp.IntegerDivide));
    });
  }

  [Test]
  public void Parse_GivenPowerChain_WhenParsed_ThenPowerIsLeftAssociative() {
    var expr = ParseExpression<BinaryExpr>("2 ^ 3 ^ 2");
    Assert.Multiple(() => {
      Assert.That(expr.Op, Is.EqualTo(BinaryOp.Power));
      Assert.That(((BinaryExpr)expr.Left).Op, Is.EqualTo(BinaryOp.Power));
    });
  }

  [Test]
  public void Parse_GivenNegatedPower_WhenParsed_ThenPowerBindsTighterThanUnaryMinus() {
    var expr = ParseExpression<UnaryExpr>("-2 ^ 2");
    Assert.Multiple(() => {
      Assert.That(expr.Op, Is.EqualTo(UnaryOp.Negate));
      Assert.That(((BinaryExpr)expr.Operand).Op, Is.EqualTo(BinaryOp.Power));
    });
  }

  [Test]
  public void Parse_GivenNegativeExponent_WhenParsed_ThenExponentCarriesSign() {
    var expr = ParseExpression<BinaryExpr>("2 ^ -3");
    Assert.That(((UnaryExpr)expr.Right).Op, Is.EqualTo(UnaryOp.Negate));
  }

  [Test]
  public void Parse_GivenNotAndAnd_WhenParsed_ThenNotBindsTighter() {
    var expr = ParseExpression<BinaryExpr>("NOT a AND b");
    Assert.Multiple(() => {
      Assert.That(expr.Op, Is.EqualTo(BinaryOp.And));
      Assert.That(((UnaryExpr)expr.Left).Op, Is.EqualTo(UnaryOp.Not));
    });
  }

  [Test]
  public void Parse_GivenComparisonInsideAnd_WhenParsed_ThenComparisonBindsTighter() {
    var expr = ParseExpression<BinaryExpr>("a < b AND c = d");
    Assert.Multiple(() => {
      Assert.That(expr.Op, Is.EqualTo(BinaryOp.And));
      Assert.That(((BinaryExpr)expr.Left).Op, Is.EqualTo(BinaryOp.Less));
      Assert.That(((BinaryExpr)expr.Right).Op, Is.EqualTo(BinaryOp.Equal));
    });
  }

  [Test]
  public void Parse_GivenOrXorMix_WhenParsed_ThenXorIsLooserThanOr() {
    var expr = ParseExpression<BinaryExpr>("a OR b XOR c");
    Assert.Multiple(() => {
      Assert.That(expr.Op, Is.EqualTo(BinaryOp.Xor));
      Assert.That(((BinaryExpr)expr.Left).Op, Is.EqualTo(BinaryOp.Or));
    });
  }

  [Test]
  public void Parse_GivenEqvAndImp_WhenParsed_ThenImpIsLoosest() {
    var expr = ParseExpression<BinaryExpr>("a EQV b IMP c");
    Assert.Multiple(() => {
      Assert.That(expr.Op, Is.EqualTo(BinaryOp.Imp));
      Assert.That(((BinaryExpr)expr.Left).Op, Is.EqualTo(BinaryOp.Eqv));
    });
  }

  [Test]
  public void Parse_GivenChainedComparisons_WhenParsed_ThenLeftAssociative() {
    var expr = ParseExpression<BinaryExpr>("a < b = c");
    Assert.Multiple(() => {
      Assert.That(expr.Op, Is.EqualTo(BinaryOp.Equal));
      Assert.That(((BinaryExpr)expr.Left).Op, Is.EqualTo(BinaryOp.Less));
    });
  }

  [Test]
  public void Parse_GivenParentheses_WhenParsed_ThenGroupingWins() {
    var expr = ParseExpression<BinaryExpr>("(1 + 2) * 3");
    Assert.Multiple(() => {
      Assert.That(expr.Op, Is.EqualTo(BinaryOp.Multiply));
      Assert.That(((BinaryExpr)expr.Left).Op, Is.EqualTo(BinaryOp.Add));
    });
  }

  #endregion

  #region ISTRUE / ISFALSE

  [Test]
  public void Parse_GivenIsTrue_WhenParsed_ThenItIsTheIdentity() {
    var expr = ParseExpression("ISTRUE a");
    Assert.That(expr, Is.InstanceOf<NameExpr>());
  }

  [Test]
  public void Parse_GivenIsFalse_WhenParsed_ThenItBecomesNot() {
    var expr = ParseExpression<UnaryExpr>("ISFALSE a");
    Assert.That(expr.Op, Is.EqualTo(UnaryOp.Not));
  }

  #endregion

  #region errors

  [Test]
  public void Parse_GivenDanglingOperator_WhenParsed_ThenParserExceptionIsRaised()
    => Assert.Throws<ParserException>(() => ParseExpression("1 +"));

  [Test]
  public void Parse_GivenUnbalancedParenthesis_WhenParsed_ThenParserExceptionIsRaised()
    => Assert.Throws<ParserException>(() => ParseExpression("(1 + 2"));

  [Test]
  public void Parse_GivenExceptionPosition_WhenRaised_ThenPositionPointsIntoSource() {
    var error = Assert.Throws<ParserException>(() => Parse("x = (1 + 2"));
    Assert.That(error!.Position.File, Is.EqualTo("test.bas"));
  }

  #endregion
}
