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
}
