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
        return kv.Value.Range.IsTop ? null : kv.Value.Range;   // Top reads as "no range known"
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

  [Test]
  public void Analyze_GivenIntegerOverflow_ThenExactWrappedRangeRecoveredFromBits() {
    // The mathematical interval 60000 is invalid for INTEGER, but fixed-width known-bit transfer
    // computes the actual two's-complement result exactly: 60000 mod 2^16 = -5536.
    Assert.That(RangeOf("x% = 30000\ny% = x% + 30000\nEND", "y"), Is.EqualTo(Interval.Of(-5536)));
  }

  [Test]
  public void Analyze_GivenLongHoldsWiderRange_ThenTracked() =>
    // the same sum fits a LONG, so it is tracked there (no wrap)
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
      if (kv.Key.Name.Equals(varName, System.StringComparison.OrdinalIgnoreCase))
        return kv.Value.Range.IsTop ? null : kv.Value.Range;
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

  /// <summary>
  /// A loop's own program point is the widened invariant, never the pre-loop environment - and that
  /// has to hold for a loop the analysis CANNOT model as well as for one it can. The emitter reads
  /// this point while emitting the loop's pre-test, which runs again on every back edge, so an entry
  /// snapshot there is a statement about the first iteration presented as one about all of them.
  ///
  /// <para>
  /// <c>i% = 0 : WHILE i% &lt; 3 : i% = i% + 1 : Note i% : WEND</c> is the shape: the call makes the
  /// body unanalyzable, so <c>TransferLoop</c> - the only writer of a loop's point - never ran, and
  /// the snapshot taken in front of it survived saying <c>i = [0,0]</c>. O16's
  /// <c>TryEmitRangeComparison</c> then folded <c>i% &lt; 3</c> to a constant TRUE (<c>MOV AX,-1</c>)
  /// and the program never terminated. No entry at all is the honest answer; absence means Top.
  /// </para>
  /// </summary>
  [Test]
  public void ProgramPoints_GivenALoopTheAnalysisRefuses_ThenItsOwnPointIsNotThePreLoopEnvironment() {
    var (_, model) = BindUnit("DECLARE SUB Note(BYVAL v%)\ni% = 0\nWHILE i% < 3\ni% = i% + 1\nNote i%\nWEND\nEND\nSUB Note(BYVAL v%)\nEND SUB");
    var points = IntervalRangeAnalysis.AnalyzeProgramPoints(model.MainBody, model);
    var loop = model.MainBody.OfType<PowerBasic.Compiler.Syntax.Ast.DoLoopStmt>().Single();

    Assert.That(At(points, loop, "i"), Is.Null,
      "the loop test re-runs with loop-carried values, so the pre-loop range must not be readable here");
  }

  /// <summary>The twin: a loop the analysis DOES model still records the widened invariant, so precision is not the price.</summary>
  [Test]
  public void ProgramPoints_GivenALoopTheAnalysisModels_ThenItsOwnPointIsTheWidenedInvariant() {
    var (_, model) = BindUnit("i% = 0\nWHILE i% < 3\ni% = i% + 1\nWEND\nEND");
    var points = IntervalRangeAnalysis.AnalyzeProgramPoints(model.MainBody, model);
    var loop = model.MainBody.OfType<PowerBasic.Compiler.Syntax.Ast.DoLoopStmt>().Single();

    Assert.That(At(points, loop, "i"), Is.Not.EqualTo(Interval.Of(0)),
      "an analyzable loop widens its counter rather than reporting the value it entered with");
  }

  [Test]
  public void Refine_GivenGuardedComparison_ThenCrossDomainFactsSelectReachableJoinedValue() {
    // The preceding IF leaves x% in the set {0,100}. The interval alone says [0,100], but the
    // joined congruence/bit facts retain the hole. Under x% < 50 the only reachable value is 0.
    var (_, model) = BindUnit("x% = 0\nIF c% > 0 THEN x% = 100\nIF x% < 50 THEN\nq% = x%\nEND IF\nEND");
    var points = IntervalRangeAnalysis.AnalyzeProgramPoints(model.MainBody, model);
    var guard = (PowerBasic.Compiler.Syntax.Ast.IfStmt)model.MainBody[2];
    Assert.That(At(points, guard.Then[0], "x"), Is.EqualTo(Interval.Of(0)));
  }

  [Test]
  public void Refine_GivenGuardElse_ThenCrossDomainFactsSelectOtherJoinedValue() {
    // The ELSE requires x% >= 50; from the joined set {0,100}, that leaves exactly 100.
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
    // x% is [5,5] at the entry of the statement inside the THEN arm
    Assert.That(At(points, iff.Then[0], "x"), Is.EqualTo(Interval.Of(5)));
  }

  #endregion

  #region loops (fixpoint + widening)

  [Test]
  public void Loop_GivenForCounterUseInBody_ThenCounterRangeAvailable() {
    var (_, model) = BindUnit("FOR i% = 1 TO 10\nx% = i% \\ 2\nNEXT i%\nEND");
    var points = IntervalRangeAnalysis.AnalyzeProgramPoints(model.MainBody, model);
    var loop = (PowerBasic.Compiler.Syntax.Ast.ForStmt)model.MainBody[0];
    // inside the loop body the counter is proven to be in [1,10]
    Assert.That(At(points, loop.Body[0], "i"), Is.EqualTo(new Interval(1, 10)));
  }

  [Test]
  public void Loop_GivenDerivedVarInBody_ThenBounded() {
    var (_, model) = BindUnit("FOR i% = 1 TO 10\nj% = i% + 5\ny% = j% * 2\nNEXT i%\nEND");
    var points = IntervalRangeAnalysis.AnalyzeProgramPoints(model.MainBody, model);
    var loop = (PowerBasic.Compiler.Syntax.Ast.ForStmt)model.MainBody[0];
    // j% = i% + 5 over i% in [1,10] is [6,15] at the entry of the next statement
    Assert.That(At(points, loop.Body[1], "j"), Is.EqualTo(new Interval(6, 15)));
  }

  [Test]
  public void Loop_GivenAccumulator_ThenWidensToTopAfter() =>
    // s% accumulates without a static bound, so the lattice widens it to Top (untracked)
    Assert.That(RangeOf("s% = 0\nFOR i% = 1 TO 10\ns% = s% + i%\nNEXT i%\nPRINT s%\nEND", "s"), Is.Null);

  [Test]
  public void Loop_GivenCounter_ThenTopAfterLoop() =>
    // the post-loop counter value (the end value) is not tracked
    Assert.That(RangeOf("FOR i% = 1 TO 10\nx% = i%\nNEXT i%\nPRINT x%\nEND", "i"), Is.Null);

  [Test]
  public void Loop_GivenConstAssignBeforeLoop_ThenSurvivesLoopThatDoesNotTouchIt() =>
    // k% is set before the loop and never written in it, so its range survives the loop
    Assert.That(RangeOf("k% = 7\nFOR i% = 1 TO 5\nx% = i%\nNEXT i%\nPRINT x%\nEND", "k"),
      Is.EqualTo(Interval.Of(7)));

  #endregion

  #region kill sets - what a call, a jump or an escaped address invalidates

  /// <summary>The range of a variable inside <paramref name="procedure"/>'s body, at its end.</summary>
  private static Interval? RangeInProc(string source, string procedure, string varName) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", Dialect.Pb36), "TEST.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var proc = model.Procedures.Values.First(p => p.Name.Equals(procedure, System.StringComparison.OrdinalIgnoreCase));
    var env = IntervalRangeAnalysis.Analyze(proc.Body!, model);
    foreach (var kv in env)
      if (kv.Key.Name.Equals(varName, System.StringComparison.OrdinalIgnoreCase))
        return kv.Value.Range.IsTop ? null : kv.Value.Range;
    return null;
  }

  private const string _NOISE = "DECLARE SUB Noise()\nDECLARE SUB Touch(k%)\nDECLARE SUB P(BYVAL s%)\nP 1\nEND\n";
  private const string _NOISE_BODY = "\nSUB Noise()\ng% = g% + 1\nEND SUB\nSUB Touch(k%)\nk% = 99\nEND SUB\n";

  [Test]
  public void Analyze_GivenOpaqueCall_WhenLocalIsPrivate_ThenRangeSurvives() =>
    // Noise cannot name P's locals, so k% keeps its range across the call
    Assert.That(RangeInProc(_NOISE + "SUB P(BYVAL s%)\nk% = 3\nNoise\ny% = k% + 1\nEND SUB" + _NOISE_BODY, "P", "y"),
      Is.EqualTo(Interval.Of(4)));

  [Test]
  public void Analyze_GivenOpaqueCall_WhenVariableIsGlobal_ThenRangeIsDropped() =>
    // a module-level variable IS reachable from the callee (SHARED/PUBLIC), so it must be dropped
    Assert.That(RangeOf("DECLARE SUB Noise()\nx% = 5\nNoise\ny% = x% + 1\nEND\nSUB Noise()\ng% = g% + 1\nEND SUB", "y"),
      Is.Null);

  [Test]
  public void Analyze_GivenCallTakingTheVariable_ThenItIsDropped() =>
    // k% is handed to Touch, which takes it BYREF and writes it
    Assert.That(RangeInProc(_NOISE + "SUB P(BYVAL s%)\nk% = 3\nTouch k%\ny% = k% + 1\nEND SUB" + _NOISE_BODY, "P", "y"),
      Is.Null);

  [Test]
  public void Analyze_GivenTakenAddressAnywhere_ThenCallDropsEvenPrivateLocals() =>
    // once VARPTR hands out an address in this body, a callee could write any local through it
    Assert.That(RangeInProc(_NOISE + "SUB P(BYVAL s%)\nk% = 3\np??? = VARPTR(k%)\nNoise\ny% = k% + 1\nEND SUB" + _NOISE_BODY, "P", "y"),
      Is.Null);

  [Test]
  public void Analyze_GivenBackwardGoto_ThenLabelResetsRanges() =>
    // the label is reachable from the GOTO below it, carrying a state this walk never saw
    Assert.That(RangeOf("x% = 5\ntop:\ny% = x% + 1\nIF a% THEN GOTO top\nEND", "y"), Is.Null);

  [Test]
  public void Analyze_GivenNoJumps_ThenLabelIsInert() =>
    // no jump can target it, so a label costs no precision
    Assert.That(RangeOf("x% = 5\ntop:\ny% = x% + 1\nEND", "y"), Is.EqualTo(Interval.Of(6)));

  [Test]
  public void Analyze_GivenAndedBounds_ThenBothHalvesRefine() =>
    // IF v% >= 0 AND v% <= 7 - the everyday guarded-index spelling; each half decides one endpoint
    Assert.That(RangeOf("y% = 100\nIF v% >= 0 AND v% <= 7 THEN\ny% = v%\nEND IF\nEND", "y"),
      Is.EqualTo(new Interval(0, 100)));

  [Test]
  public void Analyze_GivenOredBounds_ThenElseArmRefines() =>
    // the ELSE of "v% < 0 OR v% > 7" is exactly "in [0,7]" - refinement through the false side
    Assert.That(RangeOf("y% = 100\nIF v% < 0 OR v% > 7 THEN\nz% = 1\nELSE\ny% = v%\nEND IF\nEND", "y"),
      Is.EqualTo(new Interval(0, 100)));

  [Test]
  public void Analyze_GivenNotCondition_ThenRefinementFlips() =>
    Assert.That(RangeOf("y% = 100\nIF NOT (v% > 7) THEN\ny% = v%\nEND IF\nEND", "y")?.Hi, Is.EqualTo(100));

  [Test]
  public void Analyze_GivenBitwiseAndOfNonConditions_ThenNoRefinement() =>
    // "v% AND 3" is bit twiddling, not a conjunction of conditions - refining from it would be wrong
    Assert.That(RangeOf("y% = 0\nIF v% AND 3 THEN\ny% = v%\nEND IF\nEND", "y"), Is.Null);

  #endregion

  #region known bits

  private static KnownBits BitsOf(string source, string varName) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", Dialect.Pb36), "TEST.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    foreach (var kv in IntervalRangeAnalysis.Analyze(model.MainBody, model))
      if (kv.Key.Name.Equals(varName, System.StringComparison.OrdinalIgnoreCase))
        return kv.Value.Bits;
    return KnownBits.Unknown;
  }

  [Test]
  public void Bits_GivenMultiplyByFour_ThenLowTwoBitsAreZero() {
    // n% is never assigned, so its value is unknown: no interval helps, but n%*4 is always a
    // multiple of 4 whatever n% is
    var bits = BitsOf("x% = n% * 4\nEND", "x");
    Assert.That(bits.TrailingZeros, Is.GreaterThanOrEqualTo(2));
    Assert.That(bits.Allows(3, 16), Is.False, "3 is odd, so it cannot be a multiple of 4");
    Assert.That(bits.Allows(8, 16), Is.True);
  }

  [Test]
  public void Bits_GivenHalveThenDouble_ThenValueIsEven() {
    // (n \ 2) * 2 - the classic case an interval cannot express at all. The parentheses matter:
    // BASIC binds \ looser than *, so "n \ 2 * 2" would mean n \ 4.
    var bits = BitsOf("x% = (n% \\ 2) * 2\nEND", "x");
    Assert.That(bits.Allows(1, 16), Is.False, "an even value can never be 1");
    Assert.That(bits.Allows(4, 16), Is.True);
  }

  [Test]
  public void Bits_GivenMaskedValue_ThenOnlyMaskBitsCanBeSet() {
    var bits = BitsOf("x% = n% AND 12\nEND", "x");
    Assert.That(bits.Allows(5, 16), Is.False, "bit 0 is masked off, so 5 is impossible");
    Assert.That(bits.Allows(12, 16), Is.True);
  }

  [Test]
  public void Bits_GivenOredBit_ThenThatBitIsAlwaysSet() {
    var bits = BitsOf("x% = n% OR 1\nEND", "x");
    Assert.That(bits.Allows(8, 16), Is.False, "bit 0 is forced on, so an even value is impossible");
    Assert.That(bits.Allows(9, 16), Is.True);
  }

  [Test]
  public void Bits_GivenWrappingArithmetic_ThenLowBitsSurvive() {
    // 30000 + 30000 wraps, so the mathematical interval 60000 is invalid; the bit transfer is
    // modulo 2^16 and therefore remains exact, which may now recover the wrapped interval too.
    var bits = BitsOf("x% = 30000\ny% = x% + 30000\nEND", "y");
    Assert.That(bits.Allows(unchecked((short)60000), 16), Is.True);
    Assert.That(bits.Allows(0, 16), Is.False);
  }

  [Test]
  public void Bits_GivenJoinOfTwoArms_ThenOnlyAgreedBitsSurvive() {
    // both arms leave bit 0 set, so that survives the merge even though the values differ
    var bits = BitsOf("IF a% > 0 THEN\nx% = 5\nELSE\nx% = 9\nEND IF\nEND", "x");
    Assert.That(bits.Allows(4, 16), Is.False, "both arms are odd");
    Assert.That(bits.Allows(13, 16), Is.True);
  }

  #endregion

  #region SELECT CASE refinement

  [Test]
  public void Analyze_GivenSelectRangeArm_ThenSubjectNarrowedInsideIt() =>
    // inside CASE 0 TO 7 the subject is in [0,7]; joined with the no-match path's y% = 100
    Assert.That(RangeOf("y% = 100\nSELECT CASE v%\nCASE 0 TO 7\ny% = v%\nEND SELECT\nEND", "y"),
      Is.EqualTo(new Interval(0, 100)));

  [Test]
  public void Analyze_GivenSelectValueArm_ThenSubjectIsThatValue() =>
    Assert.That(RangeOf("y% = 100\nSELECT CASE v%\nCASE 4\ny% = v%\nEND SELECT\nEND", "y"),
      Is.EqualTo(new Interval(4, 100)));

  [Test]
  public void Analyze_GivenSelectIsComparisonArm_ThenSubjectIsBounded() =>
    // CASE IS <= 5 bounds the subject above but not below
    Assert.That(RangeOf("y% = 0\nSELECT CASE v%\nCASE IS <= 5\ny% = v%\nEND SELECT\nEND", "y")?.Hi,
      Is.EqualTo(5));

  [Test]
  public void Analyze_GivenSelectElseArm_ThenNoRefinementAndNoFallthrough() =>
    // CASE ELSE admits everything and always runs when nothing else matched, so it is the only
    // path out of this SELECT - y% is exactly v%, with no unmatched fall-through to join
    Assert.That(RangeOf("v% = 3\ny% = 100\nSELECT CASE v%\nCASE ELSE\ny% = v%\nEND SELECT\nEND", "y"),
      Is.EqualTo(Interval.Of(3)));

  #endregion
}
