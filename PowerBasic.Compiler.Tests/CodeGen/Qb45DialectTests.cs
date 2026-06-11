using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// QuickBASIC 4.5 semantics, pinned against the genuine BC.EXE 4.50 + LINK
/// oracle: PB-like typing with a 16-digit DOUBLE display, D exponent marker
/// for doubles, two-digit zero-padded exponents and argument-typed math.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class Qb45DialectTests {

  #region helpers

  private sealed class MemorySourceProvider(string text) : ISourceProvider {
    public bool TryReadSource(string name, string? includedFrom, out string sourceText, out string resolvedName) {
      sourceText = text;
      resolvedName = name;
      return true;
    }
  }

  private static string RunSource(string source) {
    var tokens = Preprocessor.Expand("T.BAS", new MemorySourceProvider(source), Dialect.Qb45);
    var unit = Parser.Parse(tokens, "T.BAS", Dialect.Qb45);
    var model = Binder.Bind(unit, Dialect.Qb45);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return DosBoxRunner.Normalize(DosBoxRunner.Run(exe));
  }


  private static string RunSourceAs(string source, Dialect dialect) {
    var tokens = Preprocessor.Expand("T.BAS", new MemorySourceProvider(source), dialect);
    var unit = Parser.Parse(tokens, "T.BAS", dialect);
    var model = Binder.Bind(unit, dialect);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return DosBoxRunner.Normalize(DosBoxRunner.Run(exe));
  }


  #endregion

  [Test]
  public void DisplayName_GivenMicrosoftDialects_ThenQbAndPdsPrefixes() {
    Assert.Multiple(() => {
      Assert.That(Dialect.Qb45.DisplayName(), Is.EqualTo("QB 4.5"));
      Assert.That(Dialect.Pds71.DisplayName(), Is.EqualTo("PDS 7.1"));
      Assert.That(Dialect.Qb45.Family(), Is.EqualTo(DialectFamily.Microsoft));
      Assert.That(Dialect.Pb35.Family(), Is.EqualTo(DialectFamily.Borland));
    });
  }

  [Test]
  public void FeatureGates_GivenMicrosoftFamily_ThenPbOnlyFeaturesUnavailable() {
    Assert.Multiple(() => {
      Assert.That(DialectFacts.IsAvailable(LanguageFeature.InlineAsm, Dialect.Qb45), Is.False);
      Assert.That(DialectFacts.IsAvailable(LanguageFeature.Pointers, Dialect.Qb45), Is.False);
      Assert.That(DialectFacts.IsAvailable(LanguageFeature.ConcatOperator, Dialect.Qb45), Is.False);
      Assert.That(DialectFacts.IsAvailable(LanguageFeature.TypeUnion, Dialect.Qb45), Is.True);
      Assert.That(DialectFacts.IsAvailable(LanguageFeature.TypeUnion, Dialect.Qb30), Is.False);
    });
  }

  [Test]
  public void PrintDouble_GivenExponentValues_ThenDMarkerAndTwoDigitPad() {
    var output = RunSource("""
      PRINT 1D16
      PRINT 1.5D16
      PRINT 1E7
      PRINT 1E8
      """);
    Assert.That(output, Is.EqualTo(" 1D+16\n 1.5D+16\n 1E+07\n 1E+08\n"));
  }

  [Test]
  public void Divide_GivenLongOperand_ThenSixteenDigitDouble() {
    var output = RunSource("""
      A& = 1
      PRINT A&/3
      PRINT 2/3
      """);
    Assert.That(output, Is.EqualTo(" .3333333333333333\n .6666667\n"));
  }

  [Test]
  public void MathIntrinsics_GivenIntegerArgs_ThenSinglePrecision() {
    var output = RunSource("""
      PRINT SQR(2); EXP(1)
      PRINT LOG(2.718281828459045#)
      PRINT 2 ^ 0.5
      """);
    Assert.That(output, Is.EqualTo(" 1.414214  2.718282\n 1\n 1.414214\n"));
  }

  [Test]
  public void PrintDouble_GivenPds71_ThenFifteenDigits() {
    var output = RunSourceAs("""
      A& = 1
      PRINT A&/3
      PRINT 1D15
      """, Dialect.Pds71);
    Assert.That(output, Is.EqualTo(" .333333333333333\n 1D+15\n"));
  }

  [Test]
  public void Hex_GivenNegativeOne_ThenSixteenBit() {
    var output = RunSource("""PRINT HEX$(-1)""");
    Assert.That(output, Is.EqualTo("FFFF\n"));
  }
}
