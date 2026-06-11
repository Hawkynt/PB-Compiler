using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.Syntax;

/// <summary>Lexer behavior of the PB 3.x suffix system, radix rules and the '&amp;' concat token.</summary>
[TestFixture]
public sealed class SuffixAndRadixTests {

  private static Token[] Lex(string source, Dialect dialect = Dialect.Pb35) => Lexer.Tokenize(source, "TEST.BAS", dialect).ToArray();

  private static Token[] LexLine(string source, Dialect dialect = Dialect.Pb35) {
    var tokens = Lex(source, dialect);
    Assert.That(tokens[^1].Kind, Is.EqualTo(TokenKind.EndOfFile));
    Assert.That(tokens[^2].Kind, Is.EqualTo(TokenKind.EndOfLine));
    return tokens[..^2];
  }

  #region identifier suffixes (maximal munch)

  [TestCase("b?", TypeSuffix.Byte)]
  [TestCase("w??", TypeSuffix.Word)]
  [TestCase("d???", TypeSuffix.Dword)]
  [TestCase("q&&", TypeSuffix.Quad)]
  [TestCase("l&", TypeSuffix.Long)]
  [TestCase("f@", TypeSuffix.Fix)]
  [TestCase("c@@", TypeSuffix.Bcd)]
  [TestCase("s$", TypeSuffix.String)]
  [TestCase("x$$", TypeSuffix.Flex)]
  [TestCase("e##", TypeSuffix.Ext)]
  public void Tokenize_GivenSuffixedIdentifier_WhenLexed_ThenMaximalMunchSuffix(string source, TypeSuffix expected) {
    var t = LexLine(source);
    Assert.That(t, Has.Length.EqualTo(1));
    Assert.That(t[0].Kind, Is.EqualTo(TokenKind.Identifier));
    Assert.That(t[0].Suffix, Is.EqualTo(expected));
  }

  [Test]
  public void Tokenize_GivenQuestionAtStatementStart_WhenLexed_ThenPrintShorthandNotSuffix() {
    var t = LexLine("? x");
    Assert.That(t[0].Kind, Is.EqualTo(TokenKind.Question));
    Assert.That(t[1].Kind, Is.EqualTo(TokenKind.Identifier));
  }

  [Test]
  public void Tokenize_GivenDetachedQuestion_WhenLexed_ThenNoSuffixAttached() {
    var t = LexLine("a ?");
    Assert.That(t[0].Suffix, Is.EqualTo(TypeSuffix.None));
    Assert.That(t[1].Kind, Is.EqualTo(TokenKind.Question));
  }

  [TestCase("255?", TypeSuffix.Byte, 255L)]
  [TestCase("65535??", TypeSuffix.Word, 65535L)]
  [TestCase("4000000000???", TypeSuffix.Dword, 4000000000L)]
  [TestCase("9000000000&&", TypeSuffix.Quad, 9000000000L)]
  public void Tokenize_GivenSuffixedNumericLiteral_WhenLexed_ThenSuffixAndValueKept(string source, TypeSuffix suffix, long value) {
    var t = LexLine(source);
    Assert.That(t, Has.Length.EqualTo(1));
    Assert.That(t[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
    Assert.That(t[0].Suffix, Is.EqualTo(suffix));
    Assert.That(t[0].IntegerValue, Is.EqualTo(value));
  }

  #endregion

  #region radix rules (PB 3.1+): signed per digit-size, leading zero widens, suffix overrides

  [TestCase("&HFFFF", -1L, TypeSuffix.Integer)]
  [TestCase("&H0FFFF", 65535L, TypeSuffix.Long)]
  [TestCase("&HA000", -24576L, TypeSuffix.Integer)]
  [TestCase("&H0A000", 40960L, TypeSuffix.Long)]
  [TestCase("&HFFFFFFFF", -1L, TypeSuffix.Long)]
  [TestCase("&H0FFFFFFFF", 4294967295L, TypeSuffix.Quad)]
  [TestCase("&HFFFFFFFFFFFFFFFF", -1L, TypeSuffix.Quad)]
  [TestCase("&H7FFF", 32767L, TypeSuffix.Integer)]
  [TestCase("&H10000", 65536L, TypeSuffix.Long)]
  [TestCase("&O177777", -1L, TypeSuffix.Integer)] // value bit-length decides, not digit count (verified vs PBC 3.50)
  [TestCase("&O0177777", 65535L, TypeSuffix.Long)]
  [TestCase("&B1111111111111111", -1L, TypeSuffix.Integer)]
  public void Tokenize_GivenUnsuffixedRadix_WhenLexed_ThenSignedAtDigitSize(string source, long value, TypeSuffix suffix) {
    var t = LexLine(source);
    Assert.That(t, Has.Length.EqualTo(1));
    Assert.That(t[0].IntegerValue, Is.EqualTo(value), source);
    Assert.That(t[0].Suffix, Is.EqualTo(suffix), source);
  }

  [TestCase("&HFFFF%", -1L, TypeSuffix.Integer)]
  [TestCase("&HFFFF??", 65535L, TypeSuffix.Word)]
  [TestCase("&HFFFF&", 65535L, TypeSuffix.Long)]
  [TestCase("&HFF?", 255L, TypeSuffix.Byte)]
  [TestCase("&HFFFFFFFF???", 4294967295L, TypeSuffix.Dword)]
  [TestCase("&HFFFFFFFF&&", 4294967295L, TypeSuffix.Quad)]
  public void Tokenize_GivenTypedRadix_WhenLexed_ThenSuffixReinterprets(string source, long value, TypeSuffix suffix) {
    var t = LexLine(source);
    Assert.That(t, Has.Length.EqualTo(1));
    Assert.That(t[0].IntegerValue, Is.EqualTo(value), source);
    Assert.That(t[0].Suffix, Is.EqualTo(suffix), source);
  }

  #endregion

  #region '&' concatenation vs radix vs long suffix

  [Test]
  public void Tokenize_GivenStandaloneAmpersand_WhenLexed_ThenConcatToken() {
    var t = LexLine("a$ & b$");
    Assert.That(t, Has.Length.EqualTo(3));
    Assert.That(t[1].Kind, Is.EqualTo(TokenKind.Ampersand));
  }

  [Test]
  public void Tokenize_GivenAmpersandBeforeIdentifierStartingWithB_WhenLexed_ThenConcatNotBinaryRadix() {
    var t = LexLine("a$ &Bee$");
    Assert.That(t[1].Kind, Is.EqualTo(TokenKind.Ampersand));
    Assert.That(t[2].Text, Is.EqualTo("Bee"));
  }

  [Test]
  public void Tokenize_GivenAmpersandSuffixThenConcat_WhenLexed_ThenLongSuffixAndOperator() {
    var t = LexLine("l& & s$");
    Assert.That(t[0].Suffix, Is.EqualTo(TypeSuffix.Long));
    Assert.That(t[1].Kind, Is.EqualTo(TokenKind.Ampersand));
  }

  [TestCase("&HFF")]
  [TestCase("&O17")]
  [TestCase("&B1010")]
  [TestCase("&17")]
  public void Tokenize_GivenRadixIntro_WhenLexed_ThenStillRadixLiteral(string source) {
    var t = LexLine(source);
    Assert.That(t, Has.Length.EqualTo(1));
    Assert.That(t[0].Kind, Is.EqualTo(TokenKind.IntegerLiteral));
  }

  #endregion

  #region '@' and brackets

  [Test]
  public void Tokenize_GivenStandaloneAt_WhenLexed_ThenAtToken() {
    var t = LexLine("@p");
    Assert.That(t[0].Kind, Is.EqualTo(TokenKind.At));
    Assert.That(t[1].Kind, Is.EqualTo(TokenKind.Identifier));
  }

  [Test]
  public void Tokenize_GivenBrackets_WhenLexed_ThenBracketTokens() {
    var t = LexLine("@p[3]");
    Assert.That(t.Select(x => x.Kind), Is.EqualTo(new[] {
      TokenKind.At, TokenKind.Identifier, TokenKind.LBracket, TokenKind.IntegerLiteral, TokenKind.RBracket,
    }));
  }

  #endregion

  #region parsing into the AST

  [Test]
  public void Parse_GivenConcatExpression_WhenParsed_ThenConcatBinaryOp() {
    var expr = ParserTestHelper.ParseExpression<BinaryExpr>("a$ & b$");
    Assert.That(expr.Op, Is.EqualTo(BinaryOp.Concat));
  }

  [Test]
  public void Parse_GivenPtrDeref_WhenParsed_ThenPtrDerefExpr() {
    var expr = ParserTestHelper.ParseExpression<PtrDerefExpr>("@p");
    Assert.That(expr.Index, Is.Null);
    Assert.That(expr.Pointer, Is.InstanceOf<NameExpr>());
  }

  [Test]
  public void Parse_GivenIndexedPtrDeref_WhenParsed_ThenIndexCaptured() {
    var expr = ParserTestHelper.ParseExpression<PtrDerefExpr>("@p[i% + 1]");
    Assert.That(expr.Index, Is.InstanceOf<BinaryExpr>());
  }

  [Test]
  public void Parse_GivenPtrDerefMember_WhenParsed_ThenMemberOfDeref() {
    var expr = ParserTestHelper.ParseExpression<MemberExpr>("@q.X");
    Assert.That(expr.Target, Is.InstanceOf<PtrDerefExpr>());
  }

  [Test]
  public void Parse_GivenPtrDerefAssignment_WhenParsed_ThenDerefIsTarget() {
    var stmt = ParserTestHelper.ParseSingle<AssignStmt>("@p = 42");
    Assert.That(stmt.Target, Is.InstanceOf<PtrDerefExpr>());
  }

  [Test]
  public void Parse_GivenPointerTypeName_WhenParsed_ThenPointerTypeNameChain() {
    var stmt = ParserTestHelper.ParseSingle<DimStmt>("DIM p AS INTEGER PTR");
    var type = stmt.Variables[0].Type!;
    Assert.That(type.IsPointer, Is.True);
    Assert.That(type.PointerTarget!.Builtin, Is.EqualTo(BuiltinType.Integer));
  }

  [Test]
  public void Parse_GivenAsciizTypeName_WhenParsed_ThenAsciizWithLength() {
    var stmt = ParserTestHelper.ParseSingle<DimStmt>("DIM z AS ASCIIZ * 16");
    var type = stmt.Variables[0].Type!;
    Assert.That(type.Builtin, Is.EqualTo(BuiltinType.Asciiz));
    Assert.That(((IntegerLiteralExpr)type.FixedLength!).Value, Is.EqualTo(16));
  }

  [Test]
  public void Parse_GivenAscStatement_WhenParsed_ThenAscAssignStmt() {
    var stmt = ParserTestHelper.ParseSingle<AscAssignStmt>("ASC(s$, 2) = 65");
    Assert.That(stmt.Index, Is.Not.Null);
    Assert.That(((IntegerLiteralExpr)stmt.Value).Value, Is.EqualTo(65));
  }

  [Test]
  public void Parse_GivenAscStatementWithoutPosition_WhenParsed_ThenRejectedLikeGenuinePbc()
    => Assert.Throws<ParserException>(() => ParserTestHelper.Parse("ASC(s$) = 65")); // real PBC 3.50: Error 411

  [Test]
  public void Parse_GivenStdOutWithSemicolon_WhenParsed_ThenNoNewline() {
    var stmt = ParserTestHelper.ParseSingle<StdOutStmt>("STDOUT a$;");
    Assert.That(stmt.NoNewline, Is.True);
    Assert.That(stmt.Value, Is.Not.Null);
  }

  [Test]
  public void Parse_GivenStdInLine_WhenParsed_ThenLineForm() {
    var stmt = ParserTestHelper.ParseSingle<StdInStmt>("STDIN LINE, s$");
    Assert.That(stmt.Line, Is.True);
    Assert.That(stmt.Count, Is.Null);
  }

  [Test]
  public void Parse_GivenStdInCount_WhenParsed_ThenCountForm() {
    var stmt = ParserTestHelper.ParseSingle<StdInStmt>("STDIN 5, s$");
    Assert.That(stmt.Line, Is.False);
    Assert.That(((IntegerLiteralExpr)stmt.Count!).Value, Is.EqualTo(5));
  }

  [Test]
  public void Parse_GivenSetEof_WhenParsed_ThenCommandStmt() {
    var stmt = ParserTestHelper.ParseSingle<CommandStmt>("SETEOF #3");
    Assert.That(stmt.Keyword, Is.EqualTo("SETEOF"));
  }

  [Test]
  public void Parse_GivenRedimPreserve_WhenParsed_ThenPreserveFlag() {
    var stmt = ParserTestHelper.ParseSingle<RedimStmt>("REDIM PRESERVE a%(100)");
    Assert.That(stmt.Preserve, Is.True);
  }

  [Test]
  public void Parse_GivenGotoDword_WhenParsed_ThenGotoPtrStmt() {
    var stmt = ParserTestHelper.ParseSingle<GotoPtrStmt>("GOTO DWORD g???");
    Assert.That(stmt.Pointer, Is.InstanceOf<NameExpr>());
  }

  [Test]
  public void Parse_GivenGosubDword_WhenParsed_ThenGosubPtrStmt()
    => Assert.That(ParserTestHelper.ParseSingle<GosubPtrStmt>("GOSUB DWORD g???").Pointer, Is.InstanceOf<NameExpr>());

  [Test]
  public void Parse_GivenDimVirtual_WhenParsed_ThenVirtualClass() {
    var stmt = ParserTestHelper.ParseSingle<DimStmt>("DIM VIRTUAL x%(1000)");
    Assert.That(stmt.Class, Is.EqualTo(ArrayClass.Virtual));
  }

  [Test]
  public void Parse_GivenDimHuge_WhenParsed_ThenHugeClass()
    => Assert.That(ParserTestHelper.ParseSingle<DimStmt>("DIM HUGE x%(1000)").Class, Is.EqualTo(ArrayClass.Huge));

  [Test]
  public void Parse_GivenDimAtSegment_WhenParsed_ThenAbsoluteClassWithAddress() {
    var stmt = ParserTestHelper.ParseSingle<DimStmt>("DIM x%(100) AT &H0A000");
    Assert.That(stmt.Class, Is.EqualTo(ArrayClass.Absolute));
    Assert.That(stmt.AtAddress, Is.Not.Null);
  }

  [Test]
  public void Parse_GivenByValArgument_WhenParsed_ThenByValArgExpr() {
    var stmt = ParserTestHelper.ParseSingle<CallStmt>("CALL Foo(BYVAL p)");
    Assert.That(stmt.Arguments[0], Is.InstanceOf<ByValArgExpr>());
  }

  [Test]
  public void Parse_GivenDefQud_WhenParsed_ThenQuadDefType() {
    var stmt = ParserTestHelper.ParseSingle<DefTypeStmt>("DEFQUD Q");
    Assert.That(stmt.Type, Is.EqualTo(BuiltinType.Quad));
  }

  #endregion
}
