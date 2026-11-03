using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// QuickBASIC SCREEN 12 GET stores four packed one-bit plane streams per
/// scanline, not Mode 13's byte-per-pixel raster. Pixel order is MSB-first;
/// the header holds width in bits and height in pixels.
/// </summary>
[TestFixture]
public sealed class PlanarSpriteGetPutTests {
  private static Cpu8086 Execute(string body, bool optimize, string cpu = "") {
    var source = cpu + "\nDIM s%(200)\nSCREEN 12\n" + body + "\nEND\n";
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "SPRITE12.BAS", Dialect.Pb36),
      "SPRITE12.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize, Container = DosContainer.Exe };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    return Cpu8086.Run(image);
  }

  [TestCase("$CPU 8086")]
  [TestCase("$CPU 80386")]
  public void Execute_GivenEightFourBitPixels_ThenGetUsesFourPackedPlaneBytes(string cpu) {
    const string body = """
      FOR x% = 0 TO 7: PSET (x%,5),x%: NEXT
      GET (0,5)-(7,5),s%(0)
      PRINT s%(0);s%(1);s%(2);s%(3)
      """;
    // Plane0: 01010101 = 55h, plane1: 00110011 = 33h;
    // plane2: 00001111 = 0Fh, plane3: 00000000.
    // Integer elements are little-endian: 3355h and 000Fh.
    var optimized = Execute(body, true, cpu);
    var baseline = Execute(body, false, cpu);
    Assert.Multiple(() => {
      Assert.That(optimized.Output, Is.EqualTo(baseline.Output));
      Assert.That(optimized.Output.Trim(), Does.Contain("8  1  13141  15"));
    });
  }

  [Test]
  public void Execute_GivenNinePixelSprite_ThenEachPlanePadsTheSecondByte() {
    const string body = """
      FOR x% = 0 TO 8: PSET (x%,5),x%: NEXT
      GET (0,5)-(8,5),s%(0)
      PRINT s%(0);s%(1);s%(2);s%(3);s%(4);s%(5)
      """;
    var optimized = Execute(body, true);
    var baseline = Execute(body, false);
    Assert.Multiple(() => {
      Assert.That(optimized.Output, Is.EqualTo(baseline.Output));
      Assert.That(optimized.Output.Trim(), Does.Contain("9  1  85  51  15  -32768"));
    });
  }

  [TestCase("PSET","9")]
  [TestCase("PRESET","6")]
  [TestCase("AND","1")]
  [TestCase("OR","11")]
  [TestCase("XOR","10")]
  public void Execute_GivenPlanarPutAction_ThenMatchesTheFourBitPaletteAlgebra(
      string action, string expected) {
    var body = $"""
      PSET (5,5),9
      GET (5,5)-(5,5),s%(0)
      PSET (20,20),3
      PUT (20,20),s%(0),{action}
      PRINT POINT(20,20)
      """;
    var optimized = Execute(body, true);
    var baseline = Execute(body, false);
    Assert.Multiple(() => {
      Assert.That(optimized.Output, Is.EqualTo(baseline.Output));
      Assert.That(optimized.Output.Trim(), Is.EqualTo(expected));
    });
  }

  [Test]
  public void Execute_GivenRepeatedXorPut_ThenTheInitialBackgroundIsRestored() {
    const string body = """
      LINE (2,2)-(9,3),7,BF
      GET (2,2)-(9,3),s%(0)
      LINE (30,30)-(37,31),3,BF
      PUT (30,30),s%(0)
      PRINT POINT(30,30);POINT(37,31);
      PUT (30,30),s%(0)
      PRINT POINT(30,30);POINT(37,31)
      """;
    var optimized = Execute(body, true);
    var baseline = Execute(body, false);
    Assert.Multiple(() => {
      Assert.That(optimized.Output, Is.EqualTo(baseline.Output));
      Assert.That(optimized.Output.Trim(), Does.Contain("4  4  3  3"));
    });
  }

  [Test]
  public void Execute_GivenUnalignedMultirowSprite_ThenPutPixelsMatchTheCapturedImage() {
    const string body = """
      PSET (5,5),1
      PSET (6,5),2
      PSET (7,5),4
      PSET (8,5),8
      PSET (9,5),15
      PSET (5,6),9
      PSET (6,6),10
      PSET (7,6),11
      PSET (8,6),12
      PSET (9,6),13
      GET (5,5)-(9,6),s%(0)
      PUT (22,26),s%(0),PSET
      PRINT POINT(22,26);POINT(23,26);POINT(24,26);POINT(25,26);POINT(26,26)
      PRINT POINT(22,27);POINT(23,27);POINT(24,27);POINT(25,27);POINT(26,27)
      """;
    var optimized = Execute(body, true);
    var baseline = Execute(body, false);
    Assert.Multiple(() => {
      Assert.That(optimized.Output, Is.EqualTo(baseline.Output));
      Assert.That(optimized.Output.Trim(), Does.Contain("1  2  4  8  15"));
      Assert.That(optimized.Output.Trim(), Does.Contain("9  10  11  12  13"));
    });
  }

  [Test]
  public void Execute_GivenScreen12SpriteBeyondMode13Viewport_ThenRoundTripUses640By480() {
    const string body = """
      LINE (321,205)-(326,207),6,BF
      GET (321,205)-(326,207),s%(0)
      PUT (340,220),s%(0),PSET
      PRINT s%(0);s%(1);POINT(340,220);POINT(345,222);POINT(346,222)
      """;
    var optimized = Execute(body, true);
    var baseline = Execute(body, false);
    Assert.Multiple(() => {
      Assert.That(optimized.Output, Is.EqualTo(baseline.Output));
      Assert.That(optimized.Output.Trim(), Does.Contain("6  3  6  6  0"));
    });
  }
}
