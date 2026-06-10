using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Syntax;

[TestFixture]
public sealed class PreprocessorTests {

  private sealed class FakeSources(params (string Name, string Text)[] files) : ISourceProvider {
    public bool TryReadSource(string name, string? includedFrom, out string text, out string resolvedName) {
      foreach (var (n, t) in files)
        if (n.Equals(name, StringComparison.OrdinalIgnoreCase)) {
          (text, resolvedName) = (t, n);
          return true;
        }
      (text, resolvedName) = ("", name);
      return false;
    }
  }

  private static Token[] Expand(string main, params (string Name, string Text)[] includes) {
    var files = new (string, string)[includes.Length + 1];
    files[0] = ("MAIN.BAS", main);
    includes.CopyTo(files.AsSpan(1));
    return Preprocessor.Expand("MAIN.BAS", new FakeSources(files)).ToArray();
  }

  private static string[] Identifiers(IEnumerable<Token> tokens) => tokens.Where(t => t.Kind == TokenKind.Identifier).Select(t => t.Text).ToArray();

  #region $INCLUDE

  [Test]
  public void Expand_GivenInclude_WhenExpanded_ThenTokensSplicedInPlace() {
    var t = Expand("a\r\n$INCLUDE \"INC.BI\"\r\nb", ("INC.BI", "x\r\ny"));
    Assert.That(Identifiers(t), Is.EqualTo(new[] { "a", "x", "y", "b" }));
  }

  [Test]
  public void Expand_GivenNestedIncludes_WhenExpanded_ThenDepthFirstSplice() {
    var t = Expand("$INCLUDE \"A.BI\"\r\nend", ("A.BI", "a1\r\n$INCLUDE \"B.BI\"\r\na2"), ("B.BI", "b1"));
    Assert.That(Identifiers(t), Is.EqualTo(new[] { "a1", "b1", "a2", "end" }));
  }

  [Test]
  public void Expand_GivenInclude_WhenExpanded_ThenExactlyOneEndOfFileToken() {
    var t = Expand("$INCLUDE \"INC.BI\"", ("INC.BI", "x"));
    Assert.That(t.Count(x => x.Kind == TokenKind.EndOfFile), Is.EqualTo(1));
    Assert.That(t[^1].Kind, Is.EqualTo(TokenKind.EndOfFile));
  }

  [Test]
  public void Expand_GivenIncludedTokens_WhenExpanded_ThenPositionsReportIncludedFile() {
    var t = Expand("$INCLUDE \"INC.BI\"", ("INC.BI", "x"));
    var x = t.Single(tok => tok.Kind == TokenKind.Identifier);
    Assert.That(x.Position.File, Is.EqualTo("INC.BI"));
  }

  [Test]
  public void Expand_GivenMissingInclude_WhenExpanded_ThenPreprocessorException() {
    Assert.Throws<PreprocessorException>(() => Expand("$INCLUDE \"NOPE.BI\""));
  }

  [Test]
  public void Expand_GivenCircularInclude_WhenExpanded_ThenPreprocessorException() {
    Assert.Throws<PreprocessorException>(() => Expand("$INCLUDE \"A.BI\"", ("A.BI", "$INCLUDE \"A.BI\"")));
  }

  [Test]
  public void Expand_GivenSameIncludeTwiceSequentially_WhenExpanded_ThenSplicedTwice() {
    // PB splices textually every time - no include guards
    var t = Expand("$INCLUDE \"INC.BI\"\r\n$INCLUDE \"INC.BI\"", ("INC.BI", "x"));
    Assert.That(Identifiers(t), Is.EqualTo(new[] { "x", "x" }));
  }

  #endregion

  #region $IF / $ELSE / $ENDIF

  [Test]
  public void Expand_GivenIfWithTrueEquate_WhenExpanded_ThenBlockKept() {
    var t = Expand("%FLAG = 1\r\n$IF %FLAG\r\nkept\r\n$ENDIF\r\nafter");
    Assert.That(Identifiers(t), Is.EqualTo(new[] { "kept", "after" }));
  }

  [Test]
  public void Expand_GivenIfWithZeroEquate_WhenExpanded_ThenBlockSkipped() {
    var t = Expand("%FLAG = 0\r\n$IF %FLAG\r\nskipped\r\n$ENDIF\r\nafter");
    Assert.That(Identifiers(t), Is.EqualTo(new[] { "after" }));
  }

  [Test]
  public void Expand_GivenIfElse_WhenConditionFalse_ThenElseBranchKept() {
    var t = Expand("%FLAG = 0\r\n$IF %FLAG\r\na\r\n$ELSE\r\nb\r\n$ENDIF");
    Assert.That(Identifiers(t), Is.EqualTo(new[] { "b" }));
  }

  [Test]
  public void Expand_GivenNestedIfInsideSkippedBlock_WhenExpanded_ThenInnerBlockIgnored() {
    var t = Expand("%F = 0\r\n$IF %F\r\n$IF %F\r\nx\r\n$ENDIF\r\ny\r\n$ENDIF\r\nz");
    Assert.That(Identifiers(t), Is.EqualTo(new[] { "z" }));
  }

  [Test]
  public void Expand_GivenIfComparisonExpression_WhenExpanded_ThenEvaluated() {
    var t = Expand("%VER = 3\r\n$IF %VER >= 2\r\nkept\r\n$ENDIF");
    Assert.That(Identifiers(t), Is.EqualTo(new[] { "kept" }));
  }

  [Test]
  public void Expand_GivenIfNotExpression_WhenExpanded_ThenNegated() {
    var t = Expand("%F = 0\r\n$IF NOT %F\r\nkept\r\n$ENDIF");
    Assert.That(Identifiers(t), Is.EqualTo(new[] { "kept" }));
  }

  [Test]
  public void Expand_GivenIfWithUnknownEquate_WhenExpanded_ThenPreprocessorException() {
    Assert.Throws<PreprocessorException>(() => Expand("$IF %UNDEFINED\r\nx\r\n$ENDIF"));
  }

  [Test]
  public void Expand_GivenUnterminatedIf_WhenExpanded_ThenPreprocessorException() {
    Assert.Throws<PreprocessorException>(() => Expand("%F = 1\r\n$IF %F\r\nx"));
  }

  [Test]
  public void Expand_GivenEquateDefinedByExpression_WhenUsedInIf_ThenFoldedValueUsed() {
    var t = Expand("%A = 2\r\n%B = %A * 3 + 1\r\n$IF %B = 7\r\nkept\r\n$ENDIF");
    Assert.That(Identifiers(t), Is.EqualTo(new[] { "kept" }));
  }

  [Test]
  public void Expand_GivenEquateDefinition_WhenExpanded_ThenDefinitionTokensStillPassedThrough() {
    // the parser also needs to see the definition
    var t = Expand("%A = 2");
    Assert.That(t.Count(x => x.Kind == TokenKind.NamedConstant), Is.EqualTo(1));
  }

  #endregion

  #region passthrough

  [Test]
  public void Expand_GivenOtherMetaCommands_WhenExpanded_ThenPassedThroughUntouched() {
    var t = Expand("$CPU 80386\r\n$STACK 8192\r\nx");
    Assert.That(t.Count(x => x.Kind == TokenKind.MetaCommand), Is.EqualTo(2));
  }

  #endregion
}
