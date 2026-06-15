using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// End-to-end tests for the PB 3.6 new-syntax surface (docs/PB36.md): source is
/// compiled with <c>--dialect pb36</c> through the full pipeline and run under
/// DOSBox. Skipped when DOSBox is unavailable. These prove the new sugar lowers
/// to the same observable behavior the hand-written equivalent would produce.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class Pb36LanguageFeatureTests {

  private static string Run(string source) {
    var tokens = Lexer.Tokenize(source, "TEST.BAS", Dialect.Pb36);
    var unit = Parser.Parse(tokens, "TEST.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return DosBoxRunner.Normalize(DosBoxRunner.Run(exe));
  }

  [Test]
  public void Execute_GivenExpressionBodiedFunction_WhenRun_ThenResultMatchesEquivalentBody() {
    const string source = """
      DECLARE FUNCTION Sq&(BYVAL x AS LONG)
      PRINT Sq&(7)
      FUNCTION Sq&(BYVAL x AS LONG) = x * x
      """;
    Assert.That(Run(source), Is.EqualTo(" 49\n"));
  }

  [Test]
  public void Execute_GivenCompoundArithmetic_WhenRun_ThenAccumulates() {
    const string source = """
      n% = 10
      n% += 5
      n% *= 3
      n% -= 1
      PRINT n%
      """;
    Assert.That(Run(source), Is.EqualTo(" 44\n"));
  }

  [Test]
  public void Execute_GivenCompoundConcat_WhenRun_ThenStringGrows() {
    const string source = """
      s$ = "ab"
      s$ &= "cd"
      s$ &= "ef"
      PRINT s$
      """;
    Assert.That(Run(source), Is.EqualTo("abcdef\n"));
  }

  [Test]
  public void Execute_GivenDimInferredInteger_WhenRun_ThenDeclaresAndInitializes() {
    const string source = """
      DIM x = 7
      PRINT x
      """;
    Assert.That(Run(source), Is.EqualTo(" 7\n"));
  }

  [Test]
  public void Execute_GivenDimInferredString_WhenRun_ThenStringStored() {
    const string source = """
      DIM s = "hello"
      PRINT s
      """;
    Assert.That(Run(source), Is.EqualTo("hello\n"));
  }

  [Test]
  public void Execute_GivenDimInferredLargeLiteral_WhenRun_ThenInfersWideEnoughType() {
    // 100000 does not fit INTEGER; inference must pick LONG so the value survives.
    const string source = """
      DIM big = 100000
      PRINT big
      """;
    Assert.That(Run(source), Is.EqualTo(" 100000\n"));
  }

  [Test]
  public void Execute_GivenDimTypedInitializer_WhenRun_ThenUsesExplicitType() {
    const string source = """
      DIM n AS LONG = 100000
      PRINT n * 2
      """;
    Assert.That(Run(source), Is.EqualTo(" 200000\n"));
  }

  [TestCase("PRINT IF(1, 10, 20)", " 10\n")]
  [TestCase("PRINT IF(0, 10, 20)", " 20\n")]
  [TestCase("PRINT IF(5 > 3, 99, -1)", " 99\n")]
  public void Execute_GivenTernaryIf_WhenRun_ThenSelectsBranch(string source, string expected) {
    Assert.That(Run(source), Is.EqualTo(expected));
  }

  [Test]
  public void Execute_GivenTernaryStringBranches_WhenRun_ThenStringResult() {
    Assert.That(Run("PRINT IF(5 > 3, \"yes\", \"no\")"), Is.EqualTo("yes\n"));
  }

  [Test]
  public void Execute_GivenTernaryIf_WhenRun_ThenUntakenBranchNotEvaluated() {
    // If the false branch (100 \ x%) were evaluated with x% = 0 it would raise the
    // genuine division-by-zero error 11; short-circuit must skip it and print 42.
    const string source = """
      x% = 0
      PRINT IF(x% = 0, 42, 100 \ x%)
      """;
    Assert.That(Run(source), Is.EqualTo(" 42\n"));
  }

  [TestCase("PRINT (1 ANDALSO 1)", "-1\n")]
  [TestCase("PRINT (1 ANDALSO 0)", " 0\n")]
  [TestCase("PRINT (0 ANDALSO 1)", " 0\n")]
  [TestCase("PRINT (0 ORELSE 1)", "-1\n")]
  [TestCase("PRINT (1 ORELSE 0)", "-1\n")]
  [TestCase("PRINT (0 ORELSE 0)", " 0\n")]
  public void Execute_GivenShortCircuitOps_WhenRun_ThenNormalizedTruth(string source, string expected) {
    Assert.That(Run(source), Is.EqualTo(expected));
  }

  [Test]
  public void Execute_GivenAndAlso_WhenRun_ThenRightOperandSkippedOnFalseLeft() {
    // (100 \ x%) would raise division-by-zero error 11 if evaluated; ANDALSO must
    // skip it because the left operand is false.
    const string source = """
      x% = 0
      PRINT (x% <> 0 ANDALSO (100 \ x%) > 0)
      """;
    Assert.That(Run(source), Is.EqualTo(" 0\n"));
  }

  [Test]
  public void Execute_GivenOrElse_WhenRun_ThenRightOperandSkippedOnTrueLeft() {
    const string source = """
      x% = 0
      PRINT (x% = 0 ORELSE (100 \ x%) > 0)
      """;
    Assert.That(Run(source), Is.EqualTo("-1\n"));
  }

  [Test]
  public void Execute_GivenDimFromTernary_WhenRun_ThenInferredFromResult() {
    const string source = """
      DIM m = IF(7 > 3, 7, 3)
      PRINT m
      """;
    Assert.That(Run(source), Is.EqualTo(" 7\n"));
  }

  [Test]
  public void Execute_GivenDimInitializerInProcedure_WhenRun_ThenLocalInferred() {
    const string source = """
      DECLARE FUNCTION Cube&(BYVAL x AS LONG)
      PRINT Cube&(4)
      FUNCTION Cube&(BYVAL x AS LONG)
        DIM r AS LONG = x * x
        r = r * x
        Cube& = r
      END FUNCTION
      """;
    Assert.That(Run(source), Is.EqualTo(" 64\n"));
  }
}
