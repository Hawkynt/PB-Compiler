using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Syntax;

/// <summary>Calls, simple mutators (INCR/SWAP/MID$/LSET), graphics statements and generic commands.</summary>
public sealed partial class Parser {

  private Statement ParseCall() {
    var pos = this.Advance().Position;
    var name = this.Expect(TokenKind.Identifier, "SUB name");
    var upper = name.Text.ToUpperInvariant();

    // CALL DWORD ptr32 [BDECL|CDECL|SDECL] (args) - far call through a pointer
    if (upper == "DWORD") {
      this.Require(LanguageFeature.CodePointers);
      var pointer = this.ParseExpression();
      string? convention = null;
      if (this.Current.Kind == TokenKind.Identifier && this.Current.Text.ToUpperInvariant() is "BDECL" or "CDECL" or "SDECL") {
        convention = this.Current.Text.ToUpperInvariant();
        this.Advance();
      }
      var ptrArgs = this.Current.Kind == TokenKind.LParen ? this.ParseArgumentList() : [];
      return new CallPtrStmt(pos, pointer, convention, ptrArgs);
    }

    // CALL INTERRUPT n / CALL ABSOLUTE addr are compiler services, not user SUBs
    if (upper is "INTERRUPT" or "ABSOLUTE") {
      var serviceArgs = new List<Expression?>();
      if (!this.IsStatementEnd())
        do
          serviceArgs.Add(this.ParseExpression());
        while (this.Match(TokenKind.Comma));
      return new CommandStmt(pos, upper, serviceArgs);
    }

    var arguments = this.Current.Kind == TokenKind.LParen ? this.ParseArgumentList() : [];
    return new CallStmt(pos, name.Text, arguments, true);
  }

  /// <summary>GET$ [#]fh, count, strvar / PUT$ [#]fh, strvar - string-file I/O statements.</summary>
  private Statement ParseGetPutString(string keyword) {
    var pos = this.Advance().Position;
    this.Match(TokenKind.Hash);
    var arguments = new List<Expression?> { this.ParseExpression() };
    while (this.Match(TokenKind.Comma))
      arguments.Add(this.Current.Kind == TokenKind.Comma ? null : this.ParseExpression());
    return new CommandStmt(pos, keyword + "$", arguments);
  }

  /// <summary>Identifier-led fallback: <c>Name (args)</c> or <c>Name a, b</c> or bare <c>Name</c>.</summary>
  private Statement ParseBareCall() {
    var name = this.Advance();

    // pb36 generics: explicit type arguments on a statement call, Name OF type(args) / Name OF type args
    if (name.Suffix == TypeSuffix.None && this.IsKeyword(0, "OF")) {
      var typeArgs = this.TryParseTypeArguments();
      if (this.Current.Kind == TokenKind.LParen && this.ParenthesesEndStatement())
        return new CallStmt(name.Position, name.Text, this.ParseArgumentList(), false, typeArgs);
      var typedArgs = new List<Expression>();
      if (!this.IsStatementEnd())
        do
          typedArgs.Add(this.ParseArgument());
        while (this.Match(TokenKind.Comma));
      return new CallStmt(name.Position, name.Text, typedArgs, false, typeArgs);
    }

    // pb36 member-call statement: receiver.Member(args) / receiver.Member args
    if (this.Current.Kind == TokenKind.Period && DialectFacts.IsAvailable(LanguageFeature.TypeMethods, this._dialect)) {
      var chain = this.ParsePostfix(new NameExpr(name.Position, name.Text, name.Suffix));
      if (chain is IndexExpr { Target: MemberExpr parenthesized } indexed)   // receiver.Member(args)
        return new MemberCallStmt(name.Position, parenthesized.Target, parenthesized.Member, indexed.Arguments);
      if (chain is MemberExpr bare) {                                        // receiver.Member [args]
        var bareArgs = new List<Expression>();
        if (!this.IsStatementEnd())
          do
            bareArgs.Add(this.ParseArgument());
          while (this.Match(TokenKind.Comma));
        return new MemberCallStmt(name.Position, bare.Target, bare.Member, bareArgs);
      }
      throw this.Error("a member statement must be a method call");
    }

    if (this.Current.Kind == TokenKind.LParen && this.ParenthesesEndStatement())
      return new CallStmt(name.Position, name.Text, this.ParseArgumentList(), false);

    var arguments = new List<Expression>();
    if (!this.IsStatementEnd())
      do
        arguments.Add(this.ParseArgument());
      while (this.Match(TokenKind.Comma));
    return new CallStmt(name.Position, name.Text, arguments, false);
  }

  private Statement ParseIncrDecr(bool increment) {
    var pos = this.Advance().Position;
    var target = this.ParseLValue();
    var amount = this.Match(TokenKind.Comma) ? this.ParseExpression() : null;
    return new IncrDecrStmt(pos, increment, target, amount);
  }

  private Statement ParseSwap() {
    var pos = this.Advance().Position;
    var left = this.ParseLValue();
    this.Expect(TokenKind.Comma, "','");
    return new SwapStmt(pos, left, this.ParseLValue());
  }

  /// <summary>BIT SET/RESET/TOGGLE var, bit-number (PB 3.0).</summary>
  private Statement ParseBit() {
    var pos = this.Advance().Position; // BIT
    var op = this.Advance().Text.ToUpperInvariant() switch {
      "SET" => BitOp.Set,
      "RESET" => BitOp.Reset,
      _ => BitOp.Toggle,
    };
    var target = this.ParseLValue();
    this.Expect(TokenKind.Comma, "','");
    return new BitStmt(pos, op, target, this.ParseExpression());
  }

  /// <summary>ARRAY SORT / ARRAY SCAN (PB 3.5) - see <see cref="ArraySortStmt"/>/<see cref="ArrayScanStmt"/>.</summary>
  private Statement ParseArrayStatement() {
    var pos = this.Advance().Position; // ARRAY
    var isScan = this.Advance().Text.Equals("SCAN", StringComparison.OrdinalIgnoreCase);

    var name = this.Expect(TokenKind.Identifier, "array name");
    this.Expect(TokenKind.LParen, "'('");
    var start = new List<Expression>();
    if (this.Current.Kind != TokenKind.RParen)
      start.Add(this.ParseExpression());
    this.Expect(TokenKind.RParen, "')'");
    var arrayRef = new CallOrIndexExpr(name.Position, name.Text, name.Suffix, start);

    Expression? count = null;
    if (this.TryMatchKeyword("FOR"))
      count = this.ParseExpression();

    Expression? fromPos = null;
    Expression? toPos = null;
    Expression? collate = null;
    var descend = false;
    CallOrIndexExpr? tagArray = null;
    CaseComparison? scanOp = null;
    Expression? match = null;
    Expression? scanTarget = null;

    while (this.Match(TokenKind.Comma)) {
      if (this.TryMatchKeyword("FROM")) {
        fromPos = this.ParseExpression();
        this.ExpectKeyword("TO");
        toPos = this.ParseExpression();
      } else if (this.TryMatchKeyword("COLLATE"))
        collate = this.ParseExpression();
      else if (this.TryMatchKeyword("ASCEND"))
        descend = false;
      else if (this.TryMatchKeyword("DESCEND"))
        descend = true;
      else if (this.TryMatchKeyword("TAGARRAY")) {
        var tag = this.Expect(TokenKind.Identifier, "tag array name");
        var tagStart = new List<Expression>();
        if (this.Match(TokenKind.LParen)) {
          if (this.Current.Kind != TokenKind.RParen)
            tagStart.Add(this.ParseExpression());
          this.Expect(TokenKind.RParen, "')'");
        }
        tagArray = new(tag.Position, tag.Text, tag.Suffix, tagStart);
      } else if (isScan && this.TryMatchKeyword("TO"))
        scanTarget = this.ParseLValue();
      else if (isScan) {
        scanOp = this.Current.Kind switch {
          TokenKind.Equals => CaseComparison.Equal,
          TokenKind.NotEquals => CaseComparison.NotEqual,
          TokenKind.Less => CaseComparison.Less,
          TokenKind.LessEquals => CaseComparison.LessEqual,
          TokenKind.Greater => CaseComparison.Greater,
          TokenKind.GreaterEquals => CaseComparison.GreaterEqual,
          _ => throw this.Error("expected comparison operator in ARRAY SCAN"),
        };
        this.Advance();
        match = this.ParseExpression();
      } else
        throw this.Error($"unexpected '{this.Current.Text}' in ARRAY SORT");
    }

    if (!isScan)
      return new ArraySortStmt(pos, arrayRef, count, fromPos, toPos, collate, descend, tagArray);

    if (scanOp == null || match == null || scanTarget == null)
      throw this.Error("ARRAY SCAN needs 'relop expr, TO var'");
    return new ArrayScanStmt(pos, arrayRef, count, fromPos, toPos, collate, scanOp.Value, match, scanTarget);
  }

  private Statement ParseMidAssign() {
    var pos = this.Advance().Position; // MID$
    this.Expect(TokenKind.LParen, "'('");
    var target = this.ParseLValue();
    this.Expect(TokenKind.Comma, "','");
    var start = this.ParseExpression();
    var length = this.Match(TokenKind.Comma) ? this.ParseExpression() : null;
    this.Expect(TokenKind.RParen, "')'");
    this.Expect(TokenKind.Equals, "'='");
    return new MidAssignStmt(pos, target, start, length, this.ParseExpression());
  }

  /// <summary>
  /// ASC(s$, position) = code statement form (PB 3.5). The position is
  /// mandatory - genuine PBC 3.50 rejects <c>ASC(s$) = code</c> with
  /// <c>Error 411: "," expected</c>.
  /// </summary>
  private Statement ParseAscAssign() {
    this.Require(LanguageFeature.AscStatement);
    var pos = this.Advance().Position; // ASC
    this.Expect(TokenKind.LParen, "'('");
    var target = this.ParseLValue();
    this.Expect(TokenKind.Comma, "','");
    var index = this.ParseExpression();
    this.Expect(TokenKind.RParen, "')'");
    this.Expect(TokenKind.Equals, "'='");
    return new AscAssignStmt(pos, target, index, this.ParseExpression());
  }

  /// <summary>STDOUT [expr] [;] - DOS handle 1 output (PB 3.5).</summary>
  private Statement ParseStdOut() {
    this.Require(LanguageFeature.StdInOut);
    var pos = this.Advance().Position;
    Expression? value = null;
    if (!this.IsStatementEnd() && this.Current.Kind != TokenKind.Semicolon)
      value = this.ParseExpression();
    var noNewline = this.Match(TokenKind.Semicolon);
    return new StdOutStmt(pos, value, noNewline);
  }

  /// <summary>STDIN n, s$ / STDIN LINE, s$ - DOS handle 0 input (PB 3.5).</summary>
  private Statement ParseStdIn() {
    this.Require(LanguageFeature.StdInOut);
    var pos = this.Advance().Position;
    if (this.TryMatchKeyword("LINE")) {
      this.Expect(TokenKind.Comma, "','");
      return new StdInStmt(pos, Line: true, null, this.ParseLValue());
    }
    var count = this.ParseExpression();
    this.Expect(TokenKind.Comma, "','");
    return new StdInStmt(pos, Line: false, count, this.ParseLValue());
  }

  /// <summary>SETEOF [#]n - truncate the file at the current position (PB 3.5).</summary>
  private Statement ParseSetEof() {
    this.Require(LanguageFeature.SetEof);
    var pos = this.Advance().Position;
    return new CommandStmt(pos, "SETEOF", [this.ParseFileNumber()]);
  }

  private Statement ParseLsetRset(bool isLeft) {
    var pos = this.Advance().Position;
    var target = this.ParseLValue();
    this.Expect(TokenKind.Equals, "'='");
    return new LsetRsetStmt(pos, isLeft, target, this.ParseExpression());
  }

  /// <summary>SHIFT/ROTATE LEFT|RIGHT lvalue, count - kept as a CommandStmt with the direction in the keyword.</summary>
  private Statement ParseShiftRotate(string keyword) {
    var pos = this.Advance().Position;
    var direction = this.Expect(TokenKind.Identifier, "LEFT or RIGHT").Text.ToUpperInvariant();
    if (direction is not ("LEFT" or "RIGHT"))
      throw this.Error($"expected LEFT or RIGHT after {keyword}, found '{direction}'");

    var target = this.ParseLValue();
    this.Expect(TokenKind.Comma, "','");
    return new CommandStmt(pos, $"{keyword} {direction}", [target, this.ParseExpression()]);
  }

  #region graphics

  private Statement ParsePset(bool isPreset) {
    var pos = this.Advance().Position;
    var point = this.ParsePoint();
    var color = this.Match(TokenKind.Comma) ? this.ParseExpression() : null;
    return new PsetStmt(pos, isPreset, point, color);
  }

  /// <summary>Graphics LINE (the LINE INPUT form is routed away in <see cref="ParseLine"/>).</summary>
  private Statement ParseLine() {
    // LINE INPUT ... / LINE INPUT# 1, ... (the '#' lexes as a Double suffix on INPUT)
    if (this.Peek() is { Kind: TokenKind.Identifier, Suffix: TypeSuffix.None or TypeSuffix.Double } next
        && next.Text.Equals("INPUT", StringComparison.OrdinalIgnoreCase)) {
      this.Advance(); // LINE
      return this.ParseInput(isLineInput: true);
    }

    var pos = this.Advance().Position;
    (Expression X, Expression Y)? from = null;
    if (this.Current.Kind == TokenKind.LParen)
      from = this.ParsePoint();
    this.Expect(TokenKind.Minus, "'-'");
    var to = this.ParsePoint();

    Expression? color = null;
    var box = false;
    var fill = false;
    Expression? style = null;
    if (this.Match(TokenKind.Comma)) {
      if (this.Current.Kind != TokenKind.Comma && !this.IsStatementEnd())
        color = this.ParseExpression();
      if (this.Match(TokenKind.Comma)) {
        if (this.Current.Kind != TokenKind.Comma && !this.IsStatementEnd()) {
          var flag = this.Expect(TokenKind.Identifier, "B or BF").Text.ToUpperInvariant();
          if (flag is not ("B" or "BF"))
            throw this.Error($"expected B or BF, found '{flag}'");
          box = true;
          fill = flag == "BF";
        }
        if (this.Match(TokenKind.Comma))
          style = this.ParseExpression();
      }
    }
    return new LineStmt(pos, from, to, color, box, fill, style);
  }

  private Statement ParseCircle() {
    var pos = this.Advance().Position;
    var center = this.ParsePoint();
    this.Expect(TokenKind.Comma, "','");
    var radius = this.ParseExpression();

    var options = new Expression?[4]; // color, start, end, aspect
    for (var i = 0; i < options.Length && this.Match(TokenKind.Comma); ++i)
      if (this.Current.Kind != TokenKind.Comma && !this.IsStatementEnd())
        options[i] = this.ParseExpression();
    return new CircleStmt(pos, center, radius, options[0], options[1], options[2], options[3]);
  }

  private Statement ParseGetPutGraphics(bool isGet) {
    var pos = this.Advance().Position;
    var first = this.ParsePoint();
    (Expression X, Expression Y)? to = null;
    if (this.Match(TokenKind.Minus))
      to = this.ParsePoint();
    this.Expect(TokenKind.Comma, "','");
    var array = this.ParseExpression();
    string? verb = null;
    if (this.Match(TokenKind.Comma))
      verb = this.Expect(TokenKind.Identifier, "action verb").Text.ToUpperInvariant();
    return new GetPutGraphicsStmt(pos, isGet, first, to, array, verb);
  }

  #endregion

  /// <summary>
  /// Generic keyword command: comma-separated optional expressions (omitted = null);
  /// coordinate pairs <c>(x, y)</c> are flattened into two arguments and a <c>-</c>
  /// between pairs is treated as a separator (VIEW/WINDOW box syntax).
  /// </summary>
  private Statement ParseCommand(string keyword) {
    var pos = this.Advance().Position;

    if (keyword == "NAME") { // NAME old$ AS new$
      var oldName = this.ParseExpression();
      this.ExpectKeyword("AS");
      return new CommandStmt(pos, keyword, [oldName, this.ParseExpression()]);
    }

    // POKE[I|L] [seg:]offset, value - the pb36 seg:offset form carries the segment inline (a 3-arg
    // CommandStmt [seg, offset, value]); it sets DEF SEG = seg before the write, like the classic
    // DEF SEG / POKE pair. The ':' is only special here, so it never clashes with the statement ':'.
    if (keyword is "POKE" or "POKEI" or "POKEL") {
      var addr = this.ParseExpression();
      if (this.Current.Kind == TokenKind.Colon) {
        this.Require(LanguageFeature.SegmentedPeekPoke);
        this.Advance();
        var off = this.ParseExpression();
        this.Expect(TokenKind.Comma, "','");
        return new CommandStmt(pos, keyword, [addr, off, this.ParseExpression()]);
      }
      this.Expect(TokenKind.Comma, "','");
      return new CommandStmt(pos, keyword, [addr, this.ParseExpression()]);
    }

    if (keyword == "VIEW" && this.TryMatchKeyword("SCREEN"))
      keyword = "VIEW SCREEN";
    else if (keyword == "VIEW" && this.TryMatchKeyword("TEXT"))
      keyword = "VIEW TEXT";
    else if (keyword == "VIEW" && this.TryMatchKeyword("PRINT"))
      keyword = "VIEW PRINT";
    else if (keyword == "PALETTE" && this.TryMatchKeyword("USING"))
      keyword = "PALETTE USING";

    var arguments = new List<Expression?>();
    if (this.IsStatementEnd())
      return new CommandStmt(pos, keyword, arguments);

    for (;;) {
      if (this.Current.Kind == TokenKind.Comma || this.IsStatementEnd())
        arguments.Add(null);
      else if (this.IsPointAhead()) {
        var (x, y) = this.ParsePoint();
        arguments.Add(x);
        arguments.Add(y);
      } else
        arguments.Add(this.ParseExpression());

      if (this.Match(TokenKind.Comma))
        continue;
      if (this.Current.Kind == TokenKind.Minus && this.Peek().Kind == TokenKind.LParen) {
        this.Advance();
        continue;
      }
      return new CommandStmt(pos, keyword, arguments);
    }
  }
}
