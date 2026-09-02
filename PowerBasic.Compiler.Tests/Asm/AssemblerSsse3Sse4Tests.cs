using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Tests.Asm;

[TestFixture]
public sealed class AssemblerSsse3Sse4Tests {
  private static byte[] Assemble(Action<Assembler> emit) {
    var asm = new Assembler();
    emit(asm);
    return asm.ToArray();
  }

  private static IEnumerable<TestCaseData> Ssse3Cases() {
    yield return new((Action<Assembler>)(a => a.Pshufb(Reg.MM0, Reg.MM1)), new byte[] { 0x0F, 0x38, 0x00, 0xC1 }) { TestName = "PSHUFB mm0,mm1" };
    yield return new((Action<Assembler>)(a => a.Pshufb(Reg.XMM0, Reg.XMM1)), new byte[] { 0x66, 0x0F, 0x38, 0x00, 0xC1 }) { TestName = "PSHUFB xmm0,xmm1" };
    yield return new((Action<Assembler>)(a => a.Phaddw(Reg.XMM2, Reg.XMM3)), new byte[] { 0x66, 0x0F, 0x38, 0x01, 0xD3 }) { TestName = "PHADDW xmm2,xmm3" };
    yield return new((Action<Assembler>)(a => a.Pmaddubsw(Reg.XMM4, Reg.XMM5)), new byte[] { 0x66, 0x0F, 0x38, 0x04, 0xE5 }) { TestName = "PMADDUBSW xmm4,xmm5" };
    yield return new((Action<Assembler>)(a => a.Pmulhrsw(Reg.MM6, Reg.MM7)), new byte[] { 0x0F, 0x38, 0x0B, 0xF7 }) { TestName = "PMULHRSW mm6,mm7" };
    yield return new((Action<Assembler>)(a => a.Pabsb(Reg.XMM0, Reg.XMM1)), new byte[] { 0x66, 0x0F, 0x38, 0x1C, 0xC1 }) { TestName = "PABSB xmm0,xmm1" };
    yield return new((Action<Assembler>)(a => a.Pabsw(Reg.XMM0, Reg.XMM1)), new byte[] { 0x66, 0x0F, 0x38, 0x1D, 0xC1 }) { TestName = "PABSW xmm0,xmm1" };
    yield return new((Action<Assembler>)(a => a.Pabsd(Reg.XMM0, Reg.XMM1)), new byte[] { 0x66, 0x0F, 0x38, 0x1E, 0xC1 }) { TestName = "PABSD xmm0,xmm1" };
    yield return new((Action<Assembler>)(a => a.Palignr(Reg.XMM2, Reg.XMM3, 5)), new byte[] { 0x66, 0x0F, 0x3A, 0x0F, 0xD3, 0x05 }) { TestName = "PALIGNR xmm2,xmm3,5" };
  }

  [TestCaseSource(nameof(Ssse3Cases))]
  public void Emit_GivenSsse3Instruction_ThenMatchesArchitecturalBytes(Action<Assembler> emit, byte[] expected)
    => Assert.That(Assemble(emit), Is.EqualTo(expected));

  private static IEnumerable<TestCaseData> Sse41Cases() {
    yield return new((Action<Assembler>)(a => a.Pcmpeqq(Reg.XMM0, Reg.XMM1)), new byte[] { 0x66, 0x0F, 0x38, 0x29, 0xC1 }) { TestName = "PCMPEQQ" };
    yield return new((Action<Assembler>)(a => a.Packusdw(Reg.XMM2, Reg.XMM3)), new byte[] { 0x66, 0x0F, 0x38, 0x2B, 0xD3 }) { TestName = "PACKUSDW" };
    yield return new((Action<Assembler>)(a => a.Pminsb(Reg.XMM0, Reg.XMM1)), new byte[] { 0x66, 0x0F, 0x38, 0x38, 0xC1 }) { TestName = "PMINSB" };
    yield return new((Action<Assembler>)(a => a.Pminuw(Reg.XMM0, Reg.XMM1)), new byte[] { 0x66, 0x0F, 0x38, 0x3A, 0xC1 }) { TestName = "PMINUW" };
    yield return new((Action<Assembler>)(a => a.Pmaxud(Reg.XMM6, Reg.XMM7)), new byte[] { 0x66, 0x0F, 0x38, 0x3F, 0xF7 }) { TestName = "PMAXUD" };
    yield return new((Action<Assembler>)(a => a.Pmulld(Reg.XMM2, Reg.XMM3)), new byte[] { 0x66, 0x0F, 0x38, 0x40, 0xD3 }) { TestName = "PMULLD" };
    yield return new((Action<Assembler>)(a => a.Pblendw(Reg.XMM4, Reg.XMM5, 0xAA)), new byte[] { 0x66, 0x0F, 0x3A, 0x0E, 0xE5, 0xAA }) { TestName = "PBLENDW" };
  }

  [TestCaseSource(nameof(Sse41Cases))]
  public void Emit_GivenSse41Instruction_ThenMatchesArchitecturalBytes(Action<Assembler> emit, byte[] expected)
    => Assert.That(Assemble(emit), Is.EqualTo(expected));

  private static IEnumerable<TestCaseData> Sse42Cases() {
    yield return new((Action<Assembler>)(a => a.Pcmpgtq(Reg.XMM0, Reg.XMM1)), new byte[] { 0x66, 0x0F, 0x38, 0x37, 0xC1 }) { TestName = "PCMPGTQ" };
    yield return new((Action<Assembler>)(a => a.Pcmpestrm(Reg.XMM0, Reg.XMM1, 0x12)), new byte[] { 0x66, 0x0F, 0x3A, 0x60, 0xC1, 0x12 }) { TestName = "PCMPESTRM" };
    yield return new((Action<Assembler>)(a => a.Pcmpestri(Reg.XMM0, Reg.XMM1, 0x34)), new byte[] { 0x66, 0x0F, 0x3A, 0x61, 0xC1, 0x34 }) { TestName = "PCMPESTRI" };
    yield return new((Action<Assembler>)(a => a.Pcmpistrm(Reg.XMM0, Reg.XMM1, 0x56)), new byte[] { 0x66, 0x0F, 0x3A, 0x62, 0xC1, 0x56 }) { TestName = "PCMPISTRM" };
    yield return new((Action<Assembler>)(a => a.Pcmpistri(Reg.XMM0, Reg.XMM1, 0x78)), new byte[] { 0x66, 0x0F, 0x3A, 0x63, 0xC1, 0x78 }) { TestName = "PCMPISTRI" };
  }

  [TestCaseSource(nameof(Sse42Cases))]
  public void Emit_GivenSse42Instruction_ThenMatchesArchitecturalBytes(Action<Assembler> emit, byte[] expected)
    => Assert.That(Assemble(emit), Is.EqualTo(expected));

  [Test]
  public void Crc32_ByteSource_DoesNotAcquireOperandSizePrefix() {
    Assert.That(Assemble(a => a.Crc32Byte(Reg.EAX, Reg.CL)),
      Is.EqualTo(new byte[] { 0xF2, 0x0F, 0x38, 0xF0, 0xC1 }));
  }

  [Test]
  public void Crc32_WordSource_UsesDefault16BitSourceWidth() {
    Assert.That(Assemble(a => a.Crc32(Reg.EAX, Reg.CX)),
      Is.EqualTo(new byte[] { 0xF2, 0x0F, 0x38, 0xF1, 0xC1 }));
  }

  [Test]
  public void Crc32_DwordSource_EmitsOperandSizeOverrideBeforeF2() {
    Assert.That(Assemble(a => a.Crc32(Reg.EAX, Reg.ECX)),
      Is.EqualTo(new byte[] { 0x66, 0xF2, 0x0F, 0x38, 0xF1, 0xC1 }));
  }

  [Test]
  public void Crc32_DwordMemorySource_EmitsOperandSizeOverride() {
    Assert.That(Assemble(a => a.Crc32(Reg.EDX, Mem.Dword(Reg.BX))),
      Is.EqualTo(new byte[] { 0x66, 0xF2, 0x0F, 0x38, 0xF1, 0x17 }));
  }

  [Test]
  public void Ssse3_MemorySource_EmitsSegmentPrefixBeforeMandatory66() {
    Assert.That(Assemble(a => a.Pshufb(Reg.XMM0, Mem.Qword(Reg.BP, 0).Es())),
      Is.EqualTo(new byte[] { 0x26, 0x66, 0x0F, 0x38, 0x00, 0x46, 0x00 }));
  }
}
