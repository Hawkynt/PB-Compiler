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
}
