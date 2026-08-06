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
    "LET", "CALL", "SUB", "FUNCTION", "DECLARE", "TYPE", "UNION", "ENUM", "WITH", "EVENT", "USING",
    "DEF", "DEFINT", "DEFLNG", "DEFQUD", "DEFSNG", "DEFDBL", "DEFEXT", "DEFFIX", "DEFBCD", "DEFSTR", "DEFFLX",
    "DIM", "LOCAL", "STATIC", "SHARED", "PUBLIC", "EXT", "COMMON", "REDIM", "ERASE",
    "STDOUT", "STDIN", "SETEOF", "ERRCLEAR",
    "IF", "SELECT", "FOR", "DO", "WHILE", "EXIT", "GOTO", "GOSUB", "RETURN",
    "ON", "RESUME", "ERROR", "TRY", "END", "STOP", "SYSTEM",
    "PRINT", "LPRINT", "INPUT", "LINE", "OPEN", "CLOSE", "GET", "PUT", "SEEK", "FIELD", "LSET", "RSET",
    "DATA", "READ", "RESTORE", "INCR", "DECR", "SWAP", "PSET", "PRESET", "CIRCLE",
    "KEY", "TIMER", "COM", "PEN", "STRIG", "PLAY", "VIEW", "PALETTE",
    "NEXT", "LOOP", "WEND", "CASE", "ELSE", "ELSEIF",
    "BEEP", "CLS", "SCREEN", "COLOR", "LOCATE", "WINDOW", "PAINT", "SOUND", "RANDOMIZE",
    "SLEEP", "DELAY", "SHELL", "KILL", "NAME", "CHDIR", "MKDIR", "RMDIR", "ENVIRON", "WIDTH",
    "POKE", "OUT", "WAIT", "REG", "FILES", "BLOAD", "BSAVE", "DRAW", "PCOPY", "SHIFT", "ROTATE",
    "OPTION",
  };

  /// <summary>
  /// Every keyword that can begin a statement. Exposed because "the compiler handles every statement"
  /// is a claim worth checking mechanically rather than believing: the statement-surface tests assert
  /// that each of these is exercised by at least one form, so a keyword added to the parser without a
  /// test is a failure rather than a quiet hole.
  /// </summary>
  public static IReadOnlyCollection<string> StatementKeywords => _statementKeywords;

  /// <summary>Soft command statements parsed as <see cref="CommandStmt"/> with positional arguments.</summary>
  private static readonly HashSet<string> _commandKeywords = new(StringComparer.OrdinalIgnoreCase) {
    "BEEP", "CLS", "SCREEN", "COLOR", "LOCATE", "WINDOW", "PAINT", "SOUND", "RANDOMIZE",
    "SLEEP", "DELAY", "SHELL", "KILL", "NAME", "CHDIR", "MKDIR", "RMDIR", "ENVIRON", "WIDTH",
    "POKE", "OUT", "WAIT", "REG", "FILES", "BLOAD", "BSAVE", "DRAW", "PCOPY", "VIEW", "PALETTE",
  };

  private static readonly HashSet<string> _structuralEndKeywords = new(StringComparer.OrdinalIgnoreCase)
    { "IF", "SELECT", "SUB", "FUNCTION", "TYPE", "UNION", "DEF", "ENUM", "TRY" };

  private static readonly HashSet<string> _eventKinds = new(StringComparer.OrdinalIgnoreCase)
    { "KEY", "TIMER", "COM", "PLAY", "PEN", "STRIG", "UEVENT" };

  private readonly List<Token> _tokens;
  private readonly Dialect _dialect;
  private int _pos;
  private bool _atLineStart = true;
  private int _pendingNexts;

  /// <summary>Active WITH subjects (innermost last); a leading-dot member binds to the top.</summary>
  private readonly List<Expression> _withSubjects = [];

  /// <summary>Parser-only marker: a WITH body whose statements ParseBody splices inline (never reaches the binder).</summary>
  private sealed record StatementGroup(SourcePosition Position, IReadOnlyList<Statement> Statements) : Ast.Statement(Position);

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

  /// <summary>True while parsing the expression parts of a statement that uses IN as its own delimiter (REPLACE), so the membership operator stays out of the way.</summary>
  private bool _suppressInOperator;

  /// <summary>pb36 discriminated unions: case name → (union, tag index, payload fields); filled while parsing UNION ... CASE declarations, consulted by constructors and IS tests.</summary>
  private readonly Dictionary<string, DuCase> _duCases = new(StringComparer.OrdinalIgnoreCase);
  private sealed record DuCase(string UnionName, int Index, string CaseName, IReadOnlyList<TypeField> Fields);
  /// <summary>Payload bindings collected while parsing an IF condition (IS Case var): hoisted as DIM + copy before the IF.</summary>
  private readonly List<Statement> _patternBindings = [];
  /// <summary>False while a loop test / FOR bound is being parsed - an IS binding there would be copied once instead of per iteration, so it is rejected.</summary>
  private bool _patternBindingAllowed = true;

  /// <summary>Takes (and clears) the pattern bindings collected so far.</summary>
  private List<Statement> TakePatternBindings() {
    if (this._patternBindings.Count == 0)
      return [];
    var taken = new List<Statement>(this._patternBindings);
    this._patternBindings.Clear();
    return taken;
  }

  private static Statement WrapBindings(List<Statement> bindings, Statement statement)
    => bindings.Count == 0 ? statement : new StatementGroup(statement.Position, [.. bindings, statement]);

  /// <summary>Runs <paramref name="parse"/> with IS payload bindings rejected (loop tests: the hoisted copy would run once, not per iteration).</summary>
  private T WithoutPatternBindings<T>(Func<T> parse) {
    var saved = this._patternBindingAllowed;
    this._patternBindingAllowed = false;
    try {
      return parse();
    } finally {
      this._patternBindingAllowed = saved;
    }
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
      // A BASICA/GW-BASIC block terminator still begins with that physical line's mandatory number:
      //   10 FOR I=1 TO 3
      //   20 PRINT I
      //   30 NEXT I
      // Keep 30 as a real label (GOTO 30 must work), then let the surrounding parser consume NEXT.
      if (this._dialect.IsGwBasica() && this.Current.Kind == TokenKind.IntegerLiteral
          && terminators.Any(t => this.IsAtTerminator(t, 1))) {
        var line = this.Advance();
        result.Add(new LabelStmt(line.Position, line.IntegerValue.ToString()));
        this._atLineStart = false;
      }
      if (this._pendingNexts > 0)
        return LowerDefers(result);
      if (this.Current.Kind == TokenKind.EndOfFile) {
        if (terminators.Length > 0)
          throw this.Error($"unexpected end of file, expected {terminators[0]}");
        return LowerDefers(result);
      }
      if (this.IsAtAnyTerminator(terminators))
        return LowerDefers(result);
      // a WITH block desugars to a StatementGroup spliced inline here
      if (this.ParseStatement() is var parsed && parsed is StatementGroup group)
        result.AddRange(group.Statements);
      else
        result.Add(parsed);
    }
  }

  /// <summary>
  /// Lowers DEFER in a block: the first DEFER wraps the rest of the block in a TRY ... FINALLY so the
  /// deferred statement runs on every exit (normal or fault); a later DEFER nests inside, so deferred
  /// statements run last-in-first-out. Blocks with no DEFER are returned unchanged.
  /// </summary>
  private static List<Statement> LowerDefers(List<Statement> body) {
    var i = body.FindIndex(s => s is DeferStmt);
    if (i < 0)
      return body;
    var defer = (DeferStmt)body[i];
    var after = LowerDefers(body.GetRange(i + 1, body.Count - i - 1));
    var result = body.GetRange(0, i);
    result.Add(new TryStmt(defer.Position, after, null, [defer.Deferred]));
    return result;
  }

  private bool IsAtAnyTerminator(string[] terminators) {
    foreach (var terminator in terminators)
      if (this.IsAtTerminator(terminator))
        return true;
    return false;
  }

  private bool IsAtTerminator(string terminator) {
    return this.IsAtTerminator(terminator, 0);
  }

  private bool IsAtTerminator(string terminator, int offset) {
    var space = terminator.IndexOf(' ');
    return space < 0
      ? this.IsKeyword(offset, terminator)
      : this.IsKeyword(offset, terminator[..space]) && this.IsKeyword(offset + 1, terminator[(space + 1)..]);
  }

  #endregion

  #region statement dispatch

  private Statement ParseStatement() {
    var statement = this.ParseStatementCore();
    // IS payload bindings collected inside this statement's expressions (ternary conditions,
    // SELECT subjects, ...) hoist as DIM + copy right before it; ParseIf/ParseSelect have
    // already taken their own, so anything left here belongs to this statement
    return this._patternBindings.Count == 0 || statement is StatementGroup
      ? statement
      : WrapBindings(this.TakePatternBindings(), statement);
  }

  private Statement ParseStatementCore() {
    var atLineStart = this._atLineStart;
    this._atLineStart = false;

    var token = this.Current;
    if (atLineStart && this._dialect.IsGwBasica() && token.Kind != TokenKind.IntegerLiteral)
      throw this.Error($"{this._dialect.DisplayName()} requires a numeric line number on every program line");
    if (atLineStart && this._dialect.IsGwBasica() && token.IntegerValue is < 0 or > 65529)
      throw this.Error($"{this._dialect.DisplayName()} line number must be between 0 and 65529");

    return token.Kind switch {
      TokenKind.InlineAsm => new InlineAsmStmt(this.Advance().Position, token.Text),
      TokenKind.MetaCommand => this.ParseMeta(),
      TokenKind.NamedConstant => this.ParseEquate(),
      TokenKind.IntegerLiteral when atLineStart => new LabelStmt(this.Advance().Position, token.IntegerValue.ToString()),
      TokenKind.Question => this.ParsePrint(false),
      TokenKind.At => this.ParseAssignment(), // @p = value
      TokenKind.Period when this._withSubjects.Count > 0 && this.Peek().Kind == TokenKind.Identifier => this.ParseAssignment(), // WITH: .member = value
      TokenKind.Identifier => this.ParseIdentifierStatement(atLineStart),
      _ => throw this.Error($"unexpected '{token.Text}'"),
    };
  }

  private Statement ParseIdentifierStatement(bool atLineStart) {
    var token = this.Current;
    var keyword = token.Text.ToUpperInvariant();

    if (token.Suffix == TypeSuffix.None && this.Peek().Kind == TokenKind.Colon
        && !DialectFacts.IsAvailable(LanguageFeature.NamedLabels, this._dialect))
      throw this.Error($"{this._dialect.DisplayName()} supports numeric line labels only");

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

    // pb36 tuple destructuring: a, b = expr (a top-level comma before '=')
    if (keyword is not ("IF" or "WHILE") && this.IsDestructuringAhead())
      return this.ParseDestructuring();

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
      case "ENUM": return this.ParseEnum();
      case "EVENT": return this.ParseEventDecl();
      case "USING": return this.ParseUsing();
      case "WITH": return this.ParseWith();
      case "DEF": return this.ParseDef();
      case "DEFINT": return this.ParseDefType(BuiltinType.Integer);
      case "DEFLNG": this.Require(LanguageFeature.LongType); return this.ParseDefType(BuiltinType.Long);
      case "DEFQUD": this.Require(LanguageFeature.QuadType); return this.ParseDefType(BuiltinType.Quad);
      case "DEFSNG": return this.ParseDefType(BuiltinType.Single);
      case "DEFDBL": return this.ParseDefType(BuiltinType.Double);
      case "DEFEXT": this.Require(LanguageFeature.ExtendedNumericTypes); return this.ParseDefType(BuiltinType.Ext);
      case "DEFFIX": this.Require(LanguageFeature.ExtendedNumericTypes); return this.ParseDefType(BuiltinType.Fix);
      case "DEFBCD": this.Require(LanguageFeature.ExtendedNumericTypes); return this.ParseDefType(BuiltinType.Bcd);
      case "DEFSTR": return this.ParseDefType(BuiltinType.String);
      case "DEFFLX": this.Require(LanguageFeature.ExtendedNumericTypes); return this.ParseDefType(BuiltinType.Flex);
      case "DIM": return this.ParseDim(StorageClass.Dim);
      case "LOCAL": return this.ParseDim(StorageClass.Local);
      case "STATIC": return this.ParseDim(StorageClass.Static);
      case "SHARED": return this.ParseDim(StorageClass.Shared);
      case "PUBLIC": this.Require(LanguageFeature.PublicStorage); return this.ParseDim(StorageClass.Public);
      case "EXT": this.Require(LanguageFeature.PublicStorage); return this.ParseDim(StorageClass.Ext);
      case "COMMON": return this.ParseDim(StorageClass.Common);
      case "REDIM": return this.ParseRedim();
      case "ERASE": return this.ParseErase();
      case "OPTION": return this.ParseOption();
      case "IF": return this.ParseIf();
      case "SELECT": return this.ParseSelect();
      case "TRY": return this.ParseTry();
      case "DEFER": return this.ParseDefer();
      case "FOR": return this.ParseFor();
      case "DO": return this.ParseDo();
      case "WHILE": return this.ParseWhile();
      case "EXIT": return this.ParseExit();
      case "ITERATE": return this.ParseIterate();
      case "REQUIRE" or "ENSURE" when DialectFacts.IsAvailable(LanguageFeature.Contracts, this._dialect): {
        // pb36 contracts: a checked condition (error 5 on violation), compiled out under --optimize
        var kw = this.Advance();
        var cond = this.ParseExpression();
        string? msg = null;
        if (this.Match(TokenKind.Comma))
          msg = this.Expect(TokenKind.StringLiteral, "contract message").StringValue;
        return new RequireStmt(kw.Position, cond, msg, kw.Text.Equals("ENSURE", StringComparison.OrdinalIgnoreCase));
      }
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
        // REPLACE find WITH with IN target: IN is this statement's delimiter, so the pb36
        // membership operator is suppressed while parsing the find/with expressions
        var replacePos = this.Advance().Position;
        this._suppressInOperator = true;
        Expression find, with;
        try {
          find = this.ParseExpression();
          this.ExpectKeyword("WITH");
          with = this.ParseExpression();
        } finally {
          this._suppressInOperator = false;
        }
        this.ExpectKeyword("IN");
        return new ReplaceStmt(replacePos, find, with, this.ParseLValue());
      }
      case "YIELD" when this.IsCoroutineYield(): return this.ParseYield();
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
    // pb36 $ASSERT cond [, "message"]: a real expression, checked at compile time by the binder
    if (token.Text.Equals("ASSERT", StringComparison.OrdinalIgnoreCase)) {
      this.Require(LanguageFeature.StaticAssert);
      var condition = this.ParseExpression();
      string? message = null;
      if (this.Match(TokenKind.Comma))
        message = this.Expect(TokenKind.StringLiteral, "assertion message").StringValue;
      return new StaticAssertStmt(token.Position, condition, message);
    }
    // pb36 $RESOURCE name, "file": bake a file into the image as a BYTE array
    if (token.Text.Equals("RESOURCE", StringComparison.OrdinalIgnoreCase)) {
      this.Require(LanguageFeature.ResourceEmbed);
      var name = this.Expect(TokenKind.Identifier, "resource array name");
      this.Expect(TokenKind.Comma, "','");
      var file = this.Expect(TokenKind.StringLiteral, "resource file name");
      return new ResourceStmt(token.Position, name.Text, file.StringValue!);
    }
    this.Require(LanguageFeature.MetaStatements);
    var arguments = new List<Token>();
    while (this.Current.Kind is not (TokenKind.EndOfLine or TokenKind.EndOfFile))
      arguments.Add(this.Advance());
    return new MetaStmt(token.Position, token.Text, arguments);
  }

  private Statement ParseEquate() {
    this.Require(LanguageFeature.EquateStatement);   // Bob Zale's; BC 1.00 and 4.50 both reject it
    var name = this.Advance();
    this.Expect(TokenKind.Equals, "'='");
    return new EquateStmt(name.Position, name.Text, this.ParseExpression());
  }

  private Statement ParseDestructuring() {
    this.Require(LanguageFeature.Tuples);
    var targets = new List<Expression>();
    do
      targets.Add(this.ParseLValue());
    while (this.Match(TokenKind.Comma));
    var eq = this.Expect(TokenKind.Equals, "'='");
    return new DestructureStmt(eq.Position, targets, this.ParseExpression());
  }

  private Statement ParseAssignment() {
    var target = this.ParseLValue();

    // compound assignment (PB 3.6): 'target OP= value' desugars to
    // 'target = target OP value'. The lvalue node is reused in both the write
    // and read positions - structurally correct (it is genuinely read+written).
    if (CompoundOp(this.Current.Kind) is { } op && this.Peek().Kind == TokenKind.Equals) {
      this.Require(LanguageFeature.CompoundAssignment);
      if (IsShiftRotateToken(this.Current.Kind)) // <<= >>= <<<= >>>= <<>= <>>= |=
        this.Require(LanguageFeature.ShiftRotateOps);
      this.Advance(); // operator
      this.Advance(); // '='
      var value = this.ParseExpression();
      return new AssignStmt(target.Position, target, new BinaryExpr(target.Position, op, target, value));
    }

    this.Expect(TokenKind.Equals, "'='");
    var assigned = this.ParseExpression();
    // pb36 discriminated-union constructor: s = Case(args) / s = Case lowers to the tag store
    // plus one payload-field store each - the case name acts as a module-wide constructor
    if (this._duCases.Count > 0) {
      var (caseName, args) = assigned switch {
        CallOrIndexExpr c when c.Suffix == TypeSuffix.None && this._duCases.ContainsKey(c.Name) => (c.Name, c.Arguments),
        NameExpr n when n.Suffix == TypeSuffix.None && this._duCases.ContainsKey(n.Name) => (n.Name, (IReadOnlyList<Expression>)[]),
        _ => (null, []),
      };
      if (caseName != null) {
        var duCase = this._duCases[caseName];
        if (args.Count != duCase.Fields.Count)
          throw this.Error($"union case {duCase.CaseName} takes {duCase.Fields.Count} value(s), got {args.Count}");
        var group = new List<Statement> {
          new AssignStmt(target.Position, new MemberExpr(target.Position, target, "$tag", TypeSuffix.None),
            new IntegerLiteralExpr(target.Position, duCase.Index, TypeSuffix.None)),
        };
        for (var i = 0; i < args.Count; ++i)
          group.Add(new AssignStmt(target.Position,
            new MemberExpr(target.Position, new MemberExpr(target.Position, target, "$" + duCase.CaseName, TypeSuffix.None), duCase.Fields[i].Name, TypeSuffix.None),
            args[i]));
        return new StatementGroup(target.Position, group);
      }
    }
    return new AssignStmt(target.Position, target, assigned);
  }

  /// <summary>Binary operator behind a compound-assignment token (when followed by '='), else null.</summary>
  private static BinaryOp? CompoundOp(TokenKind kind) => kind switch {
    TokenKind.Plus => BinaryOp.Add,
    TokenKind.Minus => BinaryOp.Subtract,
    TokenKind.Star => BinaryOp.Multiply,
    TokenKind.Slash => BinaryOp.Divide,
    TokenKind.Backslash => BinaryOp.IntegerDivide,
    TokenKind.Caret => BinaryOp.Power,
    TokenKind.Ampersand => BinaryOp.Concat,
    TokenKind.Pipe => BinaryOp.Or,
    TokenKind.ShiftLeft or TokenKind.ShiftLeftLogical => BinaryOp.ShiftLeft,
    TokenKind.ShiftRight => BinaryOp.ShiftRightArith,
    TokenKind.ShiftRightLogical => BinaryOp.ShiftRightLogical,
    TokenKind.RotateLeft => BinaryOp.RotateLeft,
    TokenKind.RotateRight => BinaryOp.RotateRight,
    _ => null,
  };

  private static bool IsShiftRotateToken(TokenKind kind) => kind
    is TokenKind.Pipe or TokenKind.ShiftLeft or TokenKind.ShiftLeftLogical
    or TokenKind.ShiftRight or TokenKind.ShiftRightLogical or TokenKind.RotateLeft or TokenKind.RotateRight;

  #endregion
}

/// <summary>Raised when the token stream violates PowerBASIC 3.5 grammar.</summary>
public sealed class ParserException(string message, SourcePosition position) : Exception($"{position}: {message}") {
  public SourcePosition Position { get; } = position;
}
