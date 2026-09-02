using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Runtime;

namespace PowerBasic.Compiler.Tests.Runtime;

[TestFixture]
public sealed class RuntimeTargetFeatureTests {
  [Test]
  public void ExplicitFloor_WithLaterFeatureRequirements_RaisesMinimumHardware() {
    var target = RuntimeTarget.For("8086", ["MMX", "SSE2", "AVX512"]);

    Assert.Multiple(() => {
      Assert.That(target.CpuLevel, Is.GreaterThanOrEqualTo(686));
      Assert.That(target.Has32BitGeneralPurpose, Is.True);
      Assert.That(target.HasMmx, Is.True);
      Assert.That(target.HasSse2, Is.True);
      Assert.That(target.HasAvx512, Is.True);
      Assert.That(target.DwordGeneralPurposeRegisters, Does.Contain(Reg.EDI));
      Assert.That(target.VectorRegisters, Does.Contain(Reg.ZMM7));
    });
  }

  [Test]
  public void CpuFloors_AreCumulative() {
    Assert.Multiple(() => {
      Assert.That(RuntimeTarget.For("80386").Has32BitGeneralPurpose, Is.True);
      Assert.That(RuntimeTarget.For("80486").Has486, Is.True);
      Assert.That(RuntimeTarget.For("80586").Has486, Is.True);
      Assert.That(RuntimeTarget.For("80686").HasP6, Is.True);
    });
  }

  [Test]
  public void Sse_DoesNotInventSse2_ButCanUseBitwiseXmmBulkMoves() {
    var target = RuntimeTarget.For("80586", ["SSE"]);

    Assert.Multiple(() => {
      Assert.That(target.HasSse, Is.True);
      Assert.That(target.HasSse2, Is.False);
      Assert.That(target.VectorRegisters, Does.Contain(Reg.XMM7));
      Assert.That(target.MaxRuntimeBulkVectorWidthBytes, Is.EqualTo(16));
    });
  }

  [TestCase("SSE2", RuntimeCpuFeatures.Sse | RuntimeCpuFeatures.Sse2)]
  [TestCase("SSE3", RuntimeCpuFeatures.Sse | RuntimeCpuFeatures.Sse2 | RuntimeCpuFeatures.Sse3)]
  [TestCase("SSSE3", RuntimeCpuFeatures.Sse | RuntimeCpuFeatures.Sse2 | RuntimeCpuFeatures.Sse3 | RuntimeCpuFeatures.Ssse3)]
  [TestCase("SSE4.1", RuntimeCpuFeatures.Sse | RuntimeCpuFeatures.Sse2 | RuntimeCpuFeatures.Sse3 | RuntimeCpuFeatures.Ssse3 | RuntimeCpuFeatures.Sse41)]
  [TestCase("SSE4.2", RuntimeCpuFeatures.Sse | RuntimeCpuFeatures.Sse2 | RuntimeCpuFeatures.Sse3 | RuntimeCpuFeatures.Ssse3 | RuntimeCpuFeatures.Sse41 | RuntimeCpuFeatures.Sse42)]
  public void SseFamily_NormalizesOnlyRealPredecessors(string feature, RuntimeCpuFeatures expected) {
    var target = RuntimeTarget.For("80586", [feature]);
    Assert.That(target.Features & expected, Is.EqualTo(expected));
  }

  [Test]
  public void Avx_DoesNotInventAvx2() {
    var target = RuntimeTarget.For("80586", ["AVX"]);

    Assert.Multiple(() => {
      Assert.That(target.HasAvx, Is.True);
      Assert.That(target.HasAvx2, Is.False);
      Assert.That(target.HasSse2, Is.True);
      Assert.That(target.MaxRuntimeBulkVectorWidthBytes, Is.EqualTo(32));
      Assert.That(target.VectorRegisters, Does.Contain(Reg.YMM7));
    });
  }

  [Test]
  public void Avx512_ExposesTheWidestRegisterSetAndInheritedFeatures() {
    var target = RuntimeTarget.For("80586", ["AVX512", "AES", "BMI2"]);

    Assert.Multiple(() => {
      Assert.That(target.HasAvx512, Is.True);
      Assert.That(target.HasAvx2, Is.True);
      Assert.That(target.HasAvx, Is.True);
      Assert.That(target.HasSse2, Is.True);
      Assert.That(target.HasAes, Is.True);
      Assert.That(target.Has(RuntimeCpuFeatures.Bmi2), Is.True);
      Assert.That(target.MaxRuntimeBulkVectorWidthBytes, Is.EqualTo(64));
      Assert.That(target.VectorRegisters, Does.Contain(Reg.ZMM7));
      Assert.That(target.DwordGeneralPurposeRegisters, Does.Contain(Reg.EDI));
    });
  }

  [Test]
  public void SegmentSafeSse1Store_EmitsEsBeforeOpcodeEscape() {
    var asm = new Assembler();
    asm.MovupsTargetStore(Mem.At(Reg.DI).Es(), Reg.XMM0);

    Assert.That(asm.ToArray(), Is.EqualTo(new byte[] { 0x26, 0x0F, 0x11, 0x05 }));
  }

  [Test]
  public void SegmentSafeSimdStore_EmitsEsBeforeMandatoryPrefix() {
    var asm = new Assembler();
    asm.MovdquTargetStore(Mem.At(Reg.DI).Es(), Reg.XMM0);

    Assert.That(asm.ToArray(), Is.EqualTo(new byte[] { 0x26, 0xF3, 0x0F, 0x7F, 0x05 }));
  }

  [Test]
  public void SegmentSafeVexAndEvexStores_KeepEsOverrideAheadOfEncodingPrefix() {
    var avx = new Assembler();
    avx.VmovdquTargetStore(Mem.At(Reg.DI).Es(), Reg.YMM0);
    var avx512 = new Assembler();
    avx512.Vmovdqu512TargetStore(Mem.At(Reg.DI).Es(), Reg.ZMM0);

    Assert.Multiple(() => {
      Assert.That(avx.ToArray()[..2], Is.EqualTo(new byte[] { 0x26, 0xC5 }));
      Assert.That(avx512.ToArray()[..2], Is.EqualTo(new byte[] { 0x26, 0x62 }));
    });
  }
}
