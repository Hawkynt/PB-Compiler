using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
public sealed class InlineAsmBmiSemanticTests {
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
  public void Andn_GivenDestinationAliasesFirstSource_ThenSnapshotsBothSources() {
    var output = Run("""
      DIM result&
      ! MOV EAX, 252645135
      ! MOV ECX, 858993459
      ! ANDN EAX, EAX, ECX
      ! MOV result&, EAX
      IF result& = 808464432 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Bextr_GivenDestinationAliasesControl_ThenUsesOriginalControlAndPreservesAxScratch() {
    var output = Run("""
      DIM result&, saved&
      ! MOV EAX, -559038737
      ! MOV ECX, 2056
      ! MOV EDX, 305419896
      ! BEXTR ECX, EAX, ECX
      ! MOV result&, ECX
      ! MOV saved&, EDX
      IF result& = 190 AND saved& = 305419896 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [TestCase("BLSI", 88, 8)]
  [TestCase("BLSMSK", 88, 15)]
  [TestCase("BLSR", 88, 80)]
  [TestCase("TZCNT", 256, 8)]
  public void Bmi1Unary_GivenAliasedSource_ThenProducesArchitecturalResult(string mnemonic, int source, int expected) {
    var output = Run($$"""
      DIM result&
      ! MOV EAX, {{source}}
      ! {{mnemonic}} EAX, EAX
      ! MOV result&, EAX
      IF result& = {{expected}} THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Tzcnt_GivenSeparateDestination_ThenDoesNotLeakTemporaryAx() {
    var output = Run("""
      DIM result&, saved&
      ! MOV EAX, 305419896
      ! MOV ECX, 256
      ! TZCNT EDX, ECX
      ! MOV result&, EDX
      ! MOV saved&, EAX
      IF result& = 8 AND saved& = 305419896 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [TestCase(0, 64)]
  [TestCase(-2147483648, 129)]
  public void Blsi_GivenSource_ThenSynthesizesDefinedFlags(int source, int expectedStatus) {
    var output = Run($$"""
      DIM flags%
      ! MOV AX, 2261
      ! PUSH AX
      ! POPF
      ! MOV EAX, {{source}}
      ! BLSI ECX, EAX
      ! PUSHF
      ! POP AX
      ! AND AX, 2241
      ! MOV flags%, AX
      IF flags% = {{expectedStatus}} THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [TestCase(0, 1)]
  [TestCase(1, 64)]
  public void Tzcnt_GivenZeroAndOne_ThenSetsCfAndZfPerIntel(int source, int expectedStatus) {
    var output = Run($$"""
      DIM flags%
      ! MOV AX, 65
      ! PUSH AX
      ! POPF
      ! MOV EAX, {{source}}
      ! TZCNT ECX, EAX
      ! PUSHF
      ! POP AX
      ! AND AX, 65
      ! MOV flags%, AX
      IF flags% = {{expectedStatus}} THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void PdepAndPext_GivenAliasedOperands_ThenPreserveOriginalSources() {
    var output = Run("""
      DIM deposited&, extracted&
      ! MOV EAX, 11
      ! MOV ECX, 84
      ! PDEP EAX, EAX, ECX
      ! MOV deposited&, EAX
      ! MOV EAX, 68
      ! MOV ECX, 84
      ! PEXT ECX, EAX, ECX
      ! MOV extracted&, ECX
      IF deposited& = 20 AND extracted& = 5 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Bmi2ShiftsAndRorx_GivenCountAliasesDestination_ThenUseOriginalCountAndPreserveFlags() {
    var output = Run("""
      DIM shl&, shr&, sar&, ror&, flags%
      ! MOV AX, 2261
      ! PUSH AX
      ! POPF
      ! MOV EAX, 305419896
      ! MOV ECX, 4
      ! SHLX ECX, EAX, ECX
      ! MOV shl&, ECX
      ! MOV EAX, -2147483648
      ! MOV ECX, 4
      ! SHRX ECX, EAX, ECX
      ! MOV shr&, ECX
      ! MOV EAX, -2147483648
      ! MOV ECX, 4
      ! SARX ECX, EAX, ECX
      ! MOV sar&, ECX
      ! MOV EAX, 305419896
      ! RORX EDX, EAX, 5
      ! MOV ror&, EDX
      ! PUSHF
      ! POP AX
      ! AND AX, 2261
      ! MOV flags%, AX
      IF shl& = 591751040 AND shr& = 134217728 AND sar& = -134217728 AND ror& = -1064197453 AND flags% = 2261 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Bzhi_GivenBoundaryIndex_ThenSaturatesResultAndSetsDefinedFlags() {
    var output = Run("""
      DIM result&, flags%
      ! MOV AX, 2261
      ! PUSH AX
      ! POPF
      ! MOV EAX, -1
      ! MOV ECX, 32
      ! BZHI EDX, EAX, ECX
      ! MOV result&, EDX
      ! PUSHF
      ! POP AX
      ! AND AX, 2241
      ! MOV flags%, AX
      IF result& = -1 AND flags% = 129 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Mulx_GivenDistinctDestinations_ThenFirstGetsHighSecondGetsLowAndFlagsAreUntouched() {
    var output = Run("""
      DIM high&, low&, flags%
      ! MOV AX, 2261
      ! PUSH AX
      ! POPF
      ! MOV EDX, -1
      ! MOV EBX, 2
      ! MULX EAX, ECX, EBX
      ! MOV high&, EAX
      ! MOV low&, ECX
      ! PUSHF
      ! POP AX
      ! AND AX, 2261
      ! MOV flags%, AX
      IF high& = 1 AND low& = -2 AND flags% = 2261 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Mulx_GivenIdenticalDestinations_ThenHighHalfWins() {
    var output = Run("""
      DIM result&
      ! MOV EDX, -1
      ! MOV EBX, 2
      ! MULX EAX, EAX, EBX
      ! MOV result&, EAX
      IF result& = 1 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }
}
