using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.Syntax;

/// <summary>
/// <see cref="AstWalker"/> visits every expression and statement inside a body - the soundness
/// foundation of every analysis built on it - and never descends into a separately compiled body.
/// </summary>
[TestFixture]
public sealed class AstWalkerTests {

  private static SemanticModel Bind(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static IReadOnlyList<string> NameRefs(IReadOnlyList<Statement> body)
    => [.. AstWalker.DescendantNodes(body).OfType<NameExpr>().Select(n => n.Name)];

  [Test]
  public void DescendantNodes_GivenNestedAndTupleBearingStatements_ThenFindsEveryExpression() {
    // references buried in a nested IF body, a FOR header, and a LINE statement's coordinate
    // tuples must all be discovered - a missed one would let DCE drop live code.
    var model = Bind("""
      DIM a AS INTEGER, b AS INTEGER, c AS INTEGER, d AS INTEGER
      IF a THEN
        FOR b = c TO d
          PRINT b
        NEXT
      END IF
      LINE (a, b)-(c, d)
      """);
    var names = NameRefs(model.MainBody);
    foreach (var n in new[] { "a", "b", "c", "d" })
      Assert.That(names, Does.Contain(n), $"reference to {n} must be reachable through the walker");
  }

  [Test]
  public void DescendantNodes_GivenNestedProcedure_ThenDoesNotDescendIntoItsBody() {
    // a nested SUB is a separate procedure; the outer walk must not pull its body in
    var model = Bind("""
      DECLARE SUB Outer()
      Outer
      SUB Outer()
        DIM seenOuter AS INTEGER
        seenOuter = 1
        SUB Inner()
          DIM seenInner AS INTEGER
          seenInner = 2
        END SUB
      END SUB
      """);
    var outer = model.Procedures["Outer"];
    var names = NameRefs(outer.Body!);
    Assert.That(names, Does.Contain("seenOuter"));
    Assert.That(names, Does.Not.Contain("seenInner"), "the nested procedure's body is walked on its own, not as part of the outer");
  }
}
