namespace PowerBasic.Compiler.Syntax;

/// <summary>
/// Streams lexed tokens with metastatement handling: <c>$INCLUDE</c> splices the
/// included file's tokens in place, <c>$IF</c>/<c>$ELSEIF</c>/<c>$ELSE</c>/<c>$ENDIF</c>
/// conditionally drop regions based on named-constant (equate) values observed in the
/// stream ($ELSEIF requires PB 3.5). All other tokens - including equate definitions
/// and other metastatements - pass through.
/// </summary>
public sealed class Preprocessor {

  private const int _maxIncludeDepth = 32;

  private readonly ISourceProvider _provider;
  private readonly Dialect _dialect;
  private readonly Dictionary<string, long> _equates = new(StringComparer.OrdinalIgnoreCase);
  private readonly List<string> _includeStack = [];
  private readonly Stack<Token> _openConditionals = [];

  private Preprocessor(ISourceProvider provider, Dialect dialect) {
    this._provider = provider;
    this._dialect = dialect;
  }

  /// <summary>Expands <paramref name="mainFile"/> into a single token stream ending with one <see cref="TokenKind.EndOfFile"/>.</summary>
  public static IEnumerable<Token> Expand(string mainFile, ISourceProvider provider, Dialect dialect = Dialect.Pb35) {
    var self = new Preprocessor(provider, dialect);
    if (!provider.TryReadSource(mainFile, null, out var text, out var resolved))
      throw new PreprocessorException($"cannot read source '{mainFile}'", new(mainFile, 0, 0));

    foreach (var token in self.ExpandFile(text, resolved))
      yield return token;

    if (self._openConditionals.Count > 0)
      throw new PreprocessorException("$IF without matching $ENDIF", self._openConditionals.Peek().Position);

    yield return new(TokenKind.EndOfFile, "", new(resolved, 0, 0));
  }

  /// <summary>Yields all tokens of one file except its <see cref="TokenKind.EndOfFile"/>.</summary>
  private IEnumerable<Token> ExpandFile(string text, string resolvedName) {
    if (this._includeStack.Contains(resolvedName, StringComparer.OrdinalIgnoreCase))
      throw new PreprocessorException($"circular $INCLUDE of '{resolvedName}'", new(resolvedName, 0, 0));
    if (this._includeStack.Count >= _maxIncludeDepth)
      throw new PreprocessorException($"$INCLUDE nesting deeper than {_maxIncludeDepth}", new(resolvedName, 0, 0));

    this._includeStack.Add(resolvedName);
    try {
      using var tokens = Lexer.Tokenize(text, resolvedName, this._dialect).GetEnumerator();
      var pending = new List<Token>();

      while (tokens.MoveNext()) {
        var token = tokens.Current;
        switch (token.Kind) {
          case TokenKind.EndOfFile:
            foreach (var t in this.FlushLine(pending))
              yield return t;
            yield break;

          case TokenKind.EndOfLine:
          case TokenKind.Colon when pending.Count == 0 || pending[0].Kind != TokenKind.MetaCommand:
            pending.Add(token);
            foreach (var t in this.FlushLine(pending))
              yield return t;
            break;

          case TokenKind.MetaCommand when token.Text == "INCLUDE":
            foreach (var t in this.FlushLine(pending))
              yield return t;
            if (!tokens.MoveNext() || tokens.Current.Kind != TokenKind.StringLiteral)
              throw new PreprocessorException("$INCLUDE requires a quoted file name", token.Position);
            foreach (var t in this.ExpandInclude(tokens.Current))
              yield return t;
            break;

          case TokenKind.MetaCommand when token.Text is "IF" or "ELSEIF" or "ELSE" or "ENDIF":
            foreach (var t in this.FlushLine(pending))
              yield return t;
            this.HandleConditional(token, tokens);
            break;

          default:
            pending.Add(token);
            break;
        }
      }
    } finally {
      this._includeStack.RemoveAt(this._includeStack.Count - 1);
    }
  }

  /// <summary>Emits a completed statement, recording any equate definition (<c>%NAME = const-expr</c>) it contains.</summary>
  private IEnumerable<Token> FlushLine(List<Token> pending) {
    if (pending.Count >= 3 && pending[0].Kind == TokenKind.NamedConstant && pending[1].Kind == TokenKind.Equals) {
      var bodyEnd = pending[^1].Kind is TokenKind.EndOfLine or TokenKind.Colon ? pending.Count - 1 : pending.Count;
      if (this.TryEvaluate(pending[2..bodyEnd], out var value))
        this._equates[pending[0].Text] = value;
    }

    foreach (var t in pending)
      yield return t;
    pending.Clear();
  }

  private IEnumerable<Token> ExpandInclude(Token nameToken) {
    var name = nameToken.StringValue!;
    if (!this._provider.TryReadSource(name, this._includeStack[^1], out var text, out var resolved))
      throw new PreprocessorException($"cannot read $INCLUDE file '{name}'", nameToken.Position);
    return this.ExpandFile(text, resolved);
  }

  private void HandleConditional(Token token, IEnumerator<Token> tokens) {
    switch (token.Text) {
      case "IF": {
        var condition = ReadToEndOfLine(tokens);
        if (!this.TryEvaluate(condition, out var value))
          throw new PreprocessorException("$IF condition is not a constant expression of known equates", token.Position);
        if (value != 0) {
          this._openConditionals.Push(token); // branch live - the matching $ENDIF is still owed
          break;
        }
        this.SkipToLiveBranch(tokens, token);
        break;
      }

      case "ELSEIF":
        this.RequireElseIf(token);
        if (this._openConditionals.Count == 0)
          throw new PreprocessorException("$ELSEIF without $IF", token.Position);
        // reaching a live $ELSEIF means an earlier branch was taken - skip to $ENDIF
        ReadToEndOfLine(tokens); // its condition is irrelevant
        SkipRegion(tokens, token, allowElse: false);
        this._openConditionals.Pop();
        break;

      case "ELSE":
        if (this._openConditionals.Count == 0)
          throw new PreprocessorException("$ELSE without $IF", token.Position);
        // reaching a live $ELSE means the $IF branch was taken - skip to $ENDIF
        SkipRegion(tokens, token, allowElse: false);
        this._openConditionals.Pop();
        break;

      case "ENDIF":
        if (this._openConditionals.Count == 0)
          throw new PreprocessorException("$ENDIF without $IF", token.Position);
        this._openConditionals.Pop();
        break;
    }
  }

  /// <summary>
  /// After a false condition: skips branches until a live <c>$ELSEIF</c>/<c>$ELSE</c>
  /// (the matching <c>$ENDIF</c> is then still owed) or the <c>$ENDIF</c> itself.
  /// </summary>
  private void SkipToLiveBranch(IEnumerator<Token> tokens, Token opener) {
    for (;;)
      switch (SkipRegion(tokens, opener, allowElse: true)) {
        case SkipStop.Else:
          this._openConditionals.Push(opener);
          return;

        case SkipStop.ElseIf: {
          this.RequireElseIf(opener);
          var condition = ReadToEndOfLine(tokens);
          if (!this.TryEvaluate(condition, out var value))
            throw new PreprocessorException("$ELSEIF condition is not a constant expression of known equates", opener.Position);
          if (value == 0)
            continue;
          this._openConditionals.Push(opener);
          return;
        }

        default:
          return; // $ENDIF
      }
  }

  private void RequireElseIf(Token token) {
    if (!DialectFacts.IsAvailable(LanguageFeature.ElseIfMeta, this._dialect))
      throw new PreprocessorException(DialectFacts.RequirementMessage(LanguageFeature.ElseIfMeta, this._dialect), token.Position);
  }

  private static List<Token> ReadToEndOfLine(IEnumerator<Token> tokens) {
    var result = new List<Token>();
    while (tokens.MoveNext() && tokens.Current.Kind is not (TokenKind.EndOfLine or TokenKind.EndOfFile))
      result.Add(tokens.Current);
    return result;
  }

  private enum SkipStop { Else, ElseIf, Endif }

  /// <summary>Discards tokens until the matching <c>$ENDIF</c> (or live <c>$ELSEIF</c>/<c>$ELSE</c>), honoring nesting.</summary>
  private static SkipStop SkipRegion(IEnumerator<Token> tokens, Token opener, bool allowElse) {
    var depth = 0;
    while (tokens.MoveNext()) {
      var t = tokens.Current;
      if (t.Kind == TokenKind.EndOfFile)
        break;
      if (t.Kind != TokenKind.MetaCommand)
        continue;

      switch (t.Text) {
        case "IF":
          ++depth;
          break;
        case "ELSEIF" when depth == 0 && allowElse:
          return SkipStop.ElseIf;
        case "ELSE" when depth == 0 && allowElse:
          return SkipStop.Else;
        case "ENDIF" when depth == 0:
          return SkipStop.Endif;
        case "ENDIF":
          --depth;
          break;
      }
    }

    throw new PreprocessorException($"${opener.Text} without matching $ENDIF", opener.Position);
  }

  #region constant expression evaluation

  /// <summary>Minimal constant folder for equate expressions: literals, equates, NOT/-, * \ + - comparisons, AND/OR.</summary>
  private bool TryEvaluate(IReadOnlyList<Token> tokens, out long value) {
    var pos = 0;
    try {
      value = this.EvalOr(tokens, ref pos);
      return pos == tokens.Count;
    } catch (PreprocessorException) {
      value = 0;
      return false;
    }
  }

  private long EvalOr(IReadOnlyList<Token> t, ref int pos) {
    var left = this.EvalAnd(t, ref pos);
    while (IsKeyword(t, pos, "OR")) {
      ++pos;
      var right = this.EvalAnd(t, ref pos);
      left = (left != 0 || right != 0) ? -1 : 0;
    }
    return left;
  }

  private long EvalAnd(IReadOnlyList<Token> t, ref int pos) {
    var left = this.EvalComparison(t, ref pos);
    while (IsKeyword(t, pos, "AND")) {
      ++pos;
      var right = this.EvalComparison(t, ref pos);
      left = (left != 0 && right != 0) ? -1 : 0;
    }
    return left;
  }

  private long EvalComparison(IReadOnlyList<Token> t, ref int pos) {
    var left = this.EvalAdditive(t, ref pos);
    if (pos >= t.Count)
      return left;

    var op = t[pos].Kind;
    if (op is not (TokenKind.Equals or TokenKind.NotEquals or TokenKind.Less or TokenKind.Greater or TokenKind.LessEquals or TokenKind.GreaterEquals))
      return left;

    ++pos;
    var right = this.EvalAdditive(t, ref pos);
    var result = op switch {
      TokenKind.Equals => left == right,
      TokenKind.NotEquals => left != right,
      TokenKind.Less => left < right,
      TokenKind.Greater => left > right,
      TokenKind.LessEquals => left <= right,
      _ => left >= right,
    };
    return result ? -1 : 0;
  }

  private long EvalAdditive(IReadOnlyList<Token> t, ref int pos) {
    var left = this.EvalMultiplicative(t, ref pos);
    while (pos < t.Count && t[pos].Kind is TokenKind.Plus or TokenKind.Minus) {
      var op = t[pos++].Kind;
      var right = this.EvalMultiplicative(t, ref pos);
      left = op == TokenKind.Plus ? left + right : left - right;
    }
    return left;
  }

  private long EvalMultiplicative(IReadOnlyList<Token> t, ref int pos) {
    var left = this.EvalUnary(t, ref pos);
    while (pos < t.Count && t[pos].Kind is TokenKind.Star or TokenKind.Backslash) {
      var op = t[pos++].Kind;
      var right = this.EvalUnary(t, ref pos);
      if (op == TokenKind.Backslash && right == 0)
        throw new PreprocessorException("division by zero in constant expression", t[pos - 1].Position);
      left = op == TokenKind.Star ? left * right : left / right;
    }
    return left;
  }

  private long EvalUnary(IReadOnlyList<Token> t, ref int pos) {
    if (pos >= t.Count)
      throw new PreprocessorException("unexpected end of constant expression", default);

    var token = t[pos];
    if (token.Kind == TokenKind.Minus) {
      ++pos;
      return -this.EvalUnary(t, ref pos);
    }

    if (IsKeyword(t, pos, "NOT")) {
      ++pos;
      return this.EvalUnary(t, ref pos) == 0 ? -1 : 0;
    }

    if (token.Kind == TokenKind.LParen) {
      ++pos;
      var inner = this.EvalOr(t, ref pos);
      if (pos >= t.Count || t[pos].Kind != TokenKind.RParen)
        throw new PreprocessorException("missing ')' in constant expression", token.Position);
      ++pos;
      return inner;
    }

    switch (token.Kind) {
      case TokenKind.IntegerLiteral:
        ++pos;
        return token.IntegerValue;

      case TokenKind.NamedConstant when this._equates.TryGetValue(token.Text, out var known):
        ++pos;
        return known;

      default:
        throw new PreprocessorException($"'{token.Text}' is not usable in a constant expression", token.Position);
    }
  }

  private static bool IsKeyword(IReadOnlyList<Token> t, int pos, string keyword)
    => pos < t.Count && t[pos].Kind == TokenKind.Identifier && t[pos].Text.Equals(keyword, StringComparison.OrdinalIgnoreCase);

  #endregion
}

/// <summary>Raised for metastatement errors ($INCLUDE resolution, $IF evaluation, nesting).</summary>
public sealed class PreprocessorException(string message, SourcePosition position) : Exception($"{position}: {message}") {
  public SourcePosition Position { get; } = position;
}
