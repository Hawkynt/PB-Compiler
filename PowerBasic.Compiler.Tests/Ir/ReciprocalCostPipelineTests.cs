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

  [Test]
  public void Standard_GivenTargetCostDecliningReciprocalReuse_ThenSpeedPipelineKeepsRepeatedDivisions() {
    var x = new IrArgument(IrType.F64, 0, "x");
    var y = new IrArgument(IrType.F64, 1, "y");
    var divisor = new IrArgument(IrType.F64, 2, "d");
    var fn = new IrFunction("pipelineCost", IrType.F64, [x, y, divisor]);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var next = fn.AddBlock(new IrBasicBlock("next"));
    var left = entry.Append(new IrBinary(IrBinaryOp.FDiv, x, divisor));
    entry.Append(new IrBr(next));
    var right = next.Append(new IrBinary(IrBinaryOp.FDiv, y, divisor));
    next.Append(new IrRet(next.Append(new IrBinary(IrBinaryOp.FAdd, left, right))));

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
