using PowerBasic.Compiler.Runtime;

namespace PowerBasic.Compiler.Tests.Runtime;

[TestFixture]
public sealed class RuntimePopcntTargetTests {
  [Test]
  public void PopcntFeatureTarget_RequiresA32BitCapableCore() {
    var target = RuntimeTarget.For("POPCNT");

    Assert.Multiple(() => {
      Assert.That(target.Has(RuntimeCpuFeatures.Popcnt), Is.True);
      Assert.That(target.Has32BitGeneralPurpose, Is.True);
      Assert.That(target.CpuLevel, Is.GreaterThanOrEqualTo(686));
    });
  }

  [Test]
  public void Sse42Target_DoesNotInventIndependentPopcntFeature() {
    var target = RuntimeTarget.For("SSE4.2");

    Assert.That(target.Has(RuntimeCpuFeatures.Popcnt), Is.False);
  }
}
