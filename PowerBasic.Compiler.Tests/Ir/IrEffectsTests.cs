using PowerBasic.Compiler.Ir.Analysis;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class IrEffectsTests {

  [TestCase("llvm.sqrt.f32")]
  [TestCase("llvm.sin.f64")]
  [TestCase("llvm.pow.f80")]
  public void ForExternalCall_GivenCheckedMathIntrinsic_ThenItIsEffectFreeDeterministicAndSpeculatable(string name) {
    var effects = IrEffects.ForExternalCall(name);

    Assert.Multiple(() => {
      Assert.That(effects.Effects, Is.EqualTo(IrEffectKind.None));
      Assert.That(effects.Deterministic, Is.True);
      Assert.That(effects.CanCse, Is.True);
      Assert.That(effects.CanSpeculate, Is.True);
      Assert.That(FunctionSummaries.IsPureExternal(name), Is.True);
      Assert.That(FunctionSummaries.IsSpeculatableExternal(name), Is.True);
    });
  }

  [TestCase("rt_str_len")]
  [TestCase("rt_str_dup")]
  [TestCase("rt_print_nl")]
  [TestCase("llvm.memcpy.p0.p0.i32")]
  public void ForExternalCall_GivenUnmodeledRuntimeEntry_ThenItRemainsConservative(string name) {
    var effects = IrEffects.ForExternalCall(name);

    Assert.Multiple(() => {
      Assert.That(effects.Effects, Is.Not.EqualTo(IrEffectKind.None));
      Assert.That(effects.Effects.HasFlag(IrEffectKind.ReadsMemory), Is.True);
      Assert.That(effects.Effects.HasFlag(IrEffectKind.WritesMemory), Is.True);
      Assert.That(effects.Effects.HasFlag(IrEffectKind.MayAllocate), Is.True);
      Assert.That(effects.Effects.HasFlag(IrEffectKind.MayRelease), Is.True);
      Assert.That(effects.Effects.HasFlag(IrEffectKind.MayTrap), Is.True);
      Assert.That(effects.Effects.HasFlag(IrEffectKind.MaySynchronize), Is.True);
      Assert.That(effects.Effects.HasFlag(IrEffectKind.PerformsIo), Is.True);
      Assert.That(effects.Deterministic, Is.False);
      Assert.That(effects.CanCse, Is.False);
      Assert.That(effects.CanSpeculate, Is.False);
      Assert.That(FunctionSummaries.IsPureExternal(name), Is.False);
      Assert.That(FunctionSummaries.IsSpeculatableExternal(name), Is.False);
    });
  }
}
