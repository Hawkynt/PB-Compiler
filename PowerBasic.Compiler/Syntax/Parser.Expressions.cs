using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Syntax;

/// <summary>Expression parsing: PowerBASIC operator precedence, atoms and postfix chains.</summary>
public sealed partial class Parser {

  // precedence low -> high: IMP, EQV, XOR, OR, AND, NOT, comparisons, ISTRUE/ISFALSE,
  // + -, MOD, \, * /, unary -, ^, atom
  private Expression ParseExpression() => this.ParseCoalesce();

  // pb36 null-coalescing '??' - lowest precedence (below IMP), left-associative: a ?? b ?? c
  private Expression ParseCoalesce() {
    var left = this.ParseImp();
    while (this.Current.Kind == TokenKind.QuestionQuestion) {
      this.Require(LanguageFeature.NullableTypes);
      left = new CoalesceExpr(this.Advance().Position, left, this.ParseImp());
    }
    return left;
  }

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
    for (;;) {
      if (this.IsKeyword(0, "OR")) {
        left = new BinaryExpr(this.Advance().Position, BinaryOp.Or, left, this.ParseAnd());
        continue;
      }
      if (this.Current.Kind == TokenKind.Pipe) { // PB 3.6 bitwise OR operator
        this.Require(LanguageFeature.ShiftRotateOps);
        left = new BinaryExpr(this.Advance().Position, BinaryOp.Or, left, this.ParseAnd());
        continue;
      }
      if (this.IsKeyword(0, "ORELSE")) {
        left = this.MakeShortCircuit(this.Advance().Position, isAnd: false, left, this.ParseAnd());
        continue;
      }
      return left;
    }
  }

  private Expression ParseAnd() {
    var left = this.ParseNot();
    for (;;) {
      if (this.IsKeyword(0, "AND")) {
        left = new BinaryExpr(this.Advance().Position, BinaryOp.And, left, this.ParseNot());
        continue;
      }
      if (this.IsKeyword(0, "ANDALSO")) {
        left = this.MakeShortCircuit(this.Advance().Position, isAnd: true, left, this.ParseNot());
        continue;
      }
      return left;
    }
  }

  /// <summary>
  /// PB 3.6 short-circuit boolean operators, lowered to the ternary (so they reuse
  /// its short-circuit codegen and stay sound in the optimizer):
  /// <c>a ANDALSO b</c> = <c>IF(a, b &lt;&gt; 0, 0)</c> (b skipped when a is false);
  /// <c>a ORELSE b</c> = <c>IF(a, -1, b &lt;&gt; 0)</c> (b skipped when a is true).
  /// The result is the normalized PB truth value (-1 / 0).
  /// </summary>
  private Expression MakeShortCircuit(SourcePosition pos, bool isAnd, Expression left, Expression right) {
    this.Require(LanguageFeature.ShortCircuitOps);
    var rightTruth = new BinaryExpr(pos, BinaryOp.NotEqual, right, new IntegerLiteralExpr(pos, 0, TypeSuffix.None));
    return isAnd
      ? new IfExpr(pos, left, rightTruth, new IntegerLiteralExpr(pos, 0, TypeSuffix.None))
      : new IfExpr(pos, left, new IntegerLiteralExpr(pos, -1, TypeSuffix.None), rightTruth);
  }

  private Expression ParseNot() {
    if (!this.IsKeyword(0, "NOT"))
      return this.ParseComparison();

    var pos = this.Advance().Position;
    return new UnaryExpr(pos, UnaryOp.Not, this.ParseNot());
  }

  private Expression ParseComparison() {
    var left = this.ParseTruth();
    var chained = DialectFacts.IsAvailable(LanguageFeature.ChainedComparison, this._dialect);
    Expression? prevRight = null;   // the last comparison's right operand, reused as the next link's left when chaining
    var links = 0;
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

      var position = this.Advance().Position;
      // PB tolerates whitespace inside two-character relations: "> =", "< =", "< >"
      op = (op, this.Current.Kind) switch {
        (BinaryOp.Greater, TokenKind.Equals) => BinaryOp.GreaterEqual,
        (BinaryOp.Less, TokenKind.Equals) => BinaryOp.LessEqual,
        (BinaryOp.Less, TokenKind.Greater) => BinaryOp.NotEqual,
        _ => op,
      };
      if (op is BinaryOp.GreaterEqual or BinaryOp.LessEqual or BinaryOp.NotEqual && this.Current.Kind is TokenKind.Equals or TokenKind.Greater)
        this.Advance();

      var right = this.ParseTruth();
      // pb36 chained comparison: a < b < c is (a < b) AND (b < c), reusing the middle operand as the
      // next link's left. The first comparison is emitted as usual; each subsequent one ANDs a new
      // link. (The reused middle operand should be side-effect-free - it is read once per link.)
      if (chained && ++links >= 2)
        left = new BinaryExpr(position, BinaryOp.And, left, new BinaryExpr(position, op.Value, prevRight!, right));
      else
        left = new BinaryExpr(position, op.Value, left, right);
      prevRight = right;
    }
  }

  private Expression ParseTruth() {
    if (this.TryMatchKeyword("ISTRUE"))
      return this.ParseTruth(); // ISTRUE x is the identity in boolean context

    if (this.IsKeyword(0, "ISFALSE")) {
      var pos = this.Advance().Position;
      return new UnaryExpr(pos, UnaryOp.Not, this.ParseTruth());
    }

    return this.ParseShift();
  }

  /// <summary>
  /// PB 3.6 shift/rotate operators, binding looser than +/- but tighter than
  /// comparison: &lt;&lt; / &lt;&lt;&lt; shift left, &gt;&gt; arithmetic shift right,
  /// &gt;&gt;&gt; logical shift right, &lt;&lt;&gt; rotate left, &lt;&gt;&gt; rotate right.
  /// </summary>
  private Expression ParseShift() {
    var left = this.ParseAdditive();
    for (;;) {
      var op = this.Current.Kind switch {
        TokenKind.ShiftLeft or TokenKind.ShiftLeftLogical => BinaryOp.ShiftLeft,
        TokenKind.ShiftRight => BinaryOp.ShiftRightArith,
        TokenKind.ShiftRightLogical => BinaryOp.ShiftRightLogical,
        TokenKind.RotateLeft => BinaryOp.RotateLeft,
        TokenKind.RotateRight => BinaryOp.RotateRight,
        _ => (BinaryOp?)null,
      };
      if (op == null)
        return left;
      this.Require(LanguageFeature.ShiftRotateOps);
      left = new BinaryExpr(this.Advance().Position, op.Value, left, this.ParseAdditive());
    }
  }

  private Expression ParseAdditive() {
    var left = this.ParseModulo();
    for (;;) {
      var op = this.Current.Kind switch {
        TokenKind.Plus => BinaryOp.Add,
        TokenKind.Minus => BinaryOp.Subtract,
        TokenKind.Ampersand => BinaryOp.Concat,
        TokenKind.PlusStar => BinaryOp.PointerAdd,
        TokenKind.MinusStar => BinaryOp.PointerSub,
        _ => (BinaryOp?)null,
      };
      if (op == null)
        return left;
      if (op == BinaryOp.Concat)
        this.Require(LanguageFeature.ConcatOperator);
      if (op is BinaryOp.PointerAdd or BinaryOp.PointerSub)
        this.Require(LanguageFeature.PointerArithmetic);

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

  /// <summary>
  /// Default type of a bare FP literal (verified against genuine PBC 3.50):
  /// a D exponent or more than seven significant mantissa digits makes it
  /// DOUBLE (PRINT 123456.789 keeps all nine digits), otherwise SINGLE
  /// (PRINT 1E7 shows the SINGLE exponent threshold "1E+7").
  /// </summary>
  private TypeSuffix InferFloatSuffix(Token token) {
    if (token.Suffix != TypeSuffix.None)
      return token.Suffix;
    // Turbo Basic has no SINGLE expression semantics - every bare FP literal
    // lives in its 16-digit double runtime
    if (this._dialect.IsTurboBasic())
      return TypeSuffix.Double;
    var significant = 0;
    var seenNonZero = false;
    foreach (var c in token.Text) {
      if (c is 'E' or 'e' or 'D' or 'd')
        return c is 'D' or 'd' ? TypeSuffix.Double : significant > 7 ? TypeSuffix.Double : TypeSuffix.Single;
      if (!char.IsAsciiDigit(c))
        continue;
      if (c != '0')
        seenNonZero = true;
      if (seenNonZero)
        ++significant;
    }
    return significant > 7 ? TypeSuffix.Double : TypeSuffix.Single;
  }

  private Expression ParsePrimary() {
    var token = this.Current;
    switch (token.Kind) {
      case TokenKind.IntegerLiteral:
        return new IntegerLiteralExpr(this.Advance().Position, token.IntegerValue, token.Suffix);
      case TokenKind.FloatLiteral:
        return new FloatLiteralExpr(this.Advance().Position, token.FloatValue, this.InferFloatSuffix(token));
      case TokenKind.StringLiteral:
        return new StringLiteralExpr(this.Advance().Position, token.StringValue!);
      case TokenKind.InterpString:
        return this.ParseInterpString();
      case TokenKind.NamedConstant:
        return new NamedConstantExpr(this.Advance().Position, token.Text);
      case TokenKind.LParen: {
        // PB 3.6 concise lambda: (params) => expr. Only an unambiguous parameter
        // list (empty, or with a top-level comma) is taken as a lambda - a single
        // parenthesised value followed by '=>' stays a '>=' comparison, since the
        // lexer maps both arrows to the same token.
        if (this.IsConciseLambdaAhead())
          return this.ParseConciseLambda();
        var parenPos = this.Advance().Position;
        var inner = this.ParseExpression();
        // pb36 tuple literal: (e1, e2, ...) - a comma after the first value makes it a tuple
        if (this.Current.Kind == TokenKind.Comma) {
          this.Require(LanguageFeature.Tuples);
          var elements = new List<Expression> { inner };
          while (this.Match(TokenKind.Comma))
            elements.Add(this.ParseExpression());
          this.Expect(TokenKind.RParen, "')'");
          return new TupleExpr(parenPos, elements);
        }
        this.Expect(TokenKind.RParen, "')'");
        return inner;
      }
      case TokenKind.Hash: // file number in I/O argument position, e.g. INPUT$(2, #1)
        return new FileNumberExpr(this.Advance().Position, this.ParsePrimary());
      case TokenKind.At:
        return this.ParsePtrDeref();
      case TokenKind.LBrace:    // PB 3.6 array-initializer literal { ... }
      case TokenKind.LBracket:  // PB 3.6 collection/range literal [ ... ] (Require gates it per dialect)
        return this.ParseArrayLiteral();
      // PB 3.6 WITH: a leading '.member' binds to the innermost WITH subject
      case TokenKind.Period when this.Peek().Kind == TokenKind.Identifier && this._withSubjects.Count > 0:
        return this.ParseImplicitWithMember();
      // pb36 NOTHING: the empty value of a nullable type (only when the dialect has nullables, so it stays a usable identifier elsewhere)
      case TokenKind.Identifier when token.Suffix == TypeSuffix.None && token.Text.Equals("NOTHING", StringComparison.OrdinalIgnoreCase) && DialectFacts.IsAvailable(LanguageFeature.NullableTypes, this._dialect):
        return new NothingExpr(this.Advance().Position);
      case TokenKind.Identifier:
        return this.ParseNameExpression();
      default:
        throw this.Error($"unexpected '{token.Text}' in expression");
    }
  }

  private Expression ParseImplicitWithMember() {
    var dot = this.Advance(); // '.'
    var member = this.Advance(); // member name
    Expression expr = new MemberExpr(dot.Position, this._withSubjects[^1], member.Text, member.Suffix);
    return this.ParsePostfix(expr);
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
    // PB 3.6 ternary: IF(condition, whenTrue, whenFalse) in expression position.
    // IF is a reserved statement keyword (no valid program has a function named IF),
    // so intercepting IF( here never shadows existing code; statement-leading IF is
    // dispatched before expression parsing.
    if (this.Current is { Suffix: TypeSuffix.None } && this.IsKeyword(0, "IF") && this.Peek().Kind == TokenKind.LParen)
      return this.ParseTernaryIf();

    // PB 3.6 object initializer: NEW type { .field = value, ... }
    if (this.IsKeyword(0, "NEW") && this.Peek().Kind == TokenKind.Identifier && this.Peek(2).Kind == TokenKind.LBrace)
      return this.ParseNewExpr();

    // PB 3.6 inline lambda: FUNCTION(params) [AS type] => expr (in expression position)
    if (this.IsKeyword(0, "FUNCTION") && this.Peek().Kind == TokenKind.LParen)
      return this.ParseLambda();

    // PB 3.6 statement-bodied SUB lambda: SUB(params) statement (in expression position)
    if (this.IsKeyword(0, "SUB") && this.Peek().Kind == TokenKind.LParen)
      return this.ParseSubLambda();

    // PB 3.6 bare single-parameter lambda: x => expr. The '=>' arrow is its own token
    // (distinct from the '>=' comparison), so this is unambiguous.
    if (this.Current.Kind == TokenKind.Identifier && this.Peek().Kind == TokenKind.FatArrow) {
      this.Require(LanguageFeature.Lambdas);
      var p = this.Advance();
      this.Advance(); // '=>'
      return new LambdaExpr(p.Position, [new Parameter(p.Position, p.Text, p.Suffix, null, false, false, false)], null, this.ParseExpression());
    }

    var token = this.Advance();
    // pb36 segmented PEEK[I|L]: PEEK(seg:offset) reads at seg:off (sets DEF SEG = seg first), emitted
    // as a 2-argument [seg, offset] intrinsic call. The ':' is only special inside a PEEK argument.
    if (token.Suffix == TypeSuffix.None && token.Text.ToUpperInvariant() is "PEEK" or "PEEKI" or "PEEKL" && this.Current.Kind == TokenKind.LParen) {
      this.Advance(); // '('
      var first = this.ParseExpression();
      if (this.Current.Kind == TokenKind.Colon) {
        this.Require(LanguageFeature.SegmentedPeekPoke);
        this.Advance();
        var off = this.ParseExpression();
        this.Expect(TokenKind.RParen, "')'");
        return this.ParsePostfix(new CallOrIndexExpr(token.Position, token.Text, token.Suffix, [first, off]));
      }
      this.Expect(TokenKind.RParen, "')'");
      return this.ParsePostfix(new CallOrIndexExpr(token.Position, token.Text, token.Suffix, [first]));
    }
    // pb36 generics: explicit type arguments on a call, Name OF type(args) / Name OF (T1, T2)(args)
    if (token.Suffix == TypeSuffix.None && this.IsKeyword(0, "OF")) {
      var typeArgs = this.TryParseTypeArguments();
      return this.ParsePostfix(new CallOrIndexExpr(token.Position, token.Text, token.Suffix, this.ParseArgumentList(), typeArgs));
    }
    Expression expr = this.Current.Kind == TokenKind.LParen
      ? new CallOrIndexExpr(token.Position, token.Text, token.Suffix, this.ParseArgumentList())
      : new NameExpr(token.Position, token.Text, token.Suffix);
    return this.ParsePostfix(expr);
  }

  private Expression ParseNewExpr() {
    this.Require(LanguageFeature.ObjectInitializer);
    var pos = this.Advance().Position; // NEW
    var typeName = this.Expect(TokenKind.Identifier, "type name").Text;
    this.Expect(TokenKind.LBrace, "'{'");
    var fields = new List<(string Field, Expression Value)>();
    if (this.Current.Kind != TokenKind.RBrace)
      do {
        this.Expect(TokenKind.Period, "'.' before field name");
        var field = this.Expect(TokenKind.Identifier, "field name").Text;
        this.Expect(TokenKind.Equals, "'='");
        fields.Add((field, this.ParseExpression()));
      } while (this.Match(TokenKind.Comma));
    this.Expect(TokenKind.RBrace, "'}'");
    return new NewExpr(pos, typeName, fields);
  }

  private Expression ParseLambda() {
    this.Require(LanguageFeature.Lambdas);
    var pos = this.Advance().Position; // FUNCTION
    var parameters = this.ParseParameterList();
    var returnType = this.TryMatchKeyword("AS") ? this.ParseTypeName() : null;
    if (this.Current.Kind != TokenKind.FatArrow)
      throw this.Error("expected '=>' in lambda expression");
    this.Advance();
    return new LambdaExpr(pos, parameters, returnType, this.ParseExpression());
  }

  /// <summary>
  /// PB 3.6 statement-bodied SUB lambda: <c>SUB(params) statement</c> in expression position - the
  /// body is one statement (ending at ':' or end of line), optionally introduced by '=>'. The value
  /// is the lifted anonymous SUB's code pointer.
  /// </summary>
  private Expression ParseSubLambda() {
    this.Require(LanguageFeature.Lambdas);
    var pos = this.Advance().Position; // SUB
    var parameters = this.ParseParameterList();
    this.Match(TokenKind.FatArrow);    // optional '=>' for symmetry with the FUNCTION form
    var body = this.ParseStatement();
    return new LambdaExpr(pos, parameters, null, new IntegerLiteralExpr(pos, 0, TypeSuffix.None)) { StatementBody = body };
  }

  /// <summary>
  /// PB 3.6 concise lambda <c>(params) => expr</c>: the <c>FUNCTION</c> keyword and
  /// the result type are omitted; parameter types may be omitted too and are then
  /// inferred from the delegate the lambda is assigned to (the binder fills them).
  /// </summary>
  private Expression ParseConciseLambda() {
    this.Require(LanguageFeature.Lambdas);
    var pos = this.Current.Position;
    var parameters = this.ParseParameterList();
    if (this.Current.Kind != TokenKind.FatArrow)
      throw this.Error("expected '=>' in lambda expression");
    this.Advance();
    return new LambdaExpr(pos, parameters, null, this.ParseExpression());
  }

  /// <summary>
  /// True when the '(' at the cursor opens a concise-lambda parameter list - a
  /// balanced parenthesis group immediately followed by the '=>' arrow. The arrow is
  /// its own token (distinct from '>='), so this never collides with a parenthesised
  /// comparison.
  /// </summary>
  private bool IsConciseLambdaAhead() {
    if (!DialectFacts.IsAvailable(LanguageFeature.Lambdas, this._dialect))
      return false;
    var depth = 0;
    for (var i = 0; ; ++i) {
      switch (this.Peek(i).Kind) {
        case TokenKind.LParen:
          ++depth;
          break;
        case TokenKind.RParen:
          if (--depth == 0)
            return this.Peek(i + 1).Kind == TokenKind.FatArrow;
          break;
        case TokenKind.EndOfLine or TokenKind.Colon or TokenKind.EndOfFile:
          return false;
      }
    }
  }

  private Expression ParseArrayLiteral() {
    // '{ ... }' is the array-initializer literal; '[ ... ]' is the (equivalent)
    // bracketed collection/range literal (PB 3.6), also usable as a FOR EACH source.
    var bracketed = this.Current.Kind == TokenKind.LBracket;
    this.Require(bracketed ? LanguageFeature.CollectionLiteral : LanguageFeature.ArrayInitializer);
    var close = bracketed ? TokenKind.RBracket : TokenKind.RBrace;
    var pos = this.Advance().Position; // '{' or '['
    var elements = new List<CollectionElement>();
    if (this.Current.Kind != close)
      do {
        if (this.Match(TokenKind.DotDot)) { // ..arr spread | ..arr(lo TO hi) slice spread
          var srcName = this.Expect(TokenKind.Identifier, "array name");
          var source = new NameExpr(srcName.Position, srcName.Text, srcName.Suffix);
          if (this.Current.Kind == TokenKind.LParen) {
            this.Advance(); // '('
            Expression? ParseSliceBound() {
              if (this.IsKeyword(0, "TO") || this.Current.Kind == TokenKind.RParen)
                return null;   // omitted bound = the source's LBOUND/UBOUND
              if (this.Match(TokenKind.Caret))
                return new FromEndExpr(srcName.Position, this.ParseExpression());
              return this.ParseExpression();
            }
            var lo = ParseSliceBound();
            this.ExpectKeyword("TO");
            var hi = ParseSliceBound();
            this.Expect(TokenKind.RParen, "')'");
            elements.Add(new SpreadElement(pos, source) { SliceLo = lo, SliceHi = hi, IsSlice = true });
          } else
            elements.Add(new SpreadElement(pos, source));
          continue;
        }
        var first = this.ParseExpression();
        elements.Add(this.Match(TokenKind.DotDot)
          ? new RangeElement(first.Position, first, this.ParseExpression()) // lo..hi range
          : new ValueElement(first.Position, first));
      } while (this.Match(TokenKind.Comma));
    this.Expect(close, bracketed ? "']'" : "'}'");
    return new ArrayLiteralExpr(pos, elements);
  }

  /// <summary>
  /// PB 3.6 interpolated string: splits the raw inner text of the <c>$"..."</c> token into
  /// literal runs and <c>{expr[:fmt]}</c> holes (with <c>{{</c>/<c>}}</c> de-escaped in the
  /// literal runs), parsing each hole's expression with the active dialect. The binder
  /// turns the resulting <see cref="InterpolatedStringExpr"/> into a concatenation.
  /// </summary>
  private Expression ParseInterpString() {
    this.Require(LanguageFeature.StringInterpolation);
    var token = this.Advance();
    var raw = token.StringValue!;
    var parts = new List<InterpolationPart>();
    var literal = new System.Text.StringBuilder();

    void FlushLiteral() {
      if (literal.Length == 0)
        return;
      parts.Add(new InterpolationPart(token.Position, literal.ToString(), null, null));
      literal.Clear();
    }

    for (var i = 0; i < raw.Length;) {
      var c = raw[i];
      if (c == '{') {
        if (i + 1 < raw.Length && raw[i + 1] == '{') { // '{{' -> literal '{'
          literal.Append('{');
          i += 2;
          continue;
        }
        FlushLiteral();
        i = this.ParseInterpHole(raw, i + 1, token.Position, parts);
        continue;
      }
      if (c == '}') {
        if (i + 1 < raw.Length && raw[i + 1] == '}') { // '}}' -> literal '}'
          literal.Append('}');
          i += 2;
          continue;
        }
        throw new ParserException("unmatched '}' in interpolated string (use '}}' for a literal brace)", token.Position);
      }
      literal.Append(c);
      ++i;
    }
    FlushLiteral();
    return new InterpolatedStringExpr(token.Position, parts);
  }

  /// <summary>
  /// Scans one <c>{expr[:fmt]}</c> hole starting just past the <c>{</c>; the format is
  /// everything after the first top-level <c>:</c> up to the matching <c>}</c>. Returns the
  /// index just past the closing <c>}</c>.
  /// </summary>
  private int ParseInterpHole(string raw, int start, SourcePosition position, List<InterpolationPart> parts) {
    var depth = 0;
    var colon = -1;
    var i = start;
    for (; i < raw.Length; ++i) {
      var c = raw[i];
      if (c == '"') { // skip a nested string expression literally
        for (++i; i < raw.Length && raw[i] != '"'; ++i) { }
        continue;
      }
      if (c is '(' or '[' or '{')
        ++depth;
      else if (c is ')' or ']')
        --depth;
      else if (c == '}') {
        if (depth == 0)
          break;
        --depth;
      } else if (c == ':' && depth == 0 && colon < 0)
        colon = i;
    }
    if (i >= raw.Length)
      throw new ParserException("unterminated '{' in interpolated string", position);

    var exprText = (colon < 0 ? raw[start..i] : raw[start..colon]).Trim();
    var format = colon < 0 ? null : raw[(colon + 1)..i];
    if (exprText.Length == 0)
      throw new ParserException("empty '{}' hole in interpolated string", position);

    var hole = ParseSubExpression(exprText, position, this._dialect);
    parts.Add(new InterpolationPart(position, null, hole, format));
    return i + 1; // past '}'
  }

  /// <summary>Parses a standalone expression from source text (used for interpolation holes).</summary>
  private static Expression ParseSubExpression(string text, SourcePosition position, Dialect dialect) {
    var tokens = Lexer.Tokenize(text, position.File, dialect).ToList();
    var sub = new Parser(tokens, dialect);
    var expr = sub.ParseExpression();
    if (sub.Current.Kind is not (TokenKind.EndOfLine or TokenKind.EndOfFile))
      throw new ParserException($"unexpected '{sub.Current.Text}' in interpolated expression", position);
    return expr;
  }

  private Expression ParseTernaryIf() {
    this.Require(LanguageFeature.TernaryIf);
    var pos = this.Advance().Position; // IF
    this.Expect(TokenKind.LParen, "'('");
    var condition = this.ParseExpression();
    this.Expect(TokenKind.Comma, "','");
    var whenTrue = this.ParseExpression();
    this.Expect(TokenKind.Comma, "','");
    var whenFalse = this.ParseExpression();
    this.Expect(TokenKind.RParen, "')'");
    return this.ParsePostfix(new IfExpr(pos, condition, whenTrue, whenFalse));
  }

  private Expression ParsePostfix(Expression expr) {
    for (;;) {
      // pb36 null-conditional access: expr?.member / expr?[index] (the '?' lexes as its own token here,
      // the lexer having declined to glue it as a BYTE suffix before '.'/'[').
      if (this.Current.Kind == TokenKind.Question && this.Peek().Kind is TokenKind.Period or TokenKind.LBracket) {
        this.Require(LanguageFeature.NullConditional);
        var qpos = this.Advance().Position; // '?'
        if (this.Match(TokenKind.Period)) {
          var member = this.Expect(TokenKind.Identifier, "member name");
          expr = new NullConditionalExpr(qpos, expr, member.Text, null);
        } else {
          this.Advance(); // '['
          var index = this.ParseExpression();
          this.Expect(TokenKind.RBracket, "']'");
          expr = new NullConditionalExpr(qpos, expr, null, index);
        }
        continue;
      }

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

  /// <summary>
  /// One call argument; <c>BYVAL expr</c> overrides the default by-reference passing,
  /// <c>ANY set$</c> flags a match-any character set (INSTR/EXTRACT$/...).
  /// </summary>
  private Expression ParseArgument() {
    // from-end array index (PB 3.6): arr(^n) - the binder validates it is an index
    if (this.Current.Kind == TokenKind.Caret) {
      this.Require(LanguageFeature.FromEndIndex);
      var pos = this.Advance().Position; // '^'
      return new FromEndExpr(pos, this.ParseExpression());
    }
    // named argument (PB 3.6): name := value
    if (this.Current.Kind == TokenKind.Identifier && this.Peek().Kind == TokenKind.Colon && this.Peek(2).Kind == TokenKind.Equals) {
      this.Require(LanguageFeature.NamedArguments);
      var nameToken = this.Advance(); // name
      this.Advance(); // ':'
      this.Advance(); // '='
      return new NamedArgExpr(nameToken.Position, nameToken.Text, this.ParseExpression());
    }
    if (this.IsKeyword(0, "BYVAL")) {
      var pos = this.Advance().Position;
      return new ByValArgExpr(pos, this.ParseExpression());
    }
    if (this.IsKeyword(0, "ANY") && !this.IsStatementEnd() && this.Peek().Kind is not (TokenKind.Comma or TokenKind.RParen)) {
      var pos = this.Advance().Position;
      return new AnyMatchExpr(pos, this.ParseExpression());
    }
    return this.ParseExpression();
  }

  /// <summary>Parses an assignable expression: name, array element, member chain, indexed member or pointer target.</summary>
  private Expression ParseLValue() {
    if (this.Current.Kind == TokenKind.At)
      return this.ParsePtrDeref();
    if (this.Current.Kind == TokenKind.Period && this.Peek().Kind == TokenKind.Identifier && this._withSubjects.Count > 0)
      return this.ParseImplicitWithMember(); // PB 3.6 WITH: .member = value
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
  /// <summary>
  /// True when the statement is a tuple destructuring <c>a, b = expr</c>: a comma-separated list of two
  /// or more lvalues (each an identifier with optional <c>()</c> index and <c>.member</c> chains) followed
  /// by <c>=</c> (pb36). Precise, so a command like <c>PRINT #1, 1 = 1</c> is not mistaken for one.
  /// </summary>
  private bool IsDestructuringAhead() {
    if (!DialectFacts.IsAvailable(LanguageFeature.Tuples, this._dialect))
      return false;
    var i = this._pos;
    var lvalues = 0;
    for (;;) {
      if (this.TokenAt(i).Kind != TokenKind.Identifier)
        return false;                              // each target starts with a name
      ++i;
      for (;;) {                                    // optional (index) and .member chains
        var k = this.TokenAt(i).Kind;
        if (k == TokenKind.LParen) {
          if (!this.TrySkipBalancedParens(ref i))
            return false;
        } else if (k == TokenKind.Period && this.TokenAt(i + 1).Kind == TokenKind.Identifier) {
          i += 2;
        } else {
          break;
        }
      }
      ++lvalues;
      switch (this.TokenAt(i).Kind) {
        case TokenKind.Comma: ++i; continue;        // another target
        case TokenKind.Equals: return lvalues >= 2; // need at least two targets to be a destructuring
        default: return false;
      }
    }
  }

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
      // plain '=' assignment, or a compound 'OP=' (PB 3.6: += -= *= /= \= ^= &=)
      return kind == TokenKind.Equals
        || (CompoundOp(kind) != null && this.TokenAt(i + 1).Kind == TokenKind.Equals);
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
