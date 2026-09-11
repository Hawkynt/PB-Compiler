using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0338 target-cost propagation and reciprocal-formation accounting.</summary>
[TestFixture]
public sealed class ReciprocalCostPipelineTests {

  private sealed class NeverReuseCost : IIrArithmeticCostModel {
    public bool PreferReciprocalReuse(IrType type, int divisionCount) => false;
  }

  private sealed class ExistingOnlyCost : IIrArithmeticCostModel {
    public bool PreferReciprocalReuse(IrType type, int divisionCount) => false;
    public bool PreferExistingReciprocalUse(IrType type, int divisionCount) => divisionCount > 0;
  }

  /// <summary>
  /// The two divisions have to live in blocks the pipeline cannot merge into one. O0345 common-denominator
  /// factoring runs before O0338 in every sweep, groups divisions WITHIN a block and has no target opinion
  /// at all - so on a straight-line <c>br</c> chain simplifycfg merges the blocks, the next sweep's
  /// O0345 performs the reciprocal rewrite itself, and O0338's veto is never consulted. A conditional
  /// region keeps the two divisions in separate blocks, which is exactly the shape O0338 owns.
  /// </summary>
  [Test]
  public void Standard_GivenTargetCostDecliningReciprocalReuse_ThenSpeedPipelineKeepsRepeatedDivisions() {
    var fn = DominatedDivisionPair();

    IrPassManager.Standard(
      optimizeForSpeed: true,
      includeModulePasses: false,
      arithmeticCostModel: new NeverReuseCost()).RunToFixpoint(fn);

    Assert.Multiple(() => {
      Assert.That(fn.AllInstructions.OfType<IrBinary>().Count(binary => binary.Op == IrBinaryOp.FDiv), Is.EqualTo(2));
      Assert.That(fn.AllInstructions.OfType<IrBinary>().Any(binary => binary.Op == IrBinaryOp.FMul), Is.False);
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  /// <summary>
  /// The negative test above would also pass if the pipeline simply never reached this shape, so state the
  /// other half: with no target opinion the same function DOES become one reciprocal plus two multiplies.
  /// The veto is therefore what suppressed it, not the fixture.
  /// </summary>
  [Test]
  public void Standard_GivenNoCostModel_ThenTheSameDominatedPairSharesOneReciprocal() {
    var fn = DominatedDivisionPair();

    IrPassManager.Standard(optimizeForSpeed: true, includeModulePasses: false).RunToFixpoint(fn);

    Assert.Multiple(() => {
      Assert.That(fn.AllInstructions.OfType<IrBinary>().Count(binary => binary.Op == IrBinaryOp.FDiv), Is.EqualTo(1));
      Assert.That(fn.AllInstructions.OfType<IrBinary>().Count(binary => binary.Op == IrBinaryOp.FMul), Is.EqualTo(2));
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  private static IrFunction DominatedDivisionPair() {
    var x = new IrArgument(IrType.F64, 0, "x");
    var y = new IrArgument(IrType.F64, 1, "y");
    var divisor = new IrArgument(IrType.F64, 2, "d");
    var taken = new IrArgument(IrType.I1, 3, "c");
    var fn = new IrFunction("pipelineCost", IrType.F64, [x, y, divisor, taken]);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var hot = fn.AddBlock(new IrBasicBlock("hot"));
    var exit = fn.AddBlock(new IrBasicBlock("exit"));
    var left = entry.Append(new IrBinary(IrBinaryOp.FDiv, x, divisor));
    entry.Append(new IrCondBr(taken, hot, exit));
    var right = hot.Append(new IrBinary(IrBinaryOp.FDiv, y, divisor));
    var sum = hot.Append(new IrBinary(IrBinaryOp.FAdd, left, right));
    hot.Append(new IrBr(exit));
    var result = new IrPhi(IrType.F64);
    result.AddIncoming(left, entry);
    result.AddIncoming(sum, hot);
    exit.Append(result);
    exit.Append(new IrRet(result));
    return fn;
  }

  [Test]
  public void RepeatedDivision_GivenAnExistingReciprocal_ThenCostingDoesNotChargeForFormingItAgain() {
    var x = new IrArgument(IrType.F64, 0, "x");
    var divisor = new IrArgument(IrType.F64, 1, "d");
    var fn = new IrFunction("existingReciprocal", IrType.F64, [x, divisor]);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var reciprocal = entry.Append(new IrBinary(IrBinaryOp.FDiv, new IrConstantFloat(IrType.F64, 1.0), divisor) {
      FastMathFlags = IrFastMathFlags.AllowReciprocal,
    });
    var division = entry.Append(new IrBinary(IrBinaryOp.FDiv, x, divisor) {
      FastMathFlags = IrFastMathFlags.AllowReciprocal,
    });
    entry.Append(new IrRet(entry.Append(new IrBinary(IrBinaryOp.FAdd, reciprocal, division))));

    Assert.That(ReciprocalSequenceReuse.Run(fn, new ExistingOnlyCost()), Is.EqualTo(1));

    Assert.Multiple(() => {
      Assert.That(fn.AllInstructions.OfType<IrBinary>().Count(binary => binary.Op == IrBinaryOp.FDiv), Is.EqualTo(1));
      Assert.That(fn.AllInstructions.OfType<IrBinary>().Count(binary => binary.Op == IrBinaryOp.FMul), Is.EqualTo(1));
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }
}
