using PowerBasic.Compiler.CodeGen;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
public sealed class InlineAsmCanonicalizerTests {
  [TestCase("MOV AX, AX")]
  [TestCase("mov eax, eax ; redundant")]
  [TestCase("MOVDQA XMM3, XMM3")]
  [TestCase("MOVDQU xmm7, xmm7")]
  [TestCase("PAND XMM1, XMM1")]
  [TestCase("POR XMM1, XMM1")]
  [TestCase("PMINSB XMM2, XMM2")]
  [TestCase("PMAXUD XMM2, XMM2")]
  [TestCase("PBLENDW XMM4, XMM4, 170")]
  public void IsRedundant_GivenRegisterIdentity_ThenReturnsTrue(string line) {
    Assert.That(InlineAsmCanonicalizer.IsRedundant(line), Is.True);
  }

  [TestCase("ADD AX, 0")]
  [TestCase("XOR AX, AX")]
  [TestCase("MOV AX, BX")]
  [TestCase("MOV [BX], AX")]
  [TestCase("VMOVDQA XMM0, XMM0")]
  [TestCase("PCMPEQQ XMM0, XMM0")]
  [TestCase("PBLENDW XMM0, XMM0, mask")]
  [TestCase("MOV DS, DS")]
  public void IsRedundant_GivenObservableOrInvalidCase_ThenReturnsFalse(string line) {
    Assert.That(InlineAsmCanonicalizer.IsRedundant(line), Is.False);
  }
}
