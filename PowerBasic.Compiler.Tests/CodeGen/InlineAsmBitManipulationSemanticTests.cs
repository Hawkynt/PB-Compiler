using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
public sealed class InlineAsmBitManipulationSemanticTests {
  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static byte[] Compile(string body) {
    var generator = new CodeGenerator(Bind("$CPU 8086\n" + body)) { Optimize = false };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return image;
  }

  private static string Run(string body) => Cpu8086.Run(Compile(body)).Output;

  [Test]
  public void Popcnt32_GivenTwoSetBits_ThenCountsBitsAndClearsDefinedStatusFlags() {
    var output = Run("""
      DIM result&, flags%
      ! MOV EAX, -2147483647
      ! POPCNT ECX, EAX
      ! MOV result&, ECX
      ! PUSHF
      ! POP flags%
      IF result& = 2 AND (flags% AND 1) = 0 AND (flags% AND 4) = 0 AND (flags% AND 16) = 0 AND (flags% AND 64) = 0 AND (flags% AND 128) = 0 AND (flags% AND 2048) = 0 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Popcnt32_GivenZero_ThenSetsOnlyZeroFlagAmongDefinedStatusFlags() {
    var output = Run("""
      DIM result&, flags%
      ! MOV EAX, 0
      ! POPCNT EDX, EAX
      ! MOV result&, EDX
      ! PUSHF
      ! POP flags%
      IF result& = 0 AND (flags% AND 64) <> 0 AND (flags% AND 1) = 0 AND (flags% AND 4) = 0 AND (flags% AND 16) = 0 AND (flags% AND 128) = 0 AND (flags% AND 2048) = 0 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Popcnt16_GivenAllBitsSet_ThenProducesSixteen() {
    var output = Run("""
      DIM result%
      ! MOV AX, -1
      ! POPCNT CX, AX
      ! MOV result%, CX
      IF result% = 16 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Popcnt32_GivenMemorySource_ThenStagesSourceBeforeWritingDestination() {
    var output = Run("""
      DIM source&, result&
      source& = 255
      ! POPCNT EAX, source&
      ! MOV result&, EAX
      IF result& = 8 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }
}
