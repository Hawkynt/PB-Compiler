using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Syntax;

/// <summary>Control flow: IF, SELECT CASE, loops, jumps, error handling and event statements.</summary>
public sealed partial class Parser {

  private Statement ParseIf() {
    var pos = this.Advance().Position;
    var condition = this.ParseExpression();
    // IS payload bindings from the condition are taken NOW - the body statements parsed below
    // must not adopt them - and hoisted as DIM + unconditional payload copy before the IF
    var bindings = this.TakePatternBindings();

    if (this.TryMatchKeyword("GOTO")) {
      var target = this.ParseLabelReference();
      var elseBody = this.TryMatchKeyword("ELSE") ? this.ParseInlineBody() : null;
      return WrapBindings(bindings, new IfStmt(pos, condition, [new GotoStmt(pos, target)], [], elseBody));
    }

    this.ExpectKeyword("THEN");
    if (this.Current.Kind is not (TokenKind.EndOfLine or TokenKind.EndOfFile))
      return WrapBindings(bindings, this.ParseSingleLineIf(pos, condition));

    this.Require(LanguageFeature.BlockIf);
    var then = this.ParseBody("ELSEIF", "ELSE", "END IF");
    var elseIfs = new List<(Expression Condition, IReadOnlyList<Statement> Body)>();
    while (this.TryMatchKeyword("ELSEIF")) {
      var elseIfCondition = this.ParseExpression();
      bindings.AddRange(this.TakePatternBindings());
      this.ExpectKeyword("THEN");
      elseIfs.Add((elseIfCondition, this.ParseBody("ELSEIF", "ELSE", "END IF")));
    }

    List<Statement>? elseBlock = null;
    if (this.TryMatchKeyword("ELSE"))
      elseBlock = this.ParseBody("END IF");

    this.ExpectKeyword("END");
    this.ExpectKeyword("IF");
    return WrapBindings(bindings, new IfStmt(pos, condition, then, elseIfs, elseBlock));
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

      if (this.ParseInlineStatement() is var parsed && parsed is StatementGroup group)   // hoisted IS bindings splice inline
        result.AddRange(group.Statements);
      else
        result.Add(parsed);
    }
  }

  /// <summary>
  /// BASICA/GW-BASIC store a line without necessarily validating every statement on it. If an
  /// inline branch contains text this compiler cannot parse, preserve that text as a deferred node.
  /// Code generation may discard it only after proving the branch unreachable; otherwise it emits
  /// a hard diagnostic instead of silently inventing semantics.
  /// </summary>
  private Statement ParseInlineStatement() {
    if (!this._dialect.IsGwBasica())
      return this.ParseStatement();

    var start = this._pos;
    var position = this.Current.Position;
    var atLineStart = this._atLineStart;
    var pendingNexts = this._pendingNexts;
    var patternBindingCount = this._patternBindings.Count;
    var withSubjectCount = this._withSubjects.Count;
    try {
      return this.ParseStatement();
    } catch (ParserException) {
      this._pos = start;
      this._atLineStart = atLineStart;
      this._pendingNexts = pendingNexts;
      if (this._patternBindings.Count > patternBindingCount)
        this._patternBindings.RemoveRange(patternBindingCount, this._patternBindings.Count - patternBindingCount);
      if (this._withSubjects.Count > withSubjectCount)
        this._withSubjects.RemoveRange(withSubjectCount, this._withSubjects.Count - withSubjectCount);

      var text = new System.Text.StringBuilder();
      while (!this.IsStatementEnd()) {
        if (text.Length > 0)
          text.Append(' ');
        text.Append(this.Advance().Text);
      }
      if (text.Length == 0)
        throw;
      return new DeferredSourceStmt(position, text.ToString());
    }
  }

  private Statement ParseSelect() {
    this.Require(LanguageFeature.SelectCase);
    var pos = this.Advance().Position;
    this.ExpectKeyword("CASE");
    var subject = this.ParseExpression();
    var bindings = this.TakePatternBindings();   // a ternary in the subject may have bound already

    // pb36 discriminated-union matching: CASE Tag [bindVar] arms compare the hidden tag; the
    // subject rewrites to subject.$tag and each binding hoists as DIM + payload copy before
    // the SELECT (unconditional copies - harmless for the arms not taken)
    var patternArms = 0;
    var plainArms = 0;
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
        do {
          if (this.Current.Kind == TokenKind.Identifier && this._duCases.TryGetValue(this.Current.Text, out var duCase)) {
            this.Require(LanguageFeature.DiscriminatedUnions);
            var caseTok = this.Advance();
            selectors.Add(new(caseTok.Position, new IntegerLiteralExpr(caseTok.Position, duCase.Index, TypeSuffix.None), null, null));
            if (this.Current.Kind == TokenKind.Identifier && !this.IsKeyword(0, "TO"))
              bindings.AddRange(this.BuildPatternBinding(this.Advance(), subject, duCase));
            ++patternArms;
          } else {
            selectors.Add(this.ParseCaseSelector());
            ++plainArms;
          }
        } while (this.Match(TokenKind.Comma));
      arms.Add(new(armPos, selectors, this.ParseBody("CASE", "END SELECT")));
    }

    if (patternArms > 0 && plainArms > 0)
      throw this.Error("a SELECT CASE cannot mix union-case patterns with ordinary selectors");
    if (patternArms > 0)
      subject = new MemberExpr(pos, subject, "$tag", TypeSuffix.None);
    return WrapBindings(bindings, new SelectStmt(pos, subject, arms));
  }

  private CaseSelector ParseCaseSelector() {
    var pos = this.Current.Position;

    // CASE = 34 / CASE > x - the IS keyword is optional before a relation
    if (this.Current.Kind is TokenKind.Equals or TokenKind.NotEquals or TokenKind.Less or TokenKind.LessEquals or TokenKind.Greater or TokenKind.GreaterEquals) {
      var bare = this.Current.Kind switch {
        TokenKind.Equals => CaseComparison.Equal,
        TokenKind.NotEquals => CaseComparison.NotEqual,
        TokenKind.Less => CaseComparison.Less,
        TokenKind.LessEquals => CaseComparison.LessEqual,
        TokenKind.Greater => CaseComparison.Greater,
        _ => CaseComparison.GreaterEqual,
      };
      this.Advance();
      return new(pos, this.ParseExpression(), null, bare);
    }

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

  private int _foreachCounter;

  private Statement ParseFor() {
    var pos = this.Advance().Position;
    // FOR EACH v IN <collection> (EACH followed by the loop variable, not '=', so a
    // loop over a variable literally named "each" is unaffected)
    if (this.IsKeyword(0, "EACH") && this.Peek().Kind != TokenKind.Equals)
      return this.ParseForEach(pos);
    var variable = this.ParseLValue();
    this.Expect(TokenKind.Equals, "'='");
    var from = this.WithoutPatternBindings(this.ParseExpression);
    this.ExpectKeyword("TO");
    var to = this.WithoutPatternBindings(this.ParseExpression);
    var step = this.TryMatchKeyword("STEP") ? this.WithoutPatternBindings(this.ParseExpression) : null;
    var body = this.ParseBody("NEXT");
    this.ConsumeNext();
    return new ForStmt(pos, variable, from, to, step, body);
  }

  /// <summary>
  /// PB 3.6 <c>FOR EACH v IN source ... NEXT</c>, desugared to a counted FOR:
  /// a <c>[lo TO hi]</c> range literal becomes <c>FOR v = lo TO hi</c>; an array
  /// becomes <c>FOR i = LBOUND(a) TO UBOUND(a) : v = a(i) : ...</c> with a hidden
  /// index whose name (containing '$') can never collide with a source identifier.
  /// </summary>
  private Statement ParseForEach(SourcePosition pos) {
    this.Require(LanguageFeature.ForEach);
    this.Advance(); // EACH
    var variable = this.ParseLValue();
    this.ExpectKeyword("IN");
    var collection = this.WithoutPatternBindings(this.ParseExpression);
    // a bare range source (FOR EACH v IN lo TO hi [STEP s]) - no brackets needed
    Expression? rangeHi = null, rangeStep = null;
    if (this.TryMatchKeyword("TO")) {
      rangeHi = this.WithoutPatternBindings(this.ParseExpression);
      if (this.TryMatchKeyword("STEP"))
        rangeStep = this.WithoutPatternBindings(this.ParseExpression);
    }
    var body = this.ParseBody("NEXT");
    this.ConsumeNext();

    if (rangeHi != null)
      return new ForStmt(pos, variable, collection, rangeHi, rangeStep, body);

    // a single [lo TO hi] range -> plain counted loop over the user variable
    if (collection is ArrayLiteralExpr { Elements: [RangeElement range] })
      return new ForStmt(pos, variable, range.Lo, range.Hi, null, body);

    // array or generator: the binder lowers it once the collection's type is known
    return new ForEachStmt(pos, variable, collection, body);
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
    this.Require(LanguageFeature.DoLoop);
    var pos = this.Advance().Position;
    var (preTest, preCondition) = this.ParseLoopTest();
    var body = this.ParseBody("LOOP");
    this.ExpectKeyword("LOOP");
    var (postTest, postCondition) = this.ParseLoopTest();
    return new DoLoopStmt(pos, preTest, preCondition, postTest, postCondition, body);
  }

  private (LoopTestKind Kind, Expression? Condition) ParseLoopTest() {
    if (this.TryMatchKeyword("WHILE"))
      return (LoopTestKind.While, this.WithoutPatternBindings(this.ParseExpression));
    if (this.TryMatchKeyword("UNTIL"))
      return (LoopTestKind.Until, this.WithoutPatternBindings(this.ParseExpression));
    return (LoopTestKind.None, null);
  }

  private Statement ParseWhile() {
    var pos = this.Advance().Position;
    var condition = this.WithoutPatternBindings(this.ParseExpression);
    var body = this.ParseBody("WEND");
    this.ExpectKeyword("WEND");
    return new DoLoopStmt(pos, LoopTestKind.While, condition, LoopTestKind.None, null, body);
  }

  private Statement ParseExit() {
    this.Require(LanguageFeature.ExitStatement);
    var pos = this.Advance().Position;
    var token = this.Expect(TokenKind.Identifier, "EXIT kind");

    // EXIT FAR [AT label] - record/trigger the far unwind point
    if (token.Text.Equals("FAR", StringComparison.OrdinalIgnoreCase)) {
      this.Require(LanguageFeature.ExitFar);
      return new ExitFarStmt(pos, this.TryMatchKeyword("AT") ? this.ParseLabelReference() : null);
    }

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

  /// <summary>ITERATE [FOR|DO|LOOP|WHILE] - continue with the next loop pass.</summary>
  private Statement ParseIterate() {
    this.Require(LanguageFeature.IterateStatement);   // Bob Zale's; BC 1.00 and 4.50 both reject it
    var pos = this.Advance().Position;
    var kind = ExitKind.Loop; // bare ITERATE: innermost loop
    if (this.TryMatchKeyword("FOR"))
      kind = ExitKind.For;
    else if (this.TryMatchKeyword("DO") || this.TryMatchKeyword("LOOP") || this.TryMatchKeyword("WHILE"))
      kind = ExitKind.Do;
    else if (!this.IsStatementEnd())
      throw this.Error($"cannot ITERATE '{this.Current.Text}'");
    return new IterateStmt(pos, kind);
  }

  /// <summary>GOTO/GOSUB label, line number, or <c>DWORD ptr32</c> (PB 3.2 code pointers).</summary>
  private Statement ParseGotoGosub(bool isGosub) {
    var pos = this.Advance().Position;
    if (this.IsKeyword(0, "DWORD")) {
      this.Require(LanguageFeature.CodePointers);
      this.Advance();
      var pointer = this.ParseExpression();
      return isGosub ? new GosubPtrStmt(pos, pointer) : new GotoPtrStmt(pos, pointer);
    }
    var target = this.ParseLabelReference();
    return isGosub ? new GosubStmt(pos, target) : new GotoStmt(pos, target);
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

  /// <summary>
  /// True when a leading <c>YIELD</c> is the coroutine statement form (a value follows)
  /// rather than a bare identifier reference. Assignment (<c>YIELD = x</c>) and suffixed
  /// (<c>YIELD%</c>) forms are routed away before the keyword switch, so reaching here with
  /// an expression ahead means the statement form; pre-pb36 it is rejected by
  /// <see cref="ParseYield"/>'s feature gate (the TRY / FOR EACH convention).
  /// </summary>
  private bool IsCoroutineYield() => !this.IsStatementEnd();

  /// <summary>
  /// <c>YIELD &lt;expression&gt;</c> (PB 3.6 coroutines): gated to pb36; older dialects reject
  /// it with the standard requirement message, mirroring the shift/rotate operator gate.
  /// </summary>
  private Statement ParseYield() {
    this.Require(LanguageFeature.Coroutines);
    var pos = this.Advance().Position;
    return new YieldStmt(pos, this.ParseExpression());
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

  /// <summary>
  /// PB 3.6 structured exception handling: TRY &lt;body&gt; [CATCH [errnum] [WHEN cond] &lt;handler&gt;]...
  /// [FINALLY &lt;cleanup&gt;] END TRY. At least one of CATCH / FINALLY is required. A CATCH may be
  /// filtered by an error number and/or a WHEN guard; several filtered CATCH clauses are tried in
  /// order, and if none matches the error is re-raised to the outer handler (after FINALLY).
  /// </summary>
  private Statement ParseTry() {
    this.Require(LanguageFeature.TryCatch);
    var pos = this.Advance().Position; // TRY

    var body = this.ParseBody("CATCH", "FINALLY", "END TRY");

    // each clause: optional error-number, optional WHEN guard, body (null filter = catch-all)
    var clauses = new List<(Expression? ErrNum, Expression? When, List<Statement> Body)>();
    while (this.TryMatchKeyword("CATCH")) {
      var errNum = !this.IsStatementEnd() && !this.IsKeyword(0, "WHEN") ? this.ParseExpression() : (Expression?)null;
      var when = this.TryMatchKeyword("WHEN") ? this.ParseExpression() : null;
      clauses.Add((errNum, when, this.ParseBody("CATCH", "FINALLY", "END TRY")));
    }

    List<Statement>? finallyBody = null;
    if (this.TryMatchKeyword("FINALLY"))
      finallyBody = this.ParseBody("END TRY");

    if (clauses.Count == 0 && finallyBody == null)
      throw this.Error("TRY requires a CATCH or FINALLY block");

    this.ExpectKeyword("END");
    this.ExpectKeyword("TRY");
    return new TryStmt(pos, body, this.FoldCatchClauses(pos, clauses, finallyBody), finallyBody);
  }

  /// <summary>
  /// Folds filtered CATCH clauses into a single catch body (the existing TRY machinery runs one
  /// handler). A clause's filter is <c>ERR = errnum</c>, the <c>WHEN</c> guard, or both (ANDALSO-style,
  /// the guard is evaluated only when the number matches). Clauses become an IF/ELSEIF chain; an
  /// unfiltered clause is the catch-all ELSE. If no catch-all is present, the ELSE runs FINALLY and
  /// re-raises the current ERR to the now-restored outer handler - so an unmatched error propagates.
  /// A single unfiltered CATCH folds to its bare body (identical to the pre-filter lowering).
  /// </summary>
  private List<Statement>? FoldCatchClauses(SourcePosition pos, List<(Expression? ErrNum, Expression? When, List<Statement> Body)> clauses, List<Statement>? finallyBody) {
    if (clauses.Count == 0)
      return null;
    if (clauses is [{ ErrNum: null, When: null, Body: var only }])
      return only;                       // a plain CATCH - unchanged lowering

    Expression Err() => new NameExpr(pos, "ERR", TypeSuffix.None);
    Expression? Filter((Expression? ErrNum, Expression? When, List<Statement>) c) {
      Expression? cond = c.ErrNum is { } e ? new BinaryExpr(pos, BinaryOp.Equal, Err(), e) : null;
      if (c.When is { } w)
        cond = cond is null ? w : new IfExpr(pos, cond, w, new IntegerLiteralExpr(pos, 0, TypeSuffix.None));   // short-circuit AND
      return cond;
    }

    var arms = new List<(Expression Condition, IReadOnlyList<Statement> Body)>();
    IReadOnlyList<Statement>? elseBody = null;
    foreach (var c in clauses) {
      if (Filter(c) is { } cond)
        arms.Add((cond, c.Body));
      else { elseBody = c.Body; break; }   // catch-all - later clauses are unreachable
    }
    // no catch-all: an unmatched error runs FINALLY then re-raises ERR to the outer handler
    elseBody ??= [.. finallyBody ?? [], new ErrorStmt(pos, Err())];

    var first = arms[0];
    return [new IfStmt(pos, first.Condition, first.Body, arms.Skip(1).ToList(), elseBody)];
  }

  /// <summary>pb36 DEFER &lt;statement&gt;: schedule a statement to run when the enclosing block exits. Lowered (in ParseBody) to a TRY ... FINALLY wrapping the rest of the block.</summary>
  private Statement ParseDefer() {
    this.Require(LanguageFeature.Defer);
    var pos = this.Advance().Position; // DEFER
    var deferred = this.ParseStatement();
    if (deferred is StatementGroup)
      throw this.Error("an IS payload binding is not allowed in a DEFER statement");
    return new DeferStmt(pos, deferred);
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
