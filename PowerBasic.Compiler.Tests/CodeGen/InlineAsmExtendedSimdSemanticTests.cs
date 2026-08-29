using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
public sealed class InlineAsmExtendedSimdSemanticTests {
  private static string Run(string body) {
    var source = "$CPU 8086\n" + body;
    var tree = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(tree, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = false };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return Cpu8086.Run(image).Output;
  }

  [TestCase(-123, 123)]
  [TestCase(int.MinValue, int.MinValue)]
  public void Pabsd_GivenSignedDword_ThenMatchesSsse3Wraparound(int input, int expected) {
    var output = Run($$"""
      DIM result&
      ! MOV EAX, {{input}}
      ! MOVD XMM0, EAX
      ! PABSD XMM1, XMM0
      ! MOVD EAX, XMM1
      ! MOV result&, EAX
      IF result& = {{expected}} THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void PsignD_GivenNegativeSign_ThenNegatesDataLane() {
    var output = Run("""
      DIM result&
      ! MOV EAX, -7
      ! MOVD XMM0, EAX
      ! MOV EAX, -1
      ! MOVD XMM1, EAX
      ! PSIGND XMM0, XMM1
      ! MOVD EAX, XMM0
      ! MOV result&, EAX
      IF result& = 7 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Pmulld_GivenNegativeProduct_ThenReturnsLowDword() {
    var output = Run("""
      DIM result&
      ! MOV EAX, -3
      ! MOVD XMM0, EAX
      ! MOV EAX, 7
      ! MOVD XMM1, EAX
      ! PMULLD XMM0, XMM1
      ! MOVD EAX, XMM0
      ! MOV result&, EAX
      IF result& = -21 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Pminud_GivenUnsignedOrdering_ThenDoesNotUseSignedComparison() {
    var output = Run("""
      DIM result&
      ! MOV EAX, -1
      ! MOVD XMM0, EAX
      ! MOV EAX, 1
      ! MOVD XMM1, EAX
      ! PMINUD XMM0, XMM1
      ! MOVD EAX, XMM0
      ! MOV result&, EAX
      IF result& = 1 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Pblendw_GivenLowWordMask_ThenOnlySelectedLaneChanges() {
    var output = Run("""
      DIM result&
      ! MOV EAX, 287454020
      ! MOVD XMM0, EAX
      ! MOV EAX, 1432778632
      ! MOVD XMM1, EAX
      ! PBLENDW XMM0, XMM1, 1
      ! MOVD EAX, XMM0
      ! AND EAX, 65535
      ! MOV result&, EAX
      IF result& = 30600 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Pcmpeqq_GivenEqualQwords_ThenProducesAllOnes() {
    var output = Run("""
      DIM result&
      ! MOV EAX, 1234567
      ! MOVD XMM0, EAX
      ! MOV EAX, 1234567
      ! MOVD XMM1, EAX
      ! PCMPEQQ XMM0, XMM1
      ! MOVD EAX, XMM0
      ! MOV result&, EAX
      IF result& = -1 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Pcmpgtq_GivenGreaterPositiveQword_ThenProducesAllOnes() {
    var output = Run("""
      DIM result&
      ! MOV EAX, 5
      ! MOVD XMM0, EAX
      ! MOV EAX, 3
      ! MOVD XMM1, EAX
      ! PCMPGTQ XMM0, XMM1
      ! MOVD EAX, XMM0
      ! MOV result&, EAX
      IF result& = -1 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Phminposuw_GivenZeroInThirdLane_ThenReturnsValueAndFirstIndex() {
    var output = Run("""
      DIM result&
      ! MOV EAX, 327690
      ! MOVD XMM1, EAX
      ! PHMINPOSUW XMM0, XMM1
      ! MOVD EAX, XMM0
      ! MOV result&, EAX
      IF result& = 131072 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }
}
