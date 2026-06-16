using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// Microsoft Binary Format single precision for BASICA / GW-BASIC: a SINGLE cell is
/// stored in MBF (biased-128 exponent, sign folded into the mantissa) and converts
/// to/from the x87 on load/store. The byte encoding is pinned via VARPTR/PEEK; the
/// load is exercised by a round-trip compare. Run under DOSBox.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class MbfTests {

  private static string Run(string source, Dialect dialect) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", dialect), "TEST.BAS", dialect);
    var model = Binder.Bind(unit, dialect);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return DosBoxRunner.Normalize(DosBoxRunner.Run(exe));
  }

  [Test]
  public void Execute_GivenSingleOne_WhenGwBasic_ThenMbfEncodingIsZeroZeroZero129() {
    // 1.0 in MBF32 is 00 00 00 81 (exponent 129 = 0x81, zero mantissa, zero sign)
    const string source = """
      x! = 1.0
      p% = VARPTR(x!)
      PRINT PEEK(p%)
      PRINT PEEK(p% + 1)
      PRINT PEEK(p% + 2)
      PRINT PEEK(p% + 3)
      """;
    Assert.That(Run(source, Dialect.Gw), Is.EqualTo(" 0\n 0\n 0\n 129\n"));
  }

  [Test]
  public void Execute_GivenNegativeHalf_WhenGwBasic_ThenSignBitFoldedIntoMantissa() {
    // -0.5 in MBF32: exponent 128 (0x80), sign bit set in byte 2 -> 00 00 80 80
    const string source = """
      x! = -0.5
      p% = VARPTR(x!)
      PRINT PEEK(p% + 2)
      PRINT PEEK(p% + 3)
      """;
    Assert.That(Run(source, Dialect.Gw), Is.EqualTo(" 128\n 128\n"));
  }

  [Test]
  public void Execute_GivenZero_WhenGwBasic_ThenAllBytesZero() {
    const string source = """
      x! = 0.0
      p% = VARPTR(x!)
      PRINT PEEK(p%) + PEEK(p% + 1) + PEEK(p% + 2) + PEEK(p% + 3)
      """;
    Assert.That(Run(source, Dialect.Gw), Is.EqualTo(" 0\n"));
  }

  [Test]
  public void Execute_GivenCopy_WhenGwBasic_ThenLoadReconstructsValue() {
    // y! = x! loads x! (MBF -> IEEE) and stores y! (IEEE -> MBF); the copy's MBF
    // exponent byte (129 for 1.0) proves the load reconstructed the stored value
    const string source = """
      x! = 1.0
      y! = x!
      p% = VARPTR(y!)
      PRINT PEEK(p% + 3)
      """;
    Assert.That(Run(source, Dialect.Gw), Is.EqualTo(" 129\n"));
  }

  [Test]
  public void Execute_GivenArithmetic_WhenGwBasic_ThenComputesOnX87AndStoresMbf() {
    // 2.5 * 4.0 = 10.0; MBF32 of 10.0 has exponent byte 132 (0x84)
    const string source = """
      a! = 2.5
      b! = 4.0
      c! = a! * b!
      p% = VARPTR(c!)
      PRINT PEEK(p% + 3)
      """;
    Assert.That(Run(source, Dialect.Gw), Is.EqualTo(" 132\n"));
  }
}
