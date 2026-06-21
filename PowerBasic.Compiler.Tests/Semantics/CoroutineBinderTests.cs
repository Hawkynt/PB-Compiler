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
  public void Bind_GivenYieldInsideLoop_WhenBound_ThenLowersToStateMachineWithoutError() {
    // a YIELD inside a FOR is flattened to the MoveNext state machine (goto form), not rejected
    var model = Bind("FUNCTION Sq(BYVAL n AS INTEGER) AS LONG\n  FOR i = 1 TO n\n    YIELD i * i\n  NEXT\nEND FUNCTION\nDIM e AS Sq\n");
    Assert.Multiple(() => {
      Assert.That(model.Procedures.ContainsKey("Sq.MoveNext"), Is.True, "the loop generator still synthesizes MoveNext");
      Assert.That(model.Udts["Sq"].FindField("$i"), Is.Not.Null, "the loop counter persists across resumes as an enumerator field");
      Assert.That(model.Udts["Sq"].FindField("$n"), Is.Not.Null, "the parameter persists across resumes as an enumerator field");
    });
  }

  [Test]
  public void Bind_GivenSuffixTypedParameter_WhenBound_ThenSeededAndCaptured() {
    // a parameter typed by suffix (n%) - not an explicit AS clause - is still captured and seeded
    var model = Bind("FUNCTION Sq(BYVAL n%) AS LONG\n  YIELD n%\nEND FUNCTION\nDIM e AS Sq\ne = Sq(4)\n");
    var assign = model.DesugaredStatements.Keys.OfType<AssignStmt>()
      .Single(a => a.Value is CallOrIndexExpr { Name: "Sq" });
    var construct = (IfStmt)model.DesugaredStatements[assign];
    var writes = construct.Then.OfType<AssignStmt>().Select(s => ((MemberExpr)s.Target).Member).ToList();
    Assert.That(writes, Is.EqualTo(new[] { "$state", "$n" }), "the suffix-typed parameter is seeded from the argument");
  }

  [Test]
  public void Bind_GivenGeneratorYieldingWhileIteratingAnother_WhenBound_ThenInnerEnumeratorPersistsAsField() {
    // a generator that does FOR EACH over another generator and YIELDs inside the loop keeps the
    // inner iterator alive across the outer resumes by storing it in a UDT field of the enumerator
    var model = Bind(
      "FUNCTION Count(BYVAL n%) AS INTEGER\n  FOR i% = 1 TO n%\n    YIELD i%\n  NEXT\nEND FUNCTION\n" +
      "FUNCTION Doubled(BYVAL n%) AS INTEGER\n  FOR EACH x% IN Count(n%)\n    YIELD x% * 2\n  NEXT\nEND FUNCTION\n" +
      "DIM e AS Doubled\n");
    Assert.Multiple(() => {
      Assert.That(model.Procedures.ContainsKey("Doubled.MoveNext"), Is.True);
      Assert.That(model.Udts["Doubled"].FindField("$fe1"), Is.Not.Null, "the inner enumerator persists across resumes as a UDT field");
      Assert.That(model.Udts["Doubled"].FindField("$x"), Is.Not.Null, "the FOR EACH loop variable persists across resumes");
    });
  }

  [Test]
  public void Bind_GivenYieldInsideSelectCase_WhenBound_ThenLowersWithoutError() {
    // a YIELD inside SELECT CASE (over a simple subject) flattens to per-arm labels, not rejected
    var model = Bind(
      "FUNCTION Pick(BYVAL n%) AS INTEGER\n  SELECT CASE n%\n    CASE 1\n      YIELD 10\n    CASE ELSE\n      YIELD 20\n  END SELECT\nEND FUNCTION\nDIM e AS Pick\n");
    Assert.That(model.Procedures.ContainsKey("Pick.MoveNext"), Is.True);
  }

  [Test]
  public void Bind_GivenYieldInsideSelectCaseOnComplexSubject_WhenBound_ThenRejectedClearly() {
    // a side-effecting SELECT subject would be re-evaluated per arm, so it is rejected
    var unit = Parser.Parse(Lexer.Tokenize(
      "FUNCTION F(BYVAL n%) AS INTEGER\n  SELECT CASE n% + Side(n%)\n    CASE 1\n      YIELD 1\n  END SELECT\nEND FUNCTION\nFUNCTION Side(BYVAL x%) AS INTEGER\nEND FUNCTION\n", "t.bas", Dialect.Pb36), "t.bas", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors.Any(e => e.Message.Contains("SELECT CASE needs a simple subject")), Is.True);
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
  public void Bind_GivenGeneratorConstruction_WhenBound_ThenResetsStateAndSeedsParameters() {
    var model = Bind(
      "FUNCTION Range(BYVAL lo AS INTEGER, BYVAL hi AS INTEGER) AS INTEGER\n  YIELD lo\n  YIELD hi\nEND FUNCTION\n" +
      "DIM e AS Range\ne = Range(3, 8)\n");
    var assign = model.DesugaredStatements.Keys.OfType<AssignStmt>()
      .Single(a => a.Value is CallOrIndexExpr { Name: "Range" });
    var construct = (IfStmt)model.DesugaredStatements[assign];
    var writes = construct.Then.OfType<AssignStmt>().Select(s => ((MemberExpr)s.Target).Member).ToList();
    Assert.That(writes, Is.EqualTo(new[] { "$state", "$lo", "$hi" }), "reset state, then seed each captured parameter");
  }
}
