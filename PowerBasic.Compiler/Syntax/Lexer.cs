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
    if (dialect.IsGwBasica())
      ValidateNumberedPhysicalLines(source, file, dialect);

    var lexer = new Lexer(source, file, dialect);
    for (;;) {
      var token = lexer.Next();
      yield return token;
      if (token.Kind == TokenKind.EndOfFile)
        yield break;
    }
  }

  /// <summary>
  /// BASICA and GW-BASIC edit and store numbered program lines. Check the raw source before comments
  /// and blank tokens disappear, otherwise an unnumbered <c>REM</c> or apostrophe-only line would
  /// accidentally bypass the parser's line-number check.
  /// </summary>
  private static void ValidateNumberedPhysicalLines(string source, string file, Dialect dialect) {
    var line = 1;
    var start = 0;
    while (start < source.Length) {
      var end = start;
      while (end < source.Length && source[end] is not ('\r' or '\n'))
        ++end;

      var first = start;
      while (first < end && source[first] is ' ' or '\t')
        ++first;
      if (line == 1 && first < end && source[first] == '\uFEFF') {
        ++first;
        while (first < end && source[first] is ' ' or '\t')
          ++first;
      }
      if (first < end && !char.IsAsciiDigit(source[first]))
        throw new LexerException(
          $"{dialect.DisplayName()} requires a numeric line number on every program line",
          new SourcePosition(file, line, first - start + 1));

      if (end < source.Length && source[end] == '\r')
        ++end;
      if (end < source.Length && source[end] == '\n')
        ++end;
      start = end;
      ++line;
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

    if (c == '\'' && this.TryLexMicrosoftCommentMeta(position, markerLength: 1, out var commentMeta))
      return commentMeta;

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

    // PB 3.6 interpolated string: '$' immediately followed by '"' (never a valid
    // metacommand or type suffix start), gated to pb36 in LexInterpString.
    if (c == '$' && this.Peek() == '"')
      return this.LexInterpString(position);

    if (c == '$' && atStatementStart) {
      this.Require(LanguageFeature.MetaStatements, position);
      return this.LexMetaCommand(position);
    }

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
        // a comment may follow the continuation - `... + _    ' hotX=0, hotY=0` still joins the
        // next line (verified against the oracle: PBC 3.50 compiles DRAW_ANI.BAS, which does this)
        if (look < this._source.Length && this._source[look] == '\'')
          while (look < this._source.Length && this._source[look] is not ('\r' or '\n'))
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

  /// <summary>
  /// Recognizes Microsoft's compiler directives embedded in a comment. <paramref name="markerLength"/>
  /// is one for an apostrophe and zero after <c>REM</c> has already been consumed. Looking ahead
  /// before advancing lets an ordinary comment retain the normal skip-to-EOL path.
  /// </summary>
  private bool TryLexMicrosoftCommentMeta(SourcePosition position, int markerLength, out Token token) {
    token = default;
    // The Microsoft family only, which is what the gate's own note in DialectFacts claims but did
    // not enforce: IsAvailable consults the BORLAND table for a Borland dialect, so a pb36 minimum
    // made pb36 read these too. An unrecognised `$word` after a comment marker is then a hard parse
    // error, and PowerBASIC comments that merely mention a metastatement stop compiling - six of the
    // differential battery's own sources describe the `$OPTIMIZE`/`$ERROR`/`$CPU` they use, and all
    // six broke. In this family the syntax is a comment and stays one.
    if (this._dialect.Family() != DialectFamily.Microsoft
        || !DialectFacts.IsAvailable(LanguageFeature.MicrosoftCommentMetaStatements, this._dialect))
      return false;

    var look = this._index + markerLength;
    while (look < this._source.Length && this._source[look] is ' ' or '\t')
      ++look;
    if (look >= this._source.Length || this._source[look] != '$')
      return false;
    ++look;

    var commandStart = look;
    while (look < this._source.Length && IsIdentifierPart(this._source[look]))
      ++look;
    if (look == commandStart)
      return false;

    var tailStart = look;
    while (look < this._source.Length && this._source[look] is not ('\r' or '\n'))
      ++look;
    var command = this._source[commandStart..tailStart].ToUpperInvariant();
    var tail = this._source[tailStart..look].Trim();
    this.Advance(look - this._index);
    token = new(TokenKind.MicrosoftMetaCommand, command, position, StringValue: tail);
    return true;
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
      if (this.TryLexMicrosoftCommentMeta(position, markerLength: 0, out var meta))
        return meta;
      this.SkipToEndOfLine();
      this._atStatementStart = true;
      return this.Next();
    }

    var suffix = this.LexSuffix(position, allowCoalesce: true);
    return new(TokenKind.Identifier, text, position, suffix);
  }

  /// <summary>
  /// Lexes a type suffix directly attached to an identifier/literal tail
  /// (maximal munch: <c>??</c> before <c>?</c>, <c>&amp;&amp;</c> before <c>&amp;</c>, ...).
  /// When <paramref name="allowCoalesce"/> (identifiers only), a glued run of <c>?</c> that is
  /// followed by an unambiguous operand keeps a trailing <c>??</c> unconsumed so it lexes as the
  /// null-coalescing operator - so <c>a??15</c> is <c>a ?? 15</c>, <c>a????5</c> is <c>(a AS WORD) ?? 5</c>,
  /// while terminal <c>a??</c> stays the WORD suffix. (A coalesce default that begins with an
  /// identifier/keyword/sign needs a space - <c>a ?? other</c> - so a binary keyword like <c>AND</c>
  /// after a suffixed value is never mistaken for an operand.)
  /// </summary>
  private TypeSuffix LexSuffix(SourcePosition position, bool allowCoalesce = false) {
    switch (this.Current) {
      case '%':
        this.Advance();
        return TypeSuffix.Integer;
      case '?': {
        var run = 0;
        while (this.Peek(run) == '?')
          ++run;
        // pb36 null-conditional operator: a single '?' glued before '.' or '[' is the '?.'/'?[' access,
        // not a BYTE suffix (a scalar has no members/elements, so this removes no valid pb35 meaning).
        if (allowCoalesce && run == 1 && this.Peek(run) is '.' or '[' && DialectFacts.IsAvailable(LanguageFeature.NullConditional, this._dialect))
          return TypeSuffix.None;   // leave the '?' for the parser
        this.Require(LanguageFeature.UnsignedTypes, position);
        var after = run;
        while (this.Peek(after) is ' ' or '\t')
          ++after;
        var next = this.Peek(after);
        // a digit / string / interpolation after '??' is unambiguously an operand -> the trailing '??'
        // is the coalescing operator. '(' and '.' are NOT split triggers: they are an array subscript or
        // member on a suffixed variable (w??(i), w??.x); a parenthesised/identifier coalesce default needs a space.
        var coalesceFollows = allowCoalesce && run >= 2 && (char.IsAsciiDigit(next) || next is '"' or '$');
        var suffixCount = coalesceFollows ? run - 2 : run;
        if (suffixCount > 3)
          throw new LexerException("too many '?' type-suffix marks (use '?' = BYTE, '??' = WORD, '???' = DWORD)", position);
        this.Advance(suffixCount); // any trailing '??' is left for the null-coalescing operator token
        return suffixCount switch { 0 => TypeSuffix.None, 1 => TypeSuffix.Byte, 2 => TypeSuffix.Word, _ => TypeSuffix.Dword };
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
        // EXT, FIX and BCD are Bob Zale's, and the SUFFIX spelling has to be gated as well as the
        // DEFEXT/DEFFIX/DEFBCD statements were. Without this, `x## = 1` bound to an 80-bit EXT under
        // QuickBASIC, which never had the type at all - the dialect battery's numeric-typing probe
        // found it on its first run.
        this.Require(LanguageFeature.ExtendedNumericTypes, position);
        this.Advance();
        return TypeSuffix.Ext;
      case '@':
        this.Advance();
        this.Require(LanguageFeature.ExtendedNumericTypes, position);
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

    // '..' is the PB 3.6 spread marker, never a decimal point (0..3 is 0, .., 3)
    if (this.Current == '.' && this.Peek() != '.' && (char.IsAsciiDigit(this.Peek()) || !IsIdentifierStart(this.Peek()))) {
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

  /// <summary>
  /// PB 3.6 interpolated string <c>$"text {expr} {expr:fmt}"</c>: the raw inner text
  /// (between the quotes) is captured verbatim - holes, <c>{{</c>/<c>}}</c> escapes and
  /// nested string expressions are all left to the parser. The closing quote is the
  /// first <c>"</c> at brace depth 0; a <c>"</c> inside a <c>{ ... }</c> hole belongs to a
  /// nested string expression and is skipped.
  /// </summary>
  private Token LexInterpString(SourcePosition position) {
    this.Require(LanguageFeature.StringInterpolation, position);
    this.Advance(); // $
    this.Advance(); // opening quote
    var start = this._index;
    var depth = 0;
    while (!this.AtEnd && this.Current is not ('\r' or '\n')) {
      var c = this.Current;
      if (c == '{') {
        // '{{' is a literal brace, not a hole opener
        if (this.Peek() == '{') {
          this.Advance(2);
          continue;
        }
        ++depth;
      } else if (c == '}') {
        if (depth == 0 && this.Peek() == '}') { // literal '}}' outside a hole
          this.Advance(2);
          continue;
        }
        if (depth > 0)
          --depth;
      } else if (c == '"') {
        if (depth == 0)
          break; // closing quote of the interpolation
        // a '"' inside a hole opens a nested string expression - skip to its close
        this.Advance();
        while (!this.AtEnd && this.Current is not ('"' or '\r' or '\n'))
          this.Advance();
      }
      this.Advance();
    }
    var raw = this._source[start..this._index];
    if (this.Current == '"')
      this.Advance();
    return new(TokenKind.InterpString, raw, position, StringValue: raw);
  }

  private Token LexPunctuation(SourcePosition position) {
    var c = this.Current;
    this.Advance();

    var (kind, text) = c switch {
      // PB 3.6 scaled pointer arithmetic: '+*' / '-*' (contiguous; '+ *' / '- *'
      // never occur in valid source since PB has no prefix '*')
      '+' when this.Current == '*' => this.AdvanceTo(TokenKind.PlusStar, "+*"),
      '-' when this.Current == '*' => this.AdvanceTo(TokenKind.MinusStar, "-*"),
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
      '.' => this.Current == '.' ? this.AdvanceTo(TokenKind.DotDot, "..") : (TokenKind.Period, "."), // PB 3.6 '..' range/spread

      '#' => (TokenKind.Hash, "#"),
      // a standalone '?' (not glued to an identifier/literal - those become a BYTE/WORD suffix
      // in LexSuffix): pb36 null-coalescing '??' / a nullable type marker '?'
      '?' => this.Current == '?' ? this.AdvanceTo(TokenKind.QuestionQuestion, "??") : (TokenKind.Question, "?"),
      '&' => (TokenKind.Ampersand, "&"),
      '@' => (TokenKind.At, "@"),
      '[' => (TokenKind.LBracket, "["),
      ']' => (TokenKind.RBracket, "]"),
      '{' => (TokenKind.LBrace, "{"),
      '}' => (TokenKind.RBrace, "}"),
      '=' => this.Current switch {
        '<' => this.AdvanceTo(TokenKind.LessEquals, "=<"),
        // PB 3.6 lexes '=>' as a distinct lambda arrow; older dialects keep the
        // historical tolerance of '=>' meaning '>=' (so pb35 stays byte-identical)
        '>' => this.AdvanceTo(DialectFacts.IsAvailable(LanguageFeature.Lambdas, this._dialect) ? TokenKind.FatArrow : TokenKind.GreaterEquals, "=>"),
        _ => (TokenKind.Equals, "="),
      },
      '<' => this.LexLess(),
      '>' => this.LexGreater(),
      '|' => (TokenKind.Pipe, "|"),
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

  // '<' family: <= (LessEqual), <> (NotEqual), and the PB 3.6 operators
  // << (ShiftLeft), <<< (ShiftLeftLogical), <<> (RotateLeft), <>> (RotateRight).
  private (TokenKind, string) LexLess() {
    switch (this.Current) {
      case '=':
        return this.AdvanceTo(TokenKind.LessEquals, "<=");
      case '<':
        this.Advance();
        return this.Current switch {
          '<' => this.AdvanceTo(TokenKind.ShiftLeftLogical, "<<<"),
          '>' => this.AdvanceTo(TokenKind.RotateLeft, "<<>"),
          _ => (TokenKind.ShiftLeft, "<<"),
        };
      case '>':
        this.Advance();
        return this.Current == '>' ? this.AdvanceTo(TokenKind.RotateRight, "<>>") : (TokenKind.NotEquals, "<>");
      default:
        return (TokenKind.Less, "<");
    }
  }

  // '>' family: >= (GreaterEqual), >< (NotEqual), and the PB 3.6 operators
  // >> (ShiftRight, arithmetic) and >>> (ShiftRightLogical).
  private (TokenKind, string) LexGreater() {
    switch (this.Current) {
      case '=':
        return this.AdvanceTo(TokenKind.GreaterEquals, ">=");
      case '<':
        return this.AdvanceTo(TokenKind.NotEquals, "><");
      case '>':
        this.Advance();
        return this.Current == '>' ? this.AdvanceTo(TokenKind.ShiftRightLogical, ">>>") : (TokenKind.ShiftRight, ">>");
      default:
        return (TokenKind.Greater, ">");
    }
  }
}

/// <summary>Raised when source contains a character sequence the lexer cannot tokenize.</summary>
public sealed class LexerException(string message, SourcePosition position) : Exception($"{position}: {message}") {
  public SourcePosition Position { get; } = position;
}
