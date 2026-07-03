using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.Semantics;

/// <summary>
/// pb36 tuples / multiple return values: a tuple type <c>(T1, T2)</c> is an anonymous UDT with fields
/// Item1..ItemN; a tuple-returning FUNCTION uses struct return; <c>a, b = f()</c> destructures it.
/// </summary>
[TestFixture]
public sealed class TupleBinderTests {

  private static SemanticModel Bind(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "t.bas", Dialect.Pb36), "t.bas", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Success, Is.True, string.Join("; ", model.Errors));
    return model;
  }

  [Test]
  public void Bind_GivenTupleTypedVariable_WhenBound_ThenSynthesizesUdtWithItemFields() {
    var model = Bind("DIM p AS (LONG, STRING)\np.Item1 = 1\np.Item2 = \"x\"\n");
    var udt = (UdtType)model.ModuleVariables.Values.Single(v => v.Name.Equals("p", System.StringComparison.OrdinalIgnoreCase)).Type;
    Assert.Multiple(() => {
      Assert.That(udt.FindField("Item1")!.Type, Is.EqualTo(PbType.Long));
      Assert.That(udt.FindField("Item2")!.Type, Is.InstanceOf<StringType>());
    });
  }

  [Test]
  public void Bind_GivenIdenticalTupleTypes_WhenBound_ThenShareOneUdt() {
    var model = Bind("DIM a AS (LONG, LONG)\nDIM b AS (LONG, LONG)\n");
    var a = model.ModuleVariables.Values.Single(v => v.Name.Equals("a", System.StringComparison.OrdinalIgnoreCase));
    var b = model.ModuleVariables.Values.Single(v => v.Name.Equals("b", System.StringComparison.OrdinalIgnoreCase));
    Assert.That(((UdtType)a.Type).Name, Is.EqualTo(((UdtType)b.Type).Name), "identical tuple types map to one synthesized UDT");
  }

  [Test]
  public void Bind_GivenTupleReturningFunction_WhenBound_ThenStructReturn() {
    var model = Bind("FUNCTION DivMod(BYVAL a AS LONG, BYVAL b AS LONG) AS (LONG, LONG)\n  DivMod.Item1 = a \\ b\n  DivMod.Item2 = a MOD b\nEND FUNCTION\nDIM q&, r&\nq&, r& = DivMod(17, 5)\n");
    Assert.That(model.Procedures["DivMod"].HasSretParam, Is.True, "a tuple-returning function returns by struct return");
  }

  [Test]
  public void Bind_GivenDestructuring_WhenBound_ThenAssignsEachElement() {
    var model = Bind("FUNCTION P() AS (LONG, LONG)\n  P.Item1 = 1\n  P.Item2 = 2\nEND FUNCTION\nDIM x&, y&\nx&, y& = P()\n");
    var ds = model.DesugaredStatements.Keys.OfType<DestructureStmt>().Single();
    var lowered = (GroupStmt)model.DesugaredStatements[ds];
    // one CALL P(buffer) plus two element assignments
    Assert.Multiple(() => {
      Assert.That(lowered.Body.OfType<CallStmt>().Any(c => c.Name == "P"), Is.True, "the tuple call fills the buffer");
      Assert.That(lowered.Body.OfType<AssignStmt>().Count(), Is.EqualTo(2), "each element assigned to a target");
    });
  }

  [Test]
  public void Bind_GivenTupleLiteralSwap_WhenBound_ThenEvaluatesAllValuesIntoTempsFirst() {
    // a, b = (b, a) must read both right-hand values into temps before assigning (simultaneous swap)
    var model = Bind("DIM a&, b&\na& = 1 : b& = 2\na&, b& = (b&, a&)\n");
    var ds = model.DesugaredStatements.Keys.OfType<DestructureStmt>().Single();
    var lowered = (GroupStmt)model.DesugaredStatements[ds];
    Assert.That(lowered.Body.OfType<AssignStmt>().Count(), Is.EqualTo(4), "two temp loads then two target stores");
  }

  [Test]
  public void Bind_GivenTupleLiteralToTupleVariable_WhenBound_ThenSetsEachItem() {
    var model = Bind("DIM t AS (LONG, STRING)\nt = (99, \"x\")\n");
    var assign = model.DesugaredStatements.Keys.OfType<AssignStmt>().Single(a => a.Value is TupleExpr);
    var lowered = (GroupStmt)model.DesugaredStatements[assign];
    Assert.That(lowered.Body.OfType<AssignStmt>().Any(s => s.Target is MemberExpr { Member: "Item1" }), Is.True);
  }

  [Test]
  public void Parse_GivenPrintWithCommaAndEquals_WhenPb36_ThenNotMistakenForDestructuring() {
    // PRINT #1, 1 = 1  has a top-level comma before '=', but is a PRINT, not a destructuring
    var unit = Parser.Parse(Lexer.Tokenize("PRINT #1, 1 = 1\n", "t.bas", Dialect.Pb36), "t.bas", Dialect.Pb36);
    Assert.That(unit.Statements[0], Is.InstanceOf<PrintStmt>());
    Assert.That(unit.Statements.OfType<DestructureStmt>().Any(), Is.False);
  }

  [Test]
  public void Bind_GivenTuplesBelowPb36_WhenParsed_ThenRejected() {
    Assert.Throws<ParserException>(() =>
      Parser.Parse(Lexer.Tokenize("DIM p AS (LONG, LONG)\n", "t.bas", Dialect.Pb35), "t.bas", Dialect.Pb35));
  }
}
