using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
public sealed class InlineAsmSsse3ArithmeticSemanticTests {
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
  public void Phaddsw_GivenPositiveOverflow_ThenSaturates() {
    var output = Run("""
      DIM result&
      ! MOV EAX, 655390000
      ! MOVD XMM0, EAX
      ! PHADDSW XMM0, XMM0
      ! MOVD EAX, XMM0
      ! MOV result&, EAX
      IF result& = 32767 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Phsubsw_GivenPositiveOverflow_ThenSaturates() {
    var output = Run("""
      DIM result&
      ! MOV EAX, -655330000
      ! MOVD XMM0, EAX
      ! PHSUBSW XMM0, XMM0
      ! MOVD EAX, XMM0
      ! MOV result&, EAX
      IF result& = 32767 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Pmaddubsw_GivenOverflowingUnsignedSignedProducts_ThenSaturates() {
    var output = Run("""
      DIM result&
      ! MOV EAX, 65535
      ! MOVD XMM0, EAX
      ! MOV EAX, 32639
      ! MOVD XMM1, EAX
      ! PMADDUBSW XMM0, XMM1
      ! MOVD EAX, XMM0
      ! MOV result&, EAX
      IF result& = 32767 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Pmulhrsw_GivenQuarterScaleOperands_ThenRoundsAndShiftsByFifteen() {
    var output = Run("""
      DIM result&
      ! MOV EAX, 16384
      ! MOVD XMM0, EAX
      ! MOV EAX, 16384
      ! MOVD XMM1, EAX
      ! PMULHRSW XMM0, XMM1
      ! MOVD EAX, XMM0
      ! MOV result&, EAX
      IF result& = 8192 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }
}
