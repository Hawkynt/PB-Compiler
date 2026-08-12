using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Syntax;

/// <summary>
/// <c>VIEW PRINT topline TO bottomline</c>, whose <c>TO</c> the generic command parser used to read
/// as the start of another expression - so the spelling every real program uses was refused with
/// "unknown SUB TO" while the comma form nobody writes was accepted.
///
/// <c>TO</c> is a separator only here. It belongs to FOR, to DIM's bounds and to SELECT CASE
/// everywhere else, and the tests below exist because making it a separator generally would swallow
/// those - the fix is worth less than the regression it could cause.
///
/// Read as QuickBASIC, because VIEW PRINT is Microsoft's: PBC 3.0 and 3.5 answer it '"(" expected',
/// their VIEW being the graphics viewport and nothing else. The statements that must keep their own
/// TO are still read as pb36, since that is where the swallowing would have to be avoided.
/// </summary>
[TestFixture]
public sealed class ViewPrintRangeTests {

  private static List<string> Errors(string body, Dialect dialect = Dialect.Pb36) {
    try {
      var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(body + "\nEND\n", "T.BAS", dialect), "T.BAS", dialect), dialect);
      return model.Errors.Select(e => e.Message).ToList();
    } catch (Exception e) {
      return [e.Message];
    }
  }

  [TestCase("VIEW PRINT 1 TO 20")]
  [TestCase("VIEW PRINT")]
  [TestCase("VIEW PRINT 1, 20")]
  public void ViewPrint_GivenEachSpelling_ThenItParses(string statement) =>
    Assert.That(Errors(statement, Dialect.Qb45), Is.Empty);

  [TestCase("FOR i% = 1 TO 3\nNEXT i%", TestName = "TO still opens a FOR's limit")]
  [TestCase("DIM z%(1 TO 4)", TestName = "TO still separates DIM's bounds")]
  [TestCase("SELECT CASE 2\nCASE 1 TO 3\nEND SELECT", TestName = "TO still forms a CASE range")]
  [TestCase("VIEW (0, 0)-(10, 10)", TestName = "VIEW's own box syntax is unaffected")]
  public void Statements_GivenTheirOwnTo_ThenTheViewPrintSeparatorDoesNotSwallowIt(string statement) =>
    Assert.That(Errors(statement), Is.Empty);

  [Test]
  public void ViewPrint_GivenARange_ThenBothBoundsSurviveAsArguments() {
    var unit = Parser.Parse(Lexer.Tokenize("VIEW PRINT 1 TO 20\nEND\n", "T.BAS", Dialect.Qb45), "T.BAS", Dialect.Qb45);
    var command = unit.Statements.OfType<CommandStmt>().Single(c => c.Keyword == "VIEW PRINT");
    Assert.That(command.Arguments, Has.Count.EqualTo(2), "the range is two arguments, as the comma form is");
  }
}
