using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Syntax;

/// <summary>Expression parsing: PowerBASIC operator precedence, atoms and postfix chains.</summary>
public sealed partial class Parser {

  // precedence low -> high: IMP, EQV, XOR, OR, AND, NOT, comparisons, ISTRUE/ISFALSE,
  // + -, MOD, \, * /, unary -, ^, atom
  private Expression ParseExpression() => this.ParseImp();

  private Expression ParseImp() {
    var left = this.ParseEqv();
    while (this.IsKeyword(0, "IMP"))
      left = new BinaryExpr(this.Advance().Position, BinaryOp.Imp, left, this.ParseEqv());
    return left;
  }

  private Expression ParseEqv() {
    var left = this.ParseXor();
    while (this.IsKeyword(0, "EQV"))
      left = new BinaryExpr(this.Advance().Position, BinaryOp.Eqv, left, this.ParseXor());
    return left;
  }

  private Expression ParseXor() {
    var left = this.ParseOr();
    while (this.IsKeyword(0, "XOR"))
      left = new BinaryExpr(this.Advance().Position, BinaryOp.Xor, left, this.ParseOr());
    return left;
  }

  private Expression ParseOr() {
    var left = this.ParseAnd();
    while (this.IsKeyword(0, "OR"))
      left = new BinaryExpr(this.Advance().Position, BinaryOp.Or, left, this.ParseAnd());
    return left;
  }

  private Expression ParseAnd() {
    var left = this.ParseNot();
    while (this.IsKeyword(0, "AND"))
      left = new BinaryExpr(this.Advance().Position, BinaryOp.And, left, this.ParseNot());
    return left;
  }

  private Expression ParseNot() {
    if (!this.IsKeyword(0, "NOT"))
      return this.ParseComparison();

    var pos = this.Advance().Position;
    return new UnaryExpr(pos, UnaryOp.Not, this.ParseNot());
  }

  private Expression ParseComparison() {
    var left = this.ParseTruth();
    for (;;) {
      var op = this.Current.Kind switch {
        TokenKind.Equals => BinaryOp.Equal,
        TokenKind.NotEquals => BinaryOp.NotEqual,
        TokenKind.Less => BinaryOp.Less,
        TokenKind.Greater => BinaryOp.Greater,
        TokenKind.LessEquals => BinaryOp.LessEqual,
        TokenKind.GreaterEquals => BinaryOp.GreaterEqual,
        _ => (BinaryOp?)null,
      };
      if (op == null)
        return left;

      left = new BinaryExpr(this.Advance().Position, op.Value, left, this.ParseTruth());
    }
  }

  private Expression ParseTruth() {
    if (this.TryMatchKeyword("ISTRUE"))
      return this.ParseTruth(); // ISTRUE x is the identity in boolean context

    if (this.IsKeyword(0, "ISFALSE")) {
      var pos = this.Advance().Position;
      return new UnaryExpr(pos, UnaryOp.Not, this.ParseTruth());
    }

    return this.ParseAdditive();
  }

  private Expression ParseAdditive() {
    var left = this.ParseModulo();
    for (;;) {
      var op = this.Current.Kind switch {
        TokenKind.Plus => BinaryOp.Add,
        TokenKind.Minus => BinaryOp.Subtract,
        TokenKind.Ampersand => BinaryOp.Concat,
        _ => (BinaryOp?)null,
      };
      if (op == null)
        return left;
      if (op == BinaryOp.Concat)
        this.Require(LanguageFeature.ConcatOperator);

      left = new BinaryExpr(this.Advance().Position, op.Value, left, this.ParseModulo());
    }
  }

  private Expression ParseModulo() {
    var left = this.ParseIntegerDivision();
    while (this.IsKeyword(0, "MOD"))
      left = new BinaryExpr(this.Advance().Position, BinaryOp.Modulo, left, this.ParseIntegerDivision());
    return left;
  }

  private Expression ParseIntegerDivision() {
    var left = this.ParseMultiplicative();
    while (this.Current.Kind == TokenKind.Backslash)
      left = new BinaryExpr(this.Advance().Position, BinaryOp.IntegerDivide, left, this.ParseMultiplicative());
    return left;
  }

  private Expression ParseMultiplicative() {
    var left = this.ParseUnary();
    for (;;) {
      var op = this.Current.Kind switch {
        TokenKind.Star => BinaryOp.Multiply,
        TokenKind.Slash => BinaryOp.Divide,
        _ => (BinaryOp?)null,
      };
      if (op == null)
        return left;

      left = new BinaryExpr(this.Advance().Position, op.Value, left, this.ParseUnary());
    }
  }

  private Expression ParseUnary() {
    switch (this.Current.Kind) {
      case TokenKind.Minus: {
        var pos = this.Advance().Position;
        return new UnaryExpr(pos, UnaryOp.Negate, this.ParseUnary());
      }
      case TokenKind.Plus:
        this.Advance();
        return this.ParseUnary();
      default:
        return this.ParsePower();
    }
  }

  private Expression ParsePower() {
    var left = this.ParsePrimary();
    while (this.Current.Kind == TokenKind.Caret)
      left = new BinaryExpr(this.Advance().Position, BinaryOp.Power, left, this.ParseExponent());
    return left;
  }

  // the exponent may carry its own sign: 2 ^ -3
  private Expression ParseExponent() {
    if (this.Current.Kind != TokenKind.Minus)
      return this.ParsePrimary();

    var pos = this.Advance().Position;
    return new UnaryExpr(pos, UnaryOp.Negate, this.ParseExponent());
  }

  private Expression ParsePrimary() {
    var token = this.Current;
    switch (token.Kind) {
      case TokenKind.IntegerLiteral:
        return new IntegerLiteralExpr(this.Advance().Position, token.IntegerValue, token.Suffix);
      case TokenKind.FloatLiteral:
        return new FloatLiteralExpr(this.Advance().Position, token.FloatValue, token.Suffix);
      case TokenKind.StringLiteral:
        return new StringLiteralExpr(this.Advance().Position, token.StringValue!);
      case TokenKind.NamedConstant:
        return new NamedConstantExpr(this.Advance().Position, token.Text);
      case TokenKind.LParen: {
        this.Advance();
        var inner = this.ParseExpression();
        this.Expect(TokenKind.RParen, "')'");
        return inner;
      }
      case TokenKind.Hash: // file number in I/O argument position, e.g. INPUT$(2, #1)
        return new FileNumberExpr(this.Advance().Position, this.ParsePrimary());
      case TokenKind.At:
        return this.ParsePtrDeref();
      case TokenKind.Identifier:
        return this.ParseNameExpression();
      default:
        throw this.Error($"unexpected '{token.Text}' in expression");
    }
  }

  /// <summary>
  /// Pointer dereference <c>@p</c> / <c>@p(i)</c> array-of-pointer element /
  /// indexed <c>@p[i]</c> (PB 3.5); member access binds to the target
  /// (<c>@p.field</c> reads the field of the pointed-to TYPE).
  /// </summary>
  private Expression ParsePtrDeref() {
    this.Require(LanguageFeature.Pointers);
    var pos = this.Advance().Position; // @
    var token = this.Expect(TokenKind.Identifier, "pointer variable");
    Expression pointer = this.Current.Kind == TokenKind.LParen
      ? new CallOrIndexExpr(token.Position, token.Text, token.Suffix, this.ParseArgumentList())
      : new NameExpr(token.Position, token.Text, token.Suffix);

    Expression? index = null;
    if (this.Current.Kind == TokenKind.LBracket) {
      this.Require(LanguageFeature.IndexedPointers);
      this.Advance();
      index = this.ParseExpression();
      this.Expect(TokenKind.RBracket, "']'");
    }
    return this.ParsePostfix(new PtrDerefExpr(pos, pointer, index));
  }

  private Expression ParseNameExpression() {
    var token = this.Advance();
    Expression expr = this.Current.Kind == TokenKind.LParen
      ? new CallOrIndexExpr(token.Position, token.Text, token.Suffix, this.ParseArgumentList())
      : new NameExpr(token.Position, token.Text, token.Suffix);
    return this.ParsePostfix(expr);
  }

  private Expression ParsePostfix(Expression expr) {
    for (;;) {
      if (this.Current.Kind == TokenKind.Period && this.Peek().Kind == TokenKind.Identifier) {
        this.Advance();
        var member = this.Advance();
        expr = new MemberExpr(member.Position, expr, member.Text, member.Suffix);
        continue;
      }

      // array UDT fields: ctx.NamedTimers(i)
      if (this.Current.Kind == TokenKind.LParen && expr is MemberExpr) {
        expr = new IndexExpr(this.Current.Position, expr, this.ParseArgumentList());
        continue;
      }

      return expr;
    }
  }

  /// <summary>Parses a parenthesized argument list including both parens; may be empty.</summary>
  private List<Expression> ParseArgumentList() {
    this.Expect(TokenKind.LParen, "'('");
    var arguments = new List<Expression>();
    if (this.Match(TokenKind.RParen))
      return arguments;

    do
      arguments.Add(this.ParseArgument());
    while (this.Match(TokenKind.Comma));
    this.Expect(TokenKind.RParen, "')'");
    return arguments;
  }

  /// <summary>One call argument; <c>BYVAL expr</c> overrides the default by-reference passing.</summary>
  private Expression ParseArgument() {
    if (!this.IsKeyword(0, "BYVAL"))
      return this.ParseExpression();
    var pos = this.Advance().Position;
    return new ByValArgExpr(pos, this.ParseExpression());
  }

  /// <summary>Parses an assignable expression: name, array element, member chain, indexed member or pointer target.</summary>
  private Expression ParseLValue() {
    if (this.Current.Kind == TokenKind.At)
      return this.ParsePtrDeref();
    if (this.Current.Kind != TokenKind.Identifier)
      throw this.Error($"expected variable, found '{this.Current.Text}'");
    return this.ParseNameExpression();
  }

  /// <summary>Parses a graphics coordinate pair <c>(x, y)</c>.</summary>
  private (Expression X, Expression Y) ParsePoint() {
    this.Expect(TokenKind.LParen, "'('");
    var x = this.ParseExpression();
    this.Expect(TokenKind.Comma, "','");
    var y = this.ParseExpression();
    this.Expect(TokenKind.RParen, "')'");
    return (x, y);
  }

  #region lookahead

  /// <summary>True when the current identifier starts an assignment: name {(args) | .member}* '='.</summary>
  private bool IsAssignmentAhead() {
    var i = this._pos + 1;
    for (;;) {
      var kind = this.TokenAt(i).Kind;
      if (kind == TokenKind.LParen) {
        if (!this.TrySkipBalancedParens(ref i))
          return false;
        continue;
      }
      if (kind == TokenKind.Period && this.TokenAt(i + 1).Kind == TokenKind.Identifier) {
        i += 2;
        continue;
      }
      return kind == TokenKind.Equals;
    }
  }

  /// <summary>True when the current <c>(</c> opens a coordinate pair (top-level comma inside).</summary>
  private bool IsPointAhead() {
    if (this.Current.Kind != TokenKind.LParen)
      return false;

    var i = this._pos;
    var depth = 0;
    do {
      var kind = this.TokenAt(i).Kind;
      if (kind == TokenKind.LParen)
        ++depth;
      else if (kind == TokenKind.RParen)
        --depth;
      else if (kind == TokenKind.Comma && depth == 1)
        return true;
      else if (kind is TokenKind.EndOfLine or TokenKind.EndOfFile)
        return false;
      ++i;
    } while (depth > 0);

    return false;
  }

  /// <summary>True when the current <c>(</c> closes exactly at the end of the statement (whole-call parens).</summary>
  private bool ParenthesesEndStatement() {
    var i = this._pos;
    if (!this.TrySkipBalancedParens(ref i))
      return false;

    var next = this.TokenAt(i);
    return next.Kind is TokenKind.EndOfLine or TokenKind.Colon or TokenKind.EndOfFile
      || next is { Kind: TokenKind.Identifier, Suffix: TypeSuffix.None } && next.Text.Equals("ELSE", StringComparison.OrdinalIgnoreCase);
  }

  private bool TrySkipBalancedParens(ref int index) {
    var depth = 0;
    do {
      var kind = this.TokenAt(index).Kind;
      if (kind == TokenKind.LParen)
        ++depth;
      else if (kind == TokenKind.RParen)
        --depth;
      else if (kind is TokenKind.EndOfLine or TokenKind.EndOfFile)
        return false;
      ++index;
    } while (depth > 0);

    return true;
  }

  #endregion
}
