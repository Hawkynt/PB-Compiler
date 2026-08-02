using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Syntax;

[TestFixture]
public sealed class LexerTests {

  private static Token[] Lex(string source) => Lexer.Tokenize(source, "TEST.BAS").ToArray();

  private static Token[] LexLine(string source) {
    var tokens = Lex(source);
    Assert.That(tokens[^1].Kind, Is.EqualTo(TokenKind.EndOfFile));
    Assert.That(tokens[^2].Kind, Is.EqualTo(TokenKind.EndOfLine));
    return tokens[..^2];
  }

  #region pb36 shift/rotate operators

  [TestCase("<<", TokenKind.ShiftLeft)]
  [TestCase("<<<", TokenKind.ShiftLeftLogical)]
  [TestCase(">>", TokenKind.ShiftRight)]
  [TestCase(">>>", TokenKind.ShiftRightLogical)]
  [TestCase("<<>", TokenKind.RotateLeft)]
  [TestCase("<>>", TokenKind.RotateRight)]
  [TestCase("|", TokenKind.Pipe)]
  public void Tokenize_GivenShiftRotateOperator_WhenLexed_ThenSingleToken(string src, TokenKind expected) {
    var t = LexLine("a " + src + " b");
    Assert.That(t, Has.Length.EqualTo(3), src);
    Assert.That(t[1].Kind, Is.EqualTo(expected), src);
  }

  [TestCase("<", TokenKind.Less)]
  [TestCase("<=", TokenKind.LessEquals)]
  [TestCase("<>", TokenKind.NotEquals)]
  [TestCase(">", TokenKind.Greater)]
  [TestCase(">=", TokenKind.GreaterEquals)]
  public void Tokenize_GivenComparisonOperator_WhenLexed_ThenStillRecognized(string src, TokenKind expected) {
    var t = LexLine("a " + src + " b");
    Assert.That(t[1].Kind, Is.EqualTo(expected), src);
  }

  #endregion

  #region identifiers and suffixes

  [Test]
  public void Tokenize_GivenPlainIdentifier_WhenLexed_ThenIdentifierWithoutSuffix() {
    var t = LexLine("counter");
    Assert.That(t, Has.Length.EqualTo(1));
    Assert.That(t[0].Kind, Is.EqualTo(TokenKind.Identifier));
    Assert.That(t[0].Text, Is.EqualTo("counter"));
    Assert.That(t[0].Suffix, Is.EqualTo(TypeSuffix.None));
  }

  [TestCase("i%", TypeSuffix.Integer)]
  [TestCase("n&", TypeSuffix.Long)]
  [TestCase("f!", TypeSuffix.Single)]
  [TestCase("d#", TypeSuffix.Double)]
  [TestCase("e##", TypeSuffix.Ext)]
  [TestCase("s$", TypeSuffix.String)]
  public void Tokenize_GivenSuffixedIdentifier_WhenLexed_ThenSuffixCaptured(string src, TypeSuffix expected) {
    var t = LexLine(src);
    Assert.That(t, Has.Length.EqualTo(1));
    Assert.That(t[0].Kind, Is.EqualTo(TokenKind.Identifier));
    Assert.That(t[0].Suffix, Is.EqualTo(expected));
  }

  [Test]
  public void Tokenize_GivenIdentifierWithDigitsAndUnderscoresAndPeriods_WhenLexed_ThenSingleIdentifier() {
    var t = LexLine("My_Var2");
    Assert.That(t, Has.Length.EqualTo(1));
    Assert.That(t[0].Text, Is.EqualTo("My_Var2"));
  }

  [Test]
  public void Tokenize_GivenNamedConstantReference_WhenLexed_ThenNamedConstantToken() {
    var t = LexLine("%SVGA_MODEX");
    Assert.That(t, Has.Length.EqualTo(1));
    Assert.That(t[0].Kind, Is.EqualTo(TokenKind.NamedConstant));
    Assert.That(t[0].Text, Is.EqualTo("SVGA_MODEX"));
  }

  [Test]
  public void Tokenize_GivenSuffixThenPercentIdentifier_WhenLexed_ThenSuffixBindsToLeftAndConstantToRight() {
    // i% = %MAX  ->  Identifier(i,%) Equals NamedConstant(MAX)
    var t = LexLine("i% = %MAX");
    Assert.That(t.Select(x => x.Kind), Is.EqualTo(new[] { TokenKind.Identifier, TokenKind.Equals, TokenKind.NamedConstant }));
    Assert.That(t[0].Suffix, Is.EqualTo(TypeSuffix.Integer));
  }

  #endregion

  #region numeric literals

  [TestCase("0", 0L)]
  [TestCase("42", 42L)]
  [TestCase("32767", 32767L)]
  [TestCase("32768", 32768L)]   // boundary: too big for INTEGER, still one token
  [TestCase("2147483647", 2147483647L)]
  public void Tokenize_GivenDecimalInteger_WhenLexed_ThenIntegerLiteralWithValue(string src, long expected) {
    var t = LexLine(src);
    Assert.That(t, Has.Length.EqualTo(1));
    Assert.That(t[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
    Assert.That(t[0].IntegerValue, Is.EqualTo(expected));
  }

  [TestCase("&H0", 0L)]
  [TestCase("&HFF", 255L)]
  [TestCase("&hff", 255L)]
  [TestCase("&H4F05", 0x4F05L)]
  [TestCase("&HFFFF", -1L)]         // 4 hex digits: signed INTEGER (PB 3.1+ rule)
  [TestCase("&H0FFFF", 65535L)]      // leading zero widens to LONG
  [TestCase("&O17", 15L)]
  [TestCase("&17", 15L)]        // bare & ocal form
  [TestCase("&B1010", 10L)]
  public void Tokenize_GivenRadixPrefixedInteger_WhenLexed_ThenValueDecoded(string src, long expected) {
    var t = LexLine(src);
    Assert.That(t, Has.Length.EqualTo(1));
    Assert.That(t[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
    Assert.That(t[0].IntegerValue, Is.EqualTo(expected));
  }

  [TestCase("1%", TypeSuffix.Integer)]
  [TestCase("1&", TypeSuffix.Long)]
  public void Tokenize_GivenSuffixedInteger_WhenLexed_ThenSuffixCaptured(string src, TypeSuffix expected) {
    var t = LexLine(src);
    Assert.That(t[0].Suffix, Is.EqualTo(expected));
  }

  [TestCase("1.5", 1.5)]
  [TestCase(".5", 0.5)]
  [TestCase("1.", 1.0)]
  [TestCase("1E3", 1000.0)]
  [TestCase("1.5E-2", 0.015)]
  [TestCase("2D3", 2000.0)]     // D exponent = double
  public void Tokenize_GivenFloatLiteral_WhenLexed_ThenFloatValueDecoded(string src, double expected) {
    var t = LexLine(src);
    Assert.That(t, Has.Length.EqualTo(1));
    Assert.That(t[0].Kind, Is.EqualTo(TokenKind.FloatLiteral));
    Assert.That(t[0].FloatValue, Is.EqualTo(expected).Within(1e-12));
  }

  [TestCase("1!", TypeSuffix.Single)]
  [TestCase("1#", TypeSuffix.Double)]
  [TestCase("1##", TypeSuffix.Ext)]
  public void Tokenize_GivenSuffixedNumber_WhenLexed_ThenFloatWithSuffix(string src, TypeSuffix expected) {
    var t = LexLine(src);
    Assert.That(t[0].Kind, Is.EqualTo(TokenKind.FloatLiteral));
    Assert.That(t[0].Suffix, Is.EqualTo(expected));
  }

  [Test]
  public void Tokenize_GivenHugeDecimal_WhenLexed_ThenStillSingleIntegerToken() {
    var t = LexLine("4294967295");
    Assert.That(t[0].IntegerValue, Is.EqualTo(4294967295L));
  }

  #endregion

  #region strings

  [Test]
  public void Tokenize_GivenStringLiteral_WhenLexed_ThenValueWithoutQuotes() {
    var t = LexLine("\"hello world\"");
    Assert.That(t, Has.Length.EqualTo(1));
    Assert.That(t[0].Kind, Is.EqualTo(TokenKind.StringLiteral));
    Assert.That(t[0].StringValue, Is.EqualTo("hello world"));
  }

  [Test]
  public void Tokenize_GivenEmptyString_WhenLexed_ThenEmptyValue() {
    var t = LexLine("\"\"");
    Assert.That(t[0].StringValue, Is.EqualTo(""));
  }

  [Test]
  public void Tokenize_GivenUnterminatedString_WhenLexed_ThenValueRunsToEndOfLine() {
    // PB tolerates a missing closing quote at end of line
    var t = LexLine("\"abc");
    Assert.That(t[0].Kind, Is.EqualTo(TokenKind.StringLiteral));
    Assert.That(t[0].StringValue, Is.EqualTo("abc"));
  }

  #endregion

  #region comments

  [Test]
  public void Tokenize_GivenApostropheComment_WhenLexed_ThenCommentSkipped() {
    var t = LexLine("a = 1 ' set it");
    Assert.That(t.Select(x => x.Kind), Is.EqualTo(new[] { TokenKind.Identifier, TokenKind.Equals, TokenKind.IntegerLiteral }));
  }

  [Test]
  public void Tokenize_GivenRemComment_WhenLexed_ThenWholeLineSkipped() {
    var t = Lex("REM a = 1 : b = 2");
    Assert.That(t.Select(x => x.Kind), Is.EqualTo(new[] { TokenKind.EndOfFile }));
  }

  [Test]
  public void Tokenize_GivenRemAfterColon_WhenLexed_ThenRestSkipped() {
    // the separating colon itself is still emitted; the parser skips empty statements
    var t = LexLine("a = 1 : REM comment");
    Assert.That(t.Select(x => x.Kind), Is.EqualTo(new[] {
      TokenKind.Identifier, TokenKind.Equals, TokenKind.IntegerLiteral, TokenKind.Colon,
    }));
  }

  [Test]
  public void Tokenize_GivenApostropheInsideString_WhenLexed_ThenNotAComment() {
    var t = LexLine("\"don't\"");
    Assert.That(t[0].StringValue, Is.EqualTo("don't"));
  }

  #endregion

  #region operators and punctuation

  [Test]
  public void Tokenize_GivenAllOperators_WhenLexed_ThenKindsMatch() {
    var t = LexLine("+ - * / \\ ^ ( ) , ; = < > <= >= <> .");
    Assert.That(t.Select(x => x.Kind), Is.EqualTo(new[] {
      TokenKind.Plus, TokenKind.Minus, TokenKind.Star, TokenKind.Slash, TokenKind.Backslash,
      TokenKind.Caret, TokenKind.LParen, TokenKind.RParen, TokenKind.Comma, TokenKind.Semicolon,
      TokenKind.Equals, TokenKind.Less, TokenKind.Greater, TokenKind.LessEquals,
      TokenKind.GreaterEquals, TokenKind.NotEquals, TokenKind.Period,
    }));
  }

  [Test]
  public void Tokenize_GivenReversedComparisons_WhenLexed_ThenNormalized() {
    var t = LexLine("=< => ><");
    Assert.That(t.Select(x => x.Kind), Is.EqualTo(new[] {
      TokenKind.LessEquals, TokenKind.GreaterEquals, TokenKind.NotEquals,
    }));
  }

  [Test]
  public void Tokenize_GivenFileNumberHash_WhenLexed_ThenHashToken() {
    var t = LexLine("PRINT #1, x");
    Assert.That(t.Select(x => x.Kind), Is.EqualTo(new[] {
      TokenKind.Identifier, TokenKind.Hash, TokenKind.IntegerLiteral, TokenKind.Comma, TokenKind.Identifier,
    }));
  }

  [Test]
  public void Tokenize_GivenQuestionMark_WhenLexed_ThenQuestionToken() {
    var t = LexLine("? \"hi\"");
    Assert.That(t[0].Kind, Is.EqualTo(TokenKind.Question));
  }

  [Test]
  public void Tokenize_GivenColon_WhenLexed_ThenStatementSeparator() {
    var t = LexLine("a = 1 : b = 2");
    Assert.That(t.Count(x => x.Kind == TokenKind.Colon), Is.EqualTo(1));
  }

  #endregion

  #region lines, continuation, EOF

  [Test]
  public void Tokenize_GivenEmptySource_WhenLexed_ThenOnlyEndOfFile() {
    var t = Lex("");
    Assert.That(t.Select(x => x.Kind), Is.EqualTo(new[] { TokenKind.EndOfFile }));
  }

  [Test]
  public void Tokenize_GivenTwoLines_WhenLexed_ThenEndOfLineBetween() {
    var t = Lex("a\r\nb");
    Assert.That(t.Select(x => x.Kind), Is.EqualTo(new[] {
      TokenKind.Identifier, TokenKind.EndOfLine, TokenKind.Identifier, TokenKind.EndOfLine, TokenKind.EndOfFile,
    }));
  }

  [Test]
  public void Tokenize_GivenTrailingUnderscore_WhenLexed_ThenLinesJoined() {
    var t = Lex("a = 1 + _\r\n    2");
    Assert.That(t.Count(x => x.Kind == TokenKind.EndOfLine), Is.EqualTo(1));
  }

  [Test]
  public void Tokenize_GivenTrailingUnderscoreThenComment_WhenLexed_ThenLinesJoined() {
    // PBC 3.50 accepts a comment after the continuation - it compiles the SVGA corpus's
    // DRAW_ANI.BAS, which writes "... + _           ' hotX=0, hotY=0" and carries on next line
    var t = Lex("a = 1 + _   ' why\r\n    2");
    Assert.Multiple(() => {
      Assert.That(t.Count(x => x.Kind == TokenKind.EndOfLine), Is.EqualTo(1));
      Assert.That(t.Count(x => x.Kind == TokenKind.IntegerLiteral), Is.EqualTo(2), "both operands are on one logical line");
    });
  }

  [Test]
  public void Tokenize_GivenUnderscoreInsideIdentifier_WhenLexed_ThenNoContinuation() {
    var t = Lex("a_b\r\nc");
    Assert.That(t.Count(x => x.Kind == TokenKind.EndOfLine), Is.EqualTo(2));
  }

  [Test]
  public void Tokenize_GivenSource_WhenLexed_ThenPositionsTracked() {
    var t = Lex("a = 1\r\nbb = 2");
    Assert.That(t[0].Position.Line, Is.EqualTo(1));
    Assert.That(t[0].Position.Column, Is.EqualTo(1));
    Assert.That(t[4].Position.Line, Is.EqualTo(2));   // bb
    Assert.That(t[6].Position.Column, Is.EqualTo(6)); // 2
    Assert.That(t[0].Position.File, Is.EqualTo("TEST.BAS"));
  }

  #endregion

  #region metastatements

  [Test]
  public void Tokenize_GivenIncludeMeta_WhenLexed_ThenMetaCommandAndString() {
    var t = LexLine("$INCLUDE \"GRAPHICS.SUB\"");
    Assert.That(t[0].Kind, Is.EqualTo(TokenKind.MetaCommand));
    Assert.That(t[0].Text, Is.EqualTo("INCLUDE"));
    Assert.That(t[1].Kind, Is.EqualTo(TokenKind.StringLiteral));
    Assert.That(t[1].StringValue, Is.EqualTo("GRAPHICS.SUB"));
  }

  [Test]
  public void Tokenize_GivenCpuMeta_WhenLexed_ThenArgumentsAreNormalTokens() {
    var t = LexLine("$CPU 80386");
    Assert.That(t[0].Text, Is.EqualTo("CPU"));
    Assert.That(t[1].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
  }

  [Test]
  public void Tokenize_GivenMetaNotAtStatementStart_WhenLexed_ThenNotAMetaCommand() {
    // a metastatement may follow a colon: only statement-leading $ introduces one
    var t = LexLine("a = 1 : $ERROR ALL ON");
    Assert.That(t[3].Kind, Is.EqualTo(TokenKind.Colon));
    Assert.That(t[4].Kind, Is.EqualTo(TokenKind.MetaCommand));
    Assert.That(t[4].Text, Is.EqualTo("ERROR"));
  }

  #endregion

  #region inline assembly

  [Test]
  public void Tokenize_GivenInlineAsmLine_WhenLexed_ThenRawTextCaptured() {
    var t = LexLine("!MOV AX,&H4F05");
    Assert.That(t, Has.Length.EqualTo(1));
    Assert.That(t[0].Kind, Is.EqualTo(TokenKind.InlineAsm));
    Assert.That(t[0].Text, Is.EqualTo("MOV AX,&H4F05"));
  }

  [Test]
  public void Tokenize_GivenIndentedInlineAsmWithComment_WhenLexed_ThenRawIncludesAsmComment() {
    var t = LexLine("    !XOR BX,BX              ; BH=0 set, BL=0 window A");
    Assert.That(t[0].Kind, Is.EqualTo(TokenKind.InlineAsm));
    Assert.That(t[0].Text, Does.StartWith("XOR BX,BX"));
    Assert.That(t[0].Text, Does.Contain("; BH=0"));
  }

  [Test]
  public void Tokenize_GivenSegmentOverrideAsm_WhenLexed_ThenColonStaysInRawText() {
    var t = LexLine("!MOV ES:[BX], AL");
    Assert.That(t, Has.Length.EqualTo(1));
    Assert.That(t[0].Text, Is.EqualTo("MOV ES:[BX], AL"));
  }

  [Test]
  public void Tokenize_GivenExclamationAfterIdentifier_WhenLexed_ThenSingleSuffixNotAsm() {
    var t = LexLine("f! = 1.5");
    Assert.That(t[0].Kind, Is.EqualTo(TokenKind.Identifier));
    Assert.That(t[0].Suffix, Is.EqualTo(TypeSuffix.Single));
  }

  #endregion

  #region labels and line numbers

  [Test]
  public void Tokenize_GivenLabelLine_WhenLexed_ThenIdentifierAndColon() {
    var t = LexLine("ErrorHandler:");
    Assert.That(t.Select(x => x.Kind), Is.EqualTo(new[] { TokenKind.Identifier, TokenKind.Colon }));
  }

  [Test]
  public void Tokenize_GivenNumberedLine_WhenLexed_ThenIntegerThenStatement() {
    var t = LexLine("100 PRINT \"hi\"");
    Assert.That(t[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
    Assert.That(t[1].Kind, Is.EqualTo(TokenKind.Identifier));
  }

  #endregion

  #region real-world smoke

  [Test]
  public void Tokenize_GivenRealWorldSvgaLine_WhenLexed_ThenNoErrors() {
    var t = LexLine("IF VESASystemContext.CurrentMode = %SVGA_MODEX THEN");
    Assert.That(t.Select(x => x.Kind), Is.EqualTo(new[] {
      TokenKind.Identifier, TokenKind.Identifier, TokenKind.Period, TokenKind.Identifier,
      TokenKind.Equals, TokenKind.NamedConstant, TokenKind.Identifier,
    }));
  }

  [Test]
  public void Tokenize_GivenConstantDefinitionLine_WhenLexed_ThenNamedConstantEqualsValue() {
    var t = LexLine("%GIF_BLOCK_IMAGE = &H2C");
    Assert.That(t.Select(x => x.Kind), Is.EqualTo(new[] {
      TokenKind.NamedConstant, TokenKind.Equals, TokenKind.IntegerLiteral,
    }));
    Assert.That(t[2].IntegerValue, Is.EqualTo(0x2C));
  }

  #endregion
}
