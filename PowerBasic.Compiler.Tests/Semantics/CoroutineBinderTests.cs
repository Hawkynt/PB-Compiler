using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.Semantics;

/// <summary>
/// Binding of PB 3.6 generators: a FUNCTION whose body contains YIELD is lowered to a
/// first-class enumerator TYPE named after the generator (with MoveNext / Current / Reset),
/// and calling it (e = Gen()) constructs an instance rather than calling a procedure.
/// </summary>
[TestFixture]
public sealed class CoroutineBinderTests {

  private const string _gen =
    "FUNCTION Gen() AS INTEGER\n  YIELD 10\n  YIELD 20\nEND FUNCTION\n";

  private static SemanticModel Bind(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "t.bas", Dialect.Pb36), "t.bas", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Success, Is.True, string.Join("; ", model.Errors));
    return model;
  }

  [Test]
  public void Bind_GivenGenerator_WhenBound_ThenEnumeratorTypeWithMembersSynthesized() {
    var model = Bind(_gen + "DIM e AS Gen\n");
    Assert.Multiple(() => {
      Assert.That(model.Udts.ContainsKey("Gen"), Is.True, "the generator name becomes an enumerator TYPE");
      Assert.That(model.Procedures.ContainsKey("Gen.MoveNext"), Is.True);
      Assert.That(model.Procedures.ContainsKey("Gen.get_Current"), Is.True);
      Assert.That(model.Procedures.ContainsKey("Gen.Reset"), Is.True);
      Assert.That(model.Udts["Gen"].FindField("$state"), Is.Not.Null, "the enumerator holds resume state");
      Assert.That(model.Procedures.ContainsKey("Gen"), Is.False, "the generator is not a callable procedure");
    });
  }

  [Test]
  public void Bind_GivenForEachOverGenerator_WhenBound_ThenLowersToIteratorLoop() {
    var model = Bind(_gen + "FOR EACH v IN Gen()\n  PRINT v\nNEXT\n");
    var foreachStmt = model.DesugaredStatements.Keys.OfType<ForEachStmt>().Single();
    // lowered to: IF (-1) THEN <construct> : WHILE $e.MoveNext ... WEND
    Assert.That(model.DesugaredStatements[foreachStmt], Is.InstanceOf<IfStmt>());
    Assert.That(((IfStmt)model.DesugaredStatements[foreachStmt]).Then.OfType<DoLoopStmt>().Any(), Is.True,
      "the generator FOR EACH lowers to a MoveNext WHILE loop");
  }

  [Test]
  public void Bind_GivenGeneratorConstruction_WhenBound_ThenAssignmentDesugarsToStateReset() {
    var model = Bind(_gen + "DIM e AS Gen\ne = Gen()\n");
    var assign = model.DesugaredStatements.Keys.OfType<AssignStmt>()
      .Single(a => a.Value is CallOrIndexExpr { Name: "Gen" });
    var construct = (AssignStmt)model.DesugaredStatements[assign];
    Assert.That(construct.Target, Is.InstanceOf<MemberExpr>());
    Assert.That(((MemberExpr)construct.Target).Member, Is.EqualTo("$state"));
  }
}
