using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// HEX$, OCT$ and BIN$ through the x86-16 back end, including the two-argument form.
///
/// The runtime reads the conversion from ONE word - <c>(minimum digits &lt;&lt; 8) | bits-per-digit</c> -
/// so the routing has to pack it, and getting either half wrong produces a plausible string rather
/// than a failure: the wrong bits give a different base, and the wrong count gives the right digits
/// with the wrong padding. Each case reads the printed digits, so either mistake shows as a
/// different string.
///
/// Two behaviours are worth naming because they are easy to "simplify" away:
///
///   * the digit count is a MINIMUM. HEX$(255, 2) is "FF" and HEX$(255, 1) is still "FF" - a value
///     needing more digits prints them all. It never truncates.
///   * a value that fits in [-32768, 65535] renders at SIXTEEN bits, so HEX$(-1) is "FFFF" and not
///     "FFFFFFFF". A small negative arrives at the runtime sign-extended, and the fold is what makes
///     genuine PB's answer come back.
/// </summary>
[TestFixture]
public sealed class BackendRadixTests {

  private static string Run(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = true};
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }

  [TestCase("HEX$", "00F448|00F830|00FC18|000000|0003E8|0007D0|000BB8|FF|000000FF")]
  [TestCase("OCT$", "172110|174060|176030|000000|001750|003720|005670|377|00000377")]
  [TestCase("BIN$", "1111010001001000|1111100000110000|1111110000011000|000000|1111101000|11111010000|101110111000|11111111|11111111")]
  public void Radix_GivenADigitCount_ThenItPadsToTheCountAndNeverTruncates(string name, string expected) {
    // a loop, so the value is not a constant either side could fold away
    var source = $"""
      FOR i% = -3 TO 3
        PRINT {name}(i% * 1000, 6)
      NEXT i%
      PRINT {name}(255, 1)
      PRINT {name}(255, 8)
      END
      """;
    Assert.That(Run(source), Is.EqualTo(expected));
  }

  [TestCase("HEX$", "FDA8|FED4|0|12C|258")]
  [TestCase("OCT$", "176650|177324|0|454|1130")]
  [TestCase("BIN$", "1111110110101000|1111111011010100|0|100101100|1001011000")]
  public void Radix_GivenNoDigitCount_ThenNegativesRenderAtSixteenBits(string name, string expected) {
    var source = $"""
      FOR i% = -2 TO 2
        PRINT {name}(i% * 300)
      NEXT i%
      END
      """;
    Assert.That(Run(source), Is.EqualTo(expected));
  }

  /// <summary>The count pads and never truncates, and the negative folds to sixteen bits.</summary>
  [TestCase("PRINT HEX$(255, 2)", "FF")]
  [TestCase("PRINT HEX$(255, 1)", "FF")]
  [TestCase("PRINT HEX$(255, 6)", "0000FF")]
  [TestCase("PRINT HEX$(-1)", "FFFF")]
  [TestCase("PRINT HEX$(-1, 6)", "00FFFF")]
  [TestCase("PRINT OCT$(8, 4)", "0010")]
  [TestCase("PRINT BIN$(5, 8)", "00000101")]
  public void Radix_GivenAKnownValue_ThenTheRoutedPathPrintsTheVintageAnswer(string statement, string expected) =>
    Assert.That(Run(statement + "\nEND\n"), Is.EqualTo(expected));
}
