using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
public sealed class InlineAsmPopcntSemanticTests {
  private static string Run(string body) {
    var source = "$CPU 8086\n" + body;
    var tree = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(tree, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = false };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return Cpu8086.Run(image, maxSteps: 2_000_000).Output;
  }

  [Test]
  public void Popcnt_GivenWordSource_ThenCountsSetBitsOn8086() {
    var output = Run("""
      DIM result%
      ! MOV AX, 4660
      ! POPCNT BX, AX
      ! MOV result%, BX
      IF result% = 5 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Popcnt_GivenAliasedDwordSource_ThenReusesVirtualGp32State() {
    var output = Run("""
      DIM result&
      ! MOV EAX, -1
      ! POPCNT EAX, EAX
      ! MOV result&, EAX
      IF result& = 32 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Popcnt_GivenUnsizedLongMemorySource_ThenDestinationDeterminesDwordWidth() {
    var output = Run("""
      DIM value&, result&
      value& = 305419896
      ! POPCNT EDX, value&
      ! MOV result&, EDX
      IF result& = 13 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [TestCase(0, 64)]
  [TestCase(1, 0)]
  public void Popcnt_GivenSourceZeroState_ThenSynthesizesArchitecturalStatusFlags(int sourceValue, int expectedStatus) {
    var output = Run($$"""
      DIM flags%
      ! MOV AX, 2261
      ! PUSH AX
      ! POPF
      ! MOV AX, {{sourceValue}}
      ! POPCNT BX, AX
      ! PUSHF
      ! POP CX
      ! AND CX, 2261
      ! MOV flags%, CX
      IF flags% = {{expectedStatus}} THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }
}
