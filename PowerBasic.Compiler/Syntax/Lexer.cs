using System.Globalization;

namespace PowerBasic.Compiler.Syntax;

/// <summary>
/// Tokenizer for PowerBASIC 3.5 source. Keywords are not distinguished from
/// identifiers here (the parser resolves them case-insensitively); the only
/// keyword the lexer knows is <c>REM</c>, which swallows the rest of the line.
/// </summary>
public sealed class Lexer {

  private readonly string _source;
  private readonly string _file;
  private int _index;
  private int _line = 1;
  private int _column = 1;
  private bool _atStatementStart = true;
  private bool _lineHasContent;

  private Lexer(string source, string file) {
    this._source = source;
    this._file = file;
  }

  /// <summary>Tokenizes <paramref name="source"/>; the stream always ends with <see cref="TokenKind.EndOfFile"/>.</summary>
  public static IEnumerable<Token> Tokenize(string source, string file) {
    var lexer = new Lexer(source, file);
    for (;;) {
      var token = lexer.Next();
      yield return token;
      if (token.Kind == TokenKind.EndOfFile)
        yield break;
    }
  }

  private char Current => this._index < this._source.Length ? this._source[this._index] : '\0';
  private char Peek(int offset = 1) => this._index + offset < this._source.Length ? this._source[this._index + offset] : '\0';
  private bool AtEnd => this._index >= this._source.Length;

  private SourcePosition Position => new(this._file, this._line, this._column);

  private void Advance(int count = 1) {
    for (var i = 0; i < count && !this.AtEnd; ++i) {
      if (this._source[this._index] == '\n') {
        ++this._line;
        this._column = 1;
      } else
        ++this._column;
      ++this._index;
    }
  }

  private Token Next() {
    var token = this.NextCore();
    if (token.Kind is not (TokenKind.EndOfLine or TokenKind.EndOfFile))
      this._lineHasContent = true;
    return token;
  }

  private Token NextCore() {
    this.SkipNonSignificant();

    var position = this.Position;
    if (this.AtEnd) {
      // a final line with content still terminates with EndOfLine before EndOfFile
      if (this._lineHasContent) {
        this._atStatementStart = true;
        this._lineHasContent = false;
        return new(TokenKind.EndOfLine, "", position);
      }
      return new(TokenKind.EndOfFile, "", position);
    }

    var c = this.Current;

    if (c is '\r' or '\n') {
      this.ConsumeNewline();
      if (!this._lineHasContent)
        return this.Next(); // collapse blank/comment-only lines
      this._atStatementStart = true;
      this._lineHasContent = false;
      return new(TokenKind.EndOfLine, "", position);
    }

    if (c == '\'') {
      this.SkipToEndOfLine();
      return this.Next();
    }

    var atStatementStart = this._atStatementStart;
    this._atStatementStart = false;

    if (c == '!' && atStatementStart)
      return this.LexInlineAsm(position);

    if (c == '$' && atStatementStart)
      return this.LexMetaCommand(position);

    if (c == '%' && IsIdentifierStart(this.Peek()))
      return this.LexNamedConstant(position);

    if (IsIdentifierStart(c))
      return this.LexIdentifierOrRem(position);

    if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(this.Peek())))
      return this.LexNumber(position);

    if (c == '&')
      return this.LexRadixNumber(position);

    if (c == '"')
      return this.LexString(position);

    return this.LexPunctuation(position);
  }

  private void SkipNonSignificant() {
    for (;;) {
      var c = this.Current;
      if (c is ' ' or '\t') {
        this.Advance();
        continue;
      }

      // trailing `_` joins the next physical line
      if (c == '_' && !IsIdentifierPart(this.Peek())) {
        var look = this._index + 1;
        while (look < this._source.Length && this._source[look] is ' ' or '\t')
          ++look;
        if (look >= this._source.Length || this._source[look] is '\r' or '\n') {
          this.Advance(look - this._index);
          this.ConsumeNewline();
          continue;
        }
      }

      return;
    }
  }

  private void ConsumeNewline() {
    if (this.Current == '\r')
      this.Advance();
    if (this.Current == '\n')
      this.Advance();
  }

  private void SkipToEndOfLine() {
    while (!this.AtEnd && this.Current is not ('\r' or '\n'))
      this.Advance();
  }

  private static bool IsIdentifierStart(char c) => char.IsAsciiLetter(c);
  private static bool IsIdentifierPart(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

  private Token LexInlineAsm(SourcePosition position) {
    this.Advance(); // !
    var start = this._index;
    this.SkipToEndOfLine();
    var text = this._source[start..this._index].Trim();
    return new(TokenKind.InlineAsm, text, position);
  }

  private Token LexMetaCommand(SourcePosition position) {
    this.Advance(); // $
    var start = this._index;
    while (IsIdentifierPart(this.Current))
      this.Advance();
    return new(TokenKind.MetaCommand, this._source[start..this._index].ToUpperInvariant(), position);
  }

  private Token LexNamedConstant(SourcePosition position) {
    this.Advance(); // %
    var start = this._index;
    while (IsIdentifierPart(this.Current))
      this.Advance();
    return new(TokenKind.NamedConstant, this._source[start..this._index], position);
  }

  private Token LexIdentifierOrRem(SourcePosition position) {
    var start = this._index;
    while (IsIdentifierPart(this.Current))
      this.Advance();
    var text = this._source[start..this._index];

    if (text.Equals("REM", StringComparison.OrdinalIgnoreCase)) {
      this.SkipToEndOfLine();
      this._atStatementStart = true;
      return this.Next();
    }

    var suffix = this.LexSuffix();
    return new(TokenKind.Identifier, text, position, suffix);
  }

  private TypeSuffix LexSuffix() {
    switch (this.Current) {
      case '%': this.Advance(); return TypeSuffix.Integer;
      case '&': this.Advance(); return TypeSuffix.Long;
      case '!': this.Advance(); return TypeSuffix.Single;
      case '$': this.Advance(); return TypeSuffix.String;
      case '#':
        this.Advance();
        if (this.Current == '#') {
          this.Advance();
          return TypeSuffix.Ext;
        }
        return TypeSuffix.Double;
      default:
        return TypeSuffix.None;
    }
  }

  private Token LexNumber(SourcePosition position) {
    var start = this._index;
    var isFloat = false;

    while (char.IsAsciiDigit(this.Current))
      this.Advance();

    if (this.Current == '.' && (char.IsAsciiDigit(this.Peek()) || !IsIdentifierStart(this.Peek()))) {
      isFloat = true;
      this.Advance();
      while (char.IsAsciiDigit(this.Current))
        this.Advance();
    }

    var hasExponent = false;
    if (this.Current is 'E' or 'e' or 'D' or 'd') {
      var look = 1;
      if (this.Peek(look) is '+' or '-')
        ++look;
      if (char.IsAsciiDigit(this.Peek(look))) {
        hasExponent = isFloat = true;
        this.Advance(look);
        while (char.IsAsciiDigit(this.Current))
          this.Advance();
      }
    }

    var text = this._source[start..this._index];
    var suffix = this.LexSuffix();
    if (suffix is TypeSuffix.Single or TypeSuffix.Double or TypeSuffix.Ext)
      isFloat = true;

    if (!isFloat) {
      var value = long.Parse(text, CultureInfo.InvariantCulture);
      return new(TokenKind.IntegerLiteral, text, position, suffix, IntegerValue: value);
    }

    var normalized = hasExponent ? text.Replace('D', 'E').Replace('d', 'E') : text;
    if (normalized.EndsWith('.'))
      normalized += "0";
    var floatValue = double.Parse(normalized, CultureInfo.InvariantCulture);
    return new(TokenKind.FloatLiteral, text, position, suffix, FloatValue: floatValue);
  }

  private Token LexRadixNumber(SourcePosition position) {
    var start = this._index;
    this.Advance(); // &

    var (radix, digits) = char.ToUpperInvariant(this.Current) switch {
      'H' => (16, "0123456789ABCDEF"),
      'O' => (8, "01234567"),
      'B' => (2, "01"),
      _ => (8, "01234567"), // bare &nnn is octal
    };
    if (!char.IsAsciiDigit(this.Current))
      this.Advance(); // radix letter

    var value = 0L;
    while (digits.IndexOf(char.ToUpperInvariant(this.Current)) is var digit && digit >= 0 && this.Current != '\0') {
      value = value * radix + digit;
      this.Advance();
    }

    var suffix = this.Current == '&' && this.Peek() != 'H' && this.Peek() != 'h' ? TypeSuffix.Long : TypeSuffix.None;
    if (suffix == TypeSuffix.Long)
      this.Advance();

    return new(TokenKind.IntegerLiteral, this._source[start..this._index], position, suffix, IntegerValue: value);
  }

  private Token LexString(SourcePosition position) {
    this.Advance(); // opening quote
    var start = this._index;
    while (!this.AtEnd && this.Current is not ('"' or '\r' or '\n'))
      this.Advance();
    var value = this._source[start..this._index];
    if (this.Current == '"')
      this.Advance();
    return new(TokenKind.StringLiteral, value, position, StringValue: value);
  }

  private Token LexPunctuation(SourcePosition position) {
    var c = this.Current;
    this.Advance();

    var (kind, text) = c switch {
      '+' => (TokenKind.Plus, "+"),
      '-' => (TokenKind.Minus, "-"),
      '*' => (TokenKind.Star, "*"),
      '/' => (TokenKind.Slash, "/"),
      '\\' => (TokenKind.Backslash, "\\"),
      '^' => (TokenKind.Caret, "^"),
      '(' => (TokenKind.LParen, "("),
      ')' => (TokenKind.RParen, ")"),
      ',' => (TokenKind.Comma, ","),
      ';' => (TokenKind.Semicolon, ";"),
      '.' => (TokenKind.Period, "."),
      '#' => (TokenKind.Hash, "#"),
      '?' => (TokenKind.Question, "?"),
      '=' => this.Current switch {
        '<' => this.AdvanceTo(TokenKind.LessEquals, "=<"),
        '>' => this.AdvanceTo(TokenKind.GreaterEquals, "=>"),
        _ => (TokenKind.Equals, "="),
      },
      '<' => this.Current switch {
        '=' => this.AdvanceTo(TokenKind.LessEquals, "<="),
        '>' => this.AdvanceTo(TokenKind.NotEquals, "<>"),
        _ => (TokenKind.Less, "<"),
      },
      '>' => this.Current switch {
        '=' => this.AdvanceTo(TokenKind.GreaterEquals, ">="),
        '<' => this.AdvanceTo(TokenKind.NotEquals, "><"),
        _ => (TokenKind.Greater, ">"),
      },
      ':' => (TokenKind.Colon, ":"),
      _ => throw new LexerException($"unexpected character '{c}'", position),
    };

    if (kind == TokenKind.Colon)
      this._atStatementStart = true;

    return new(kind, text, position);
  }

  private (TokenKind, string) AdvanceTo(TokenKind kind, string text) {
    this.Advance();
    return (kind, text);
  }
}

/// <summary>Raised when source contains a character sequence the lexer cannot tokenize.</summary>
public sealed class LexerException(string message, SourcePosition position) : Exception($"{position}: {message}") {
  public SourcePosition Position { get; } = position;
}
