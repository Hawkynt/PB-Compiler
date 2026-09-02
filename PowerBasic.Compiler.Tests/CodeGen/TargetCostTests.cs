using PowerBasic.Compiler.CodeGen;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// O0174 the per-target cost model (docs/optimizations/O0174). These pin the trade-offs the model exists to
/// arbitrate - the ones that contradict each other across the four decades of targets the compiler emits for -
/// so a profitability-gated pass reading the model gets the historically correct answer per tier.
/// </summary>
[TestFixture]
public sealed class TargetCostTests {

  private static TargetCost Cost(CpuTier tier, CostObjective objective = CostObjective.Balanced) => new(tier, objective);

  [Test]
  public void For_GivenCpuAndOptimizeFlags_WhenBuilt_ThenMapsToTierAndObjective() {
    Assert.Multiple(() => {
      Assert.That(TargetCost.For(86, false, false).Tier, Is.EqualTo(CpuTier.I8086));
      Assert.That(TargetCost.For(286, false, false).Tier, Is.EqualTo(CpuTier.I80286));
      Assert.That(TargetCost.For(386, false, false).Tier, Is.EqualTo(CpuTier.I80386));
      Assert.That(TargetCost.For(486, false, false).Tier, Is.EqualTo(CpuTier.I80486));
      Assert.That(TargetCost.For(586, false, false).Tier, Is.EqualTo(CpuTier.Pentium));
      Assert.That(TargetCost.For(686, false, false).Tier, Is.EqualTo(CpuTier.P6));
      Assert.That(TargetCost.For(86, true, false).Objective, Is.EqualTo(CostObjective.Speed));
      Assert.That(TargetCost.For(86, false, true).Objective, Is.EqualTo(CostObjective.Size));
      Assert.That(TargetCost.For(86, false, false).Objective, Is.EqualTo(CostObjective.Balanced));
    });
  }

  [Test]
  public void PreferBranchless_GivenNoPredictorTiers_WhenAsked_ThenKeepsTheBranch() {
    // 8086/286/386 have no dynamic predictor: a not-taken branch is nearly free, so branchless mask
    // arithmetic is pure overhead regardless of arm size.
    foreach (var tier in new[] { CpuTier.I8086, CpuTier.I80286, CpuTier.I80386 })
      Assert.That(Cost(tier, CostObjective.Speed).PreferBranchless(armInstrBytes: 2, sideEffectFree: true),
        Is.False, $"{tier} should keep the branch");
  }

  [Test]
  public void PreferBranchless_GivenPipelinedTierAndTinyArms_WhenAsked_ThenGoesBranchless() {
    // A real predictor to defeat and arms small enough that executing both is cheaper than a flush.
    Assert.That(Cost(CpuTier.Pentium, CostObjective.Speed).PreferBranchless(armInstrBytes: 3, sideEffectFree: true), Is.True);
    Assert.That(Cost(CpuTier.P6, CostObjective.Speed).PreferBranchless(armInstrBytes: 6, sideEffectFree: true), Is.True);
  }

  [Test]
  public void PreferBranchless_GivenLargeArmsOrSideEffectsOrSizeObjective_WhenAsked_ThenKeepsTheBranch() {
    var p6 = Cost(CpuTier.P6, CostObjective.Speed);
    Assert.Multiple(() => {
      Assert.That(p6.PreferBranchless(armInstrBytes: 40, sideEffectFree: true), Is.False, "arms too large");
      Assert.That(p6.PreferBranchless(armInstrBytes: 2, sideEffectFree: false), Is.False, "arm has a side effect - both would run");
      Assert.That(Cost(CpuTier.P6, CostObjective.Size).PreferBranchless(armInstrBytes: 2, sideEffectFree: true),
        Is.False, "size objective never prefers the wider mask form");
    });
  }

  [Test]
  public void SubRegisterPacking_GivenTier_WhenAsked_ThenWinsBelowP6AndStallsOnIt() {
    // The headline contradiction from the doc: 8-bit sub-register packing is a win on an 8086 and a stall on a P6.
    Assert.That(Cost(CpuTier.I8086).SubRegisterPackingProfitable, Is.True);
    Assert.That(Cost(CpuTier.I80486).SubRegisterPackingProfitable, Is.True);
    Assert.That(Cost(CpuTier.P6).SubRegisterPackingProfitable, Is.False);
  }

  [Test]
  public void MacroFusion_GivenTier_WhenAsked_ThenMattersOnlyFromPentium() {
    // Keeping CMP adjacent to its branch: required on a Pentium, counterproductive on an 8086.
    Assert.That(Cost(CpuTier.I8086).MacroFusionMatters, Is.False);
    Assert.That(Cost(CpuTier.I80386).MacroFusionMatters, Is.False);
    Assert.That(Cost(CpuTier.Pentium).MacroFusionMatters, Is.True);
    Assert.That(Cost(CpuTier.P6).MacroFusionMatters, Is.True);
  }

  [Test]
  public void Mul16_GivenTier_WhenAsked_ThenFallsSteeplyFrom8086ToP6() {
    // The multiply-to-shift/add rewrite (O0078) is a huge win on an 8086 and a wash on a P6 - the model must
    // rank the tiers monotonically so the decomposition pass can price it.
    Assert.That(Cost(CpuTier.I8086).Mul16Cycles, Is.GreaterThan(Cost(CpuTier.I80386).Mul16Cycles));
    Assert.That(Cost(CpuTier.I80386).Mul16Cycles, Is.GreaterThanOrEqualTo(Cost(CpuTier.P6).Mul16Cycles));
    // On an 8086 a MUL dwarfs a shift/add pair; on a P6 they are within a small factor.
    Assert.That(Cost(CpuTier.I8086).Mul16Cycles, Is.GreaterThan(Cost(CpuTier.I8086).ShiftAddCycles * 10));
    Assert.That(Cost(CpuTier.P6).Mul16Cycles, Is.LessThan(Cost(CpuTier.P6).ShiftAddCycles * 10));
  }

  [Test]
  public void PreferShiftAddMultiply_GivenSetBitsAndTier_WhenAsked_ThenDecomposesWhereTheMultiplyIsSlow() {
    // A four-set-bit chain (~8 instructions) only beats IMUL where the multiply is genuinely expensive:
    // the 8086's microcoded MUL. From the 386 up the compact IMUL is a handful of cycles and wins.
    Assert.That(Cost(CpuTier.I8086).PreferShiftAddMultiply(4), Is.True, "8086 MUL is ~124 cycles");
    Assert.That(Cost(CpuTier.I80386).PreferShiftAddMultiply(4), Is.False);
    Assert.That(Cost(CpuTier.Pentium).PreferShiftAddMultiply(4), Is.False);
    // A single shift (power of two) wins everywhere.
    Assert.That(Cost(CpuTier.Pentium).PreferShiftAddMultiply(1), Is.True);
  }

  [Test]
  public void PreferLoopInstruction_GivenTierAndObjective_WhenAsked_ThenAvoidsMicrocodedLoopOn486PlusUnlessSize() {
    Assert.That(Cost(CpuTier.I80386, CostObjective.Speed).PreferLoopInstruction, Is.True, "LOOP is still fast <=386");
    Assert.That(Cost(CpuTier.I80486, CostObjective.Speed).PreferLoopInstruction, Is.False, "microcoded from the 486");
    Assert.That(Cost(CpuTier.Pentium, CostObjective.Size).PreferLoopInstruction, Is.True, "a size objective still takes the one-byte form");
  }

  [Test]
  public void UnrollFactor_GivenTierTripAndBody_WhenAsked_ThenScalesWithTierAndRespectsSizeAndShortTrips() {
    Assert.Multiple(() => {
      Assert.That(Cost(CpuTier.I8086, CostObjective.Speed).UnrollFactor(tripCount: 0, bodyInstrBytes: 8), Is.EqualTo(2),
        "fetch-bound: modest unrolling");
      Assert.That(Cost(CpuTier.P6, CostObjective.Speed).UnrollFactor(tripCount: 0, bodyInstrBytes: 8), Is.EqualTo(8),
        "superscalar: wide unrolling to expose parallelism");
      Assert.That(Cost(CpuTier.P6, CostObjective.Size).UnrollFactor(tripCount: 0, bodyInstrBytes: 8), Is.EqualTo(1),
        "a size objective never unrolls");
      Assert.That(Cost(CpuTier.P6, CostObjective.Speed).UnrollFactor(tripCount: 3, bodyInstrBytes: 8), Is.EqualTo(1),
        "too few iterations to amortise a tail");
      Assert.That(Cost(CpuTier.P6, CostObjective.Speed).UnrollFactor(tripCount: 0, bodyInstrBytes: 200), Is.EqualTo(1),
        "a fat body must not blow the instruction-fetch window");
      Assert.That(Cost(CpuTier.P6, CostObjective.Speed).UnrollFactor(tripCount: 6, bodyInstrBytes: 8), Is.EqualTo(6),
        "prefer a factor that divides the trip count so no remainder tail is needed");
    });
  }

  [Test]
  public void MaxFullUnrollTrips_GivenTier_WhenAsked_ThenWidensOn486Plus() {
    Assert.That(Cost(CpuTier.I8086).MaxFullUnrollTrips, Is.EqualTo(4), "fetch-bound: conservative");
    Assert.That(Cost(CpuTier.I80386).MaxFullUnrollTrips, Is.EqualTo(4));
    Assert.That(Cost(CpuTier.I80486).MaxFullUnrollTrips, Is.EqualTo(8), "an instruction cache tolerates a wider unroll");
    Assert.That(Cost(CpuTier.Pentium).MaxFullUnrollTrips, Is.EqualTo(8));
  }

  [Test]
  public void AlignHotLoops_GivenTierAndObjective_WhenAsked_ThenOnlySpeedOn486Plus() {
    Assert.That(Cost(CpuTier.I80386, CostObjective.Speed).AlignHotLoops, Is.False);
    Assert.That(Cost(CpuTier.I80486, CostObjective.Speed).AlignHotLoops, Is.True);
    Assert.That(Cost(CpuTier.Pentium, CostObjective.Speed).AlignHotLoops, Is.True);
    Assert.That(Cost(CpuTier.I80486, CostObjective.Size).AlignHotLoops, Is.False);
    Assert.That(Cost(CpuTier.I80486, CostObjective.Balanced).AlignHotLoops, Is.False);
  }
}
