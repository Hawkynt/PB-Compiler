using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Syntax;

/// <summary>
/// Syntax that belongs to no dialect. This is deliberately separate from
/// <see cref="StatementSurfaceCensusTests"/>: that fixture asks whether syntax valid in one BASIC
/// is rejected by another, while this one makes every advertised dialect reject structurally broken
/// programs. A rejection is only a pass when it is a normal lexer/parser/binder diagnostic; an
/// IndexOutOfRangeException is not a particularly obscure BASIC dialect.
/// </summary>
[TestFixture]
public sealed class InvalidSyntaxSurfaceTests {

  /// <param name="BorlandOnly">
  /// Invalid in Bob Zale's lineage only. <c>CALL DWORD</c> is the case: DWORD is a TYPE keyword
  /// there, so the indirect call is missing its target, while to Microsoft it is an ordinary call
  /// to a SUB that happens to be named DWORD - which BC compiles without complaint. Asking a
  /// Microsoft oracle to reject it measures nothing.
  /// </param>
  public sealed record InvalidForm(string Id, string Source, bool BorlandOnly = false);

  internal static readonly InvalidForm[] Forms = [
    new("lexer.unexpected-character", "PRINT ~\n"),
    new("expression.unclosed", "x% = (1 + 2\n"),
    new("if.missing-then", "IF x% = 1\n"),
    new("for.missing-to", "FOR i% = 1\nNEXT i%\n"),
    new("while.missing-condition", "WHILE\nWEND\n"),
    new("do-while.missing-condition", "DO WHILE\nLOOP\n"),
    new("select.missing-subject", "SELECT CASE\nEND SELECT\n"),
    new("on-goto.missing-targets", "ON x% GOTO\n"),
    new("open.missing-file-name", "OPEN FOR OUTPUT AS #1\n"),
    new("input-file.missing-number", "INPUT #, x%\n"),
    new("line.missing-coordinate", "LINE (0,)-(1,1)\n"),
    new("circle.missing-radius", "CIRCLE (0,0)\n"),
    new("pset.missing-coordinate", "PSET (,0)\n"),
    new("dim.missing-upper-bound", "DIM a%(1 TO)\n"),
    new("redim-preserve.missing-array", "REDIM PRESERVE\n"),
    new("declare-sub.missing-name", "DECLARE SUB (x%)\n"),
    new("sub.missing-parameter", "SUB S(\nEND SUB\n"),
    new("type-field.missing-type", "TYPE T\n  x AS\nEND TYPE\n"),
    new("call-dword.missing-target", "CALL DWORD\n", BorlandOnly: true),
    new("resume.too-many-targets", "RESUME first, second\n"),
    new("meta.unknown-command", "$WHATEVER 1\n"),
    new("meta.compile.bad-kind", "$COMPILE SOMETHING\n"),
    new("meta.cpu.missing-tier", "$CPU\n"),
    new("meta.error.bad-check", "$ERROR CARRY ON\n"),
    new("meta.error.bad-mode", "$ERROR BOUNDS MAYBE\n"),
    new("meta.optimize.bad-mode", "$OPTIMIZE TURBO\n"),
    new("meta.option.bad-name", "$OPTION MAGIC\n"),
    new("meta.stack.missing-size", "$STACK\n"),
    new("meta.string.bad-size", "$STRING 3\n"),
    new("meta.dynamic.unexpected-argument", "$DYNAMIC NOW\n"),
    new("meta.static.unexpected-argument", "$STATIC NOW\n"),
    new("meta.dim.bad-scope", "$DIM SOMETHING\n"),
  ];

  public static IEnumerable<TestCaseData> Cases() =>
    from dialect in Enum.GetValues<Dialect>()
    from form in Forms
    select new TestCaseData(dialect, form) {
      TestName = $"Reject_{dialect}_{form.Id}",
    };

  [TestCaseSource(nameof(Cases))]
  public void Compile_GivenStructurallyInvalidSyntax_ThenEveryDialectRejectsCleanly(Dialect dialect, InvalidForm form) {
    var source = dialect.IsGwBasica() ? StatementSurface.NumberPhysicalLines(form.Source) : form.Source;
    try {
      var unit = Parser.Parse(Lexer.Tokenize(source, "INVALID.BAS", dialect), "INVALID.BAS", dialect);
      var model = Binder.Bind(unit, dialect);
      Assert.That(model.Errors, Is.Not.Empty,
        $"{dialect.CanonicalName()} accepted malformed form '{form.Id}'");
    } catch (Exception e) when (e is LexerException or ParserException or PreprocessorException or BindException) {
      Assert.Pass();
    } catch (Exception e) {
      Assert.Fail($"{dialect.CanonicalName()} crashed on malformed form '{form.Id}': {e}");
    }
  }
}
