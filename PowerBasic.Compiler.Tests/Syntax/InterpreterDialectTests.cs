using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Syntax;

/// <summary>
/// Wave 1 scaffolding for the classic Microsoft BASIC interpreters - BASICA,
/// GW-BASIC and QBasic - as selectable dialects: family classification, display
/// names, and that a simple program compiles end-to-end through the existing
/// Microsoft front end. Interpreter-faithful numerics (MBF floats for BASICA/
/// GW-BASIC) and the interpreter-oracle differential harness are later waves.
/// </summary>
[TestFixture]
public sealed class InterpreterDialectTests {

  [TestCase(Dialect.Basica, "BASICA")]
  [TestCase(Dialect.Gw, "GW-BASIC")]
  [TestCase(Dialect.Qbasic, "QBasic")]
  public void DisplayName_GivenInterpreterDialect_ThenFriendlyName(Dialect dialect, string expected)
    => Assert.That(dialect.DisplayName(), Is.EqualTo(expected));

  [TestCase(Dialect.Basica)]
  [TestCase(Dialect.Gw)]
  [TestCase(Dialect.Qbasic)]
  public void Family_GivenInterpreterDialect_ThenMicrosoft(Dialect dialect)
    => Assert.That(dialect.Family(), Is.EqualTo(DialectFamily.Microsoft));

  [TestCase(Dialect.Basica)]
  [TestCase(Dialect.Gw)]
  [TestCase(Dialect.Qbasic)]
  public void IsInterpreter_GivenInterpreterDialect_ThenTrue(Dialect dialect)
    => Assert.That(dialect.IsInterpreter(), Is.True);

  [Test]
  public void IsGwBasica_GivenEachDialect_ThenOnlyBasicaAndGw() {
    Assert.Multiple(() => {
      Assert.That(Dialect.Basica.IsGwBasica(), Is.True);
      Assert.That(Dialect.Gw.IsGwBasica(), Is.True);
      Assert.That(Dialect.Qbasic.IsGwBasica(), Is.False); // QBasic is the QB 4.5-era IEEE interpreter
    });
  }

  [Test]
  public void IsBascomRuntime_GivenInterpreters_ThenOnlyMbfEraIsTrue() {
    Assert.Multiple(() => {
      // BASICA / GW-BASIC share the MBF / half-away-rounding heritage
      Assert.That(Dialect.Basica.IsBascomRuntime(), Is.True);
      Assert.That(Dialect.Gw.IsBascomRuntime(), Is.True);
      // QBasic is QuickBASIC 4.5-era (IEEE), past the BASCOM runtime
      Assert.That(Dialect.Qbasic.IsBascomRuntime(), Is.False);
    });
  }

  [TestCase(Dialect.Basica)]
  [TestCase(Dialect.Gw)]
  [TestCase(Dialect.Qbasic)]
  public void Compile_GivenSimpleProgram_WhenInterpreterDialect_ThenNoErrors(Dialect dialect) {
    var source = dialect.IsGwBasica() ? "10 PRINT \"HI\"\n20 END" : "PRINT \"HI\"\nEND";
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", dialect), "T.BAS", dialect);
    var model = Binder.Bind(unit, dialect);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    _ = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
  }

  [TestCase(Dialect.Basica)]
  [TestCase(Dialect.Gw)]
  public void Parse_GivenAnUnnumberedProgramLine_ThenMbfInterpreterDialectRejectsIt(Dialect dialect) {
    Assert.That(
      () => Parser.Parse(Lexer.Tokenize("PRINT 1\n", "T.BAS", dialect), "T.BAS", dialect),
      Throws.TypeOf<LexerException>().With.Message.Contains("requires a numeric line number"));
  }

  [TestCase(Dialect.Basica)]
  [TestCase(Dialect.Gw)]
  public void Parse_GivenAnUnnumberedCommentOnlyLine_ThenMbfInterpreterDialectRejectsIt(Dialect dialect) {
    Assert.That(
      () => Parser.Parse(Lexer.Tokenize("10 PRINT 1\nREM not numbered\n20 END\n", "T.BAS", dialect), "T.BAS", dialect),
      Throws.TypeOf<LexerException>().With.Message.Contains("requires a numeric line number"));
  }

  [TestCase(Dialect.Basica)]
  [TestCase(Dialect.Gw)]
  public void Parse_GivenANamedLabel_ThenMbfInterpreterDialectRejectsIt(Dialect dialect) {
    Assert.That(
      () => Parser.Parse(Lexer.Tokenize("10 start: PRINT 1\n", "T.BAS", dialect), "T.BAS", dialect),
      Throws.TypeOf<ParserException>().With.Message.Contains("numeric line labels only"));
  }

  [TestCase(Dialect.Basica)]
  [TestCase(Dialect.Gw)]
  public void Compile_GivenNumberedForAndWhileBlocks_ThenMbfInterpreterDialectAcceptsThem(Dialect dialect) {
    const string source = "10 FOR I% = 1 TO 2\n20 PRINT I%\n30 NEXT I%\n40 WHILE I% < 4\n50 I% = I% + 1\n60 WEND\n70 END\n";
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", dialect), "T.BAS", dialect), dialect);
    Assert.That(model.Errors, Is.Empty);

    var generator = new CodeGenerator(model);
    _ = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty);
  }

  [TestCase(Dialect.Basica)]
  [TestCase(Dialect.Gw)]
  public void Parse_GivenLaterStructuredSyntax_ThenMbfInterpreterDialectRejectsIt(Dialect dialect) {
    const string source = "10 SELECT CASE X%\n20 CASE 1\n30 PRINT 1\n40 END SELECT\n50 END\n";
    Assert.That(
      () => Parser.Parse(Lexer.Tokenize(source, "T.BAS", dialect), "T.BAS", dialect),
      Throws.TypeOf<ParserException>().With.Message.Contains("SELECT CASE"));
  }

  [TestCase(Dialect.Qb45)]
  [TestCase(Dialect.Pb35)]
  public void Parse_GivenANamedLabel_ThenLaterCompiledDialectAcceptsIt(Dialect dialect) {
    var model = Binder.Bind(
      Parser.Parse(Lexer.Tokenize("start:\nPRINT 1\nEND\n", "T.BAS", dialect), "T.BAS", dialect),
      dialect);
    Assert.That(model.Errors, Is.Empty);
  }

  [TestCase(Dialect.Basica)]
  [TestCase(Dialect.Gw)]
  public void Emit_GivenDeferredTextBehindAConstantFalseBranch_ThenWarnsAndCompiles(Dialect dialect) {
    const string source = "10 IF 0 THEN PRINT ( THIS IS ARBITRARY TEXT\n20 PRINT 1\n30 END\n";
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", dialect), "T.BAS", dialect), dialect);
    Assert.That(model.Errors, Is.Empty);
    Assert.That(model.Warnings, Has.Some.Matches<Diagnostic>(w => w.Message.Contains("deferred", StringComparison.OrdinalIgnoreCase)));

    var generator = new CodeGenerator(model) { Optimize = false, UseExperimentalBackend = true };
    _ = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty,
      "constant-false interpreter text is unreachable and must not reach emission");
    Assert.That(generator.BackendRoutedNames, Does.Contain("main"),
      "dead deferred text must not force the x86-16 back end to decline");
  }

  [TestCase(Dialect.Basica)]
  [TestCase(Dialect.Gw)]
  public void Emit_GivenDeferredTextOnAPossiblyReachablePath_ThenCompilationFails(Dialect dialect) {
    const string source = "10 INPUT X%\n20 IF X% THEN THIS IS ARBITRARY TEXT\n30 END\n";
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", dialect), "T.BAS", dialect), dialect);
    Assert.That(model.Errors, Is.Empty);

    var generator = new CodeGenerator(model) { Optimize = false };
    _ = generator.EmitExecutable();
    Assert.That(generator.Errors,
      Has.Some.Matches<Diagnostic>(e => e.Message.Contains("path is not provably unreachable", StringComparison.OrdinalIgnoreCase)));
  }

  [TestCase(Dialect.Basica)]
  [TestCase(Dialect.Gw)]
  public void Emit_GivenDeferredTextAtTopLevel_ThenCompilationFails(Dialect dialect) {
    const string source = "10 THIS IS ARBITRARY TEXT\n20 END\n";
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", dialect), "T.BAS", dialect), dialect);
    Assert.That(model.Errors, Is.Empty);

    var generator = new CodeGenerator(model);
    _ = generator.EmitExecutable();
    Assert.That(generator.Errors,
      Has.Some.Matches<Diagnostic>(e => e.Message.Contains("path is not provably unreachable", StringComparison.OrdinalIgnoreCase)));
  }
}
