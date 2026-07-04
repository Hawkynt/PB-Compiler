using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// ITERATE - continue with the next loop pass. <c>ITERATE FOR</c> jumps to the FOR
/// increment, <c>ITERATE DO</c>/<c>LOOP</c>/<c>WHILE</c> to the DO retest, a bare
/// <c>ITERATE</c> to the innermost loop of any kind. Behavioral tests run under
/// DOSBox and are skipped when it is unavailable.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class IterateTests {

  private static string Run(string source, Dialect dialect = Dialect.Pb35) {
    var tokens = Lexer.Tokenize(source, "TEST.BAS", dialect);
    var unit = Parser.Parse(tokens, "TEST.BAS", dialect);
    var model = Binder.Bind(unit, dialect);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return DosBoxRunner.Normalize(DosBoxRunner.Run(exe));
  }

  [Test]
  public void Execute_GivenIterateForInsideIf_WhenRun_ThenSkipsRestOfPass() {
    const string source = """
      FOR i% = 1 TO 6
        IF i% MOD 2 = 0 THEN ITERATE FOR
        PRINT i%;
      NEXT
      PRINT "end"
      """;
    Assert.That(Run(source), Is.EqualTo(" 1  3  5 end\n"));
  }

  [Test]
  public void Execute_GivenBareIterateInsideDo_WhenRun_ThenContinuesInnermostLoop() {
    const string source = """
      i% = 0
      DO WHILE i% < 6
        i% = i% + 1
        IF i% MOD 2 = 0 THEN ITERATE
        PRINT i%;
      LOOP
      PRINT "end"
      """;
    Assert.That(Run(source), Is.EqualTo(" 1  3  5 end\n"));
  }

  [Test]
  public void Execute_GivenIterateDoInsideNestedFor_WhenRun_ThenContinuesOuterDo() {
    // ITERATE DO from inside the inner FOR must resume the enclosing DO's retest,
    // abandoning the rest of the FOR entirely
    const string source = """
      i% = 0
      DO WHILE i% < 3
        i% = i% + 1
        FOR j% = 1 TO 5
          IF j% = 2 THEN ITERATE DO
          PRINT i%; j%;
        NEXT
        PRINT "afterfor";
      LOOP
      PRINT "end"
      """;
    Assert.That(Run(source), Is.EqualTo(" 1  1  2  1  3  1 end\n"));
  }

  [Test]
  public void Execute_GivenBareIterateDecompiled_WhenRunUnderPb35_ThenSameOutput() {
    // a bare ITERATE targets the innermost loop of ANY kind; the decompiled spelling
    // must keep that binding (inside a FOR it may not become ITERATE DO)
    const string source = """
      FOR i% = 1 TO 6
        IF i% MOD 2 = 0 THEN ITERATE
        PRINT i%;
      NEXT
      PRINT "end"
      """;
    var direct = Run(source, Dialect.Pb36);

    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", Dialect.Pb36), "TEST.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty);
    var basic = BasicWriter.Render(model, unit);
    Assert.That(Run(basic), Is.EqualTo(direct), $"decompiled:\n{basic}");
  }
}
