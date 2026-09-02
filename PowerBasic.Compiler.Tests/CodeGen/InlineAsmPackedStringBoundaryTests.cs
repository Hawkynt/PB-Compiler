using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
public sealed class InlineAsmPackedStringBoundaryTests {
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
  public void Pcmpestri_GivenIntMinExplicitLength_ThenSaturatesAbsoluteLengthWithoutOverflow() {
    var output = Run("""
      DIM result&
      ! MOV EAX, 16961
      ! MOVD XMM1, EAX
      ! MOV EAX, 66
      ! MOVD XMM2, EAX
      ! MOV EAX, -2147483648
      ! MOV EDX, 1
      ! PCMPESTRI XMM1, XMM2, 0
      ! MOV result&, ECX
      IF result& = 0 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [TestCase(16, 0)]
  [TestCase(8, 1)]
  public void Pcmpestri_GivenFullVectorExplicitLength_ThenKeepsEveryByteOrWordValid(int length, int control) {
    var output = Run($$"""
      DIM result&
      ! PCMPEQB XMM1, XMM1
      ! PCMPEQB XMM2, XMM2
      ! MOV EAX, {{length}}
      ! MOV EDX, {{length}}
      ! PCMPESTRI XMM1, XMM2, {{control}}
      ! MOV result&, ECX
      IF result& = 0 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Pcmpistri_GivenFirstNullAtFinalWordLane_ThenSetsSecondStringEndFlag() {
    var output = Run("""
      DIM flags%
      ! PCMPEQB XMM1, XMM1
      ! PCMPEQB XMM2, XMM2
      ! PXOR XMM0, XMM0
      ! PBLENDW XMM2, XMM0, 128
      ! PCMPISTRI XMM1, XMM2, 9
      ! PUSHF
      ! POP AX
      ! MOV flags%, AX
      IF (flags% AND 64) = 64 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }
}
