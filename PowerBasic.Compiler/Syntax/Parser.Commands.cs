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
    if (this.Current.Kind == TokenKind.LParen && this.ParenthesesEndStatement())
      return new CallStmt(name.Position, name.Text, this.ParseArgumentList(), false);

    var arguments = new List<Expression>();
    if (!this.IsStatementEnd())
      do
        arguments.Add(this.ParseExpression());
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
    if (this.IsKeyword(1, "INPUT")) {
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

    if (keyword == "VIEW" && this.TryMatchKeyword("SCREEN"))
      keyword = "VIEW SCREEN";
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
