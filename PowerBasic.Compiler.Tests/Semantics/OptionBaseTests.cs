using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.CodeGen;

namespace PowerBasic.Compiler.Tests.Semantics;

/// <summary>
/// OPTION BASE 0|1 - the implicit lower bound of an array declared without one.
///
/// The statement is read by the binder's module pre-pass rather than by the code generator, because
/// it has to take effect on DIMs that come after it in the file but are processed in the same sweep.
/// Nothing is emitted for it: by the time the code generator runs, the bounds already carry the
/// answer. That is why the runtime checks below ask LBOUND and UBOUND rather than looking at bytes.
/// </summary>
[TestFixture]
public sealed class OptionBaseTests {

  private static (SemanticModel Model, List<string> Errors) Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    return (model, model.Errors.Select(e => e.Message).ToList());
  }

  private static string Run(string source) {
    var (model, errors) = Bind(source);
    Assert.That(errors, Is.Empty, "bind: " + string.Join("; ", errors));
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return DosBoxRunner.Normalize(DosBoxRunner.Run(exe));
  }

  [TestCase("0", " 0  4\n")]
  [TestCase("1", " 1  4\n")]
  public void OptionBase_GivenAnArrayDeclaredWithoutALowerBound_WhenRun_ThenTheBaseIsItsLowerBound(string basis, string expected) {
    // DIM a(4) means 0..4 under base 0 and 1..4 under base 1 - the upper bound is written down either
    // way, so only the lower one moves, and the array is one element shorter for it
    Assert.That(Run($"OPTION BASE {basis}\nDIM a%(4)\nPRINT LBOUND(a%); UBOUND(a%)\nEND\n"), Is.EqualTo(expected));
  }

  [Test]
  public void OptionBase_GivenAnExplicitLowerBound_WhenBound_ThenTheDeclarationWins() {
    // OPTION BASE only supplies the bound that was left out; a DIM that states one is unaffected
    Assert.That(Run("OPTION BASE 1\nDIM a%(0 TO 4)\nPRINT LBOUND(a%); UBOUND(a%)\nEND\n"), Is.EqualTo(" 0  4\n"));
  }

  [TestCase("OPTION BASE 2")]
  [TestCase("OPTION BASE -1")]
  [TestCase("OPTION BASE n%")]
  public void OptionBase_GivenAnythingButALiteralZeroOrOne_WhenBound_ThenItIsRefused(string statement) {
    // Silently ignoring it would be the worst of the three answers: the statement decides the
    // subscripts of every array declared after it, so a value that does not take effect shifts a
    // whole program's indices with nothing to show for it
    var (_, errors) = Bind($"{statement}\nDIM a%(4)\nEND\n");
    Assert.That(errors, Has.Some.Contains("OPTION BASE takes a literal 0 or 1"));
  }

  [Test]
  public void OptionBase_GivenNoBaseKeyword_WhenParsed_ThenItIsRefused() {
    // OPTION is not a statement on its own - BASE is the only thing modelled after it, and the
    // parser says so rather than shrugging the rest of the line off
    Assert.That(() => Bind("OPTION EXPLICIT\nEND\n"), Throws.Exception);
  }
}
