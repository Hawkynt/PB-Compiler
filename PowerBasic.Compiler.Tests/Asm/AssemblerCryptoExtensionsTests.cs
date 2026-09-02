using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Tests.Asm;

[TestFixture]
public sealed class AssemblerCryptoExtensionsTests {
  private static byte[] Assemble(Action<Assembler> emit) {
    var asm = new Assembler();
    emit(asm);
    return asm.ToArray();
  }

  [TestCaseSource(nameof(CryptoCases))]
  public void Emit_GivenCryptoInstruction_ThenMatchesIntelEncoding(Action<Assembler> emit, byte[] expected)
    => Assert.That(Assemble(emit), Is.EqualTo(expected));

  private static IEnumerable<TestCaseData> CryptoCases() {
    yield return new((Action<Assembler>)(a => a.Aesimc(Reg.XMM0, Reg.XMM1)), new byte[] { 0x66, 0x0F, 0x38, 0xDB, 0xC1 }) { TestName = "AESIMC" };
    yield return new((Action<Assembler>)(a => a.Aesenc(Reg.XMM2, Reg.XMM3)), new byte[] { 0x66, 0x0F, 0x38, 0xDC, 0xD3 }) { TestName = "AESENC" };
    yield return new((Action<Assembler>)(a => a.Aesenclast(Reg.XMM4, Reg.XMM5)), new byte[] { 0x66, 0x0F, 0x38, 0xDD, 0xE5 }) { TestName = "AESENCLAST" };
    yield return new((Action<Assembler>)(a => a.Aesdec(Reg.XMM6, Reg.XMM7)), new byte[] { 0x66, 0x0F, 0x38, 0xDE, 0xF7 }) { TestName = "AESDEC" };
    yield return new((Action<Assembler>)(a => a.Aesdeclast(Reg.XMM0, Reg.XMM1)), new byte[] { 0x66, 0x0F, 0x38, 0xDF, 0xC1 }) { TestName = "AESDECLAST" };
    yield return new((Action<Assembler>)(a => a.Aeskeygenassist(Reg.XMM2, Reg.XMM3, 0x1B)), new byte[] { 0x66, 0x0F, 0x3A, 0xDF, 0xD3, 0x1B }) { TestName = "AESKEYGENASSIST" };
    yield return new((Action<Assembler>)(a => a.Pclmulqdq(Reg.XMM4, Reg.XMM5, 0x11)), new byte[] { 0x66, 0x0F, 0x3A, 0x44, 0xE5, 0x11 }) { TestName = "PCLMULQDQ" };
  }

  [Test]
  public void Aesenc_GivenSegmentedMemory_ThenSegmentOverridePrecedesMandatory66() {
    Assert.That(Assemble(a => a.Aesenc(Reg.XMM0, Mem.At(Reg.BX).Es())),
      Is.EqualTo(new byte[] { 0x26, 0x66, 0x0F, 0x38, 0xDC, 0x07 }));
  }
}
