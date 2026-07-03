using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.Semantics;

/// <summary>
/// pb36 nullable types <c>T?</c>: a synthesized UDT with a <c>Value</c> field of T and an INTEGER
/// <c>HasValue</c> presence flag. <c>x = v</c> sets both; <c>x = NOTHING</c> clears the flag;
/// <c>x ?? d</c> yields the value or the fallback; a nullable auto-unwraps to its <c>.Value</c> in
/// arithmetic and when assigned to a plain target. pb36-only - verified by these binder tests +
/// execution, not the differential oracle (genuine PBC has no <c>?</c> type marker).
/// </summary>
[TestFixture]
public sealed class NullableBinderTests {

  private static SemanticModel Bind(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "t.bas", Dialect.Pb36), "t.bas", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Success, Is.True, string.Join("; ", model.Errors));
    return model;
  }

  [Test]
  public void Bind_GivenNullableVariable_ThenSynthesizesValueAndHasValueFields() {
    var model = Bind("DIM x AS LONG?\n");
    var udt = (UdtType)model.ModuleVariables.Values.Single(v => v.Name.Equals("x", System.StringComparison.OrdinalIgnoreCase)).Type;
    Assert.Multiple(() => {
      Assert.That(model.NullableUnderlying.ContainsKey(udt.Name), Is.True, "the UDT is registered as nullable");
      Assert.That(udt.FindField("Value")!.Type, Is.EqualTo(PbType.Long));
      Assert.That(udt.FindField("HasValue")!.Type, Is.EqualTo(PbType.Integer));
    });
  }

  [Test]
  public void Bind_GivenIdenticalNullables_ThenShareOneUdt() {
    var model = Bind("DIM a AS LONG?\nDIM b AS LONG?\n");
    var a = (UdtType)model.ModuleVariables.Values.Single(v => v.Name.Equals("a", System.StringComparison.OrdinalIgnoreCase)).Type;
    var b = (UdtType)model.ModuleVariables.Values.Single(v => v.Name.Equals("b", System.StringComparison.OrdinalIgnoreCase)).Type;
    Assert.That(a.Name, Is.EqualTo(b.Name));
  }

  [Test]
  public void Bind_GivenValueAssignment_ThenSetsValueAndFlag() {
    var model = Bind("DIM x AS LONG?\nx = 5\n");
    var assign = model.DesugaredStatements.Keys.OfType<AssignStmt>().Single(a => a.Target is NameExpr { Name: "x" } && a.Value is IntegerLiteralExpr);
    var group = (GroupStmt)model.DesugaredStatements[assign];
    Assert.Multiple(() => {
      Assert.That(group.Body.OfType<AssignStmt>().Any(s => s.Target is MemberExpr { Member: "Value" }), Is.True);
      Assert.That(group.Body.OfType<AssignStmt>().Any(s => s.Target is MemberExpr { Member: "HasValue" }), Is.True);
    });
  }

  [Test]
  public void Bind_GivenNothingAssignment_ThenClearsFlag() {
    var model = Bind("DIM x AS LONG?\nx = NOTHING\n");
    var assign = model.DesugaredStatements.Keys.OfType<AssignStmt>().Single(a => a.Value is NothingExpr);
    var clear = (AssignStmt)model.DesugaredStatements[assign];
    Assert.Multiple(() => {
      Assert.That(clear.Target, Is.InstanceOf<MemberExpr>());
      Assert.That(((MemberExpr)clear.Target).Member, Is.EqualTo("HasValue"));
      Assert.That(((IntegerLiteralExpr)clear.Value).Value, Is.EqualTo(0));
    });
  }

  [Test]
  public void Bind_GivenCoalesce_ThenLowersToTernaryOnHasValue() {
    var model = Bind("DIM x AS LONG?\nDIM r&\nr& = x ?? -1\n");
    var co = model.Desugared.Keys.OfType<CoalesceExpr>().Single();
    var ternary = (IfExpr)model.Desugared[co];
    Assert.Multiple(() => {
      Assert.That(((MemberExpr)ternary.Condition).Member, Is.EqualTo("HasValue"));
      Assert.That(((MemberExpr)ternary.WhenTrue).Member, Is.EqualTo("Value"));
    });
  }

  [Test]
  public void Bind_GivenNullableInArithmetic_ThenAutoUnwrapsToValue() {
    var model = Bind("DIM x AS LONG?\nDIM r&\nr& = x + 8\n");
    var add = model.Desugared.Keys.OfType<BinaryExpr>().Single(b => b.Op == BinaryOp.Add);
    var lowered = (BinaryExpr)model.Desugared[add];
    Assert.That(((MemberExpr)lowered.Left).Member, Is.EqualTo("Value"), "the nullable operand reads .Value");
  }

  [Test]
  public void Bind_GivenNullableAssignedToPlain_ThenAutoUnwraps() {
    var model = Bind("DIM x AS LONG?\nDIM p&\np& = x\n");
    var assign = model.DesugaredStatements.Keys.OfType<AssignStmt>().Single(a => a.Target is NameExpr { Name: "p" });
    var unwrap = (AssignStmt)model.DesugaredStatements[assign];
    Assert.That(((MemberExpr)unwrap.Value).Member, Is.EqualTo("Value"));
  }

  [Test]
  public void Bind_GivenSpacedQuestion_ThenAlsoNullable() {
    var model = Bind("DIM x AS LONG ?\n");
    var udt = (UdtType)model.ModuleVariables.Values.Single(v => v.Name.Equals("x", System.StringComparison.OrdinalIgnoreCase)).Type;
    Assert.That(model.NullableUnderlying.ContainsKey(udt.Name), Is.True);
  }

  [Test]
  public void Bind_GivenNullableBelowPb36_ThenRejected() {
    Assert.Throws<ParserException>(() =>
      Parser.Parse(Lexer.Tokenize("DIM x AS LONG ?\n", "t.bas", Dialect.Pb35), "t.bas", Dialect.Pb35));
  }

  [Test]
  public void Lex_GivenGluedDoubleQuestion_ThenWordSuffixNotCoalesce() {
    // n?? glued is the WORD type-suffix, not the coalescing operator (whitespace disambiguates)
    var tokens = Lexer.Tokenize("n??\n", "t.bas", Dialect.Pb36).ToList();
    Assert.That(tokens[0].Suffix, Is.EqualTo(TypeSuffix.Word));
    Assert.That(tokens.Any(t => t.Kind == TokenKind.QuestionQuestion), Is.False);
  }

  [Test]
  public void Lex_GivenSpacedDoubleQuestion_ThenCoalesceOperator() {
    var tokens = Lexer.Tokenize("a ?? b\n", "t.bas", Dialect.Pb36);
    Assert.That(tokens.Any(t => t.Kind == TokenKind.QuestionQuestion), Is.True);
  }

  [Test]
  public void Lex_GivenGluedDoubleQuestionThenOperand_ThenCoalesceByContext() {
    // a??15 (no space): context (an operand follows) makes ?? the coalescing operator, not a WORD suffix
    var tokens = Lexer.Tokenize("a??15\n", "t.bas", Dialect.Pb36).ToList();
    Assert.Multiple(() => {
      Assert.That(tokens[0].Suffix, Is.EqualTo(TypeSuffix.None), "a takes no suffix - the ?? is the operator");
      Assert.That(tokens.Any(t => t.Kind == TokenKind.QuestionQuestion), Is.True);
    });
  }

  [Test]
  public void Lex_GivenGluedSuffixThenCoalesce_ThenSplitsByContext() {
    // a????5: the first ?? is the WORD suffix, the trailing ?? the coalescing operator before operand 5
    var tokens = Lexer.Tokenize("a????5\n", "t.bas", Dialect.Pb36).ToList();
    Assert.Multiple(() => {
      Assert.That(tokens[0].Suffix, Is.EqualTo(TypeSuffix.Word));
      Assert.That(tokens.Count(t => t.Kind == TokenKind.QuestionQuestion), Is.EqualTo(1));
    });
  }
}
