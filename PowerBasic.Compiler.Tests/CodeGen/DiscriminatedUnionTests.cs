using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// pb36 discriminated unions: a UNION whose members are CASEs with per-case payload fields.
/// Lowered entirely in the parser onto existing machinery: a tagged TYPE (hidden $tag INTEGER
/// plus per-case view TYPEs overlapping AT offset 2), case-name constructors as assignment
/// sources, and the IS pattern test (<c>IF s IS Circle c THEN ... c.Radius</c>) as a tag
/// compare with an optional payload-copy binding hoisted before the IF.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class DiscriminatedUnionTests {

  private const string _SHAPE = """
    UNION Shape
      CASE Circle
        Radius AS SINGLE
      CASE Rect
        W AS INTEGER
        H AS INTEGER
      CASE Dot
    END UNION

    """;

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
  public void Parse_GivenDiscriminatedUnion_WhenParsed_ThenLowersToViewTypesAndTaggedType() {
    var unit = Parse(_SHAPE);
    var types = unit.Statements.OfType<TypeDecl>().ToList();
    Assert.Multiple(() => {
      Assert.That(types.Select(t => t.Name), Is.EquivalentTo(new[] { "Shape_Circle", "Shape_Rect", "Shape" }), "one view TYPE per payload case plus the tagged carrier (payloadless Dot gets none)");
      var shape = types.Single(t => t.Name == "Shape");
      Assert.That(shape.Fields[0].Name, Is.EqualTo("$tag"));
      Assert.That(shape.Fields.Skip(1).All(f => f.ExplicitOffset != null), Is.True, "case slots overlap AT a fixed offset behind the tag");
    });
  }

  [Test]
  public void Parse_GivenCaseConstructorAssignment_WhenParsed_ThenLowersToTagAndFieldStores() {
    var unit = Parse(_SHAPE + "DIM s AS Shape\ns = Rect(3, 4)\n");
    var assigns = unit.Statements.OfType<AssignStmt>().ToList();
    Assert.Multiple(() => {
      Assert.That(assigns, Has.Count.EqualTo(3), "tag store + one store per payload field");
      Assert.That(assigns[0].Target, Is.InstanceOf<MemberExpr>());
      Assert.That(((MemberExpr)assigns[0].Target).Member, Is.EqualTo("$tag"));
      Assert.That(((IntegerLiteralExpr)assigns[0].Value).Value, Is.EqualTo(1), "Rect is the second case");
    });
  }

  [Test]
  public void Parse_GivenIsTestWithoutBinding_WhenParsed_ThenTagComparison() {
    var unit = Parse(_SHAPE + "DIM s AS Shape\nIF s IS Dot THEN PRINT 1\n");
    var iff = unit.Statements.OfType<IfStmt>().Single();
    var cmp = (BinaryExpr)iff.Condition;
    Assert.Multiple(() => {
      Assert.That(cmp.Op, Is.EqualTo(BinaryOp.Equal));
      Assert.That(((MemberExpr)cmp.Left).Member, Is.EqualTo("$tag"));
      Assert.That(((IntegerLiteralExpr)cmp.Right).Value, Is.EqualTo(2));
    });
  }

  [Test]
  public void Parse_GivenIsBindingOnPayloadlessCase_WhenParsed_ThenRejected() {
    Assert.Throws<ParserException>(() => Parse(_SHAPE + "DIM s AS Shape\nIF s IS Dot d THEN PRINT 1\n"));
  }

  [Test]
  public void Parse_GivenDiscriminatedUnionBelowPb36_WhenParsed_ThenRejected() {
    Assert.Throws<ParserException>(() => Parse(_SHAPE, Dialect.Pb35));
  }

  [Test]
  public void Execute_GivenConstructAndDispatch_WhenRun_ThenPayloadsReadThroughBindings() {
    const string source = _SHAPE + """
      DECLARE SUB Show(BYREF s AS Shape)
      DIM s AS Shape
      s = Circle(2.5)
      Show s
      s = Rect(3, 4)
      Show s
      s = Dot
      Show s

      SUB Show(BYREF s AS Shape)
        IF s IS Circle c THEN PRINT "circle"; c.Radius
        IF s IS Rect r THEN PRINT "rect"; r.W * r.H
        IF s IS Dot THEN PRINT "dot"
      END SUB
      """;
    Assert.That(Run(source), Is.EqualTo("circle 2.5\nrect 12\ndot\n"));
  }

  [Test]
  public void Execute_GivenIsBindingInElse_WhenRun_ThenElseArmSeesOwnBinding() {
    const string source = _SHAPE + """
      DIM s AS Shape
      s = Rect(5, 6)
      IF s IS Circle c THEN
        PRINT "circle"; c.Radius
      ELSEIF s IS Rect r THEN
        PRINT "rect"; r.W; r.H
      ELSE
        PRINT "other"
      END IF
      """;
    Assert.That(Run(source), Is.EqualTo("rect 5  6\n"));
  }

  [Test]
  public void Execute_GivenIsBindingInTernary_WhenRun_ThenBindingHoistsBeforeStatement() {
    const string source = _SHAPE + """
      DIM s AS Shape
      DIM r AS SINGLE
      s = Circle(2.5)
      r = IF(s IS Circle c, c.Radius, 0!)
      PRINT r
      s = Dot
      r = IF(s IS Circle c2, c2.Radius, 0!)
      PRINT r
      """;
    Assert.That(Run(source), Is.EqualTo(" 2.5\n 0\n"));
  }

  [Test]
  public void Parse_GivenIsBindingInLoopTest_WhenParsed_ThenRejected() {
    // the hoisted copy would run once before the loop, not per pass - stale binding
    Assert.Throws<ParserException>(() =>
      Parse(_SHAPE + "DIM s AS Shape\nDO WHILE s IS Circle c\n  PRINT c.Radius\nLOOP\n"));
  }

  [Test]
  public void Execute_GivenSelectCasePatternArms_WhenRun_ThenDispatchesOnTagWithBindings() {
    const string source = _SHAPE + """
      DECLARE SUB Show(BYREF s AS Shape)
      DIM s AS Shape
      s = Circle(2.5)
      Show s
      s = Rect(3, 4)
      Show s
      s = Dot
      Show s

      SUB Show(BYREF s AS Shape)
        SELECT CASE s
          CASE Circle c
            PRINT "circle"; c.Radius
          CASE Rect r
            PRINT "rect"; r.W * r.H
          CASE ELSE
            PRINT "other"
        END SELECT
      END SUB
      """;
    Assert.That(Run(source), Is.EqualTo("circle 2.5\nrect 12\nother\n"));
  }

  [Test]
  public void Parse_GivenSelectCaseMixingPatternAndPlainSelectors_WhenParsed_ThenRejected() {
    Assert.Throws<ParserException>(() =>
      Parse(_SHAPE + "DIM s AS Shape\nSELECT CASE s\n  CASE Circle\n    PRINT 1\n  CASE 5\n    PRINT 2\nEND SELECT\n"));
  }

  [Test]
  public void Render_GivenSelectCasePatterns_WhenDecompiled_ThenTagSelectRecompilesUnderPb35() {
    var source = _SHAPE + "DIM s AS Shape\ns = Rect(3, 4)\nSELECT CASE s\n  CASE Circle c\n    PRINT c.Radius\n  CASE Rect r\n    PRINT r.W\nEND SELECT\n";
    var unit = Parse(source);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var basic = PowerBasic.Compiler.Emit.BasicWriter.Render(model, unit);
    Assert.That(basic, Does.Contain("SELECT CASE").And.Not.Contain("$tag"), $"tag select with sanitized member:\n{basic}");
    var model2 = Binder.Bind(Parse(basic, Dialect.Pb35), Dialect.Pb35);
    Assert.That(model2.Errors, Is.Empty, $"pb35 re-bind of:\n{basic}\nerrors: " + string.Join("; ", model2.Errors));
  }

  [Test]
  public void Render_GivenDiscriminatedUnion_WhenDecompiled_ThenPlainTypesRecompileUnderPb35() {
    var source = _SHAPE + "DIM s AS Shape\ns = Circle(1.5)\nIF s IS Circle c THEN PRINT c.Radius\n";
    var unit = Parse(source);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var basic = PowerBasic.Compiler.Emit.BasicWriter.Render(model, unit);
    Assert.That(basic, Does.Not.Contain("$tag").And.Not.Contain(" IS "), $"hidden names sanitized, IS lowered:\n{basic}");
    var model2 = Binder.Bind(Parse(basic, Dialect.Pb35), Dialect.Pb35);
    Assert.That(model2.Errors, Is.Empty, $"pb35 re-bind of:\n{basic}\nerrors: " + string.Join("; ", model2.Errors));
  }
}
