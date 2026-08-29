using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
public sealed class InlineAsmGp32SemanticTests {
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
  public void Mul32_GivenProductFitsLowDword_ThenHighDwordAndCfOfAreClear() {
    var output = Run("""
      DIM lo&, hi&, flags%
      ! MOV EAX, 65536
      ! MOV ECX, 2
      ! MUL ECX
      ! MOV lo&, EAX
      ! MOV hi&, EDX
      ! PUSHF
      ! POP flags%
      IF lo& = 131072 AND hi& = 0 AND (flags% AND 1) = 0 AND (flags% AND 2048) = 0 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Mul32_GivenNonzeroHighDword_ThenCfAndOfAreSet() {
    var output = Run("""
      DIM lo&, hi&, flags%
      ! MOV EAX, -1
      ! MOV ECX, 2
      ! MUL ECX
      ! MOV lo&, EAX
      ! MOV hi&, EDX
      ! PUSHF
      ! POP flags%
      IF lo& = -2 AND hi& = 1 AND (flags% AND 1) <> 0 AND (flags% AND 2048) <> 0 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Imul32_GivenSignExtendedProduct_ThenCfAndOfAreClear() {
    var output = Run("""
      DIM lo&, hi&, flags%
      ! MOV EAX, -2
      ! MOV ECX, 3
      ! IMUL ECX
      ! MOV lo&, EAX
      ! MOV hi&, EDX
      ! PUSHF
      ! POP flags%
      IF lo& = -6 AND hi& = -1 AND (flags% AND 1) = 0 AND (flags% AND 2048) = 0 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Imul32_GivenLowDwordWhoseHighHalfIsNotSignExtension_ThenCfAndOfAreSet() {
    var output = Run("""
      DIM lo&, hi&, flags%
      ! MOV EAX, 1073741824
      ! MOV ECX, 2
      ! IMUL ECX
      ! MOV lo&, EAX
      ! MOV hi&, EDX
      ! PUSHF
      ! POP flags%
      IF lo& = -2147483648 AND hi& = 0 AND (flags% AND 1) <> 0 AND (flags% AND 2048) <> 0 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Div32_Given64BitDividend_ThenProducesArchitecturalQuotientAndRemainder() {
    var output = Run("""
      DIM quotient&, remainder&
      ! MOV EDX, 1
      ! MOV EAX, 0
      ! MOV ECX, 3
      ! DIV ECX
      ! MOV quotient&, EAX
      ! MOV remainder&, EDX
      IF quotient& = 1431655765 AND remainder& = 1 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Div32_GivenQuotientOverflow_ThenRaisesRealDivideError() {
    var image = Compile("""
      ! MOV EDX, 1
      ! MOV EAX, 0
      ! MOV ECX, 1
      ! DIV ECX
      PRINT "SHOULD NOT REACH"
      """);

    Assert.Throws<Cpu8086Exception>(() => Cpu8086.Run(image));
  }

  [Test]
  public void Idiv32_GivenMinIntDividedByMinusOne_ThenRaisesRealDivideError() {
    var image = Compile("""
      ! MOV EDX, -1
      ! MOV EAX, -2147483648
      ! MOV ECX, -1
      ! IDIV ECX
      PRINT "SHOULD NOT REACH"
      """);

    Assert.Throws<Cpu8086Exception>(() => Cpu8086.Run(image));
  }

  [TestCase("ROL", "-2147483648", "1")]
  [TestCase("ROR", "1", "-2147483648")]
  public void Rotate32_ByOne_ThenCfAndOfMatch386Definition(string mnemonic, string input, string expected) {
    var output = Run($$"""
      DIM result&, flags%
      ! MOV EAX, {{input}}
      ! {{mnemonic}} EAX, 1
      ! MOV result&, EAX
      ! PUSHF
      ! POP flags%
      IF result& = {{expected}} AND (flags% AND 1) <> 0 AND (flags% AND 2048) <> 0 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [TestCase("RCL", "-2147483648", "1")]
  [TestCase("RCR", "1", "-2147483648")]
  public void RotateThroughCarry32_ByOne_ThenConsumesOldCfAndSetsNewCfOf(string mnemonic, string input, string expected) {
    var output = Run($$"""
      DIM result&, flags%
      ! MOV EAX, {{input}}
      ! STC
      ! {{mnemonic}} EAX, 1
      ! MOV result&, EAX
      ! PUSHF
      ! POP flags%
      IF result& = {{expected}} AND (flags% AND 1) <> 0 AND (flags% AND 2048) <> 0 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Rotate32_ByTwo_ThenPreservesUndefinedOfDeterministically() {
    var output = Run("""
      DIM result&, flags%
      ! MOV AX, 32767
      ! ADD AX, 1
      ! MOV EAX, 1073741824
      ! ROL EAX, 2
      ! MOV result&, EAX
      ! PUSHF
      ! POP flags%
      IF result& = 1 AND (flags% AND 1) <> 0 AND (flags% AND 2048) <> 0 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }
}
