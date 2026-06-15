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

  [TestCase("PRINT 1 << 4", " 16\n")]
  [TestCase("PRINT 256 >> 2", " 64\n")]
  [TestCase("PRINT 6 <<> 1", " 12\n")]              // rotate left
  [TestCase("PRINT 1 <>> 1", "-32768\n")]           // rotate right: bit0 -> bit15
  [TestCase("PRINT 12 | 1", " 13\n")]               // bitwise OR
  public void Execute_GivenShiftRotate16_WhenRun_ThenComputesPerOperator(string source, string expected) {
    Assert.That(Run(source), Is.EqualTo(expected));
  }

  [Test]
  public void Execute_GivenSignedShiftRight16_WhenRun_ThenArithmeticVsLogicalDiffer() {
    // the width follows the left operand's type: an INTEGER variable shifts 16-bit,
    // so >> (arithmetic) keeps the sign while >>> (logical) zero-fills.
    const string source = """
      i% = -16
      PRINT i% >> 2
      PRINT i% >>> 2
      """;
    Assert.That(Run(source), Is.EqualTo("-4\n 16380\n"));
  }

  [Test]
  public void Execute_GivenShiftRotateCompound_WhenRun_ThenUpdatesInPlace() {
    const string source = """
      x% = 1
      x% <<= 4
      PRINT x%
      x% |= 1
      PRINT x%
      """;
    Assert.That(Run(source), Is.EqualTo(" 16\n 17\n"));
  }

  [Test]
  public void Execute_GivenShift32_WhenRun_ThenLongWidthLoop() {
    const string source = """
      DIM n AS LONG = 1
      PRINT n << 20
      DIM m AS LONG = 1
      PRINT m <>> 1
      """;
    Assert.That(Run(source), Is.EqualTo(" 1048576\n-2147483648\n"));
  }

  [Test]
  public void Execute_GivenOverloadedFunctionByArity_WhenRun_ThenResolvesPerArgCount() {
    const string source = """
      DECLARE FUNCTION Area&(BYVAL r AS LONG)
      DECLARE FUNCTION Area&(BYVAL w AS LONG, BYVAL h AS LONG)
      PRINT Area&(5)
      PRINT Area&(4, 6)
      FUNCTION Area&(BYVAL r AS LONG)
        Area& = r * r
      END FUNCTION
      FUNCTION Area&(BYVAL w AS LONG, BYVAL h AS LONG)
        Area& = w * h
      END FUNCTION
      """;
    Assert.That(Run(source), Is.EqualTo(" 25\n 24\n"));
  }

  [Test]
  public void Execute_GivenOverloadedSubByArity_WhenRun_ThenResolvesPerArgCount() {
    const string source = """
      DECLARE SUB Show(BYVAL n AS LONG)
      DECLARE SUB Show(BYVAL a AS LONG, BYVAL b AS LONG)
      Show 7
      Show 3, 4
      SUB Show(BYVAL n AS LONG)
        PRINT n
      END SUB
      SUB Show(BYVAL a AS LONG, BYVAL b AS LONG)
        PRINT a * b
      END SUB
      """;
    Assert.That(Run(source), Is.EqualTo(" 7\n 12\n"));
  }

  [Test]
  public void Execute_GivenOverloadedFunctionByType_WhenRun_ThenResolvesPerArgType() {
    const string source = """
      DECLARE FUNCTION Kind&(BYVAL n AS LONG)
      DECLARE FUNCTION Kind&(BYVAL s AS STRING)
      PRINT Kind&(42&)
      PRINT Kind&("x")
      FUNCTION Kind&(BYVAL n AS LONG)
        Kind& = 1
      END FUNCTION
      FUNCTION Kind&(BYVAL s AS STRING)
        Kind& = 2
      END FUNCTION
      """;
    Assert.That(Run(source), Is.EqualTo(" 1\n 2\n"));
  }

  [Test]
  public void Execute_GivenObjectInitializer_WhenRun_ThenListedFieldsSetAndOthersZero() {
    // Z is not listed, so it must keep its zero-initialized value.
    const string source = """
      TYPE Point
        X AS INTEGER
        Y AS INTEGER
        Z AS INTEGER
      END TYPE
      DIM p = NEW Point { .X = 3, .Y = 4 }
      PRINT p.X
      PRINT p.Y
      PRINT p.Z
      """;
    Assert.That(Run(source), Is.EqualTo(" 3\n 4\n 0\n"));
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
