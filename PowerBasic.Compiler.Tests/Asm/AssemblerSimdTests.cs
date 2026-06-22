using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Tests.Asm;

/// <summary>
/// MMX (Pentium) integer SIMD encodings: the two-byte <c>0F xx</c> escape with a ModRM whose
/// reg field is the destination MMX register and whose r/m is an MMX register, memory, or
/// (for shifts) a group sub-opcode with an immediate count.
/// </summary>
[TestFixture]
public sealed class AssemblerSimdTests {

  private static byte[] Assemble(Action<Assembler> emit) {
    var asm = new Assembler();
    emit(asm);
    return asm.ToArray();
  }

  private static IEnumerable<TestCaseData> RegRegCases() {
    yield return new((Action<Assembler>)(a => a.Emms()), new byte[] { 0x0F, 0x77 }) { TestName = "EMMS" };
    yield return new((Action<Assembler>)(a => a.Movq(Reg.MM0, Reg.MM1)), new byte[] { 0x0F, 0x6F, 0xC1 }) { TestName = "MOVQ mm0,mm1" };
    yield return new((Action<Assembler>)(a => a.Movd(Reg.MM0, Reg.EAX)), new byte[] { 0x0F, 0x6E, 0xC0 }) { TestName = "MOVD mm0,eax" };
    yield return new((Action<Assembler>)(a => a.MovdStore(Reg.EBX, Reg.MM2)), new byte[] { 0x0F, 0x7E, 0xD3 }) { TestName = "MOVD ebx,mm2" };
    yield return new((Action<Assembler>)(a => a.Paddb(Reg.MM0, Reg.MM1)), new byte[] { 0x0F, 0xFC, 0xC1 }) { TestName = "PADDB" };
    yield return new((Action<Assembler>)(a => a.Paddw(Reg.MM0, Reg.MM1)), new byte[] { 0x0F, 0xFD, 0xC1 }) { TestName = "PADDW" };
    yield return new((Action<Assembler>)(a => a.Paddd(Reg.MM2, Reg.MM3)), new byte[] { 0x0F, 0xFE, 0xD3 }) { TestName = "PADDD mm2,mm3" };
    yield return new((Action<Assembler>)(a => a.Psubw(Reg.MM0, Reg.MM1)), new byte[] { 0x0F, 0xF9, 0xC1 }) { TestName = "PSUBW" };
    yield return new((Action<Assembler>)(a => a.Pmullw(Reg.MM0, Reg.MM1)), new byte[] { 0x0F, 0xD5, 0xC1 }) { TestName = "PMULLW" };
    yield return new((Action<Assembler>)(a => a.Pand(Reg.MM0, Reg.MM1)), new byte[] { 0x0F, 0xDB, 0xC1 }) { TestName = "PAND" };
    yield return new((Action<Assembler>)(a => a.Por(Reg.MM0, Reg.MM1)), new byte[] { 0x0F, 0xEB, 0xC1 }) { TestName = "POR" };
    yield return new((Action<Assembler>)(a => a.Pxor(Reg.MM4, Reg.MM4)), new byte[] { 0x0F, 0xEF, 0xE4 }) { TestName = "PXOR mm4,mm4" };
    yield return new((Action<Assembler>)(a => a.Pcmpeqw(Reg.MM0, Reg.MM1)), new byte[] { 0x0F, 0x75, 0xC1 }) { TestName = "PCMPEQW" };
    yield return new((Action<Assembler>)(a => a.Pcmpgtd(Reg.MM0, Reg.MM1)), new byte[] { 0x0F, 0x66, 0xC1 }) { TestName = "PCMPGTD" };
  }

  [TestCaseSource(nameof(RegRegCases))]
  public void Emit_GivenMmxRegisterForm_ThenMatchesOpcode(Action<Assembler> emit, byte[] expected)
    => Assert.That(Assemble(emit), Is.EqualTo(expected));

  [Test]
  public void Emit_GivenPaddwMemorySource_ThenModRmAddressesMemory() {
    // PADDW MM0, [BX]: 0F FD 07 (reg=mm0, r/m=[bx] = mod 00 r/m 111)
    Assert.That(Assemble(a => a.Paddw(Reg.MM0, Mem.At(Reg.BX))), Is.EqualTo(new byte[] { 0x0F, 0xFD, 0x07 }));
  }

  [Test]
  public void Emit_GivenMovqStoreToMemory_ThenUsesStoreOpcode() {
    // MOVQ [DI], MM3: 0F 7F 1D (reg=mm3, r/m=[di] = mod 00 r/m 101)
    Assert.That(Assemble(a => a.MovqStore(Mem.At(Reg.DI), Reg.MM3)), Is.EqualTo(new byte[] { 0x0F, 0x7F, 0x1D }));
  }

  private static IEnumerable<TestCaseData> ShiftImmCases() {
    // group 0F 71/72/73, ModRM C0|sub<<3|reg, then the immediate count
    yield return new((Action<Assembler>)(a => a.Psllw(Reg.MM0, 3)), new byte[] { 0x0F, 0x71, 0xF0, 0x03 }) { TestName = "PSLLW mm0,3" };
    yield return new((Action<Assembler>)(a => a.Psrlw(Reg.MM0, 1)), new byte[] { 0x0F, 0x71, 0xD0, 0x01 }) { TestName = "PSRLW mm0,1" };
    yield return new((Action<Assembler>)(a => a.Psraw(Reg.MM0, 2)), new byte[] { 0x0F, 0x71, 0xE0, 0x02 }) { TestName = "PSRAW mm0,2" };
    yield return new((Action<Assembler>)(a => a.Psrld(Reg.MM1, 4)), new byte[] { 0x0F, 0x72, 0xD1, 0x04 }) { TestName = "PSRLD mm1,4" };
    yield return new((Action<Assembler>)(a => a.Psllq(Reg.MM2, 8)), new byte[] { 0x0F, 0x73, 0xF2, 0x08 }) { TestName = "PSLLQ mm2,8" };
  }

  [TestCaseSource(nameof(ShiftImmCases))]
  public void Emit_GivenMmxShiftByImmediate_ThenMatchesGroupEncoding(Action<Assembler> emit, byte[] expected)
    => Assert.That(Assemble(emit), Is.EqualTo(expected));

  [Test]
  public void Emit_GivenPsllwByRegister_ThenVariableShiftEncoding() {
    // PSLLW MM0, MM1: 0F F1 C1 (the by-register form, distinct from the immediate group)
    Assert.That(Assemble(a => a.Psllw(Reg.MM0, Reg.MM1)), Is.EqualTo(new byte[] { 0x0F, 0xF1, 0xC1 }));
  }
}
