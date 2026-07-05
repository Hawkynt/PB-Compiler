using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// pb36 partial application and composition over typed delegates: <c>BIND(f, consts...)</c>
/// pre-fills leading parameters (compile-time constants - the delegate snapshots them) and
/// <c>COMPOSE(f, g)</c> yields h with h(x) = g(f(x)). Both lower to synthesized thunk
/// FUNCTIONs addressed via CODEPTR32 - the same machinery as lambdas/delegates.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class PartialApplicationTests {

  private static SemanticModel Bind(string source, Dialect dialect = Dialect.Pb36) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "t.bas", dialect), "t.bas", dialect);
    return Binder.Bind(unit, dialect);
  }

  private static string Run(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "t.bas", Dialect.Pb36), "t.bas", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return DosBoxRunner.Normalize(DosBoxRunner.Run(exe));
  }

  private const string _PROCS = """
    DECLARE FUNCTION Add(BYVAL a AS LONG, BYVAL b AS LONG) AS LONG
    DECLARE FUNCTION Twice(BYVAL x AS LONG) AS LONG
    DECLARE FUNCTION Inc(BYVAL x AS LONG) AS LONG

    FUNCTION Add(BYVAL a AS LONG, BYVAL b AS LONG) AS LONG
      Add = a + b
    END FUNCTION
    FUNCTION Twice(BYVAL x AS LONG) AS LONG
      Twice = x * 2
    END FUNCTION
    FUNCTION Inc(BYVAL x AS LONG) AS LONG
      Inc = x + 1
    END FUNCTION

    """;

  [Test]
  public void Execute_GivenBind_WhenInvoked_ThenPrefilledArgumentApplies() {
    const string source = _PROCS + """
      DIM add5 AS FUNCTION(LONG) AS LONG
      add5 = BIND(Add, 5)
      PRINT add5(10); add5(37)
      """;
    Assert.That(Run(source), Is.EqualTo(" 15  42\n"));
  }

  [Test]
  public void Execute_GivenCompose_WhenInvoked_ThenAppliesFThenG() {
    const string source = _PROCS + """
      DIM h AS FUNCTION(LONG) AS LONG
      h = COMPOSE(Twice, Inc)
      PRINT h(20)
      """;
    // h(x) = Inc(Twice(x)) = 41
    Assert.That(Run(source), Is.EqualTo(" 41\n"));
  }

  [Test]
  public void Bind_GivenNonConstantBindArgument_WhenBound_ThenError() {
    var model = Bind(_PROCS + "DIM n AS LONG\nDIM d AS FUNCTION(LONG) AS LONG\nd = BIND(Add, n)\n");
    Assert.That(model.Errors.Any(e => e.Message.Contains("compile-time")), Is.True, string.Join("; ", model.Errors));
  }

  [Test]
  public void Bind_GivenOverBinding_WhenBound_ThenError() {
    var model = Bind(_PROCS + "DIM d AS FUNCTION(LONG) AS LONG\nd = BIND(Add, 1, 2)\n");
    Assert.That(model.Errors.Any(e => e.Message.Contains("fewer arguments")), Is.True, string.Join("; ", model.Errors));
  }

  [Test]
  public void Bind_GivenUserProcedureNamedBind_WhenCalled_ThenUserWins() {
    var model = Bind("DECLARE FUNCTION BIND(BYVAL x AS LONG) AS LONG\nPRINT BIND(3)\nFUNCTION BIND(BYVAL x AS LONG) AS LONG\n  BIND = x\nEND FUNCTION\n");
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
  }

  [Test]
  public void Render_GivenBindAndCompose_WhenDecompiled_ThenThunksRecompileUnderPb35() {
    var source = _PROCS + "DIM add5 AS FUNCTION(LONG) AS LONG\nadd5 = BIND(Add, 5)\nDIM h AS FUNCTION(LONG) AS LONG\nh = COMPOSE(Twice, Inc)\nPRINT add5(1); h(1)\n";
    var unit = Parser.Parse(Lexer.Tokenize(source, "t.bas", Dialect.Pb36), "t.bas", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var basic = PowerBasic.Compiler.Emit.BasicWriter.Render(model, unit);
    Assert.That(basic, Does.Not.Contain("BIND(").And.Not.Contain("COMPOSE("), $"the forms lower to thunks:\n{basic}");
    var unit2 = Parser.Parse(Lexer.Tokenize(basic, "rt.bas", Dialect.Pb35), "rt.bas", Dialect.Pb35);
    var model2 = Binder.Bind(unit2, Dialect.Pb35);
    Assert.That(model2.Errors, Is.Empty, $"pb35 re-bind of:\n{basic}\nerrors: " + string.Join("; ", model2.Errors));
  }
}
