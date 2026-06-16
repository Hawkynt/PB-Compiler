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
    const string source = "PRINT \"HI\"\nEND";
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", dialect), "T.BAS", dialect);
    var model = Binder.Bind(unit, dialect);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    _ = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
  }
}
