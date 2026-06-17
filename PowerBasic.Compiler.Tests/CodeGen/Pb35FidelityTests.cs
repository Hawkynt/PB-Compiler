using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// Byte-fidelity regressions pinned against the genuine PBC 3.50 oracle
/// (probed 1996-12-16 build): division result typing, float literal
/// precision, exponent display format and VAL radix/exponent parsing.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class Pb35FidelityTests {

  #region helpers

  private sealed class MemorySourceProvider(string text) : ISourceProvider {
    public bool TryReadSource(string name, string? includedFrom, out string sourceText, out string resolvedName) {
      sourceText = text;
      resolvedName = name;
      return true;
    }
  }

  private static string RunSource(string source) {
    var tokens = Preprocessor.Expand("T.BAS", new MemorySourceProvider(source));
    var unit = Parser.Parse(tokens, "T.BAS");
    var model = Binder.Bind(unit);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return DosBoxRunner.Normalize(DosBoxRunner.Run(exe));
  }

  #endregion

  [Test]
  public void Divide_GivenSixteenBitOperands_WhenPrinted_ThenSinglePrecision() {
    var output = RunSource("""
      PRINT 2/3
      B% = 2
      C% = 3
      PRINT B%/C%
      H? = 200
      PRINT H?/3
      """);
    Assert.That(output, Is.EqualTo(" .6666667\n .6666667\n 66.66667\n"));
  }

  [Test]
  public void Divide_GivenLongOperand_WhenPrinted_ThenDoublePrecision() {
    var output = RunSource("""
      A& = 1
      PRINT A&/3
      G& = 100000
      PRINT G&/3
      PRINT 100000/3
      """);
    Assert.That(output, Is.EqualTo(" .333333333333333\n 33333.3333333333\n 33333.3333333333\n"));
  }

  [Test]
  public void Divide_GivenLongLiteralFittingInteger_WhenPrinted_ThenFolderNormalizesToSingle() {
    var output = RunSource("PRINT 1&/3");
    Assert.That(output, Is.EqualTo(" .3333333\n"));
  }

  [Test]
  public void Divide_GivenFloatOperand_WhenPrinted_ThenOperandPrecisionWins() {
    var output = RunSource("""
      D! = 1
      PRINT D!/3
      E# = 1
      PRINT E#/3
      """);
    Assert.That(output, Is.EqualTo(" .3333333\n .333333333333333\n"));
  }

  [Test]
  public void FloatLiteral_GivenBareLiteral_WhenPrinted_ThenKeepsFullPrecision() {
    var output = RunSource("""
      PRINT 123456.789
      PRINT 1.23456789012345678
      PRINT 123456.789!
      """);
    Assert.That(output, Is.EqualTo(" 123456.789\n 1.23456789012346\n 123456.8\n"));
  }

  [Test]
  public void PrintFloat_GivenSingleDigitExponent_WhenPrinted_ThenNoZeroPadding() {
    FpuAssume.RequireExtendedPrecision();
    var output = RunSource("""
      PRINT 1E7
      PRINT 1E10
      PRINT 1E16
      PRINT 1E-7
      """);
    Assert.That(output, Is.EqualTo(" 1E+7\n 1E+10\n 1E+16\n .0000001\n"));
  }

  [Test]
  public void Val_GivenExponentForms_WhenParsed_ThenScaled() {
    var output = RunSource("""
      PRINT VAL("1e3")
      PRINT VAL("1D3")
      PRINT VAL("1.5E-2")
      PRINT VAL(".5E1")
      PRINT VAL("1E")
      PRINT VAL("2E+2Q")
      """);
    Assert.That(output, Is.EqualTo(" 1000\n 1000\n .015\n 5\n 1\n 200\n"));
  }

  [Test]
  public void Val_GivenRadixPrefixes_WhenParsed_ThenLiteralRules() {
    var output = RunSource("""
      PRINT VAL("&HFF")
      PRINT VAL("&HFFFF")
      PRINT VAL("&H10000")
      PRINT VAL("&O777")
      PRINT VAL("&B101")
      """);
    Assert.That(output, Is.EqualTo(" 255\n-1\n 65536\n 511\n 5\n"));
  }

  [Test]
  public void ArraySort_GivenNumericArray_WhenSorted_ThenAscendingAndDescending() {
    // GIVEN an unsorted INTEGER array; WHEN ARRAY SORT runs THEN it orders
    // ascending, and DESCEND reverses it - identical to genuine PBC.
    var output = RunSource("""
      DIM a%(1 TO 5)
      a%(1)=30 : a%(2)=10 : a%(3)=50 : a%(4)=20 : a%(5)=40
      ARRAY SORT a%(1)
      FOR i%=1 TO 5 : PRINT a%(i%); : NEXT : PRINT
      ARRAY SORT a%(1) FOR 5, DESCEND
      FOR i%=1 TO 5 : PRINT a%(i%); : NEXT : PRINT
      """);
    Assert.That(output, Is.EqualTo(" 10  20  30  40  50\n 50  40  30  20  10\n"));
  }

  [Test]
  public void ArrayScan_GivenNumericArray_WhenScannedWithRelops_ThenOneBasedPositions() {
    // GIVEN a sorted INTEGER array; WHEN ARRAY SCAN walks it under each relop
    // THEN it returns the 1-based position of the first match (0 when none).
    var output = RunSource("""
      DIM a%(1 TO 5)
      a%(1)=10 : a%(2)=20 : a%(3)=30 : a%(4)=40 : a%(5)=50
      ARRAY SCAN a%(1), = 30, TO p%  : PRINT p%
      ARRAY SCAN a%(1), > 25, TO p%  : PRINT p%
      ARRAY SCAN a%(1), <= 10, TO p% : PRINT p%
      ARRAY SCAN a%(1), > 99, TO p%  : PRINT p%
      """);
    Assert.That(output, Is.EqualTo(" 3\n 3\n 1\n 0\n"));
  }

  [Test]
  public void ArraySort_GivenTagArray_WhenKeySorted_ThenTagFollowsThePermutation() {
    // GIVEN a key array and a parallel LONG tag array; WHEN ARRAY SORT ... TAGARRAY
    // runs THEN the tag array is reordered by the key's permutation.
    var output = RunSource("""
      DIM k%(1 TO 4)
      DIM t&(1 TO 4)
      k%(1)=30 : k%(2)=10 : k%(3)=20 : k%(4)=40
      t&(1)=300 : t&(2)=100 : t&(3)=200 : t&(4)=400
      ARRAY SORT k%(1), TAGARRAY t&()
      FOR i%=1 TO 4 : PRINT k%(i%); : NEXT : PRINT
      FOR i%=1 TO 4 : PRINT t&(i%); : NEXT : PRINT
      """);
    Assert.That(output, Is.EqualTo(" 10  20  30  40\n 100  200  300  400\n"));
  }

  [Test]
  public void ArraySort_GivenUnsignedAndQuadElements_WhenSorted_ThenValueCorrectOrder() {
    // GIVEN unsigned WORD/DWORD and signed QUAD arrays whose values exceed the
    // signed range of their width; WHEN sorted THEN ordering is by true value.
    var output = RunSource("""
      DIM w??(1 TO 3)
      w??(1)=50000 : w??(2)=100 : w??(3)=65535
      ARRAY SORT w??(1)
      FOR i%=1 TO 3 : PRINT w??(i%); : NEXT : PRINT
      DIM q&&(1 TO 3)
      q&&(1)=5000000000 : q&&(2)=-9000000000 : q&&(3)=1
      ARRAY SORT q&&(1)
      FOR i%=1 TO 3 : PRINT q&&(i%); : NEXT : PRINT
      """);
    Assert.That(output, Is.EqualTo(" 100  50000  65535\n-9000000000  1  5000000000\n"));
  }

  [Test]
  public void Quad_GivenEveryOperator_WhenRun_ThenSixtyFourBitResults() {
    // GIVEN 64-bit QUAD operands; WHEN each operator runs THEN the integral ops
    // (+ - * \ MOD, the bitwise family, comparisons, unary - and NOT) stay exact
    // in 64 bits, while the float-typed / and ^ run on the x87 (DOUBLE / EXT).
    var output = RunSource("""
      DIM a AS QUAD, b AS QUAD
      a = 5000000000
      b = 3
      PRINT a + b
      PRINT a * b
      PRINT a \ b
      PRINT a MOD b
      PRINT a AND b
      PRINT a OR b
      PRINT a XOR b
      PRINT (a > b); (a = b)
      PRINT a / b
      PRINT a ^ 2
      PRINT NOT a
      """);
    Assert.That(output, Is.EqualTo(
      " 5000000003\n 15000000000\n 1666666666\n 2\n 0\n 5000000003\n 5000000003\n-1  0\n 1666666666.66667\n 2.5E+19\n-5000000001\n"));
  }

  [Test]
  public void OnErrorResumeNext_GivenFaultingStatement_WhenRun_ThenExecutionContinuesAndErrStaysSet() {
    // GIVEN inline error mode; WHEN a statement faults THEN the next runs, ERR
    // holds the last code (no auto-clear), a fresh fault overwrites it, ERRCLEAR
    // resets it to 0, and a clean statement leaves it untouched.
    var output = RunSource("""
      ON ERROR RESUME NEXT
      ERROR 5
      PRINT "a"; ERR
      ERROR 7
      PRINT "b"; ERR
      ERRCLEAR
      PRINT "c"; ERR
      """);
    Assert.That(output, Is.EqualTo("a 5\nb 7\nc 0\n"));
  }
}
