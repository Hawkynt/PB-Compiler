using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
public sealed class InlineAsmHorizontalSimdSemanticTests {
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

  [Test]
  public void Phaddw_GivenTwoLowWords_ThenAddsAdjacentPair() {
    var output = Run("""
      DIM result&
      ! MOV EAX, 262147
      ! MOVD XMM0, EAX
      ! MOV EAX, 393221
      ! MOVD XMM1, EAX
      ! PHADDW XMM0, XMM1
      ! MOVD EAX, XMM0
      ! MOV result&, EAX
      IF result& = 7 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Psubd_GivenAdjacentDwords_ThenSubtractsInSourceOrder() {
    var output = Run("""
      DIM result&
      ! MOV EAX, 10
      ! MOVD XMM0, EAX
      ! PHSUBD XMM0, XMM0
      ! MOVD EAX, XMM0
      ! MOV result&, EAX
      IF result& = 10 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Palignr_GivenOneByteShift_ThenConcatenatesSourceBelowDestination() {
    var output = Run("""
      DIM result&
      ! MOV EAX, 1432778632
      ! MOVD XMM0, EAX
      ! MOV EAX, 287454020
      ! MOVD XMM1, EAX
      ! PALIGNR XMM0, XMM1, 1
      ! MOVD EAX, XMM0
      ! MOV result&, EAX
      IF result& = 1122867 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Packusdw_GivenValueAboveUnsignedWord_ThenSaturatesTo65535() {
    var output = Run("""
      DIM result&
      ! MOV EAX, 70000
      ! MOVD XMM0, EAX
      ! PACKUSDW XMM0, XMM0
      ! MOVD EAX, XMM0
      ! MOV result&, EAX
      IF result& = 65535 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }
}
