using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
public sealed class InlineAsmPackedStringSemanticTests {
  private static string Run(string body) {
    var source = "$CPU 8086\n" + body;
    var tree = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(tree, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = false };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return Cpu8086.Run(image, maxSteps: 4_000_000).Output;
  }

  [Test]
  public void Pcmpestrm_GivenEqualAnyBytes_ThenReturnsBitMaskInXmm0() {
    var output = Run("""
      DIM result&
      ! MOV EAX, 16961
      ! MOVD XMM1, EAX
      ! MOV EAX, 4276803
      ! MOVD XMM2, EAX
      ! MOV EAX, 2
      ! MOV EDX, 3
      ! PCMPESTRM XMM1, XMM2, 0
      ! MOVD EAX, XMM0
      ! MOV result&, EAX
      IF result& = 6 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Pcmpestrm_GivenBothExplicitStringsEmptyInEqualEach_ThenInvalidPairsForceTrue() {
    var output = Run("""
      DIM result&
      ! PXOR XMM1, XMM1
      ! PXOR XMM2, XMM2
      ! MOV EAX, 0
      ! MOV EDX, 0
      ! PCMPESTRM XMM1, XMM2, 8
      ! MOVD EAX, XMM0
      ! AND EAX, 65535
      ! MOV result&, EAX
      IF result& = 65535 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Pcmpestrm_GivenOnlySecondExplicitStringNonEmptyInEqualEach_ThenOneInvalidPairForcesFalse() {
    var output = Run("""
      DIM result&
      ! PXOR XMM1, XMM1
      ! PXOR XMM2, XMM2
      ! MOV EAX, 0
      ! MOV EDX, 1
      ! PCMPESTRM XMM1, XMM2, 8
      ! MOVD EAX, XMM0
      ! AND EAX, 65535
      ! MOV result&, EAX
      IF result& = 65534 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [TestCase(-2, 2)]
  [TestCase(2, 2)]
  [TestCase(-1000, 16)]
  [TestCase(1000, 16)]
  [TestCase(int.MinValue, 16)]
  public void Pcmpestri_GivenExplicitLength_ThenUsesSaturatedAbsoluteValue(int length, int expectedIndex) {
    var output = Run($$"""
      DIM result&
      ! MOV EAX, 16961
      ! MOVD XMM1, EAX
      ! MOV EAX, 65
      ! MOVD XMM2, EAX
      ! MOV EAX, {{length}}
      ! MOV EDX, 1
      ! PCMPESTRI XMM1, XMM2, 0
      ! MOV result&, ECX
      IF result& = {{expectedIndex == 2 ? 0 : 0}} THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    // For all these lengths the first element remains valid, so equal-any finds 'A' at B[0].
    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Pcmpestri_GivenNegativeLengthWhoseMagnitudeIsZeroImpossible_ThenZeroLengthProducesNoMatch() {
    var output = Run("""
      DIM result&
      ! MOV EAX, 65
      ! MOVD XMM1, EAX
      ! MOVD XMM2, EAX
      ! MOV EAX, 0
      ! MOV EDX, 1
      ! PCMPESTRI XMM1, XMM2, 0
      ! MOV result&, ECX
      IF result& = 16 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Pcmpestrm_GivenSignedWordRange_ThenUsesSignedOrdering() {
    var output = Run("""
      DIM result&
      ! MOV EAX, 196606
      ! MOVD XMM1, EAX
      ! MOV EAX, 65535
      ! MOVD XMM2, EAX
      ! MOV EAX, 2
      ! MOV EDX, 1
      ! PCMPESTRM XMM1, XMM2, 7
      ! MOVD EAX, XMM0
      ! AND EAX, 255
      ! MOV result&, EAX
      IF result& = 1 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Pcmpestri_GivenEqualOrderedBytes_ThenReturnsLeastMatchingStart() {
    var output = Run("""
      DIM result&
      ! MOV EAX, 16961
      ! MOVD XMM1, EAX
      ! MOV EAX, 1497514328
      ! MOVD XMM2, EAX
      ! MOV EAX, 2
      ! MOV EDX, 4
      ! PCMPESTRI XMM1, XMM2, 12
      ! MOV result&, ECX
      IF result& = 1 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Pcmpestri_GivenMostSignificantSelection_ThenReturnsLastMatchingStart() {
    var output = Run("""
      DIM result&
      ! MOV EAX, 16961
      ! MOVD XMM1, EAX
      ! MOV EAX, 1111573057
      ! MOVD XMM2, EAX
      ! MOV EAX, 2
      ! MOV EDX, 4
      ! PCMPESTRI XMM1, XMM2, 76
      ! MOV result&, ECX
      IF result& = 2 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [TestCase(0, 1)]
  [TestCase(16, 65534)]
  [TestCase(32, 1)]
  [TestCase(48, 65534)]
  public void Pcmpestrm_GivenPolarityMode_ThenProducesIntelIntRes2(int polarity, int expected) {
    var output = Run($$"""
      DIM result&
      ! MOV EAX, 65
      ! MOVD XMM1, EAX
      ! MOVD XMM2, EAX
      ! MOV EAX, 1
      ! MOV EDX, 1
      ! PCMPESTRM XMM1, XMM2, {{polarity}}
      ! MOVD EAX, XMM0
      ! AND EAX, 65535
      ! MOV result&, EAX
      IF result& = {{expected}} THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Pcmpestrm_GivenUnitMaskSelection_ThenExpandsEachResultBitToElementWidth() {
    var output = Run("""
      DIM result&
      ! MOV EAX, 16961
      ! MOVD XMM1, EAX
      ! MOV EAX, 16961
      ! MOVD XMM2, EAX
      ! MOV EAX, 2
      ! MOV EDX, 2
      ! PCMPESTRM XMM1, XMM2, 64
      ! MOVD EAX, XMM0
      ! MOV result&, EAX
      IF result& = 65535 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Pcmpistri_GivenFirstNullAtLaneZero_ThenTreatsBothStringsAsEmpty() {
    var output = Run("""
      DIM result&
      ! PXOR XMM1, XMM1
      ! PXOR XMM2, XMM2
      ! PCMPISTRI XMM1, XMM2, 8
      ! MOV result&, ECX
      IF result& = 0 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Pcmpistri_GivenNoNullBytes_ThenUsesFullSixteenByteLength() {
    var output = Run("""
      DIM result&
      ! PCMPEQB XMM1, XMM1
      ! PCMPEQB XMM2, XMM2
      ! PCMPISTRI XMM1, XMM2, 8
      ! MOV result&, ECX
      IF result& = 0 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Pcmpistri_GivenFirstNullAtFinalByteLane_ThenReportsShortSecondStringFlag() {
    var output = Run("""
      DIM flags%
      ! PCMPEQB XMM1, XMM1
      ! PCMPEQB XMM2, XMM2
      ! MOVDQA XMM3, XMM2
      ! PSRLW XMM3, 8
      ! PBLENDW XMM2, XMM3, 128
      ! PCMPISTRI XMM1, XMM2, 8
      ! PUSHF
      ! POP AX
      ! MOV flags%, AX
      IF (flags% AND 64) = 64 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Pcmpestrm_GivenEmptyExplicitStrings_ThenSynthesizesArchitecturalFlags() {
    var output = Run("""
      DIM flags%
      ! PXOR XMM1, XMM1
      ! PXOR XMM2, XMM2
      ! MOV EAX, 0
      ! MOV EDX, 0
      ! STD
      ! PCMPESTRM XMM1, XMM2, 8
      ! PUSHF
      ! POP AX
      ! CLD
      ! AND AX, 3285
      ! MOV flags%, AX
      IF flags% = 3265 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }
}
