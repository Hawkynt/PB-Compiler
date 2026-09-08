using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>O0174/O0338 cost cases that distinguish reciprocal formation from using an existing value.</summary>
[TestFixture]
public sealed class TargetCostReciprocalTests {

  [Test]
  public void ExistingReciprocal_GivenX87SpeedTarget_ThenEvenOneLaterDivisionPrefersMultiply() {
    var cost = new TargetCost(CpuTier.I8086, CostObjective.Speed);

    Assert.Multiple(() => {
      Assert.That(cost.PreferReciprocalReuse(IrType.F80, 2), Is.False,
        "forming a new 8087 reciprocal is not amortized by only two divisions");
      Assert.That(cost.PreferExistingReciprocalUse(IrType.F80, 1), Is.True,
        "once 1/d already exists, the comparison is only FDIV versus FMUL");
      Assert.That(new TargetCost(CpuTier.I8086, CostObjective.Size)
        .PreferExistingReciprocalUse(IrType.F80, 1), Is.False);
      Assert.That(cost.PreferExistingReciprocalUse(IrType.I16, 1), Is.False);
    });
  }
}
