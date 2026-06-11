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
  private readonly Dialect _dialect;
  private int _index;
  private int _line = 1;
  private int _column = 1;
  private bool _atStatementStart = true;
  private bool _lineHasContent;

  private Lexer(string source, string file, Dialect dialect) {
    this._source = source;
    this._file = file;
    this._dialect = dialect;
  }

  /// <summary>Tokenizes <paramref name="source"/>; the stream always ends with <see cref="TokenKind.EndOfFile"/>.</summary>
  public static IEnumerable<Token> Tokenize(string source, string file, Dialect dialect = Dialect.Pb35) {
    var lexer = new Lexer(source, file, dialect);
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

    if (c == '!' && atStatementStart) {
      this.Require(LanguageFeature.InlineAsm, position);
      return this.LexInlineAsm(position);
    }

    if (c == '$' && atStatementStart)
      return this.LexMetaCommand(position);

    if (c == '%' && IsIdentifierStart(this.Peek()))
      return this.LexNamedConstant(position);

    if (IsIdentifierStart(c))
      return this.LexIdentifierOrRem(position);

    if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(this.Peek())))
      return this.LexNumber(position);

    // & introduces a radix literal only with a radix letter + digits (or bare
    // octal digits) attached; otherwise it is the PB 3.5 concatenation operator
    if (c == '&' && this.IsRadixIntro())
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

    if (text.Contains('_'))
      this.Require(LanguageFeature.IdentifierUnderscores, position);

    if (text.Equals("REM", StringComparison.OrdinalIgnoreCase)) {
      this.SkipToEndOfLine();
      this._atStatementStart = true;
      return this.Next();
    }

    var suffix = this.LexSuffix(position);
    return new(TokenKind.Identifier, text, position, suffix);
  }

  /// <summary>
  /// Lexes a type suffix directly attached to an identifier/literal tail
  /// (maximal munch: <c>??</c> before <c>?</c>, <c>&amp;&amp;</c> before <c>&amp;</c>, ...).
  /// </summary>
  private TypeSuffix LexSuffix(SourcePosition position) {
    switch (this.Current) {
      case '%':
        this.Advance();
        return TypeSuffix.Integer;
      case '?': {
        this.Require(LanguageFeature.UnsignedTypes, position);
        this.Advance();
        if (this.Current != '?')
          return TypeSuffix.Byte;
        this.Advance();
        if (this.Current != '?')
          return TypeSuffix.Word;
        this.Advance();
        return TypeSuffix.Dword;
      }
      case '&':
        this.Advance();
        if (this.Current != '&')
          return TypeSuffix.Long;
        this.Require(LanguageFeature.QuadType, position);
        this.Advance();
        return TypeSuffix.Quad;
      case '!':
        this.Advance();
        return TypeSuffix.Single;
      case '#':
        this.Advance();
        if (this.Current != '#')
          return TypeSuffix.Double;
        this.Advance();
        return TypeSuffix.Ext;
      case '@':
        this.Advance();
        if (this.Current != '@')
          return TypeSuffix.Fix;
        this.Advance();
        return TypeSuffix.Bcd;
      case '$':
        this.Advance();
        if (this.Current != '$')
          return TypeSuffix.String;
        this.Advance();
        return TypeSuffix.Flex;
      default:
        return TypeSuffix.None;
    }
  }

  private void Require(LanguageFeature feature, SourcePosition position) {
    if (!DialectFacts.IsAvailable(feature, this._dialect))
      throw new LexerException(DialectFacts.RequirementMessage(feature, this._dialect), position);
  }

  /// <summary>True when the <c>&amp;</c> at the cursor starts a radix literal (vs. the concat operator).</summary>
  private bool IsRadixIntro() {
    if (char.IsAsciiDigit(this.Peek()))
      return true; // bare &nnn octal
    var second = char.ToUpperInvariant(this.Peek(2));
    return char.ToUpperInvariant(this.Peek()) switch {
      'H' => char.IsAsciiHexDigit(second),
      'O' => second is >= '0' and <= '7',
      'B' => second is '0' or '1',
      _ => false,
    };
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
    var suffix = this.LexSuffix(position);
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

  /// <summary>
  /// Radix literal (PB 3.1+ rules, verified against genuine PBC 3.50): up to
  /// 64 bits; without a suffix the value's BIT LENGTH selects the size and the
  /// value is interpreted SIGNED at that size (&amp;HFFFF = -1 INTEGER,
  /// &amp;O177777 = -1 INTEGER, &amp;HFFFFFFFF = -1 LONG); a leading zero digit
  /// makes it unsigned and widens as needed (&amp;H0FFFF = 65535 LONG); a typed
  /// suffix reinterprets explicitly (&amp;HFFFF?? = 65535 WORD, &amp;HFFFF% = -1).
  /// </summary>
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

    // QUIRK 2.1/2.2 (FAQ): the leading-zero-reads-unsigned escape arrived with
    // 3.1 - PB 3.0 and older read every radix literal signed (so the FAQ's
    // "w?? = &H0A000 overflows" bug is replicated under --dialect pb30/pb2x)
    var raw = 0UL;
    var leadingZero = this.Current == '0' && this._dialect.IsPbAtLeast(Dialect.Pb31);
    while (digits.IndexOf(char.ToUpperInvariant(this.Current)) is var digit && digit >= 0 && this.Current != '\0') {
      raw = raw * (ulong)radix + (ulong)digit;
      this.Advance();
    }

    var suffix = this.LexSuffix(position);
    if (suffix != TypeSuffix.None)
      this.Require(LanguageFeature.TypedRadixLiterals, position);

    long value;
    switch (suffix) {
      case TypeSuffix.None when leadingZero: // unsigned, widened to fit
        value = (long)raw;
        suffix = raw switch {
          <= 0x7FFF => TypeSuffix.Integer,
          <= 0x7FFFFFFF => TypeSuffix.Long,
          _ => TypeSuffix.Quad,
        };
        break;
      case TypeSuffix.None: { // signed at the smallest size covering the bit length
        var bits = 64 - System.Numerics.BitOperations.LeadingZeroCount(raw);
        (value, suffix) = bits switch {
          <= 16 => ((long)(short)raw, TypeSuffix.Integer),
          <= 32 => ((long)(int)raw, TypeSuffix.Long),
          _ => ((long)raw, TypeSuffix.Quad),
        };
        break;
      }
      case TypeSuffix.Byte: value = (byte)raw; break;
      case TypeSuffix.Word: value = (ushort)raw; break;
      case TypeSuffix.Dword: value = (uint)raw; break;
      case TypeSuffix.Integer: value = (short)raw; break;
      case TypeSuffix.Long: value = (int)raw; break;
      case TypeSuffix.Quad: value = (long)raw; break;
      default:
        throw new LexerException("invalid suffix on radix literal", position);
    }

    if (suffix == TypeSuffix.Quad)
      this.Require(LanguageFeature.QuadType, position);

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
      '&' => (TokenKind.Ampersand, "&"),
      '@' => (TokenKind.At, "@"),
      '[' => (TokenKind.LBracket, "["),
      ']' => (TokenKind.RBracket, "]"),
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
