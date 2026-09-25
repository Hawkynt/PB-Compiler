using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// Loop-invariant code motion for DO/WHILE loops ($OPTIMIZE SPEED). LICM previously
/// hoisted only out of FOR loops; a DO loop has no counter, so invariance is simply
/// "not written in the body". The hoisted expression is computed once in the preheader
/// (before the loop top) and reloaded inside the body. Behaviour is unchanged.
/// </summary>
[TestFixture]
public sealed class DoLoopLicmTests {

  private static SemanticModel Bind(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  [Test]
  public void Execute_GivenDoWhileWithInvariant_WhenSpeed_ThenResultsUnchanged() {
    const string source = """
      $OPTIMIZE SPEED
      DIM a AS INTEGER, b AS INTEGER, i AS INTEGER, s AS INTEGER, t AS INTEGER
      a = 3
      b = 4
      t = 100
      DO WHILE i < 5
        s = s + a * b
        t = t - a * b
        i = i + 1
      LOOP
      PRINT s
      PRINT t
      """;
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    // a*b = 12, five iterations: s = 60, t = 100 - 60 = 40
    Assert.That(DosBoxRunner.Normalize(DosBoxRunner.Run(exe)), Is.EqualTo(" 60\n 40\n"),
      "hoisting the loop-invariant must not change the computed results");
  }
}
