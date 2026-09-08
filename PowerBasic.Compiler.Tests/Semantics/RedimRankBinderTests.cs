using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Semantics;

/// <summary>
/// REDIM may replace an existing dynamic array's bounds, but its number of dimensions is part of
/// the array's declared type. Genuine classic BASIC compilers reject attempts to change that rank;
/// IrLowering already defended the invariant, and these tests keep it at the semantic boundary too.
/// </summary>
[TestFixture]
public sealed class RedimRankBinderTests {

  private static SemanticModel Bind(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "REDIM-RANK.BAS", Dialect.Pb35), "REDIM-RANK.BAS", Dialect.Pb35);
    return Binder.Bind(unit, Dialect.Pb35);
  }

  [Test]
  public void Bind_GivenSameRankRedim_WhenBound_ThenItRemainsValid() {
    var model = Bind("""
      $DYNAMIC
      DIM a%(4, 5)
      REDIM a%(6, 7)
      """);

    Assert.That(model.Success, Is.True, string.Join("; ", model.Errors));
  }

  [Test]
  public void Bind_GivenRedimIntroducesArray_WhenBound_ThenFirstRedimEstablishesRank() {
    var model = Bind("REDIM a%(4, 5)\n");

    Assert.That(model.Success, Is.True, string.Join("; ", model.Errors));
    Assert.That(model.ModuleVariables["a%()"].Type, Is.EqualTo(new ArrayType(PbType.Integer, null, 2)));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Bind_GivenExistingArrayChangesRank_WhenBound_ThenRejectsBeforeCodegen(bool preserve) {
    var model = Bind($"""
      $DYNAMIC
      DIM a%(4)
      REDIM {(preserve ? "PRESERVE " : "")}a%(4, 4)
      """);

    Assert.That(model.Errors, Has.Some.Matches<Diagnostic>(d =>
      d.Message.Contains("rank", StringComparison.OrdinalIgnoreCase)
      && d.Message.Contains("1", StringComparison.Ordinal)
      && d.Message.Contains("2", StringComparison.Ordinal)));
  }

  [Test]
  public void Bind_GivenUnsizedRankOneDeclarationThenRankTwoRedim_WhenBound_ThenRejects() {
    var model = Bind("""
      DIM a%()
      REDIM a%(4, 4)
      """);

    Assert.That(model.Errors, Has.Some.Matches<Diagnostic>(d =>
      d.Message.Contains("rank", StringComparison.OrdinalIgnoreCase)));
  }
}
