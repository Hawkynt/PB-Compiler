using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// Turbo Basic 1.1 runtime semantics, pinned against the genuine TB.EXE 1.1
/// oracle (probed via the AUTOTYPE IDE drive): a 16-digit double-everything
/// number model with zero-padded three-digit exponents.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class Tb11DialectTests {

  #region helpers

  private sealed class MemorySourceProvider(string text) : ISourceProvider {
    public bool TryReadSource(string name, string? includedFrom, out string sourceText, out string resolvedName) {
      sourceText = text;
      resolvedName = name;
      return true;
    }
  }

  private static string RunSource(string source) {
    var tokens = Preprocessor.Expand("T.BAS", new MemorySourceProvider(source), Dialect.Tb11);
    var unit = Parser.Parse(tokens, "T.BAS", Dialect.Tb11);
    var model = Binder.Bind(unit, Dialect.Tb11);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return DosBoxRunner.Normalize(DosBoxRunner.Run(exe));
  }

  #endregion

  [Test]
  public void DisplayName_GivenTurboDialects_ThenTbPrefix() {
    Assert.Multiple(() => {
      Assert.That(Dialect.Tb11.DisplayName(), Is.EqualTo("TB 1.1"));
      Assert.That(Dialect.Tb10.DisplayName(), Is.EqualTo("TB 1.0"));
      Assert.That(Dialect.Pb35.DisplayName(), Is.EqualTo("PB 3.5"));
    });
  }

  [Test]
  public void Divide_GivenAnyOperands_WhenPrinted_ThenSixteenDigitDouble() {
    var output = RunSource("""
      PRINT 2/3
      A! = 1
      PRINT A!/3
      B& = 1
      PRINT B&/3
      """);
    Assert.That(output, Is.EqualTo(" .6666666666666667\n .3333333333333333\n .3333333333333333\n"));
  }

  [Test]
  public void PrintFloat_GivenIntegralValues_WhenPrinted_ThenExpandedToSixteenDigits() {
    var output = RunSource("""
      PRINT 1E7
      PRINT 1E15
      PRINT 1E16
      PRINT 1.5E7
      """);
    Assert.That(output, Is.EqualTo(" 10000000\n 1000000000000000\n 1E+016\n 15000000\n"));
  }

  [Test]
  public void PrintFloat_GivenSmallValues_WhenPrinted_ThenExponentBelowTenth() {
    var output = RunSource("""
      PRINT 0.5
      PRINT -0.25
      PRINT 0.01
      PRINT 1E-6
      """);
    Assert.That(output, Is.EqualTo(" .5\n-.25\n 1E-002\n 1E-006\n"));
  }

  [Test]
  public void Power_GivenFractionalExponent_WhenPrinted_ThenDoublePrecision() {
    var output = RunSource("PRINT 2 ^ 0.5");
    Assert.That(output, Is.EqualTo(" 1.414213562373095\n"));
  }

  [Test]
  public void Val_GivenRadix_WhenParsed_ThenSixteenBitWrap() {
    var output = RunSource("""
      PRINT VAL("&HFFFF")
      PRINT VAL("&H10000")
      PRINT VAL("&O777")
      PRINT VAL("1e3")
      """);
    Assert.That(output, Is.EqualTo("-1\n 0\n 511\n 1000\n"));
  }

  [Test]
  public void Str_GivenDivision_WhenFormatted_ThenSixteenDigits() {
    var output = RunSource("PRINT STR$(2/3)");
    Assert.That(output, Is.EqualTo(" .6666666666666667\n"));
  }
}
