using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
public sealed class InlineAsmShuffleSemanticTests {
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
  public void Pshufb_GivenReverseControl_ThenIndexesOriginalDestinationBytes() {
    var output = Run("""
      DIM result&
      ! MOV EAX, 287454020
      ! MOVD XMM0, EAX
      ! MOV EAX, 66051
      ! MOVD XMM1, EAX
      ! PSHUFB XMM0, XMM1
      ! MOVD EAX, XMM0
      ! MOV result&, EAX
      IF result& = 1144201745 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Pshufb_GivenControlHighBit_ThenZerosSelectedByte() {
    var output = Run("""
      DIM result&
      ! MOV EAX, 287454020
      ! MOVD XMM0, EAX
      ! MOV EAX, 128
      ! MOVD XMM1, EAX
      ! PSHUFB XMM0, XMM1
      ! MOVD EAX, XMM0
      ! AND EAX, 255
      ! MOV result&, EAX
      IF result& = 0 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }
}
