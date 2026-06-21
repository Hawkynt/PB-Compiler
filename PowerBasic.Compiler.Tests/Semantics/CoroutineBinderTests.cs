using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Semantics;

/// <summary>
/// Binding of PB 3.6 generators: a SUB/FUNCTION whose body contains YIELD is flagged
/// as a generator (the prerequisite for the MoveNext state-machine lowering).
/// </summary>
[TestFixture]
public sealed class CoroutineBinderTests {

  private static SemanticModel Bind(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "t.bas", Dialect.Pb36), "t.bas", Dialect.Pb36);
    return Binder.Bind(unit, Dialect.Pb36);
  }

  [Test]
  public void Bind_GivenFunctionWithYield_WhenBound_ThenMarkedGenerator() {
    var model = Bind("FUNCTION Squares(BYVAL n AS INTEGER) AS LONG\n  FOR i = 1 TO n\n    YIELD i * i\n  NEXT\nEND FUNCTION\n");
    Assert.That(model.Procedures["Squares"].IsGenerator, Is.True);
  }

  [Test]
  public void Bind_GivenProcedureWithoutYield_WhenBound_ThenNotGenerator() {
    var model = Bind("FUNCTION Plain(BYVAL n AS INTEGER) AS LONG\n  Plain = n + 1\nEND FUNCTION\n");
    Assert.That(model.Procedures["Plain"].IsGenerator, Is.False);
  }
}
