using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Emit;

/// <summary>
/// The back-emitter (<see cref="BasicWriter"/>): turns a bound program back into PB 3.5-compatible
/// PowerBASIC source. Each test binds a snippet, renders it, and (the round-trip contract) re-parses
/// and re-binds the rendered text under the pb35 dialect with zero errors - proving the output is
/// not just plausible text but a program the pb35 front end accepts.
/// </summary>
[TestFixture]
public sealed class BasicWriterTests {

  private static string Render(string source, Dialect dialect = Dialect.Pb36) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", dialect), "TEST.BAS", dialect);
    var model = Binder.Bind(unit, dialect);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return BasicWriter.Render(model, unit);
  }

  /// <summary>Renders the source, then re-binds the rendered text under pb35; asserts no errors.</summary>
  private static string RenderAndRebind(string source, Dialect dialect = Dialect.Pb35) {
    var basic = Render(source, dialect);
    var unit2 = Parser.Parse(Lexer.Tokenize(basic, "RT.BAS", Dialect.Pb35), "RT.BAS", Dialect.Pb35);
    var model2 = Binder.Bind(unit2, Dialect.Pb35);
    Assert.That(model2.Errors, Is.Empty, $"pb35 re-bind of:\n{basic}\nerrors: " + string.Join("; ", model2.Errors));
    return basic;
  }

  [Test]
  public void Render_Assignment_RoundTripsExpressionWithMinimalParens() {
    var basic = RenderAndRebind("A% = 2 + 3 * 4\nPRINT A%\n");
    Assert.That(basic, Does.Contain("A% = 2 + 3 * 4"), "multiply binds tighter than add - no parens needed");
    Assert.That(basic, Does.Contain("PRINT A%"));
  }

  [Test]
  public void Render_AddsParens_WherePrecedenceRequiresThem() {
    var basic = RenderAndRebind("A% = (2 + 3) * 4\n");
    Assert.That(basic, Does.Contain("(2 + 3) * 4"), "the lower-precedence add under a multiply keeps its parens");
  }

  [Test]
  public void Render_IfThenElse_ReconstructsBlockWithIndentedBody() {
    var basic = RenderAndRebind("IF A% > 0 THEN\n  PRINT \"pos\"\nELSE\n  PRINT \"neg\"\nEND IF\n");
    Assert.That(basic, Does.Contain("IF A% > 0 THEN"));
    Assert.That(basic, Does.Contain("ELSE"));
    Assert.That(basic, Does.Contain("END IF"));
    Assert.That(basic, Does.Contain("  PRINT \"pos\""), "the THEN body is indented one level");
  }

  [Test]
  public void Render_ForLoop_ReconstructsHeaderAndNext() {
    var basic = RenderAndRebind("FOR I% = 1 TO 10 STEP 2\n  PRINT I%\nNEXT\n");
    Assert.That(basic, Does.Contain("FOR I% = 1 TO 10 STEP 2"));
    Assert.That(basic, Does.Contain("NEXT"));
  }

  [Test]
  public void Render_Procedure_ReconstructsSignatureAndBody() {
    var basic = RenderAndRebind("FUNCTION Add%(BYVAL X%, BYVAL Y%)\n  Add% = X% + Y%\nEND FUNCTION\n");
    Assert.That(basic, Does.Contain("FUNCTION Add"));
    Assert.That(basic, Does.Contain("END FUNCTION"));
    Assert.That(basic, Does.Contain("X% + Y%"));
  }

  [Test]
  public void Render_SelectCase_ReconstructsArmsAndElse() {
    var basic = RenderAndRebind("SELECT CASE A%\nCASE 1\n  PRINT \"one\"\nCASE ELSE\n  PRINT \"other\"\nEND SELECT\n");
    Assert.That(basic, Does.Contain("SELECT CASE A%"));
    Assert.That(basic, Does.Contain("CASE 1"));
    Assert.That(basic, Does.Contain("CASE ELSE"));
    Assert.That(basic, Does.Contain("END SELECT"));
  }

  [Test]
  public void Render_FileIo_ReconstructsOpenPrintClose() {
    var basic = RenderAndRebind("OPEN \"R.TXT\" FOR OUTPUT AS #1\nPRINT #1, 6 \\ 2\nCLOSE #1\n");
    Assert.That(basic, Does.Contain("OPEN \"R.TXT\" FOR OUTPUT AS #1"));
    Assert.That(basic, Does.Contain("PRINT #1, 6 \\ 2"), "the file-number expression renders as #1, not a fallback comment");
    Assert.That(basic, Does.Contain("CLOSE #1"));
  }

  [Test]
  public void Render_OnErrorDisable_EmitsGotoZero() {
    var basic = RenderAndRebind("ON ERROR GOTO Trap\nA% = 1\nON ERROR GOTO 0\nTrap:\nRESUME NEXT\n");
    Assert.That(basic, Does.Contain("ON ERROR GOTO 0"), "the disable form keeps its 0 target");
  }

  [Test]
  public void Render_Type_ReconstructsTypeBlock() {
    var basic = RenderAndRebind("TYPE Pt\n  X AS INTEGER\n  Y AS INTEGER\nEND TYPE\nDIM P AS Pt\nP.X = 5\n");
    Assert.That(basic, Does.Contain("TYPE Pt"));
    Assert.That(basic, Does.Contain("X AS INTEGER"));
    Assert.That(basic, Does.Contain("END TYPE"));
    Assert.That(basic, Does.Contain("P.X = 5"));
  }

  [Test]
  public void Render_Pb36FromEndIndex_LowersToPb35ViaSideTable() {
    // a pb36 value-position construct the binder records a desugar for (arr(^1) -> UBOUND(arr)-1+1)
    // must come back as the pb35 core form, not the pb36 surface syntax.
    var basic = RenderAndRebind("DIM A%(10)\nA%(10) = 7\nDIM L%\nL% = A%(^1)\nPRINT L%\n", Dialect.Pb36);
    Assert.That(basic, Does.Not.Contain("^"), "the from-end index is lowered, not emitted as arr(^1)");
    Assert.That(basic, Does.Not.Contain("[unsupported:"), "no node is dropped to a fallback comment");
  }

  [Test]
  public void Render_Pb36InterpolatedString_LowersToConcatenation() {
    // $"...{x}..." has no pb35 form; the binder desugars it to concat/STR$, which must round-trip.
    var basic = RenderAndRebind("DIM N%\nN% = 42\nDIM S$\nS$ = $\"n={N%}\"\nPRINT S$\n", Dialect.Pb36);
    Assert.That(basic, Does.Not.Contain("$\""), "the interpolated string is lowered, not emitted as $\"...\"");
    Assert.That(basic, Does.Not.Contain("[unsupported:"));
  }

  [Test]
  public void Render_NeverDropsAStatement_NoFallbackComment() {
    var basic = RenderAndRebind("PRINT \"hi\"\n");
    Assert.That(basic, Does.Contain("PRINT \"hi\""));
    Assert.That(basic, Does.Not.Contain("[unsupported:"), "a plain PRINT needs no fallback comment");
  }
}
