using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;
using static PowerBasic.Compiler.Tests.Syntax.ParserTestHelper;

namespace PowerBasic.Compiler.Tests.Syntax;

/// <summary>
/// Front-end behavior of the PB 3.6 <c>YIELD</c> coroutine statement: it parses into a
/// <see cref="YieldStmt"/> under pb36, is rejected under older dialects, and leaves the
/// historic uses of the bare word <c>YIELD</c> (variable, SUB call) untouched.
/// </summary>
[TestFixture]
public sealed class ParserCoroutineTests {

  private static YieldStmt ParseYield(string source) {
    var unit = Parse(source, Dialect.Pb36);
    Assert.That(unit.Statements, Has.Count.EqualTo(1), source);
    Assert.That(unit.Statements[0], Is.InstanceOf<YieldStmt>(), source);
    return (YieldStmt)unit.Statements[0];
  }

  #region given pb36, when YIELD parses

  [Test]
  public void Parse_GivenYieldOfLiteral_WhenPb36_ThenYieldStmtCarriesTheValue() {
    var stmt = ParseYield("YIELD 42");
    Assert.That(stmt.Value, Is.InstanceOf<IntegerLiteralExpr>());
    Assert.That(((IntegerLiteralExpr)stmt.Value).Value, Is.EqualTo(42));
  }

  [Test]
  public void Parse_GivenYieldOfExpression_WhenPb36_ThenWholeExpressionIsTheValue() {
    var stmt = ParseYield("YIELD i% * 2 + 1");
    Assert.That(stmt.Value, Is.InstanceOf<BinaryExpr>());
  }

  [Test]
  public void Parse_GivenYieldInsideFunction_WhenPb36_ThenBodyHoldsTheYield() {
    var unit = Parse("FUNCTION Gen&()\n  YIELD 1\nEND FUNCTION", Dialect.Pb36);
    var fn = (FunctionDecl)unit.Statements[0];
    Assert.That(fn.Body, Has.Some.InstanceOf<YieldStmt>());
  }

  #endregion

  #region given older dialects, when YIELD-as-statement is rejected

  [Test]
  public void Parse_GivenYieldStatement_WhenPb35_ThenRejectedWithRequirementMessage() {
    var ex = Assert.Throws<ParserException>(() => Parse("YIELD 42", Dialect.Pb35));
    Assert.That(ex!.Message, Does.Contain("requires PowerBASIC 3.6").And.Contain("YIELD"));
  }

  #endregion

  #region given the bare word YIELD, when it is not the statement form

  [Test]
  public void Parse_GivenYieldAssignment_WhenPb35_ThenStillAPlainAssignment() {
    // 'YIELD = 5' assigns to a variable named YIELD; the coroutine gate must not fire
    var stmt = ParseSingle<AssignStmt>("YIELD = 5");
    Assert.That(((NameExpr)stmt.Target).Name, Is.EqualTo("YIELD"));
  }

  [Test]
  public void Parse_GivenSuffixedYield_WhenPb35_ThenStillAPlainAssignment() {
    var stmt = ParseSingle<AssignStmt>("YIELD% = 5");
    Assert.That(((NameExpr)stmt.Target).Name, Is.EqualTo("YIELD"));
  }

  #endregion
}
