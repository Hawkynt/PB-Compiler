using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Emit;

/// <summary>
/// The back-emitter (<see cref="BasicWriter"/>): un-parses a bound <see cref="SemanticModel"/> to
/// readable PowerBASIC so the result of the front end and the optimizer is visible as source. Each
/// test binds a snippet and asserts the rendered text contains the expected reconstructed constructs.
/// </summary>
[TestFixture]
public sealed class BasicWriterTests {

  private static string Render(string source, Dialect dialect = Dialect.Pb36) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", dialect), "TEST.BAS", dialect);
    var model = Binder.Bind(unit, dialect);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return BasicWriter.Render(model);
  }

  [Test]
  public void Render_Assignment_RoundTripsExpressionWithMinimalParens() {
    var basic = Render("A% = 2 + 3 * 4\nPRINT A%\n");
    Assert.That(basic, Does.Contain("A% = 2 + 3 * 4"), "multiply binds tighter than add - no parens needed");
    Assert.That(basic, Does.Contain("PRINT A%"));
  }

  [Test]
  public void Render_AddsParens_WherePrecedenceRequiresThem() {
    var basic = Render("A% = (2 + 3) * 4\n");
    Assert.That(basic, Does.Contain("(2 + 3) * 4"), "the lower-precedence add under a multiply keeps its parens");
  }

  [Test]
  public void Render_IfThenElse_ReconstructsBlockWithIndentedBody() {
    var basic = Render("IF A% > 0 THEN\n  PRINT \"pos\"\nELSE\n  PRINT \"neg\"\nEND IF\n");
    Assert.That(basic, Does.Contain("IF A% > 0 THEN"));
    Assert.That(basic, Does.Contain("ELSE"));
    Assert.That(basic, Does.Contain("END IF"));
    Assert.That(basic, Does.Contain("  PRINT \"pos\""), "the THEN body is indented one level");
  }

  [Test]
  public void Render_ForLoop_ReconstructsHeaderAndNext() {
    var basic = Render("FOR I% = 1 TO 10 STEP 2\n  PRINT I%\nNEXT\n");
    Assert.That(basic, Does.Contain("FOR I% = 1 TO 10 STEP 2"));
    Assert.That(basic, Does.Contain("NEXT"));
  }

  [Test]
  public void Render_Procedure_ReconstructsSignatureAndBody() {
    var basic = Render("FUNCTION Add%(BYVAL X%, BYVAL Y%)\n  Add% = X% + Y%\nEND FUNCTION\n");
    Assert.That(basic, Does.Contain("FUNCTION Add"));
    Assert.That(basic, Does.Contain("END FUNCTION"));
    Assert.That(basic, Does.Contain("X% + Y%"));
  }

  [Test]
  public void Render_SelectCase_ReconstructsArmsAndElse() {
    var basic = Render("SELECT CASE A%\nCASE 1\n  PRINT \"one\"\nCASE ELSE\n  PRINT \"other\"\nEND SELECT\n");
    Assert.That(basic, Does.Contain("SELECT CASE A%"));
    Assert.That(basic, Does.Contain("CASE 1"));
    Assert.That(basic, Does.Contain("CASE ELSE"));
    Assert.That(basic, Does.Contain("END SELECT"));
  }

  [Test]
  public void Render_NeverDropsAStatement_UnsupportedBecomesAComment() {
    // whatever the dialect produces, the renderer must emit a line for every statement -
    // an unmodelled node degrades to a comment, never to nothing.
    var basic = Render("PRINT \"hi\"\n");
    Assert.That(basic, Does.Contain("PRINT \"hi\""));
    Assert.That(basic, Does.Not.Contain("[unsupported:"), "a plain PRINT needs no fallback comment");
  }
}
