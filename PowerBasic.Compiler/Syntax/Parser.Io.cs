using PowerBasic.Compiler.Syntax.Ast;
using FileMode = PowerBasic.Compiler.Syntax.Ast.FileMode;

namespace PowerBasic.Compiler.Syntax;

/// <summary>I/O statements: PRINT, INPUT, OPEN/CLOSE, file GET/PUT, SEEK, FIELD, DATA/READ/RESTORE.</summary>
public sealed partial class Parser {

  private Statement ParsePrint(bool isLPrint) {
    var pos = this.Advance().Position; // PRINT / LPRINT / ?

    Expression? fileNumber = null;
    if (this.Current.Kind == TokenKind.Hash) {
      fileNumber = this.ParseFileNumber();
      this.Expect(TokenKind.Comma, "','");
    }

    Expression? usingFormat = null;
    if (this.TryMatchKeyword("USING")) {
      usingFormat = this.ParseExpression();
      this.Expect(TokenKind.Semicolon, "';'");
    }

    var items = new List<PrintItem>();
    while (!this.IsStatementEnd()) {
      Expression? value = null;
      if (this.Current.Kind is not (TokenKind.Semicolon or TokenKind.Comma))
        value = this.ParseExpression();

      if (this.Match(TokenKind.Semicolon))
        items.Add(new(value, PrintSeparator.Semicolon));
      else if (this.Match(TokenKind.Comma))
        items.Add(new(value, PrintSeparator.Comma));
      else {
        items.Add(new(value, PrintSeparator.Newline));
        break;
      }
    }
    return new PrintStmt(pos, fileNumber, isLPrint, usingFormat, items);
  }

  private Statement ParseInput(bool isLineInput) {
    var pos = this.Advance().Position; // INPUT

    Expression? fileNumber = null;
    string? prompt = null;
    var promptSemicolon = false;
    if (this.Current.Kind == TokenKind.Hash) {
      fileNumber = this.ParseFileNumber();
      this.Expect(TokenKind.Comma, "','");
    } else {
      this.Match(TokenKind.Semicolon); // INPUT ; - "stay on line" flag
      if (this.Current.Kind == TokenKind.StringLiteral && this.Peek().Kind is TokenKind.Semicolon or TokenKind.Comma) {
        prompt = this.Advance().StringValue;
        promptSemicolon = this.Advance().Kind == TokenKind.Semicolon;
      }
    }

    var targets = new List<Expression>();
    do
      targets.Add(this.ParseLValue());
    while (this.Match(TokenKind.Comma));
    return new InputStmt(pos, isLineInput, fileNumber, prompt, promptSemicolon, targets);
  }

  private Statement ParseOpen() {
    var pos = this.Advance().Position;
    var first = this.ParseExpression();

    if (this.TryMatchKeyword("FOR")) {
      var modeToken = this.Expect(TokenKind.Identifier, "file mode");
      var mode = modeToken.Text.ToUpperInvariant() switch {
        "INPUT" => FileMode.Input,
        "OUTPUT" => FileMode.Output,
        "APPEND" => FileMode.Append,
        "RANDOM" => FileMode.Random,
        "BINARY" => FileMode.Binary,
        _ => throw new ParserException($"unknown file mode '{modeToken.Text}'", modeToken.Position),
      };

      string? access = null;
      string? lockSpec = null;
      for (;;) {
        if (this.TryMatchKeyword("ACCESS")) {
          access = this.ParseAccessSpec();
          continue;
        }
        if (this.TryMatchKeyword("LOCK")) {
          lockSpec = this.ParseLockSpec();
          continue;
        }
        if (this.TryMatchKeyword("SHARED")) {
          lockSpec = "SHARED";
          continue;
        }
        break;
      }

      this.ExpectKeyword("AS");
      var fileNumber = this.ParseFileNumber();
      Expression? recordLength = null;
      if (this.TryMatchKeyword("LEN")) {
        this.Expect(TokenKind.Equals, "'='");
        recordLength = this.ParseExpression();
      }
      return new OpenStmt(pos, first, mode, access, lockSpec, fileNumber, recordLength);
    }

    // legacy OPEN mode$, [#]n, file$ [, reclen]
    this.Expect(TokenKind.Comma, "','");
    var number = this.ParseFileNumber();
    this.Expect(TokenKind.Comma, "','");
    var fileName = this.ParseExpression();
    Expression? recLen = this.Match(TokenKind.Comma) ? this.ParseExpression() : null;

    if (first is not StringLiteralExpr modeLiteral || modeLiteral.Value.Trim().Length == 0)
      throw new ParserException("legacy OPEN requires a literal mode string", pos);
    var legacyMode = char.ToUpperInvariant(modeLiteral.Value.Trim()[0]) switch {
      'I' => FileMode.Input,
      'O' => FileMode.Output,
      'A' => FileMode.Append,
      'R' => FileMode.Random,
      'B' => FileMode.Binary,
      _ => throw new ParserException($"unknown legacy OPEN mode '{modeLiteral.Value}'", pos),
    };
    return new OpenStmt(pos, fileName, legacyMode, null, null, number, recLen);
  }

  private string ParseAccessSpec() {
    if (this.TryMatchKeyword("READ"))
      return this.TryMatchKeyword("WRITE") ? "READ WRITE" : "READ";
    this.ExpectKeyword("WRITE");
    return "WRITE";
  }

  private string ParseLockSpec() => this.TryMatchKeyword("SHARED") ? "SHARED" : this.ParseAccessSpec();

  private Statement ParseClose() {
    var pos = this.Advance().Position;
    var fileNumbers = new List<Expression>();
    if (!this.IsStatementEnd())
      do
        fileNumbers.Add(this.ParseFileNumber());
      while (this.Match(TokenKind.Comma));
    return new CloseStmt(pos, fileNumbers);
  }

  private Statement ParseGetPut(bool isGet) =>
    this.Peek().Kind == TokenKind.LParen ? this.ParseGetPutGraphics(isGet) : this.ParseGetPutFile(isGet);

  private Statement ParseGetPutFile(bool isGet) {
    var pos = this.Advance().Position;
    var fileNumber = this.ParseFileNumber();
    Expression? record = null;
    Expression? variable = null;
    if (this.Match(TokenKind.Comma)) {
      if (this.Current.Kind != TokenKind.Comma && !this.IsStatementEnd())
        record = this.ParseExpression();
      if (this.Match(TokenKind.Comma))
        variable = this.ParseLValue();
    }
    return new GetPutFileStmt(pos, isGet, fileNumber, record, variable);
  }

  private Statement ParseSeek() {
    var pos = this.Advance().Position;
    var fileNumber = this.ParseFileNumber();
    this.Expect(TokenKind.Comma, "','");
    return new SeekStmt(pos, fileNumber, this.ParseExpression());
  }

  private Statement ParseField() {
    var pos = this.Advance().Position;
    var fileNumber = this.ParseFileNumber();
    this.Expect(TokenKind.Comma, "','");
    var fields = new List<(Expression Width, Expression Target)>();
    do {
      var width = this.ParseExpression();
      this.ExpectKeyword("AS");
      fields.Add((width, this.ParseLValue()));
    } while (this.Match(TokenKind.Comma));
    return new FieldStmt(pos, fileNumber, fields);
  }

  /// <summary>Parses an I/O file number with optional <c>#</c> prefix (kept as <see cref="FileNumberExpr"/>).</summary>
  private Expression ParseFileNumber() {
    if (this.Current.Kind != TokenKind.Hash)
      return this.ParseExpression();

    var pos = this.Advance().Position;
    return new FileNumberExpr(pos, this.ParseExpression());
  }

  private Statement ParseData() {
    var pos = this.Advance().Position;
    var items = new List<string>();
    var current = "";
    var hadTokens = false;
    Token? previous = null;
    while (this.Current.Kind is not (TokenKind.EndOfLine or TokenKind.EndOfFile)) {
      var token = this.Advance();
      hadTokens = true;
      if (token.Kind == TokenKind.Comma) {
        items.Add(current.Trim());
        current = "";
        previous = null;
        continue;
      }
      if (previous != null && IsWordLike(previous.Value.Kind) && IsWordLike(token.Kind))
        current += " ";
      current += RenderDataToken(token);
      previous = token;
    }
    if (hadTokens)
      items.Add(current.Trim());
    return new DataStmt(pos, items);
  }

  private static bool IsWordLike(TokenKind kind) => kind
    is TokenKind.Identifier or TokenKind.IntegerLiteral or TokenKind.FloatLiteral
    or TokenKind.StringLiteral or TokenKind.NamedConstant;

  private static string RenderDataToken(Token token) => token.Kind switch {
    TokenKind.StringLiteral => token.StringValue!,
    TokenKind.NamedConstant => "%" + token.Text,
    // radix literals already carry any suffix in their source text
    TokenKind.IntegerLiteral when token.Text.StartsWith('&') => token.Text,
    _ => token.Text + SuffixText(token.Suffix),
  };

  private static string SuffixText(TypeSuffix suffix) => suffix switch {
    TypeSuffix.Byte => "?",
    TypeSuffix.Word => "??",
    TypeSuffix.Dword => "???",
    TypeSuffix.Integer => "%",
    TypeSuffix.Long => "&",
    TypeSuffix.Quad => "&&",
    TypeSuffix.Single => "!",
    TypeSuffix.Double => "#",
    TypeSuffix.Ext => "##",
    TypeSuffix.Fix => "@",
    TypeSuffix.Bcd => "@@",
    TypeSuffix.String => "$",
    TypeSuffix.Flex => "$$",
    _ => "",
  };

  private Statement ParseRead() {
    var pos = this.Advance().Position;
    var targets = new List<Expression>();
    do
      targets.Add(this.ParseLValue());
    while (this.Match(TokenKind.Comma));
    return new ReadStmt(pos, targets);
  }

  private Statement ParseRestore() {
    var pos = this.Advance().Position;
    return new RestoreStmt(pos, this.IsStatementEnd() ? null : this.ParseLabelReference());
  }
}
