using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// pb36 membership test: <c>x IN lo TO hi</c> is <c>(x &gt;= lo) AND (x &lt;= hi)</c>,
/// <c>x IN {a, b, lo TO hi}</c> an OR chain of per-element tests. Desugared entirely in the
/// parser (like chained comparison), so the decompilation shows plain comparisons. A range
/// also serves as a bare <c>FOR EACH v IN lo TO hi [STEP s]</c> source, no brackets needed.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class InOperatorTests {

  private static CompilationUnit Parse(string source, Dialect dialect = Dialect.Pb36)
    => Parser.Parse(Lexer.Tokenize(source, "t.bas", dialect), "t.bas", dialect);

  private static string Run(string source) {
    var unit = Parse(source);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return DosBoxRunner.Normalize(DosBoxRunner.Run(exe));
  }

  [Test]
  public void Parse_GivenInRange_WhenParsed_ThenDesugarsToBoundsAnd() {
    var stmt = (IfStmt)Parse("IF x% IN 3 TO 5 THEN PRINT 1\n").Statements[0];
    var and = (BinaryExpr)stmt.Condition;
    Assert.Multiple(() => {
      Assert.That(and.Op, Is.EqualTo(BinaryOp.And));
      Assert.That(((BinaryExpr)and.Left).Op, Is.EqualTo(BinaryOp.GreaterEqual));
      Assert.That(((BinaryExpr)and.Right).Op, Is.EqualTo(BinaryOp.LessEqual));
    });
  }

  [Test]
  public void Parse_GivenInList_WhenParsed_ThenDesugarsToEqualityOrChain() {
    var stmt = (IfStmt)Parse("IF x% IN {1, 4, 9} THEN PRINT 1\n").Statements[0];
    var or = (BinaryExpr)stmt.Condition;
    Assert.Multiple(() => {
      Assert.That(or.Op, Is.EqualTo(BinaryOp.Or));
      Assert.That(((BinaryExpr)or.Right).Op, Is.EqualTo(BinaryOp.Equal), "last element is an equality test");
    });
  }

  [Test]
  public void Parse_GivenSpreadInMembership_WhenParsed_ThenRejected() {
    Assert.Throws<ParserException>(() => Parse("IF x% IN {..a%} THEN PRINT 1\n"));
  }

  [Test]
  public void Parse_GivenInBelowPb36_WhenParsed_ThenRejected() {
    Assert.Throws<ParserException>(() => Parse("IF x% IN 3 TO 5 THEN PRINT 1\n", Dialect.Pb35));
  }

  [Test]
  public void Parse_GivenReplaceStatementUnderPb36_WhenParsed_ThenInStaysItsDelimiter() {
    // REPLACE find WITH with IN target uses IN as a statement delimiter - the membership
    // operator must stay suppressed inside its expression parts
    var stmt = Parse("t$ = \"abc\"\nREPLACE \"a\" WITH \"x\" IN t$\n").Statements[1];
    Assert.That(stmt, Is.InstanceOf<ReplaceStmt>());
  }

  [Test]
  public void Execute_GivenRangeAndListMembership_WhenFiltering_ThenSelectsMatching() {
    const string source = """
      FOR i% = 1 TO 10
        IF i% IN 3 TO 5 THEN PRINT i%;
      NEXT
      PRINT "|";
      FOR i% = 1 TO 10
        IF i% IN {1, 4, 9 TO 10} THEN PRINT i%;
      NEXT
      PRINT "end"
      """;
    Assert.That(Run(source), Is.EqualTo(" 3  4  5 | 1  4  9  10 end\n"));
  }

  [Test]
  public void Execute_GivenBareRangeForEach_WhenRun_ThenCountsWithStep() {
    const string source = """
      DIM v AS INTEGER
      FOR EACH v IN 1 TO 9 STEP 2
        PRINT v;
      NEXT
      PRINT "end"
      """;
    Assert.That(Run(source), Is.EqualTo(" 1  3  5  7  9 end\n"));
  }

  [Test]
  public void Render_GivenInMembership_WhenDecompiled_ThenPlainComparisonsRecompileUnderPb35() {
    const string source = "DIM x AS INTEGER\nx = 4\nIF x IN 3 TO 5 THEN PRINT \"yes\"\nIF x IN {1, 4, 9} THEN PRINT \"hit\"\n";
    var unit = Parse(source);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty);
    var basic = PowerBasic.Compiler.Emit.BasicWriter.Render(model, unit);
    Assert.That(basic, Does.Not.Contain(" IN "), "membership is lowered to comparisons");
    var model2 = Binder.Bind(Parse(basic, Dialect.Pb35), Dialect.Pb35);
    Assert.That(model2.Errors, Is.Empty, $"pb35 re-bind of:\n{basic}\nerrors: " + string.Join("; ", model2.Errors));
  }
}
