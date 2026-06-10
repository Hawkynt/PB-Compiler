using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Syntax;

/// <summary>Control flow: IF, SELECT CASE, loops, jumps, error handling and event statements.</summary>
public sealed partial class Parser {

  private Statement ParseIf() {
    var pos = this.Advance().Position;
    var condition = this.ParseExpression();

    if (this.TryMatchKeyword("GOTO")) {
      var target = this.ParseLabelReference();
      var elseBody = this.TryMatchKeyword("ELSE") ? this.ParseInlineBody() : null;
      return new IfStmt(pos, condition, [new GotoStmt(pos, target)], [], elseBody);
    }

    this.ExpectKeyword("THEN");
    if (this.Current.Kind is not (TokenKind.EndOfLine or TokenKind.EndOfFile))
      return this.ParseSingleLineIf(pos, condition);

    var then = this.ParseBody("ELSEIF", "ELSE", "END IF");
    var elseIfs = new List<(Expression Condition, IReadOnlyList<Statement> Body)>();
    while (this.TryMatchKeyword("ELSEIF")) {
      var elseIfCondition = this.ParseExpression();
      this.ExpectKeyword("THEN");
      elseIfs.Add((elseIfCondition, this.ParseBody("ELSEIF", "ELSE", "END IF")));
    }

    List<Statement>? elseBlock = null;
    if (this.TryMatchKeyword("ELSE"))
      elseBlock = this.ParseBody("END IF");

    this.ExpectKeyword("END");
    this.ExpectKeyword("IF");
    return new IfStmt(pos, condition, then, elseIfs, elseBlock);
  }

  private Statement ParseSingleLineIf(SourcePosition pos, Expression condition) {
    var then = this.ParseInlineBody();
    var elseBody = this.TryMatchKeyword("ELSE") ? this.ParseInlineBody() : null;
    return new IfStmt(pos, condition, then, [], elseBody);
  }

  /// <summary>Parses colon-separated statements on the current line, stopping at end of line or ELSE.</summary>
  private List<Statement> ParseInlineBody() {
    var result = new List<Statement>();
    for (;;) {
      while (this.Current.Kind == TokenKind.Colon)
        ++this._pos;
      if (this.Current.Kind is TokenKind.EndOfLine or TokenKind.EndOfFile || this.IsKeyword(0, "ELSE"))
        return result;

      // THEN 100 / ELSE 100 shorthand for GOTO line-number
      if (result.Count == 0 && this.Current.Kind == TokenKind.IntegerLiteral) {
        var token = this.Advance();
        result.Add(new GotoStmt(token.Position, token.IntegerValue.ToString()));
        continue;
      }

      result.Add(this.ParseStatement());
    }
  }

  private Statement ParseSelect() {
    var pos = this.Advance().Position;
    this.ExpectKeyword("CASE");
    var subject = this.ParseExpression();

    var arms = new List<CaseArm>();
    for (;;) {
      this.SkipSeparators();
      if (this.IsAtTerminator("END SELECT")) {
        this.Advance();
        this.Advance();
        break;
      }
      if (this.Current.Kind == TokenKind.EndOfFile)
        throw this.Error("unexpected end of file, expected END SELECT");

      var armPos = this.Current.Position;
      this.ExpectKeyword("CASE");
      var selectors = new List<CaseSelector>();
      if (!this.TryMatchKeyword("ELSE"))
        do
          selectors.Add(this.ParseCaseSelector());
        while (this.Match(TokenKind.Comma));
      arms.Add(new(armPos, selectors, this.ParseBody("CASE", "END SELECT")));
    }
    return new SelectStmt(pos, subject, arms);
  }

  private CaseSelector ParseCaseSelector() {
    var pos = this.Current.Position;
    if (this.TryMatchKeyword("IS")) {
      var comparison = this.Current.Kind switch {
        TokenKind.Equals => CaseComparison.Equal,
        TokenKind.NotEquals => CaseComparison.NotEqual,
        TokenKind.Less => CaseComparison.Less,
        TokenKind.LessEquals => CaseComparison.LessEqual,
        TokenKind.Greater => CaseComparison.Greater,
        TokenKind.GreaterEquals => CaseComparison.GreaterEqual,
        _ => throw this.Error("expected comparison operator after IS"),
      };
      this.Advance();
      return new(pos, this.ParseExpression(), null, comparison);
    }

    var value = this.ParseExpression();
    return this.TryMatchKeyword("TO")
      ? new(pos, value, this.ParseExpression(), null)
      : new(pos, value, null, null);
  }

  private Statement ParseFor() {
    var pos = this.Advance().Position;
    var variable = this.ParseLValue();
    this.Expect(TokenKind.Equals, "'='");
    var from = this.ParseExpression();
    this.ExpectKeyword("TO");
    var to = this.ParseExpression();
    var step = this.TryMatchKeyword("STEP") ? this.ParseExpression() : null;
    var body = this.ParseBody("NEXT");
    this.ConsumeNext();
    return new ForStmt(pos, variable, from, to, step, body);
  }

  /// <summary>Consumes a NEXT terminator; <c>NEXT a, b</c> closes additional enclosing FORs.</summary>
  private void ConsumeNext() {
    if (this._pendingNexts > 0) {
      --this._pendingNexts;
      return;
    }

    this.ExpectKeyword("NEXT");
    if (this.Current.Kind != TokenKind.Identifier)
      return;

    this.Advance();
    while (this.Match(TokenKind.Comma)) {
      this.Expect(TokenKind.Identifier, "loop variable");
      ++this._pendingNexts;
    }
  }

  private Statement ParseDo() {
    var pos = this.Advance().Position;
    var (preTest, preCondition) = this.ParseLoopTest();
    var body = this.ParseBody("LOOP");
    this.ExpectKeyword("LOOP");
    var (postTest, postCondition) = this.ParseLoopTest();
    return new DoLoopStmt(pos, preTest, preCondition, postTest, postCondition, body);
  }

  private (LoopTestKind Kind, Expression? Condition) ParseLoopTest() {
    if (this.TryMatchKeyword("WHILE"))
      return (LoopTestKind.While, this.ParseExpression());
    if (this.TryMatchKeyword("UNTIL"))
      return (LoopTestKind.Until, this.ParseExpression());
    return (LoopTestKind.None, null);
  }

  private Statement ParseWhile() {
    var pos = this.Advance().Position;
    var condition = this.ParseExpression();
    var body = this.ParseBody("WEND");
    this.ExpectKeyword("WEND");
    return new DoLoopStmt(pos, LoopTestKind.While, condition, LoopTestKind.None, null, body);
  }

  private Statement ParseExit() {
    var pos = this.Advance().Position;
    var token = this.Expect(TokenKind.Identifier, "EXIT kind");
    var kind = token.Text.ToUpperInvariant() switch {
      "FOR" => ExitKind.For,
      "DO" => ExitKind.Do,
      "LOOP" => ExitKind.Loop,
      "SUB" => ExitKind.Sub,
      "FUNCTION" => ExitKind.Function,
      "DEF" => ExitKind.Def,
      "SELECT" => ExitKind.Select,
      "IF" => ExitKind.If,
      _ => throw new ParserException($"cannot EXIT '{token.Text}'", token.Position),
    };
    return new ExitStmt(pos, kind);
  }

  private string ParseLabelReference() {
    var token = this.Current;
    switch (token.Kind) {
      case TokenKind.Identifier:
        this.Advance();
        return token.Text;
      case TokenKind.IntegerLiteral:
        this.Advance();
        return token.IntegerValue.ToString();
      default:
        throw this.Error($"expected label or line number, found '{token.Text}'");
    }
  }

  private Statement ParseReturn() {
    var pos = this.Advance().Position;
    return new ReturnStmt(pos, this.IsStatementEnd() ? null : this.ParseLabelReference());
  }

  private Statement ParseOn() {
    var pos = this.Advance().Position;

    if (this.TryMatchKeyword("ERROR")) {
      if (this.TryMatchKeyword("RESUME")) {
        this.ExpectKeyword("NEXT");
        return new OnErrorStmt(pos, null, true);
      }
      this.ExpectKeyword("GOTO");
      var errorTarget = this.ParseLabelReference();
      return new OnErrorStmt(pos, errorTarget == "0" ? null : errorTarget, false);
    }

    if (this.Current.Kind == TokenKind.Identifier && _eventKinds.Contains(this.Current.Text)
        && (this.Peek().Kind == TokenKind.LParen || this.IsKeyword(1, "GOSUB"))) {
      var kind = this.Advance().Text.ToUpperInvariant();
      Expression? index = null;
      if (this.Match(TokenKind.LParen)) {
        index = this.ParseExpression();
        this.Expect(TokenKind.RParen, "')'");
      }
      this.ExpectKeyword("GOSUB");
      return new OnEventStmt(pos, kind, index, this.ParseLabelReference());
    }

    var selector = this.ParseExpression();
    bool isGosub;
    if (this.TryMatchKeyword("GOTO"))
      isGosub = false;
    else {
      this.ExpectKeyword("GOSUB");
      isGosub = true;
    }

    var targets = new List<string>();
    do
      targets.Add(this.ParseLabelReference());
    while (this.Match(TokenKind.Comma));
    return new OnGotoStmt(pos, selector, isGosub, targets);
  }

  private Statement ParseResume() {
    var pos = this.Advance().Position;
    if (this.TryMatchKeyword("NEXT"))
      return new ResumeStmt(pos, ResumeKind.Next, null);
    if (this.IsStatementEnd())
      return new ResumeStmt(pos, ResumeKind.SameStatement, null);

    var target = this.ParseLabelReference();
    return target == "0"
      ? new ResumeStmt(pos, ResumeKind.SameStatement, null)
      : new ResumeStmt(pos, ResumeKind.Label, target);
  }

  private Statement ParseEnd() {
    var token = this.Advance();
    if (this.Current.Kind == TokenKind.Identifier && _structuralEndKeywords.Contains(this.Current.Text))
      throw new ParserException($"unexpected END {this.Current.Text.ToUpperInvariant()}", token.Position);
    return new EndStmt(token.Position, this.IsStatementEnd() ? null : this.ParseExpression());
  }

  private Statement ParseProgramEnd() {
    var pos = this.Advance().Position; // STOP / SYSTEM
    return new EndStmt(pos, this.IsStatementEnd() ? null : this.ParseExpression());
  }

  /// <summary>KEY(n) ON / TIMER OFF / ... event arming, otherwise the generic command form (KEY n, s$ / PLAY s$).</summary>
  private Statement ParseEventOrCommand(string keyword) {
    var pos = this.Current.Position;

    if (this.Peek().Kind == TokenKind.LParen) {
      var i = this._pos + 1;
      if (this.TrySkipBalancedParens(ref i) && this.TokenAt(i) is { Kind: TokenKind.Identifier } follow && IsEventMode(follow.Text)) {
        this.Advance();
        this.Expect(TokenKind.LParen, "'('");
        var index = this.ParseExpression();
        this.Expect(TokenKind.RParen, "')'");
        return new EventControlStmt(pos, keyword, index, this.Advance().Text.ToUpperInvariant());
      }
    } else if (this.Peek().Kind == TokenKind.Identifier && IsEventMode(this.Peek().Text)) {
      this.Advance();
      return new EventControlStmt(pos, keyword, null, this.Advance().Text.ToUpperInvariant());
    }

    return this.ParseCommand(keyword);
  }

  private static bool IsEventMode(string text) =>
    text.Equals("ON", StringComparison.OrdinalIgnoreCase)
    || text.Equals("OFF", StringComparison.OrdinalIgnoreCase)
    || text.Equals("STOP", StringComparison.OrdinalIgnoreCase)
    || text.Equals("LIST", StringComparison.OrdinalIgnoreCase);
}
