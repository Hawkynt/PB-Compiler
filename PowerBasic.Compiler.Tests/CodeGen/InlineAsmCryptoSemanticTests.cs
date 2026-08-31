using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
public sealed class InlineAsmCryptoSemanticTests {
  private static string Run(string body) {
    var source = "$CPU 8086\n" + body;
    var tree = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(tree, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = false };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return Cpu8086.Run(image, maxSteps: 8_000_000).Output;
  }

  [Test]
  public void Aesenc_GivenZeroStateAndKey_ThenMatchesAesRoundDefinition() {
    var output = Run("""
      DIM result&
      ! PXOR XMM0, XMM0
      ! PXOR XMM1, XMM1
      ! AESENC XMM0, XMM1
      ! MOVD EAX, XMM0
      ! MOV result&, EAX
      IF result& = 1667457891 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Aesenc_GivenLowColumnData_ThenShiftRowsAndMixColumnsCrossColumns() {
    var output = Run("""
      DIM result&
      ! MOV EAX, 50462976
      ! MOVD XMM0, EAX
      ! MOV EAX, 252579084
      ! MOVD XMM1, EAX
      ! AESENC XMM0, XMM1
      ! MOVD EAX, XMM0
      ! MOV result&, EAX
      IF result& = 1819111023 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Aesdec_GivenZeroStateAndKey_ThenMatchesInverseRoundDefinition() {
    var output = Run("""
      DIM result&
      ! PXOR XMM0, XMM0
      ! PXOR XMM1, XMM1
      ! AESDEC XMM0, XMM1
      ! MOVD EAX, XMM0
      ! MOV result&, EAX
      IF result& = 1381126738 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Aeskeygenassist_GivenZeroSource_ThenSubWordIsArchitecturalAesSbox() {
    var output = Run("""
      DIM result&
      ! PXOR XMM1, XMM1
      ! AESKEYGENASSIST XMM0, XMM1, 1
      ! MOVD EAX, XMM0
      ! MOV result&, EAX
      IF result& = 1667457891 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Pclmulqdq_GivenLowQuadwords_ThenUsesCarryLessPolynomialMultiplication() {
    var output = Run("""
      DIM result&
      ! MOV EAX, 3
      ! MOVD XMM0, EAX
      ! MOV EAX, 5
      ! MOVD XMM1, EAX
      ! PCLMULQDQ XMM0, XMM1, 0
      ! MOVD EAX, XMM0
      ! MOV result&, EAX
      IF result& = 15 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [TestCase("AESENCLAST XMM0, XMM1")]
  [TestCase("PCLMULQDQ XMM0, XMM1, 0")]
  public void CryptoInstruction_GivenIncomingStatusFlags_ThenPreservesEflags(string instruction) {
    var output = Run($$"""
      DIM flags%
      ! PXOR XMM0, XMM0
      ! PXOR XMM1, XMM1
      ! MOV AX, 2261
      ! PUSH AX
      ! POPF
      ! {{instruction}}
      ! PUSHF
      ! POP AX
      ! AND AX, 2261
      ! MOV flags%, AX
      IF flags% = 2261 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }
}
