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

  private static IEnumerable<TestCaseData> Sse2Cases() {
    // SSE2 = the MMX opcodes with a mandatory 66 prefix and XMM operands
    yield return new((Action<Assembler>)(a => a.PaddwX(Reg.XMM0, Reg.XMM1)), new byte[] { 0x66, 0x0F, 0xFD, 0xC1 }) { TestName = "PADDW xmm" };
    yield return new((Action<Assembler>)(a => a.PadddX(Reg.XMM0, Reg.XMM1)), new byte[] { 0x66, 0x0F, 0xFE, 0xC1 }) { TestName = "PADDD xmm" };
    yield return new((Action<Assembler>)(a => a.PaddqX(Reg.XMM2, Reg.XMM3)), new byte[] { 0x66, 0x0F, 0xD4, 0xD3 }) { TestName = "PADDQ xmm2,xmm3" };
    yield return new((Action<Assembler>)(a => a.PsubwX(Reg.XMM0, Reg.XMM1)), new byte[] { 0x66, 0x0F, 0xF9, 0xC1 }) { TestName = "PSUBW xmm" };
    yield return new((Action<Assembler>)(a => a.PmullwX(Reg.XMM0, Reg.XMM1)), new byte[] { 0x66, 0x0F, 0xD5, 0xC1 }) { TestName = "PMULLW xmm" };
    yield return new((Action<Assembler>)(a => a.PxorX(Reg.XMM0, Reg.XMM0)), new byte[] { 0x66, 0x0F, 0xEF, 0xC0 }) { TestName = "PXOR xmm,xmm" };
    yield return new((Action<Assembler>)(a => a.Movdqa(Reg.XMM0, Reg.XMM1)), new byte[] { 0x66, 0x0F, 0x6F, 0xC1 }) { TestName = "MOVDQA xmm,xmm" };
    yield return new((Action<Assembler>)(a => a.MovdX(Reg.XMM0, Reg.EAX)), new byte[] { 0x66, 0x0F, 0x6E, 0xC0 }) { TestName = "MOVD xmm0,eax" };
  }

  [TestCaseSource(nameof(Sse2Cases))]
  public void Emit_GivenSse2RegisterForm_ThenMatchesPrefixedOpcode(Action<Assembler> emit, byte[] expected)
    => Assert.That(Assemble(emit), Is.EqualTo(expected));

  [Test]
  public void Emit_GivenMovdquLoad_ThenF3Prefixed() {
    // MOVDQU XMM0, [BX]: F3 0F 6F 07
    Assert.That(Assemble(a => a.Movdqu(Reg.XMM0, Mem.At(Reg.BX))), Is.EqualTo(new byte[] { 0xF3, 0x0F, 0x6F, 0x07 }));
  }

  [Test]
  public void Emit_GivenSse2ShiftImmediate_ThenPrefixedGroupEncoding() {
    // PSLLW XMM0, 4: 66 0F 71 F0 04
    Assert.That(Assemble(a => a.PsllwX(Reg.XMM0, 4)), Is.EqualTo(new byte[] { 0x66, 0x0F, 0x71, 0xF0, 0x04 }));
  }

  [Test]
  public void Emit_GivenPaddwMemoryXmm_ThenAddressesMemory() {
    // PADDW XMM0, [BX]: 66 0F FD 07
    Assert.That(Assemble(a => a.PaddwX(Reg.XMM0, Mem.At(Reg.BX))), Is.EqualTo(new byte[] { 0x66, 0x0F, 0xFD, 0x07 }));
  }

  private static IEnumerable<TestCaseData> AvxCases() {
    // 2-byte VEX (C5) for VEX.<L>.66.0F.WIG ops; L=1 (256/YMM) vs L=0 (128/XMM), 3-operand
    yield return new((Action<Assembler>)(a => a.VexPacked(0xFD, Reg.YMM0, Reg.YMM1, Reg.YMM2)), new byte[] { 0xC5, 0xF5, 0xFD, 0xC2 }) { TestName = "VPADDW ymm0,ymm1,ymm2" };
    yield return new((Action<Assembler>)(a => a.VexPacked(0xFD, Reg.XMM0, Reg.XMM1, Reg.XMM2)), new byte[] { 0xC5, 0xF1, 0xFD, 0xC2 }) { TestName = "VPADDW xmm0,xmm1,xmm2 (L=0)" };
    yield return new((Action<Assembler>)(a => a.VexPacked(0xEF, Reg.YMM0, Reg.YMM0, Reg.YMM0)), new byte[] { 0xC5, 0xFD, 0xEF, 0xC0 }) { TestName = "VPXOR ymm0,ymm0,ymm0" };
    yield return new((Action<Assembler>)(a => a.VexPacked(0xFE, Reg.YMM2, Reg.YMM3, Reg.YMM4)), new byte[] { 0xC5, 0xE5, 0xFE, 0xD4 }) { TestName = "VPADDD ymm2,ymm3,ymm4" };
    yield return new((Action<Assembler>)(a => a.Vmovdqa(Reg.YMM0, Reg.YMM1)), new byte[] { 0xC5, 0x85, 0x6F, 0xC1 }) { TestName = "VMOVDQA ymm0,ymm1" };
  }

  [TestCaseSource(nameof(AvxCases))]
  public void Emit_GivenAvxVexForm_ThenMatchesVexEncoding(Action<Assembler> emit, byte[] expected)
    => Assert.That(Assemble(emit), Is.EqualTo(expected));

  [Test]
  public void Emit_GivenVmovdquYmmLoad_ThenF3PpAndL256() {
    // VMOVDQU YMM0, [BX]: VEX.256.F3.0F 6F /r -> C5 86 6F 07
    Assert.That(Assemble(a => a.Vmovdqu(Reg.YMM0, Mem.At(Reg.BX))), Is.EqualTo(new byte[] { 0xC5, 0x86, 0x6F, 0x07 }));
  }

  [Test]
  public void Emit_GivenVpaddwYmmMemory_ThenAddressesMemory() {
    // VPADDW YMM0, YMM1, [SI]: C5 F5 FD 04
    Assert.That(Assemble(a => a.VexPacked(0xFD, Reg.YMM0, Reg.YMM1, Mem.At(Reg.SI))), Is.EqualTo(new byte[] { 0xC5, 0xF5, 0xFD, 0x04 }));
  }

  private static IEnumerable<TestCaseData> Avx512Cases() {
    // 4-byte EVEX (62 P0 P1 P2): P0=F1 (low regs, 0F map), P1=W0/vvvv̄/pp, P2=48 (L'L=10=512, V'=1) for a 3-op low src1
    yield return new((Action<Assembler>)(a => a.EvexPacked(0xFD, Reg.ZMM0, Reg.ZMM1, Reg.ZMM2)), new byte[] { 0x62, 0xF1, 0x75, 0x48, 0xFD, 0xC2 }) { TestName = "VPADDW zmm0,zmm1,zmm2" };
    yield return new((Action<Assembler>)(a => a.EvexPacked(0xEF, Reg.ZMM0, Reg.ZMM0, Reg.ZMM0)), new byte[] { 0x62, 0xF1, 0x7D, 0x48, 0xEF, 0xC0 }) { TestName = "VPXOR zmm0,zmm0,zmm0" };
    yield return new((Action<Assembler>)(a => a.EvexPacked(0xFE, Reg.ZMM2, Reg.ZMM3, Reg.ZMM4)), new byte[] { 0x62, 0xF1, 0x65, 0x48, 0xFE, 0xD4 }) { TestName = "VPADDD zmm2,zmm3,zmm4" };
    // two-operand move: vvvv unused (1111) -> V'=0, so P2 = 0x40
    yield return new((Action<Assembler>)(a => a.Vmovdqa512(Reg.ZMM0, Reg.ZMM1)), new byte[] { 0x62, 0xF1, 0x05, 0x40, 0x6F, 0xC1 }) { TestName = "VMOVDQA32 zmm0,zmm1" };
  }

  [TestCaseSource(nameof(Avx512Cases))]
  public void Emit_GivenAvx512EvexForm_ThenMatchesEvexEncoding(Action<Assembler> emit, byte[] expected)
    => Assert.That(Assemble(emit), Is.EqualTo(expected));

  [Test]
  public void Emit_GivenVmovdqu512Load_ThenF3PpAndL512() {
    // VMOVDQU32 ZMM0, [BX]: EVEX.512.F3.0F.W0 6F -> 62 F1 06 40 6F 07 (W0, vvvv unused, pp=10=F3, P2 V'=0)
    Assert.That(Assemble(a => a.Vmovdqu512(Reg.ZMM0, Mem.At(Reg.BX))), Is.EqualTo(new byte[] { 0x62, 0xF1, 0x06, 0x40, 0x6F, 0x07 }));
  }
}
