using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// pb36 O12 float demotion: the analyzer proves SINGLE/DOUBLE variables
/// integral and re-types them; blockers must keep the original float type
/// (observable behavior is pinned by tests/diff/DIFF28.BAS and the execution
/// tests below).
/// </summary>
[TestFixture]
public sealed class Pb36FloatDemotionTests {

  private static (SemanticModel Model, IReadOnlyList<VariableSymbol> Demoted) Analyze(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", Dialect.Pb36), "TEST.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return (model, Pb36FloatDemotion.Apply(model));
  }

  [Test]
  public void Demote_GivenPlainForCounter_ThenInteger() {
    var (_, demoted) = Analyze("FOR i = 1 TO 10\nNEXT i\nPRINT i\nEND");
    Assert.That(demoted, Has.Count.EqualTo(1));
    Assert.That(demoted[0].Type, Is.EqualTo(PbType.Integer));
  }

  [Test]
  public void Demote_GivenWideBounds_ThenLong() {
    var (_, demoted) = Analyze("FOR i = 1 TO 100000\nNEXT i\nPRINT i\nEND");
    Assert.That(demoted, Has.Count.EqualTo(1));
    Assert.That(demoted[0].Type, Is.EqualTo(PbType.Long));
  }

  [Test]
  public void Demote_GivenSubscriptAndIntegralUses_ThenDemoted() {
    var (_, demoted) = Analyze("""
      DIM a%(20)
      FOR i = 1 TO 10
        a%(i) = i * 2
        IF i > 5 THEN a%(i) = i \ 2
      NEXT i
      PRINT a%(3); a%(7)
      END
      """);
    Assert.That(demoted, Has.Count.EqualTo(1));
  }

  [Test]
  public void Demote_GivenDividedCounter_ThenBlocked() {
    var (_, demoted) = Analyze("FOR i = 1 TO 10\nPRINT i / 2\nNEXT i\nEND");
    Assert.That(demoted, Is.Empty);
  }

  [Test]
  public void Demote_GivenCallArgument_ThenBlocked() {
    var (_, demoted) = Analyze("""
      DECLARE SUB Touch(x)
      FOR i = 1 TO 10
        Touch i
      NEXT i
      END
      SUB Touch(x)
        x = 99
      END SUB
      """);
    Assert.That(demoted, Is.Empty);
  }

  [Test]
  public void Demote_GivenFractionalAssignment_ThenBlocked() {
    var (_, demoted) = Analyze("FOR i = 1 TO 10\nNEXT i\ni = 0.5\nEND");
    Assert.That(demoted, Is.Empty);
  }

  [Test]
  public void Demote_GivenIncr_ThenBlocked() {
    var (_, demoted) = Analyze("FOR i = 1 TO 10\nNEXT i\nINCR i\nEND");
    Assert.That(demoted, Is.Empty);
  }

  [Test]
  public void Demote_GivenBoundsBeyondSingleExactness_ThenKept() {
    var (_, demoted) = Analyze("FOR i = 1 TO 20000000 STEP 1000000\nNEXT i\nEND");
    Assert.That(demoted, Is.Empty, "20e6 exceeds 2^24 - SINGLE storage would round, demotion must back off");
  }

  [Test]
  public void Demote_GivenInlineAsmAnywhere_ThenNothingDemotes() {
    var (_, demoted) = Analyze("FOR i = 1 TO 10\nNEXT i\n! nop\nEND");
    Assert.That(demoted, Is.Empty);
  }

  [Test]
  public void Demote_GivenFloatCaseComparison_ThenBlocked() {
    var (_, demoted) = Analyze("""
      FOR i = 1 TO 10
        SELECT CASE i
          CASE 2.5
            PRINT "?"
        END SELECT
      NEXT i
      END
      """);
    Assert.That(demoted, Is.Empty);
  }

  [Test]
  public void Demote_GivenDoubleCounter_ThenDemoted() {
    var (_, demoted) = Analyze("FOR d# = 1 TO 100\nNEXT d#\nPRINT d#\nEND");
    Assert.That(demoted, Has.Count.EqualTo(1));
  }
}
