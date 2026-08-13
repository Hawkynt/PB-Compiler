using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Analysis;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// The IR range lattice and the trap elision it pays for.
///
/// <para>
/// Every case here is stated as "how many <c>rt_error</c> calls survive the pipeline", because that is
/// the observable the whole analysis exists to move and it is target-independent - the same count
/// decides what the x86-16 selector, the C emitter and the LLVM emitter each have to render. Both
/// directions are tested throughout: a check the lattice proves impossible must go, and the same
/// program with the bound taken away must keep it. A pass that only ever removes checks would pass
/// half of these.
/// </para>
/// </summary>
[TestFixture]
public sealed class RangeCheckElimTests {

  /// <summary>The optimized module for a pb36 source, or a failure naming why it declined.</summary>
  private static IrModule Optimized(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    IrPassManager.Standard(optimizeForSpeed: true).RunOnModule(module!);
    return module!;
  }

  /// <summary>How many <c>rt_error</c> calls with the given code survive.</summary>
  private static int Raises(IrModule module, int code) => module.Functions
    .SelectMany(f => f.Blocks)
    .SelectMany(b => b.Instructions)
    .OfType<IrCall>()
    .Count(c => (c.Callee as IrFunction)?.Name == "rt_error"
                && c.Args.FirstOrDefault() is IrConstantInt k && k.Value == code);

  #region $ERROR BOUNDS - the subscript that cannot leave its dimension

  [Test]
  public void Elim_GivenForCounterSubscript_WhenOptimized_ThenTheBoundsCheckIsGone() {
    // the counter is a phi bounded below by its initial value and above by the loop's own test, which
    // is a fact about an EDGE - the plain fixpoint cannot see it and the per-block refinement can
    const string inRange = """
      $ERROR BOUNDS ON
      DIM a%(1 TO 10)
      FOR i% = 1 TO 10
        a%(i%) = i%
      NEXT i%
      """;
    Assert.That(Raises(Optimized(inRange), 9), Is.Zero, "a counter that cannot leave the array needs no check");
  }

  [Test]
  public void Elim_GivenForCounterPastTheUpperBound_WhenOptimized_ThenTheBoundsCheckStays() {
    // one element too far, and nothing else changed. The check has to survive, and this is the case
    // that would notice a lattice that answered from the array's bounds instead of the counter's.
    const string outOfRange = """
      $ERROR BOUNDS ON
      DIM a%(1 TO 10)
      FOR i% = 1 TO 11
        a%(i%) = i%
      NEXT i%
      """;
    Assert.That(Raises(Optimized(outOfRange), 9), Is.GreaterThan(0), "a counter that can run off the end must still trap");
  }

  [Test]
  public void Elim_GivenMaskedSubscript_WhenOptimized_ThenTheBoundsCheckIsGone() {
    // x AND 7 is in [0, 7] however unknown x is - the one-sided AND rule, and the only fact here that
    // does not come from a loop
    const string masked = """
      $ERROR BOUNDS ON
      DIM a%(0 TO 7)
      INPUT x%
      a%(x% AND 7) = 1
      """;
    Assert.That(Raises(Optimized(masked), 9), Is.Zero, "a masked subscript cannot leave [0, mask]");
  }

  [Test]
  public void Elim_GivenMaskWiderThanTheArray_WhenOptimized_ThenTheBoundsCheckStays() {
    const string masked = """
      $ERROR BOUNDS ON
      DIM a%(0 TO 7)
      INPUT x%
      a%(x% AND 15) = 1
      """;
    Assert.That(Raises(Optimized(masked), 9), Is.GreaterThan(0), "a mask admitting 15 does not fit an array of 8");
  }

  [Test]
  public void Elim_GivenSubscriptFromAnIfJoin_WhenOptimized_ThenTheBoundsCheckIsGone() {
    // k% is neither a constant nor a counter - it is the join of two arms, which is the case the
    // direct emitter's lattice was built for and the phi join answers here
    const string joined = """
      $ERROR BOUNDS ON
      DIM a%(0 TO 20)
      INPUT c%
      k% = 5
      IF c% > 0 THEN k% = 10
      a%(k%) = k%
      """;
    Assert.That(Raises(Optimized(joined), 9), Is.Zero, "[5, 10] lies inside (0 TO 20)");
  }

  #endregion

  #region $ERROR OVERFLOW - the sum that cannot leave its type

  [Test]
  public void Elim_GivenBoundedCounterAdd_WhenOptimized_ThenTheOverflowTrapIsGone() {
    const string bounded = """
      $ERROR OVERFLOW ON
      FOR i% = 1 TO 100
        x% = i% + 1
      NEXT i%
      """;
    Assert.That(Raises(Optimized(bounded), 6), Is.Zero, "[1, 100] + 1 cannot leave an INTEGER");
  }

  [Test]
  public void Elim_GivenAnUnknownAdd_WhenOptimized_ThenTheOverflowTrapStays() {
    // the same statement over a value the lattice knows nothing about. It is the pair that makes the
    // test above mean anything: an elision that fired here would be a silent miscompile, and the
    // program would simply print a wrapped number where PowerBASIC raises Error 6.
    const string unknown = """
      $ERROR OVERFLOW ON
      INPUT k%
      x% = k% + 1
      PRINT x%
      """;
    Assert.That(Raises(Optimized(unknown), 6), Is.GreaterThan(0), "an unknown operand can always overflow");
  }

  [Test]
  public void Elim_GivenBoundedLongCounterSubtract_WhenOptimized_ThenTheOverflowTrapIsGone() {
    const string bounded = """
      $ERROR OVERFLOW ON
      FOR i& = 1 TO 100
        x& = i& - 1&
      NEXT i&
      """;
    Assert.That(Raises(Optimized(bounded), 6), Is.Zero, "[1, 100] - 1 cannot leave a LONG");
  }

  #endregion

  #region the divide-by-zero guard

  [Test]
  public void Elim_GivenACounterDivisor_WhenOptimized_ThenTheZeroGuardIsGone() {
    const string nonZero = """
      FOR i% = 1 TO 10
        x% = 100 \ i%
      NEXT i%
      PRINT x%
      """;
    Assert.That(Raises(Optimized(nonZero), 11), Is.Zero, "a divisor in [1, 10] cannot be zero");
  }

  [Test]
  public void Elim_GivenACounterDivisorThatReachesZero_WhenOptimized_ThenTheZeroGuardStays() {
    const string reachesZero = """
      FOR i% = 0 TO 10
        x% = 100 \ i%
      NEXT i%
      PRINT x%
      """;
    Assert.That(Raises(Optimized(reachesZero), 11), Is.GreaterThan(0), "a divisor starting at zero must still trap");
  }

  #endregion

  #region the lattice itself

  [Test]
  public void Range_GivenAnUnsignedType_WhenAskedForItsWholeRange_ThenItIsNotTheSignedOne() {
    // the ranges live in VALUE space, not bit-pattern space: a WORD holding 40000 is 40000, and an
    // analysis that read it as -25536 would prove the wrong things about every DWORD in the program
    Assert.That(ValueRange.OfType(IrType.Integer(16, signed: false)), Is.EqualTo(new ValueRange(0, 65535)));
    Assert.That(ValueRange.OfType(IrType.Integer(16, signed: true)), Is.EqualTo(new ValueRange(-32768, 32767)));
    Assert.That(ValueRange.OfType(IrType.Integer(64, signed: false)).IsTop, Is.True,
      "a QWORD's upper end does not fit a long, so nothing may be claimed about it");
  }

  [Test]
  public void Range_GivenOneNonNegativeOperand_WhenAnded_ThenTheResultIsBounded() {
    // the asymmetric rule, and the reason the signed overflow trap decides at all
    Assert.That(ValueRange.Top.And(new ValueRange(0, 127)), Is.EqualTo(new ValueRange(0, 127)));
    Assert.That(new ValueRange(0, 127).And(ValueRange.Top), Is.EqualTo(new ValueRange(0, 127)));
    Assert.That(ValueRange.Top.And(new ValueRange(-1, 127)).IsTop, Is.True,
      "an operand that may be negative bounds nothing - its sign bit may be set");
  }

  [Test]
  public void Range_GivenADivisorSpanningZero_WhenDivided_ThenNothingIsClaimed() {
    Assert.That(new ValueRange(1, 100).Divide(new ValueRange(-2, 2)).IsTop, Is.True);
    Assert.That(new ValueRange(1, 100).Divide(new ValueRange(2, 4)), Is.EqualTo(new ValueRange(0, 50)));
  }

  #endregion
}
