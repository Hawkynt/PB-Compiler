using PowerBasic.Compiler.CodeGen;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
public sealed class InlineAsmCanonicalizerTests {
  [TestCase("MOV AX, AX")]
  [TestCase("mov bx, bx ; redundant")]
  [TestCase("MOV AL, AL")]
  public void IsRedundant_GivenBaselineRegisterIdentity_ThenReturnsTrue(string line) {
    Assert.That(InlineAsmCanonicalizer.IsRedundant(line), Is.True);
  }

  [TestCase("ADD AX, 0")]
  [TestCase("XOR AX, AX")]
  [TestCase("MOV AX, BX")]
  [TestCase("MOV [BX], AX")]
  [TestCase("MOV EAX, EAX")]
  [TestCase("MOVDQA XMM3, XMM3")]
  [TestCase("PAND XMM1, XMM1")]
  [TestCase("PMAXUD XMM2, XMM2")]
  [TestCase("PBLENDW XMM4, XMM4, 170")]
  [TestCase("VMOVDQA XMM0, XMM0")]
  [TestCase("MOV DS, DS")]
  public void IsRedundant_GivenObservableInvalidOrTargetDependentCase_ThenReturnsFalse(string line) {
    Assert.That(InlineAsmCanonicalizer.IsRedundant(line), Is.False);
  }

  [TestCase("MOVDQA XMM3, XMM3")]
  [TestCase("PAND XMM1, XMM1")]
  [TestCase("POR MM2, MM2")]
  [TestCase("PMAXUD XMM2, XMM2")]
  [TestCase("PBLENDW XMM4, XMM4, 170")]
  public void IsPolicyValidatedRedundant_GivenLegacySimdIdentity_ThenReturnsTrue(string line) {
    Assert.That(InlineAsmCanonicalizer.IsPolicyValidatedRedundant(line), Is.True);
  }

  [TestCase("PXOR XMM0, XMM0")]
  [TestCase("PCMPEQQ XMM0, XMM0")]
  [TestCase("PMAXUD XMM2, XMM3")]
  [TestCase("VMOVDQA XMM0, XMM0")]
  public void IsPolicyValidatedRedundant_GivenNonIdentityOrUpperLaneObservableCase_ThenReturnsFalse(string line) {
    Assert.That(InlineAsmCanonicalizer.IsPolicyValidatedRedundant(line), Is.False);
  }
}
