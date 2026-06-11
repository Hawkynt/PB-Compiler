using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Syntax;

/// <summary>
/// Recursive-descent parser turning a (preprocessor-expanded) token stream into a
/// <see cref="CompilationUnit"/>. Keywords are matched case-insensitively against
/// identifier tokens; unknown identifier-led statements fall back to SUB calls,
/// mirroring PowerBASIC's own late binding.
/// </summary>
public sealed partial class Parser {

  /// <summary>Reserved statement keywords - these can never be labels.</summary>
  private static readonly HashSet<string> _statementKeywords = new(StringComparer.OrdinalIgnoreCase) {
    "LET", "CALL", "SUB", "FUNCTION", "DECLARE", "TYPE", "UNION",
    "DEF", "DEFINT", "DEFLNG", "DEFQUD", "DEFSNG", "DEFDBL", "DEFEXT", "DEFFIX", "DEFBCD", "DEFSTR", "DEFFLX",
    "DIM", "LOCAL", "STATIC", "SHARED", "PUBLIC", "EXT", "COMMON", "REDIM", "ERASE",
    "STDOUT", "STDIN", "SETEOF", "ERRCLEAR",
    "IF", "SELECT", "FOR", "DO", "WHILE", "EXIT", "GOTO", "GOSUB", "RETURN",
    "ON", "RESUME", "ERROR", "END", "STOP", "SYSTEM",
    "PRINT", "LPRINT", "INPUT", "LINE", "OPEN", "CLOSE", "GET", "PUT", "SEEK", "FIELD", "LSET", "RSET",
    "DATA", "READ", "RESTORE", "INCR", "DECR", "SWAP", "PSET", "PRESET", "CIRCLE",
    "KEY", "TIMER", "COM", "PEN", "STRIG", "PLAY", "VIEW", "PALETTE",
    "NEXT", "LOOP", "WEND", "CASE", "ELSE", "ELSEIF",
    "BEEP", "CLS", "SCREEN", "COLOR", "LOCATE", "WINDOW", "PAINT", "SOUND", "RANDOMIZE",
    "SLEEP", "DELAY", "SHELL", "KILL", "NAME", "CHDIR", "MKDIR", "RMDIR", "ENVIRON", "WIDTH",
    "POKE", "OUT", "WAIT", "REG", "FILES", "BLOAD", "BSAVE", "DRAW", "PCOPY", "SHIFT", "ROTATE",
  };

  /// <summary>Soft command statements parsed as <see cref="CommandStmt"/> with positional arguments.</summary>
  private static readonly HashSet<string> _commandKeywords = new(StringComparer.OrdinalIgnoreCase) {
    "BEEP", "CLS", "SCREEN", "COLOR", "LOCATE", "WINDOW", "PAINT", "SOUND", "RANDOMIZE",
    "SLEEP", "DELAY", "SHELL", "KILL", "NAME", "CHDIR", "MKDIR", "RMDIR", "ENVIRON", "WIDTH",
    "POKE", "OUT", "WAIT", "REG", "FILES", "BLOAD", "BSAVE", "DRAW", "PCOPY", "VIEW", "PALETTE",
  };

  private static readonly HashSet<string> _structuralEndKeywords = new(StringComparer.OrdinalIgnoreCase)
    { "IF", "SELECT", "SUB", "FUNCTION", "TYPE", "UNION", "DEF" };

  private static readonly HashSet<string> _eventKinds = new(StringComparer.OrdinalIgnoreCase)
    { "KEY", "TIMER", "COM", "PLAY", "PEN", "STRIG", "UEVENT" };

  private readonly List<Token> _tokens;
  private readonly Dialect _dialect;
  private int _pos;
  private bool _atLineStart = true;
  private int _pendingNexts;

  private Parser(List<Token> tokens, Dialect dialect) {
    this._tokens = tokens;
    this._dialect = dialect;
  }

  /// <summary>Parses a whole token stream (one file after preprocessing) into a compilation unit.</summary>
  public static CompilationUnit Parse(IEnumerable<Token> tokens, string fileName, Dialect dialect = Dialect.Pb35) {
    ArgumentNullException.ThrowIfNull(tokens);
    ArgumentNullException.ThrowIfNull(fileName);

    var list = tokens.ToList();
    if (list.Count == 0 || list[^1].Kind != TokenKind.EndOfFile)
      list.Add(new(TokenKind.EndOfFile, "", new(fileName, 0, 0)));

    var parser = new Parser(list, dialect);
    var statements = parser.ParseBody();
    if (parser._pendingNexts > 0)
      throw new ParserException("NEXT without FOR", parser.Current.Position);

    return new(fileName, statements);
  }

  #region token plumbing

  private Token Current => this._tokens[this._pos];

  private Token Peek(int offset = 1) => this.TokenAt(this._pos + offset);

  private Token TokenAt(int index) => index < this._tokens.Count ? this._tokens[index] : this._tokens[^1];

  private Token Advance() {
    var token = this.Current;
    if (token.Kind != TokenKind.EndOfFile)
      ++this._pos;
    return token;
  }

  private bool Match(TokenKind kind) {
    if (this.Current.Kind != kind)
      return false;

    ++this._pos;
    return true;
  }

  private Token Expect(TokenKind kind, string what) => this.Current.Kind == kind
    ? this.Advance()
    : throw this.Error($"expected {what}, found '{this.Current.Text}'");

  private bool IsKeyword(int offset, string keyword) {
    var token = offset == 0 ? this.Current : this.Peek(offset);
    return token is { Kind: TokenKind.Identifier, Suffix: TypeSuffix.None }
      && token.Text.Equals(keyword, StringComparison.OrdinalIgnoreCase);
  }

  private bool TryMatchKeyword(string keyword) {
    if (!this.IsKeyword(0, keyword))
      return false;

    ++this._pos;
    return true;
  }

  private void ExpectKeyword(string keyword) {
    if (!this.TryMatchKeyword(keyword))
      throw this.Error($"expected {keyword}, found '{this.Current.Text}'");
  }

  private bool IsStatementEnd() => this.Current.Kind
    is TokenKind.EndOfLine or TokenKind.Colon or TokenKind.EndOfFile
    || this.IsKeyword(0, "ELSE");

  private ParserException Error(string message) => new(message, this.Current.Position);

  /// <summary>Throws when <paramref name="feature"/> is not available under the active dialect.</summary>
  private void Require(LanguageFeature feature) {
    if (!DialectFacts.IsAvailable(feature, this._dialect))
      throw this.Error(DialectFacts.RequirementMessage(feature, this._dialect));
  }

  #endregion

  #region statement list

  private void SkipSeparators() {
    for (;;) {
      if (this.Current.Kind == TokenKind.EndOfLine)
        this._atLineStart = true;
      else if (this.Current.Kind != TokenKind.Colon)
        return;
      ++this._pos;
    }
  }

  /// <summary>Parses statements until end of file or one of the (one- or two-word) keyword terminators.</summary>
  private List<Statement> ParseBody(params string[] terminators) {
    var result = new List<Statement>();
    for (;;) {
      this.SkipSeparators();
      if (this._pendingNexts > 0)
        return result;
      if (this.Current.Kind == TokenKind.EndOfFile) {
        if (terminators.Length > 0)
          throw this.Error($"unexpected end of file, expected {terminators[0]}");
        return result;
      }
      if (this.IsAtAnyTerminator(terminators))
        return result;
      result.Add(this.ParseStatement());
    }
  }

  private bool IsAtAnyTerminator(string[] terminators) {
    foreach (var terminator in terminators)
      if (this.IsAtTerminator(terminator))
        return true;
    return false;
  }

  private bool IsAtTerminator(string terminator) {
    var space = terminator.IndexOf(' ');
    return space < 0
      ? this.IsKeyword(0, terminator)
      : this.IsKeyword(0, terminator[..space]) && this.IsKeyword(1, terminator[(space + 1)..]);
  }

  #endregion

  #region statement dispatch

  private Statement ParseStatement() {
    var atLineStart = this._atLineStart;
    this._atLineStart = false;

    var token = this.Current;
    return token.Kind switch {
      TokenKind.InlineAsm => new InlineAsmStmt(this.Advance().Position, token.Text),
      TokenKind.MetaCommand => this.ParseMeta(),
      TokenKind.NamedConstant => this.ParseEquate(),
      TokenKind.IntegerLiteral when atLineStart => new LabelStmt(this.Advance().Position, token.IntegerValue.ToString()),
      TokenKind.Question => this.ParsePrint(false),
      TokenKind.At => this.ParseAssignment(), // @p = value
      TokenKind.Identifier => this.ParseIdentifierStatement(atLineStart),
      _ => throw this.Error($"unexpected '{token.Text}'"),
    };
  }

  private Statement ParseIdentifierStatement(bool atLineStart) {
    var token = this.Current;
    var keyword = token.Text.ToUpperInvariant();

    if (atLineStart && token.Suffix == TypeSuffix.None && this.Peek().Kind == TokenKind.Colon && !_statementKeywords.Contains(keyword)) {
      this.Advance();
      this.Advance();
      return new LabelStmt(token.Position, token.Text);
    }

    if (keyword == "MID" && token.Suffix == TypeSuffix.String && this.Peek().Kind == TokenKind.LParen)
      return this.ParseMidAssign();

    // ASC(s$ [, n]) = code - statement form (PB 3.5)
    if (keyword == "ASC" && token.Suffix == TypeSuffix.None && this.Peek().Kind == TokenKind.LParen && this.IsAssignmentAhead())
      return this.ParseAscAssign();

    // GET$ fh, count, strvar / PUT$ fh, strvar - string-file statements
    if (keyword is "GET" or "PUT" && token.Suffix == TypeSuffix.String)
      return this.ParseGetPutString(keyword);

    // POKE$ offset, bytes$ - write string bytes at DEF SEG:offset
    if (keyword == "POKE" && token.Suffix == TypeSuffix.String) {
      var pokePos = this.Advance().Position;
      var pokeAddr = this.ParseExpression();
      this.Expect(TokenKind.Comma, "','");
      return new CommandStmt(pokePos, "POKE$", [pokeAddr, this.ParseExpression()]);
    }

    // INPUT# 1, x / PRINT# 1, x / WRITE# 1, x / CLOSE# n ... - the '#' lexes as a Double suffix on the keyword
    if (token.Suffix == TypeSuffix.Double)
      switch (keyword) {
        case "INPUT": return this.ParseInput(isLineInput: false);
        case "PRINT": return this.ParsePrint(isLPrint: false);
        case "LPRINT": return this.ParsePrint(isLPrint: true);
        case "WRITE": return this.ParseWrite();
        case "CLOSE": return this.ParseClose();
        case "GET": return this.ParseGetPutFile(isGet: true);
        case "PUT": return this.ParseGetPutFile(isGet: false);
        case "SEEK": return this.ParseSeek();
      }

    // IF/WHILE conditions may start with '(' and contain '=', which would look like an assignment
    if (keyword is not ("IF" or "WHILE") && this.IsAssignmentAhead())
      return this.ParseAssignment();

    if (token.Suffix != TypeSuffix.None)
      return this.ParseBareCall();

    switch (keyword) {
      case "LET": this.Advance(); return this.ParseAssignment();
      case "CALL": return this.ParseCall();
      case "SUB": return this.ParseSub();
      case "FUNCTION": return this.ParseFunction();
      case "DECLARE": return this.ParseDeclare();
      case "TYPE": return this.ParseTypeDecl(isUnion: false);
      case "UNION": return this.ParseTypeDecl(isUnion: true);
      case "DEF": return this.ParseDef();
      case "DEFINT": return this.ParseDefType(BuiltinType.Integer);
      case "DEFLNG": return this.ParseDefType(BuiltinType.Long);
      case "DEFQUD": this.Require(LanguageFeature.QuadType); return this.ParseDefType(BuiltinType.Quad);
      case "DEFSNG": return this.ParseDefType(BuiltinType.Single);
      case "DEFDBL": return this.ParseDefType(BuiltinType.Double);
      case "DEFEXT": return this.ParseDefType(BuiltinType.Ext);
      case "DEFFIX": return this.ParseDefType(BuiltinType.Fix);
      case "DEFBCD": return this.ParseDefType(BuiltinType.Bcd);
      case "DEFSTR": return this.ParseDefType(BuiltinType.String);
      case "DEFFLX": return this.ParseDefType(BuiltinType.Flex);
      case "DIM": return this.ParseDim(StorageClass.Dim);
      case "LOCAL": return this.ParseDim(StorageClass.Local);
      case "STATIC": return this.ParseDim(StorageClass.Static);
      case "SHARED": return this.ParseDim(StorageClass.Shared);
      case "PUBLIC": return this.ParseDim(StorageClass.Public);
      case "EXT": return this.ParseDim(StorageClass.Ext);
      case "COMMON": return this.ParseDim(StorageClass.Common);
      case "REDIM": return this.ParseRedim();
      case "ERASE": return this.ParseErase();
      case "IF": return this.ParseIf();
      case "SELECT": return this.ParseSelect();
      case "FOR": return this.ParseFor();
      case "DO": return this.ParseDo();
      case "WHILE": return this.ParseWhile();
      case "EXIT": return this.ParseExit();
      case "ITERATE": return this.ParseIterate();
      case "WRITE": return this.ParseWrite();
      case "CHAIN": {
        var chainPos = this.Advance().Position;
        return new ChainStmt(chainPos, this.ParseExpression(), IsRun: false);
      }
      case "RUN" when !this.IsStatementEnd(): {
        var runPos = this.Advance().Position;
        return new ChainStmt(runPos, this.ParseExpression(), IsRun: true);
      }
      case "EXECUTE": {
        var execPos = this.Advance().Position;
        return new CommandStmt(execPos, "EXECUTE", [this.ParseExpression()]);
      }
      case "REPLACE": {
        var replacePos = this.Advance().Position;
        var find = this.ParseExpression();
        this.ExpectKeyword("WITH");
        var with = this.ParseExpression();
        this.ExpectKeyword("IN");
        return new ReplaceStmt(replacePos, find, with, this.ParseLValue());
      }
      case "GOTO": return this.ParseGotoGosub(isGosub: false);
      case "GOSUB": return this.ParseGotoGosub(isGosub: true);
      case "RETURN": return this.ParseReturn();
      case "ON": return this.ParseOn();
      case "RESUME": return this.ParseResume();
      case "ERROR": return new ErrorStmt(this.Advance().Position, this.ParseExpression());
      case "END": return this.ParseEnd();
      case "STOP" or "SYSTEM": return this.ParseProgramEnd();
      case "PRINT": return this.ParsePrint(isLPrint: false);
      case "LPRINT": return this.ParsePrint(isLPrint: true);
      case "INPUT": return this.ParseInput(isLineInput: false);
      case "LINE": return this.ParseLine();
      case "OPEN": return this.ParseOpen();
      case "CLOSE": return this.ParseClose();
      case "GET": return this.ParseGetPut(isGet: true);
      case "PUT": return this.ParseGetPut(isGet: false);
      case "SEEK": return this.ParseSeek();
      case "FIELD": return this.ParseField();
      case "LSET": return this.ParseLsetRset(isLeft: true);
      case "RSET": return this.ParseLsetRset(isLeft: false);
      case "DATA": return this.ParseData();
      case "READ": return this.ParseRead();
      case "RESTORE": return this.ParseRestore();
      case "INCR": return this.ParseIncrDecr(increment: true);
      case "DECR": return this.ParseIncrDecr(increment: false);
      case "SWAP": return this.ParseSwap();
      case "BIT" when this.IsKeyword(1, "SET") || this.IsKeyword(1, "RESET") || this.IsKeyword(1, "TOGGLE"):
        return this.ParseBit();
      case "ARRAY" when this.IsKeyword(1, "SORT") || this.IsKeyword(1, "SCAN"):
        return this.ParseArrayStatement();
      case "PSET": return this.ParsePset(isPreset: false);
      case "PRESET": return this.ParsePset(isPreset: true);
      case "CIRCLE": return this.ParseCircle();
      case "KEY" or "TIMER" or "COM" or "PEN" or "STRIG" or "PLAY": return this.ParseEventOrCommand(keyword);
      case "SHIFT" or "ROTATE": return this.ParseShiftRotate(keyword);
      case "STDOUT": return this.ParseStdOut();
      case "STDIN": return this.ParseStdIn();
      case "SETEOF": return this.ParseSetEof();
      case "ERRCLEAR":
        this.Require(LanguageFeature.ErrClear);
        return new CommandStmt(this.Advance().Position, "ERRCLEAR", []);
      case "NEXT": throw this.Error("NEXT without FOR");
      case "LOOP": throw this.Error("LOOP without DO");
      case "WEND": throw this.Error("WEND without WHILE");
      case "CASE": throw this.Error("CASE outside SELECT CASE");
      case "ELSE" or "ELSEIF": throw this.Error($"{keyword} without IF");
      default:
        return _commandKeywords.Contains(keyword) ? this.ParseCommand(keyword) : this.ParseBareCall();
    }
  }

  private Statement ParseMeta() {
    var token = this.Advance();
    var arguments = new List<Token>();
    while (this.Current.Kind is not (TokenKind.EndOfLine or TokenKind.EndOfFile))
      arguments.Add(this.Advance());
    return new MetaStmt(token.Position, token.Text, arguments);
  }

  private Statement ParseEquate() {
    var name = this.Advance();
    this.Expect(TokenKind.Equals, "'='");
    return new EquateStmt(name.Position, name.Text, this.ParseExpression());
  }

  private Statement ParseAssignment() {
    var target = this.ParseLValue();
    this.Expect(TokenKind.Equals, "'='");
    return new AssignStmt(target.Position, target, this.ParseExpression());
  }

  #endregion
}

/// <summary>Raised when the token stream violates PowerBASIC 3.5 grammar.</summary>
public sealed class ParserException(string message, SourcePosition position) : Exception($"{position}: {message}") {
  public SourcePosition Position { get; } = position;
}
