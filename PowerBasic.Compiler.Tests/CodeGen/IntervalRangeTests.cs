using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// O16 value facts: interval arithmetic plus the forward reduced-product analysis over a bound
/// statement list. The interval domain remains a sound runtime-value over-approximation; when
/// arithmetic wraps, exact known bits may recover the exact wrapped value instead of staying Top.
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
      if (kv.Key.Name.Equals(varName, StringComparison.OrdinalIgnoreCase))
        return kv.Value.Range.IsTop ? null : kv.Value.Range;
    return null;
  }

  [Test]
  public void Analyze_GivenConstantAssign_ThenPointRange() =>
    Assert.That(RangeOf("x% = 5\nEND", "x"), Is.EqualTo(Interval.Of(5)));

  [Test]
  public void Analyze_GivenAffineChain_ThenPropagates() =>
    Assert.That(RangeOf("x% = 5\ny% = x% + 3\nEND", "y"), Is.EqualTo(Interval.Of(8)));

  [Test]
  public void Analyze_GivenArithmeticChain_ThenPropagates() =>
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
    Assert.That(RangeOf("y% = a% + 1\nEND", "y"), Is.Null);

  [Test]
  public void Analyze_GivenIntegerOverflow_ThenExactWrappedRangeRecoveredFromBits() {
    Assert.That(RangeOf("x% = 30000\ny% = x% + 30000\nEND", "y"), Is.EqualTo(Interval.Of(-5536)));
  }

  [Test]
  public void Analyze_GivenLongHoldsWiderRange_ThenTracked() =>
    Assert.That(RangeOf("x& = 30000\ny& = x& + 30000\nEND", "y"), Is.EqualTo(Interval.Of(60000)));

  #endregion

  #region per-program-point queries

  private static (PowerBasic.Compiler.Syntax.Ast.CompilationUnit unit, SemanticModel model) BindUnit(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", Dialect.Pb36), "TEST.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return (unit, model);
  }

  private static Interval? At(IReadOnlyDictionary<PowerBasic.Compiler.Syntax.Ast.Statement, IReadOnlyDictionary<VariableSymbol, ValueFacts>> points,
      PowerBasic.Compiler.Syntax.Ast.Statement stmt, string varName) {
    if (!points.TryGetValue(stmt, out var env))
      return null;
    foreach (var kv in env)
      if (kv.Key.Name.Equals(varName, StringComparison.OrdinalIgnoreCase))
        return kv.Value.Range.IsTop ? null : kv.Value.Range;
    return null;
  }

  [Test]
  public void ProgramPoints_GivenUse_ThenEntryEnvHasPriorRange() {
    var (_, model) = BindUnit("x% = 5\ny% = x% + 1\nEND");
    var points = IntervalRangeAnalysis.AnalyzeProgramPoints(model.MainBody, model);
    Assert.That(At(points, model.MainBody[1], "x"), Is.EqualTo(Interval.Of(5)));
    Assert.That(At(points, model.MainBody[0], "x"), Is.Null);
  }

  [Test]
  public void ProgramPoints_GivenALoopTheAnalysisRefuses_ThenItsOwnPointIsNotThePreLoopEnvironment() {
    var (_, model) = BindUnit("DECLARE SUB Note(BYVAL v%)\ni% = 0\nWHILE i% < 3\ni% = i% + 1\nNote i%\nWEND\nEND\nSUB Note(BYVAL v%)\nEND SUB");
    var points = IntervalRangeAnalysis.AnalyzeProgramPoints(model.MainBody, model);
    var loop = model.MainBody.OfType<PowerBasic.Compiler.Syntax.Ast.DoLoopStmt>().Single();
    Assert.That(At(points, loop, "i"), Is.Null);
  }

  [Test]
  public void ProgramPoints_GivenALoopTheAnalysisModels_ThenItsOwnPointIsTheWidenedInvariant() {
    var (_, model) = BindUnit("i% = 0\nWHILE i% < 3\ni% = i% + 1\nWEND\nEND");
    var points = IntervalRangeAnalysis.AnalyzeProgramPoints(model.MainBody, model);
    var loop = model.MainBody.OfType<PowerBasic.Compiler.Syntax.Ast.DoLoopStmt>().Single();
    Assert.That(At(points, loop, "i"), Is.Not.EqualTo(Interval.Of(0)));
  }

  [Test]
  public void Refine_GivenGuardedComparison_ThenCrossDomainFactsSelectReachableJoinedValue() {
    var (_, model) = BindUnit("x% = 0\nIF c% > 0 THEN x% = 100\nIF x% < 50 THEN\nq% = x%\nEND IF\nEND");
    var points = IntervalRangeAnalysis.AnalyzeProgramPoints(model.MainBody, model);
    var guard = (PowerBasic.Compiler.Syntax.Ast.IfStmt)model.MainBody[2];
    Assert.That(At(points, guard.Then[0], "x"), Is.EqualTo(Interval.Of(0)));
  }

  [Test]
  public void Refine_GivenGuardElse_ThenCrossDomainFactsSelectOtherJoinedValue() {
    var (_, model) = BindUnit("x% = 0\nIF c% > 0 THEN x% = 100\nIF x% < 50 THEN\nq% = 1\nELSE\nq% = x%\nEND IF\nEND");
    var points = IntervalRangeAnalysis.AnalyzeProgramPoints(model.MainBody, model);
    var guard = (PowerBasic.Compiler.Syntax.Ast.IfStmt)model.MainBody[2];
    Assert.That(At(points, guard.Else![0], "x"), Is.EqualTo(Interval.Of(100)));
  }

  [Test]
  public void ProgramPoints_GivenUseInsideIfArm_ThenArmEntryHasRange() {
    var (_, model) = BindUnit("x% = 5\nIF a% > 0 THEN\nz% = x% \\ 2\nEND IF\nEND");
    var points = IntervalRangeAnalysis.AnalyzeProgramPoints(model.MainBody, model);
    var iff = (PowerBasic.Compiler.Syntax.Ast.IfStmt)model.MainBody[1];
    Assert.That(At(points, iff.Then[0], "x"), Is.EqualTo(Interval.Of(5)));
  }

  #endregion

  #region loops

  [Test]
  public void Loop_GivenForCounterUseInBody_ThenCounterRangeAvailable() {
    var (_, model) = BindUnit("FOR i% = 1 TO 10\nx% = i% \\ 2\nNEXT i%\nEND");
    var points = IntervalRangeAnalysis.AnalyzeProgramPoints(model.MainBody, model);
    var loop = (PowerBasic.Compiler.Syntax.Ast.ForStmt)model.MainBody[0];
    Assert.That(At(points, loop.Body[0], "i"), Is.EqualTo(new Interval(1, 10)));
  }

  [Test]
  public void Loop_GivenDerivedVarInBody_ThenBounded() {
    var (_, model) = BindUnit("FOR i% = 1 TO 10\nj% = i% + 5\ny% = j% * 2\nNEXT i%\nEND");
    var points = IntervalRangeAnalysis.AnalyzeProgramPoints(model.MainBody, model);
    var loop = (PowerBasic.Compiler.Syntax.Ast.ForStmt)model.MainBody[0];
    Assert.That(At(points, loop.Body[1], "j"), Is.EqualTo(new Interval(6, 15)));
  }

  [Test]
  public void Loop_GivenAccumulator_ThenWidensToTopAfter() =>
    Assert.That(RangeOf("s% = 0\nFOR i% = 1 TO 10\ns% = s% + i%\nNEXT i%\nPRINT s%\nEND", "s"), Is.Null);

  [Test]
  public void Loop_GivenCounter_ThenTopAfterLoop() =>
    Assert.That(RangeOf("FOR i% = 1 TO 10\nx% = i%\nNEXT i%\nPRINT x%\nEND", "i"), Is.Null);

  [Test]
  public void Loop_GivenConstAssignBeforeLoop_ThenSurvivesLoopThatDoesNotTouchIt() =>
    Assert.That(RangeOf("k% = 7\nFOR i% = 1 TO 5\nx% = i%\nNEXT i%\nPRINT x%\nEND", "k"),
      Is.EqualTo(Interval.Of(7)));

  #endregion

  #region kill sets

  private static Interval? RangeInProc(string source, string procedure, string varName) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", Dialect.Pb36), "TEST.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var proc = model.Procedures.Values.First(p => p.Name.Equals(procedure, StringComparison.OrdinalIgnoreCase));
    var env = IntervalRangeAnalysis.Analyze(proc.Body!, model);
    foreach (var kv in env)
      if (kv.Key.Name.Equals(varName, StringComparison.OrdinalIgnoreCase))
        return kv.Value.Range.IsTop ? null : kv.Value.Range;
    return null;
  }

  private const string _NOISE = "DECLARE SUB Noise()\nDECLARE SUB Touch(k%)\nDECLARE SUB P(BYVAL s%)\nP 1\nEND\n";
  private const string _NOISE_BODY = "\nSUB Noise()\ng% = g% + 1\nEND SUB\nSUB Touch(k%)\nk% = 99\nEND SUB\n";

  [Test]
  public void Analyze_GivenOpaqueCall_WhenLocalIsPrivate_ThenRangeSurvives() =>
    Assert.That(RangeInProc(_NOISE + "SUB P(BYVAL s%)\nk% = 3\nNoise\ny% = k% + 1\nEND SUB" + _NOISE_BODY, "P", "y"),
      Is.EqualTo(Interval.Of(4)));

  [Test]
  public void Analyze_GivenOpaqueCall_WhenVariableIsGlobal_ThenRangeIsDropped() =>
    Assert.That(RangeOf("DECLARE SUB Noise()\nx% = 5\nNoise\ny% = x% + 1\nEND\nSUB Noise()\ng% = g% + 1\nEND SUB", "y"),
      Is.Null);

  [Test]
  public void Analyze_GivenCallTakingTheVariable_ThenItIsDropped() =>
    Assert.That(RangeInProc(_NOISE + "SUB P(BYVAL s%)\nk% = 3\nTouch k%\ny% = k% + 1\nEND SUB" + _NOISE_BODY, "P", "y"),
      Is.Null);

  [Test]
  public void Analyze_GivenTakenAddressAnywhere_ThenCallDropsEvenPrivateLocals() =>
    Assert.That(RangeInProc(_NOISE + "SUB P(BYVAL s%)\nk% = 3\np??? = VARPTR(k%)\nNoise\ny% = k% + 1\nEND SUB" + _NOISE_BODY, "P", "y"),
      Is.Null);

  [Test]
  public void Analyze_GivenBackwardGoto_ThenLabelResetsRanges() =>
    Assert.That(RangeOf("x% = 5\ntop:\ny% = x% + 1\nIF a% THEN GOTO top\nEND", "y"), Is.Null);

  [Test]
  public void Analyze_GivenNoJumps_ThenLabelIsInert() =>
    Assert.That(RangeOf("x% = 5\ntop:\ny% = x% + 1\nEND", "y"), Is.EqualTo(Interval.Of(6)));

  [Test]
  public void Analyze_GivenAndedBounds_ThenBothHalvesRefine() =>
    Assert.That(RangeOf("y% = 100\nIF v% >= 0 AND v% <= 7 THEN\ny% = v%\nEND IF\nEND", "y"),
      Is.EqualTo(new Interval(0, 100)));

  [Test]
  public void Analyze_GivenOredBounds_ThenElseArmRefines() =>
    Assert.That(RangeOf("y% = 100\nIF v% < 0 OR v% > 7 THEN\nz% = 1\nELSE\ny% = v%\nEND IF\nEND", "y"),
      Is.EqualTo(new Interval(0, 100)));

  [Test]
  public void Analyze_GivenNotCondition_ThenRefinementFlips() =>
    Assert.That(RangeOf("y% = 100\nIF NOT (v% > 7) THEN\ny% = v%\nEND IF\nEND", "y")?.Hi, Is.EqualTo(100));

  [Test]
  public void Analyze_GivenBitwiseAndOfNonConditions_ThenNoRefinement() =>
    Assert.That(RangeOf("y% = 0\nIF v% AND 3 THEN\ny% = v%\nEND IF\nEND", "y"), Is.Null);

  #endregion

  #region known bits

  private static KnownBits BitsOf(string source, string varName) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", Dialect.Pb36), "TEST.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    foreach (var kv in IntervalRangeAnalysis.Analyze(model.MainBody, model))
      if (kv.Key.Name.Equals(varName, StringComparison.OrdinalIgnoreCase))
        return kv.Value.Bits;
    return KnownBits.Unknown;
  }

  [Test]
  public void Bits_GivenMultiplyByFour_ThenLowTwoBitsAreZero() {
    var bits = BitsOf("x% = n% * 4\nEND", "x");
    Assert.That(bits.TrailingZeros, Is.GreaterThanOrEqualTo(2));
    Assert.That(bits.Allows(3, 16), Is.False);
    Assert.That(bits.Allows(8, 16), Is.True);
  }

  [Test]
  public void Bits_GivenHalveThenDouble_ThenValueIsEven() {
    var bits = BitsOf("x% = (n% \\ 2) * 2\nEND", "x");
    Assert.That(bits.Allows(1, 16), Is.False);
    Assert.That(bits.Allows(4, 16), Is.True);
  }

  [Test]
  public void Bits_GivenMaskedValue_ThenOnlyMaskBitsCanBeSet() {
    var bits = BitsOf("x% = n% AND 12\nEND", "x");
    Assert.That(bits.Allows(5, 16), Is.False);
    Assert.That(bits.Allows(12, 16), Is.True);
  }

  [Test]
  public void Bits_GivenOredBit_ThenThatBitIsAlwaysSet() {
    var bits = BitsOf("x% = n% OR 1\nEND", "x");
    Assert.That(bits.Allows(8, 16), Is.False);
    Assert.That(bits.Allows(9, 16), Is.True);
  }

  [Test]
  public void Bits_GivenWrappingArithmetic_ThenLowBitsSurvive() {
    var bits = BitsOf("x% = 30000\ny% = x% + 30000\nEND", "y");
    Assert.That(bits.Allows(unchecked((short)60000), 16), Is.True);
    Assert.That(bits.Allows(0, 16), Is.False);
  }

  [Test]
  public void Bits_GivenJoinOfTwoArms_ThenOnlyAgreedBitsSurvive() {
    var bits = BitsOf("IF a% > 0 THEN\nx% = 5\nELSE\nx% = 9\nEND IF\nEND", "x");
    Assert.That(bits.Allows(4, 16), Is.False);
    Assert.That(bits.Allows(13, 16), Is.True);
  }

  #endregion

  #region SELECT CASE refinement

  [Test]
  public void Analyze_GivenSelectRangeArm_ThenSubjectNarrowedInsideIt() =>
    Assert.That(RangeOf("y% = 100\nSELECT CASE v%\nCASE 0 TO 7\ny% = v%\nEND SELECT\nEND", "y"),
      Is.EqualTo(new Interval(0, 100)));

  [Test]
  public void Analyze_GivenSelectValueArm_ThenSubjectIsThatValue() =>
    Assert.That(RangeOf("y% = 100\nSELECT CASE v%\nCASE 4\ny% = v%\nEND SELECT\nEND", "y"),
      Is.EqualTo(new Interval(4, 100)));

  [Test]
  public void Analyze_GivenSelectIsComparisonArm_ThenSubjectIsBounded() =>
    Assert.That(RangeOf("y% = 0\nSELECT CASE v%\nCASE IS <= 5\ny% = v%\nEND SELECT\nEND", "y")?.Hi,
      Is.EqualTo(5));

  [Test]
  public void Analyze_GivenSelectElseArm_ThenNoRefinementAndNoFallthrough() =>
    Assert.That(RangeOf("v% = 3\ny% = 100\nSELECT CASE v%\nCASE ELSE\ny% = v%\nEND SELECT\nEND", "y"),
      Is.EqualTo(Interval.Of(3)));

  #endregion
}
