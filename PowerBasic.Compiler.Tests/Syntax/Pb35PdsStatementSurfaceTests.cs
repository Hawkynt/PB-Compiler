using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;
using PowerBasic.Compiler.Semantics;

namespace PowerBasic.Compiler.Tests.Syntax;

/// <summary>
/// The deliberate PB 3.5 versus BASIC PDS 7.1 boundary. The broad dialect census derives an answer
/// from version minima; this fixture requires a second, explicit classification for every form so a
/// newly-added statement cannot quietly inherit "both dialects" from a constructor default.
/// </summary>
[TestFixture]
public sealed class Pb35PdsStatementSurfaceTests {

  private static CompilationUnit Parse(string source, Dialect dialect)
    => Parser.Parse(Lexer.Tokenize(source, "T.BAS", dialect), "T.BAS", dialect);

  private sealed class Source(string text) : ISourceProvider {
    public bool TryReadSource(string name, string? includedFrom, out string source, out string resolvedName) {
      source = text;
      resolvedName = name;
      return true;
    }
  }

  [Test]
  public void Expectations_GivenEveryStatementForm_ThenThePairClassificationIsCompleteAndUnique() {
    var forms = StatementSurface.All.Select(form => form.Id).ToList();
    var classified = StatementSurface.Pb35Pds71Expectations.Keys.ToList();

    Assert.Multiple(() => {
      Assert.That(classified, Is.Unique, "a form may have only one PB35/PDS71 classification");
      Assert.That(classified, Is.EquivalentTo(forms),
        "every form needs an explicit PB35/PDS71 decision; an omitted form must not default to both");
    });
  }

  [Test]
  public void Expectations_GivenPb35AndPds71_ThenTheVersionMatrixMatchesTheExplicitPairAudit() {
    foreach (var form in StatementSurface.All) {
      var expected = StatementSurface.Pb35Pds71Expectations[form.Id];
      Assert.Multiple(() => {
        Assert.That(StatementSurface.ShouldAccept(form, Dialect.Pb35),
          Is.EqualTo(expected.HasFlag(StatementSurface.PairAvailability.Pb35)), form.Id + " under PB 3.5");
        Assert.That(StatementSurface.ShouldAccept(form, Dialect.Pds71),
          Is.EqualTo(expected.HasFlag(StatementSurface.PairAvailability.Pds71)), form.Id + " under PDS 7.1");
      });
    }
  }

  [TestCase("REM $STATIC", "STATIC")]
  [TestCase("' $STATIC", "STATIC")]
  [TestCase("REM $DYNAMIC", "DYNAMIC")]
  [TestCase("' $DYNAMIC", "DYNAMIC")]
  public void Parse_GivenPdsCommentMeta_ThenItBecomesAnEffectiveMetaStatement(string source, string command) {
    var statement = Parse(source, Dialect.Pds71).Statements.Single();
    Assert.That(statement, Is.TypeOf<MetaStmt>());
    Assert.That(((MetaStmt)statement).Command, Is.EqualTo(command));
  }

  [TestCase("REM $STATIC")]
  [TestCase("' $DYNAMIC")]
  public void Parse_GivenPdsCommentMetaSpelling_WhenPb35_ThenItStaysAnOrdinaryComment(string source) {
    Assert.That(Parse(source, Dialect.Pb35).Statements, Is.Empty);
  }

  [TestCase("$STATIC", Dialect.Pds71)]
  [TestCase("$DYNAMIC", Dialect.Pds71)]
  [TestCase("$CPU 80386", Dialect.Pds71)]
  [TestCase("REM $STATIC NOW", Dialect.Pds71)]
  [TestCase("REM $WHATEVER", Dialect.Pds71)]
  [TestCase("REM $STATIC", Dialect.Basica)]
  [TestCase("REM $STATIC", Dialect.Gw)]
  public void Parse_GivenMetacommandFromAnotherDialectOrMalformed_ThenItIsRejectedOrInert(
    string source, Dialect dialect) {
    if (dialect.IsGwBasica()) {
      var numbered = "10 " + source;
      Assert.That(Parse(numbered, dialect).Statements.OfType<MetaStmt>(), Is.Empty);
      return;
    }
    var error = Assert.Catch(() => Parse(source, dialect));
    Assert.That(error, Is.TypeOf<LexerException>().Or.TypeOf<ParserException>());
  }

  [Test]
  public void Bind_GivenPdsDynamicCommentMeta_ThenAConstantBoundArrayUsesDynamicStorage() {
    const string source = "REM $DYNAMIC\nDIM a%(10)";
    var tokens = Preprocessor.Expand("T.BAS", new Source(source), Dialect.Pds71);
    var model = Binder.Bind(Parser.Parse(tokens, "T.BAS", Dialect.Pds71), Dialect.Pds71);

    Assert.That(model.Errors, Is.Empty);
    Assert.That(((ArrayType)model.ModuleVariables["a%()"].Type).IsDynamic, Is.True);
  }

  [Test]
  public void Bind_GivenTheSameCommentUnderPb35_ThenItHasNoMetacommandEffect() {
    const string source = "REM $DYNAMIC\nDIM a%(10)";
    var tokens = Preprocessor.Expand("T.BAS", new Source(source), Dialect.Pb35);
    var model = Binder.Bind(Parser.Parse(tokens, "T.BAS", Dialect.Pb35), Dialect.Pb35);

    Assert.That(model.Errors, Is.Empty);
    Assert.That(((ArrayType)model.ModuleVariables["a%()"].Type).IsDynamic, Is.False);
  }
}
