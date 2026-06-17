using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// O16 interval lattice: the value type's arithmetic (sound over-approximation, Top on overflow)
/// and the forward range-propagation analysis over a bound statement list. This is the
/// prerequisite analysis for type narrowing; it is exercised in isolation, with no codegen wiring.
/// </summary>
[TestFixture]
public sealed class IntervalRangeTests {

  #region Interval arithmetic

  [Test]
  public void Add_GivenTwoRanges_WhenAdded_ThenEndpointsAdd() =>
    Assert.That(new Interval(1, 5).Add(new Interval(10, 20)), Is.EqualTo(new Interval(11, 25)));

  [Test]
  public void Subtract_GivenTwoRanges_WhenSubtracted_ThenCrossEndpoints() =>
    Assert.That(new Interval(1, 5).Subtract(new Interval(2, 3)), Is.EqualTo(new Interval(-2, 3)));

  [Test]
  public void Negate_GivenRange_WhenNegated_ThenFlips() =>
    Assert.That(new Interval(-3, 5).Negate(), Is.EqualTo(new Interval(-5, 3)));

  [Test]
  public void Multiply_GivenRangeStraddlingSigns_WhenMultiplied_ThenCornerHull() =>
    // hull(2*-4, 2*5, 3*-4, 3*5) = hull(-8, 10, -12, 15) = [-12, 15]
    Assert.That(new Interval(2, 3).Multiply(new Interval(-4, 5)), Is.EqualTo(new Interval(-12, 15)));

  [Test]
  public void Divide_GivenRangeByConstant_WhenDivided_ThenEndpointsDivide() =>
    Assert.That(new Interval(-10, 10).Divide(Interval.Of(3)), Is.EqualTo(new Interval(-3, 3)));

  [Test]
  public void Divide_GivenDivisorSpanningZero_WhenDivided_ThenTop() =>
    Assert.That(new Interval(1, 5).Divide(new Interval(-1, 1)).IsTop, Is.True);

  [Test]
  public void Modulo_GivenNonNegativeByConstant_WhenModded_ThenZeroToBound() =>
    Assert.That(new Interval(0, 100).Modulo(Interval.Of(8)), Is.EqualTo(new Interval(0, 7)));

  [Test]
  public void Modulo_GivenSignedByConstant_WhenModded_ThenSymmetricBound() =>
    Assert.That(new Interval(-5, 5).Modulo(Interval.Of(8)), Is.EqualTo(new Interval(-7, 7)));

  [Test]
  public void And_GivenAnyByConstantMask_WhenMasked_ThenZeroToMask() =>
    Assert.That(new Interval(-1000, 1000).And(Interval.Of(7)), Is.EqualTo(new Interval(0, 7)));

  [Test]
  public void Join_GivenTwoRanges_WhenJoined_ThenHull() =>
    Assert.That(new Interval(1, 3).Join(new Interval(5, 7)), Is.EqualTo(new Interval(1, 7)));

  [Test]
  public void Add_GivenTopOperand_WhenAdded_ThenTop() =>
    Assert.That(Interval.Of(5).Add(Interval.Top).IsTop, Is.True);

  [Test]
  public void Add_GivenOverflow_WhenAdded_ThenTop() =>
    Assert.That(Interval.Of(long.MaxValue).Add(Interval.Of(1)).IsTop, Is.True);

  #endregion

  #region forward analysis

  private static Interval? RangeOf(string source, string varName) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", Dialect.Pb36), "TEST.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var env = IntervalRangeAnalysis.Analyze(model.MainBody, model);
    foreach (var kv in env)
      if (kv.Key.Name.Equals(varName, System.StringComparison.OrdinalIgnoreCase))
        return kv.Value;
    return null; // not tracked = Top (unknown)
  }

  [Test]
  public void Analyze_GivenConstantAssign_ThenPointRange() =>
    Assert.That(RangeOf("x% = 5\nEND", "x"), Is.EqualTo(Interval.Of(5)));

  [Test]
  public void Analyze_GivenAffineChain_ThenPropagates() =>
    Assert.That(RangeOf("x% = 5\ny% = x% + 3\nEND", "y"), Is.EqualTo(Interval.Of(8)));

  [Test]
  public void Analyze_GivenArithmeticChain_ThenPropagates() =>
    // x=5, y=x*2=10, z=y-1=9
    Assert.That(RangeOf("x% = 5\ny% = x% * 2\nz% = y% - 1\nEND", "z"), Is.EqualTo(Interval.Of(9)));

  [Test]
  public void Analyze_GivenIncr_ThenSteps() =>
    Assert.That(RangeOf("x% = 5\nINCR x%\nEND", "x"), Is.EqualTo(Interval.Of(6)));

  [Test]
  public void Analyze_GivenIfElse_ThenJoinsArms() =>
    Assert.That(RangeOf("x% = 0\nIF a% > 0 THEN\nx% = 5\nELSE\nx% = 10\nEND IF\nEND", "x"),
      Is.EqualTo(new Interval(5, 10)));

  [Test]
  public void Analyze_GivenIfNoElse_ThenJoinsWithFallthrough() =>
    Assert.That(RangeOf("x% = 3\nIF a% > 0 THEN\nx% = 7\nEND IF\nEND", "x"),
      Is.EqualTo(new Interval(3, 7)));

  [Test]
  public void Analyze_GivenCallFreePrint_ThenRangeSurvives() =>
    Assert.That(RangeOf("x% = 5\nPRINT x%\ny% = x% + 1\nEND", "y"), Is.EqualTo(Interval.Of(6)));

  [Test]
  public void Analyze_GivenUnknownOperand_ThenTop() =>
    // a% is read before any assignment - conservatively unknown, so y% is too (untracked)
    Assert.That(RangeOf("y% = a% + 1\nEND", "y"), Is.Null);

  #endregion

  #region per-program-point queries

  private static (PowerBasic.Compiler.Syntax.Ast.CompilationUnit unit, SemanticModel model) BindUnit(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", Dialect.Pb36), "TEST.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return (unit, model);
  }

  private static Interval? At(IReadOnlyDictionary<PowerBasic.Compiler.Syntax.Ast.Statement, IReadOnlyDictionary<VariableSymbol, Interval>> points,
      PowerBasic.Compiler.Syntax.Ast.Statement stmt, string varName) {
    if (!points.TryGetValue(stmt, out var env))
      return null;
    foreach (var kv in env)
      if (kv.Key.Name.Equals(varName, System.StringComparison.OrdinalIgnoreCase))
        return kv.Value;
    return null;
  }

  [Test]
  public void ProgramPoints_GivenUse_ThenEntryEnvHasPriorRange() {
    var (_, model) = BindUnit("x% = 5\ny% = x% + 1\nEND");
    var points = IntervalRangeAnalysis.AnalyzeProgramPoints(model.MainBody, model);
    // before the SECOND statement (y% = x% + 1), x% is proven [5,5]; before the first, nothing
    Assert.That(At(points, model.MainBody[1], "x"), Is.EqualTo(Interval.Of(5)));
    Assert.That(At(points, model.MainBody[0], "x"), Is.Null);
  }

  [Test]
  public void ProgramPoints_GivenUseInsideIfArm_ThenArmEntryHasRange() {
    var (_, model) = BindUnit("x% = 5\nIF a% > 0 THEN\nz% = x% \\ 2\nEND IF\nEND");
    var points = IntervalRangeAnalysis.AnalyzeProgramPoints(model.MainBody, model);
    var iff = (PowerBasic.Compiler.Syntax.Ast.IfStmt)model.MainBody[1];
    // x% is [5,5] at the entry of the statement inside the THEN arm
    Assert.That(At(points, iff.Then[0], "x"), Is.EqualTo(Interval.Of(5)));
  }

  #endregion
}
