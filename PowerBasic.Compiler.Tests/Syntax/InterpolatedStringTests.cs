using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.Syntax;

/// <summary>
/// Front-end coverage for the PB 3.6 interpolated string <c>$"text {expr} {expr:fmt}"</c>:
/// lexing, parsing into literal/hole parts, and the binder's desugaring to a concatenation
/// of string literals, <c>STR$</c> (numeric holes), the hole itself (STRING holes) and
/// <c>USING$</c> (formatted holes). Strictly gated to pb36.
/// </summary>
[TestFixture]
public sealed class InterpolatedStringTests {

  private static Expression ParseInterp(string literal, Dialect dialect = Dialect.Pb36) {
    var tokens = Lexer.Tokenize("x$ = " + literal, "test.bas", dialect);
    var unit = Parser.Parse(tokens, "test.bas", dialect);
    Assert.That(unit.Statements, Has.Count.EqualTo(1));
    return ((AssignStmt)unit.Statements[0]).Value;
  }

  private static (SemanticModel Model, Expression Desugared) BindInterp(string program, string interpExprText) {
    var source = program;
    var tokens = Lexer.Tokenize(source, "test.bas", Dialect.Pb36);
    var unit = Parser.Parse(tokens, "test.bas", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors.Select(e => e.Message)));
    var interp = FindInterp(unit);
    Assert.That(interp, Is.Not.Null, interpExprText);
    Assert.That(model.Desugared.TryGetValue(interp!, out var desugared), Is.True, "no desugaring recorded");
    return (model, desugared!);
  }

  private static InterpolatedStringExpr? FindInterp(CompilationUnit unit) {
    InterpolatedStringExpr? found = null;
    void Walk(Expression e) {
      if (e is InterpolatedStringExpr i) {
        found ??= i;
        return;
      }
      foreach (var child in AstQuery.Subexpressions(e))
        Walk(child);
    }
    foreach (var stmt in unit.Statements)
      if (stmt is AssignStmt a)
        Walk(a.Value);
    return found;
  }

  /// <summary>Flattens a left-leaning '&amp;' concatenation tree into its leaf pieces.</summary>
  private static List<Expression> ConcatLeaves(Expression e) {
    var leaves = new List<Expression>();
    void Walk(Expression x) {
      if (x is BinaryExpr { Op: BinaryOp.Concat } b) {
        Walk(b.Left);
        Walk(b.Right);
      } else
        leaves.Add(x);
    }
    Walk(e);
    return leaves;
  }

  #region lexing & parsing

  [Test]
  public void Parse_GivenPlainText_WhenInterpolated_ThenOneLiteralPart() {
    var expr = (InterpolatedStringExpr)ParseInterp("$\"hello\"");
    Assert.That(expr.Parts, Has.Count.EqualTo(1));
    Assert.That(expr.Parts[0].Literal, Is.EqualTo("hello"));
    Assert.That(expr.Parts[0].Hole, Is.Null);
  }

  [Test]
  public void Parse_GivenEmptyInterpolation_WhenParsed_ThenNoParts() {
    var expr = (InterpolatedStringExpr)ParseInterp("$\"\"");
    Assert.That(expr.Parts, Is.Empty);
  }

  [Test]
  public void Parse_GivenSingleHole_WhenParsed_ThenLiteralHoleLiteralSplit() {
    var expr = (InterpolatedStringExpr)ParseInterp("$\"a {x} b\"");
    Assert.That(expr.Parts.Select(p => p.Literal), Is.EqualTo(new[] { "a ", null, " b" }));
    Assert.That(expr.Parts[1].Hole, Is.InstanceOf<NameExpr>());
    Assert.That(((NameExpr)expr.Parts[1].Hole!).Name, Is.EqualTo("x"));
  }

  [Test]
  public void Parse_GivenFormatHole_WhenParsed_ThenFormatCaptured() {
    var expr = (InterpolatedStringExpr)ParseInterp("$\"{x:###.##}\"");
    Assert.That(expr.Parts, Has.Count.EqualTo(1));
    Assert.That(expr.Parts[0].Hole, Is.InstanceOf<NameExpr>());
    Assert.That(expr.Parts[0].Format, Is.EqualTo("###.##"));
  }

  [Test]
  public void Parse_GivenBraceEscapes_WhenParsed_ThenLiteralBraces() {
    var expr = (InterpolatedStringExpr)ParseInterp("$\"{{ {x} }}\"");
    Assert.That(expr.Parts[0].Literal, Is.EqualTo("{ "));
    Assert.That(expr.Parts[1].Hole, Is.Not.Null);
    Assert.That(expr.Parts[2].Literal, Is.EqualTo(" }"));
  }

  [Test]
  public void Parse_GivenNestedStringInHole_WhenParsed_ThenQuotesAndBracesInsideHoleIgnored() {
    // BASIC: $"{a$ + "}"}" - the '"' and the '}' inside the hole's string literal belong
    // to the nested expression, not to the interpolation's closer.
    var expr = (InterpolatedStringExpr)ParseInterp("$\"{a$ + \"}\"}\"");
    Assert.That(expr.Parts, Has.Count.EqualTo(1));
    var hole = expr.Parts[0].Hole;
    Assert.That(hole, Is.InstanceOf<BinaryExpr>());
    Assert.That(((BinaryExpr)hole!).Right, Is.InstanceOf<StringLiteralExpr>());
    Assert.That(((StringLiteralExpr)((BinaryExpr)hole).Right).Value, Is.EqualTo("}"));
  }

  [Test]
  public void Parse_GivenExpressionHole_WhenParsed_ThenWholeExpressionParsed() {
    var expr = (InterpolatedStringExpr)ParseInterp("$\"sum {a% + b% * 2}\"");
    Assert.That(expr.Parts[1].Hole, Is.InstanceOf<BinaryExpr>());
  }

  #endregion

  #region dialect gating

  [Test]
  public void Lex_GivenInterpolation_WhenPb35_ThenRejectedWith36Gate() {
    var ex = Assert.Throws<LexerException>(
      () => Lexer.Tokenize("x$ = $\"a {x} b\"", "test.bas", Dialect.Pb35).ToList());
    Assert.That(ex!.Message, Does.Contain("requires PowerBASIC 3.6"));
  }

  [Test]
  public void Lex_GivenPlainStringWithDollarSuffix_WhenPb35_ThenUnaffected() {
    // pb35: '$' stays a type suffix / metacommand intro; only '$' directly before '"'
    // starts an interpolation, so ordinary pb35 source is untouched.
    var tokens = Lexer.Tokenize("a$ = \"hi\"", "test.bas", Dialect.Pb35).ToList();
    Assert.That(tokens.Any(t => t.Kind == TokenKind.InterpString), Is.False);
    Assert.That(tokens.Any(t => t.Kind == TokenKind.StringLiteral && t.StringValue == "hi"), Is.True);
  }

  #endregion

  #region binder desugaring

  [Test]
  public void Bind_GivenNumericHole_WhenBound_ThenWrappedInStrDollar() {
    var (_, desugared) = BindInterp("x% = 5\ny$ = $\"n={x%}\"", "$\"n={x%}\"");
    var leaves = ConcatLeaves(desugared);
    Assert.That(leaves[0], Is.InstanceOf<StringLiteralExpr>());
    Assert.That(((StringLiteralExpr)leaves[0]).Value, Is.EqualTo("n="));
    Assert.That(leaves[1], Is.InstanceOf<CallOrIndexExpr>());
    Assert.That(((CallOrIndexExpr)leaves[1]).Name, Is.EqualTo("STR$"));
  }

  [Test]
  public void Bind_GivenStringHole_WhenBound_ThenHoleUsedAsIs() {
    var (_, desugared) = BindInterp("s$ = \"hi\"\ny$ = $\"[{s$}]\"", "$\"[{s$}]\"");
    var leaves = ConcatLeaves(desugared);
    // "[" & s$ & "]" - the string hole is concatenated directly (no STR$)
    Assert.That(leaves.Any(l => l is CallOrIndexExpr), Is.False);
    Assert.That(leaves[1], Is.InstanceOf<NameExpr>());
    Assert.That(((NameExpr)leaves[1]).Name, Is.EqualTo("s"));
  }

  [Test]
  public void Bind_GivenFormatHole_WhenBound_ThenUsesUsingDollarFormatter() {
    var (_, desugared) = BindInterp("x! = 1.5\ny$ = $\"{x!:###.##}\"", "$\"{x!:###.##}\"");
    var call = (CallOrIndexExpr)ConcatLeaves(desugared)[0];
    Assert.That(call.Name, Is.EqualTo("USING$"));
    Assert.That(call.Arguments, Has.Count.EqualTo(2));
    Assert.That(((StringLiteralExpr)call.Arguments[0]).Value, Is.EqualTo("###.##"));
  }

  [Test]
  public void Bind_GivenInterpolation_WhenBound_ThenTypeIsString() {
    var (model, _) = BindInterp("x% = 5\ny$ = $\"n={x%}\"", "$\"n={x%}\"");
    Assert.That(model.Desugared.Keys.All(k => model.TypeOf(k) is StringType), Is.True);
  }

  [Test]
  public void Bind_GivenEmptyInterpolation_WhenBound_ThenEmptyStringLiteral() {
    var (_, desugared) = BindInterp("y$ = $\"\"", "$\"\"");
    Assert.That(desugared, Is.InstanceOf<StringLiteralExpr>());
    Assert.That(((StringLiteralExpr)desugared).Value, Is.EqualTo(""));
  }

  [Test]
  public void Bind_GivenUdtHole_WhenBound_ThenErrorRequiresStringOrNumeric() {
    const string source = "TYPE T\n  a AS INTEGER\nEND TYPE\nDIM p AS T\ny$ = $\"{p}\"";
    var tokens = Lexer.Tokenize(source, "test.bas", Dialect.Pb36);
    var model = Binder.Bind(Parser.Parse(tokens, "test.bas", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors.Select(e => e.Message), Has.Some.Contains("STRING or numeric"));
  }

  #endregion
}
