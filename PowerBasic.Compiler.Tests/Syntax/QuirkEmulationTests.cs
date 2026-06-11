using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Syntax;

/// <summary>
/// Dialect-conditional bug emulation (docs/QUIRKS.md): compiling under an old
/// --dialect replicates that compiler's documented bugs so old sources behave
/// identically; pb35 keeps the fixed behavior.
/// </summary>
[TestFixture]
public sealed class QuirkEmulationTests {

  private static Token[] LexLine(string source, Dialect dialect) {
    var tokens = Lexer.Tokenize(source, "TEST.BAS", dialect).ToArray();
    return tokens[..^2];
  }

  private static SemanticModel Bind(string source, Dialect dialect) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", dialect), "TEST.BAS", dialect);
    return Binder.Bind(unit, dialect);
  }

  #region QUIRK 2.1/2.2 - leading-zero radix escape arrived with 3.1

  [TestCase(Dialect.Pb20)]
  [TestCase(Dialect.Pb21)]
  [TestCase(Dialect.Pb30)]
  public void Lex_GivenLeadingZeroRadix_WhenPre31_ThenStillSigned(Dialect dialect) {
    var t = LexLine("&H0A000", dialect);
    Assert.That(t[0].IntegerValue, Is.EqualTo(-24576), "pre-3.1 reads every radix literal signed");
    Assert.That(t[0].Suffix, Is.EqualTo(TypeSuffix.Integer));
  }

  [TestCase(Dialect.Pb31)]
  [TestCase(Dialect.Pb32)]
  [TestCase(Dialect.Pb35)]
  public void Lex_GivenLeadingZeroRadix_When31Plus_ThenUnsignedWidened(Dialect dialect) {
    var t = LexLine("&H0A000", dialect);
    Assert.That(t[0].IntegerValue, Is.EqualTo(40960));
    Assert.That(t[0].Suffix, Is.EqualTo(TypeSuffix.Long));
  }

  [Test]
  public void Lex_GivenLeadingZeroFfff_WhenPb30_ThenMinusOne() {
    var t = LexLine("&H0FFFF", Dialect.Pb30);
    Assert.That(t[0].IntegerValue, Is.EqualTo(-1));
  }

  #endregion

  #region QUIRK 2.26 - equate constant folding bug (3.0-3.2)

  [TestCase(Dialect.Pb30)]
  [TestCase(Dialect.Pb31)]
  [TestCase(Dialect.Pb32)]
  public void Bind_GivenNegativeEquateChain_WhenBuggyDialect_ThenLeadingMinusBindsWholeChain(Dialect dialect) {
    var model = Bind("%K = -20-4", dialect);
    Assert.That(model.Equates["K"].Integer, Is.EqualTo(-16), "-20-4 folds as -(20-4) in PB 3.0-3.2");
  }

  [TestCase(Dialect.Pb21)]
  [TestCase(Dialect.Pb35)]
  public void Bind_GivenNegativeEquateChain_WhenFixedDialect_ThenCorrectFolding(Dialect dialect) {
    var model = Bind("%K = -20-4", dialect);
    Assert.That(model.Equates["K"].Integer, Is.EqualTo(-24));
  }

  [Test]
  public void Bind_GivenNegativeEquateWithAddition_WhenBuggyDialect_ThenSameMisbinding() {
    // interpretation: -(20+4) = -24 instead of the correct -16 (pending oracle verification)
    var model = Bind("%K = -20+4", Dialect.Pb30);
    Assert.That(model.Equates["K"].Integer, Is.EqualTo(-24));
  }

  [Test]
  public void Bind_GivenEquateWithoutLeadingMinus_WhenBuggyDialect_ThenUnaffected() {
    var model = Bind("%K = 20-4", Dialect.Pb30);
    Assert.That(model.Equates["K"].Integer, Is.EqualTo(16));
  }

  [Test]
  public void Expand_GivenBuggyEquate_WhenPreprocessorEvaluates_ThenAgreesWithBinder() {
    // the $IF evaluator must see the same -16 the binder computes
    var provider = new OneFile("%K = -20-4\r\n$IF %K + 16\r\nnonzero\r\n$ELSE\r\nzero\r\n$ENDIF");
    var identifiers = Preprocessor.Expand("MAIN.BAS", provider, Dialect.Pb30)
      .Where(t => t.Kind == TokenKind.Identifier).Select(t => t.Text).ToArray();
    Assert.That(identifiers, Is.EqualTo(new[] { "zero" }));
  }

  private sealed class OneFile(string text) : ISourceProvider {
    public bool TryReadSource(string name, string? includedFrom, out string source, out string resolvedName) {
      (source, resolvedName) = (text, name);
      return true;
    }
  }

  #endregion

  #region QUIRK 2.21 - PB 3.0 inline-asm operand semantics (warning only)

  [Test]
  public void Bind_GivenInlineAsm_WhenPb30_ThenSemanticsWarningOnce() {
    var model = Bind("! mov ax, 1\n! mov bx, 2", Dialect.Pb30);
    Assert.That(model.Warnings.Count(w => w.Message.Contains("FAQ 2.21")), Is.EqualTo(1));
  }

  [Test]
  public void Bind_GivenInlineAsm_WhenPb31_ThenNoSemanticsWarning() {
    var model = Bind("! mov ax, 1", Dialect.Pb31);
    Assert.That(model.Warnings.Where(w => w.Message.Contains("FAQ 2.21")), Is.Empty);
  }

  #endregion

  #region SemanticModel carries the dialect for codegen quirks

  [TestCase(Dialect.Pb30)]
  [TestCase(Dialect.Pb35)]
  public void Bind_GivenDialect_WhenBound_ThenModelCarriesIt(Dialect dialect)
    => Assert.That(Bind("x% = 1", dialect).Dialect, Is.EqualTo(dialect));

  #endregion
}
