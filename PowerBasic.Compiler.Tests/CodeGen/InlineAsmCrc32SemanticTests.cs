using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
public sealed class InlineAsmCrc32SemanticTests {
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
  public void Crc32_GivenByteSource_ThenUsesCrc32cPolynomial() {
    var output = Run("""
      DIM result&
      ! MOV EAX, 0
      ! MOV BL, 97
      ! CRC32 EAX, BL
      ! MOV result&, EAX
      IF result& = -1817374623 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Crc32_GivenWordSource_ThenProcessesLittleEndianBytes() {
    var output = Run("""
      DIM result&
      ! MOV EAX, 0
      ! MOV BX, 4660
      ! CRC32 EAX, BX
      ! MOV result&, EAX
      IF result& = -6175545 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Crc32_GivenDwordSource_ThenProcessesAllFourBytes() {
    var output = Run("""
      DIM result&
      ! MOV EAX, 0
      ! MOV EBX, 305419896
      ! CRC32 EAX, EBX
      ! MOV result&, EAX
      IF result& = -93039052 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }
}
