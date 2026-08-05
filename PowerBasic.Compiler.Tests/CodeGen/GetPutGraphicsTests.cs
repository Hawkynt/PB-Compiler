using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// <c>GET</c> and <c>PUT</c> in their graphics form - sprite capture and blit - checked by reading
/// the pixels back with <c>POINT</c>.
///
/// The array holds QuickBASIC's layout: a word of width in BITS, a word of height in pixels, then
/// the bytes row by row. Width in bits rather than pixels is a fossil of the modes where a pixel WAS
/// a bit; at eight bits per pixel it is the pixel count times eight, and a GET that wrote pixels
/// there would produce a sprite eight times too narrow on the way back out - which is why the header
/// is asserted directly and not only through a round trip.
///
/// The five actions are the point of the statement, and XOR being the default is not a detail: two
/// PUTs of the same sprite erase it, which is how anything moved on a screen before hardware sprites.
/// </summary>
[TestFixture]
public sealed class GetPutGraphicsTests {

  private static string Run(string body) {
    var source = "DIM s%(200)\nSCREEN 13\n" + body + "\nEND\n";
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var cg = new CodeGenerator(model) { Optimize = true };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }

  private const string _fourPixels = """
    PSET (5, 5), 9
    PSET (6, 5), 10
    PSET (5, 6), 11
    PSET (6, 6), 12
    GET (5, 5)-(6, 6), s%(0)
    """;

  /// <summary>Captured and blitted elsewhere, every pixel in its own place - not transposed.</summary>
  [Test]
  public void GetPut_GivenARectangle_WhenPutElsewhere_ThenEachPixelLandsInItsOwnPlace() =>
    Assert.That(Run(_fourPixels + """
      PUT (20, 20), s%(0), PSET
      PRINT POINT(20, 20); POINT(21, 20); POINT(20, 21); POINT(21, 21)
      """), Is.EqualTo("9  10  11  12"));

  /// <summary>The header is the format's, in bits and pixels - not two pixel counts.</summary>
  [Test]
  public void Get_GivenARectangle_ThenTheHeaderIsWidthInBitsAndHeightInPixels() =>
    Assert.That(Run("""
      GET (5, 5)-(12, 9), s%(0)
      PRINT s%(0); s%(1)
      """), Is.EqualTo("64  5"), "8 pixels wide is 64 bits; 5 rows stay 5");

  /// <summary>XOR is the default, and twice over restores what was underneath.</summary>
  [Test]
  public void Put_GivenNoAction_ThenItXorsAndTwiceRestoresTheBackground() =>
    Assert.That(Run(_fourPixels + """
      PSET (20, 20), 3
      PUT (20, 20), s%(0)
      PRINT POINT(20, 20);
      PUT (20, 20), s%(0)
      PRINT POINT(20, 20)
      """), Is.EqualTo("10  3"), "3 XOR 9 is 10, and XOR again is 3 back");

  [TestCase("PSET", "9")]
  [TestCase("XOR", "10")]
  [TestCase("OR", "11")]
  [TestCase("AND", "1")]
  public void Put_GivenAnAction_ThenItCombinesWithWhatIsAlreadyThere(string verb, string expected) =>
    // the sprite's first pixel is 9 and the background is 3: 9, 9 XOR 3, 9 OR 3, 9 AND 3
    Assert.That(Run(_fourPixels + $"""
      PSET (20, 20), 3
      PUT (20, 20), s%(0), {verb}
      PRINT POINT(20, 20)
      """), Is.EqualTo(expected));

  /// <summary>PRESET is PSET's complement, not "restore" - the byte goes down inverted.</summary>
  [Test]
  public void Put_GivenPreset_ThenTheSpriteIsWrittenInverted() =>
    Assert.That(Run(_fourPixels + """
      PUT (20, 20), s%(0), PRESET
      PRINT POINT(20, 20)
      """), Is.EqualTo("246"), "NOT 9 in a byte is 246");

  [Test]
  public void Get_GivenOnlyOneCorner_ThenItIsRefused() {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize("DIM s%(9)\nGET (1, 1), s%(0)\nEND\n", "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    var cg = new CodeGenerator(model);
    cg.EmitExecutable();
    Assert.That(cg.Errors.Select(e => e.Message), Has.Some.Contains("both corners"));
  }

  [Test]
  public void Put_GivenAnUnknownAction_ThenItIsRefusedRatherThanTreatedAsTheDefault() {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize("DIM s%(9)\nPUT (1, 1), s%(0), NAND\nEND\n", "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    var cg = new CodeGenerator(model);
    cg.EmitExecutable();
    Assert.That(cg.Errors.Select(e => e.Message), Has.Some.Contains("NAND"));
  }
}
