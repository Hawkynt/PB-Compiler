using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// Microsoft BASIC SCREEN 12 GET stores four packed one-bit plane streams per
/// scanline, not Mode 13's byte-per-pixel raster. Pixel order is MSB-first;
/// the header holds width and height in pixels, and every plane row is word-aligned.
/// </summary>
[TestFixture]
public sealed class PlanarSpriteGetPutTests {
  private static Cpu8086 Execute(string body, bool optimize, string cpu = "", int bufferWords = 200) {
    var source = cpu + $"\nDIM s%({bufferWords})\nSCREEN 12\n" + body + "\nEND\n";
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
  public void Execute_GivenEightFourBitPixels_ThenGetWordAlignsEveryPlane(string cpu) {
    const string body = """
      FOR x% = 0 TO 7: PSET (x%,5),x%: NEXT
      GET (0,5)-(7,5),s%(0)
      PRINT s%(0);s%(1);s%(2);s%(3);s%(4);s%(5)
      """;
    // Q51412: every color-plane row is padded to an even byte boundary.
    // Plane data is 55h, 33h, 0Fh, 00h, each followed by a zero pad byte.
    var optimized = Execute(body, true, cpu);
    var baseline = Execute(body, false, cpu);
    Assert.Multiple(() => {
      Assert.That(optimized.Output, Is.EqualTo(baseline.Output));
      Assert.That(optimized.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Select(int.Parse), Is.EqualTo(new[] { 8, 1, 85, 51, 15, 0 }));
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
      Assert.That(optimized.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Select(int.Parse), Is.EqualTo(new[] { 9, 1, 85, 51, 15, -32768 }));
    });
  }

  [Test]
  public void Execute_GivenMicrosoftQ51412Screen12Box_ThenGetMatchesPublishedPlaneBytes() {
    const string body = """
      LINE (1,1)-(10,3),4,B
      GET (1,1)-(10,3),s%(0)
      FOR i% = 0 TO 13: PRINT s%(i%);: NEXT
      """;
    var optimized = Execute(body, true);
    var baseline = Execute(body, false);
    var expected = new[] {
      10, 3,
      0, 0, -16129, 0,
      0, 0, 16512, 0,
      0, 0, -16129, 0,
    };
    Assert.Multiple(() => {
      Assert.That(optimized.Output, Is.EqualTo(baseline.Output));
      Assert.That(optimized.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Select(int.Parse), Is.EqualTo(expected));
    });
  }

  [Test]
  public void Execute_GivenWordAlignedEightPixelSprite_ThenPutIgnoresPlanePadding() {
    const string body = """
      s%(0)=8:s%(1)=1
      s%(2)=-171:s%(3)=-205:s%(4)=-241:s%(5)=-256
      LINE (16,20)-(31,20),3,BF
      PUT (16,20),s%(0),PSET
      FOR x%=16 TO 24: PRINT POINT(x%,20);: NEXT
      """;
    var optimized = Execute(body, true);
    var baseline = Execute(body, false);
    Assert.Multiple(() => {
      Assert.That(optimized.Output, Is.EqualTo(baseline.Output));
      Assert.That(optimized.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Select(int.Parse), Is.EqualTo(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 3 }));
    });
  }

  [TestCase("""
    GET (-1,0)-(0,0),s%(0)
    """)]
  [TestCase("""
    s%(0)=641:s%(1)=1
    PUT (0,0),s%(0),PSET
    """)]
  [TestCase("""
    s%(0)=8:s%(1)=1
    PUT (633,0),s%(0),PSET
    """)]
  [TestCase("""
    s%(0)=0:s%(1)=1
    PUT (0,0),s%(0),PSET
    """)]
  [TestCase("""
    s%(0)=8:s%(1)=0
    PUT (0,0),s%(0),PSET
    """)]
  [TestCase("""
    s%(0)=-1:s%(1)=1
    PUT (0,0),s%(0),PSET
    """)]
  [TestCase("""
    s%(0)=640:s%(1)=481
    PUT (0,0),s%(0),PSET
    """)]
  public void Execute_GivenOutOfRangeGetOrPut_ThenRaisesIllegalFunctionCall(string statement) {
    var body = $"""
      ON ERROR RESUME NEXT
      {statement}
      PRINT ERR
      """;
    var optimized = Execute(body, true);
    var baseline = Execute(body, false);
    Assert.Multiple(() => {
      Assert.That(optimized.Output, Is.EqualTo(baseline.Output));
      Assert.That(optimized.Output.Trim(), Is.EqualTo("5"));
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
  public void Execute_GivenAlignedFourPlaneGet_ThenReadsWholeVgaPlaneBytes() {
    const string body = """
      FOR x% = 8 TO 39: PSET (x%,21),x% MOD 16: NEXT
      GET (8,21)-(39,21),s%(0)
      PRINT s%(0);s%(1)
      PUT (56,31),s%(0),PSET
      FOR x% = 56 TO 87: PRINT POINT(x%,31);: NEXT
      """;
    var optimized = Execute(body, true);
    var baseline = Execute(body, false);
    Assert.Multiple(() => {
      Assert.That(optimized.Output, Is.EqualTo(baseline.Output));
      Assert.That(optimized.Output.Trim(), Does.Contain("32  1"));
    });
  }

  [Test]
  [Category("Performance")]
  public void Measure_GivenAlignedPlanarGet_ThenHoistsHardwarePlaneSelectionsOutOfPixelLoop() {
    const string body = """
      GET (8,21)-(39,22),s%(0)
      PRINT s%(0);s%(1)
      """;
    var optimized = Execute(body, true);
    var baseline = Execute(body, false);
    Assert.Multiple(() => {
      Assert.That(optimized.Output, Is.EqualTo(baseline.Output));
      Assert.That(optimized.PortWrites.Count(p => p.Port is 0x3CE or 0x3CF),
        Is.LessThan(100), "one GC read-map selection per plane and scanline, not per pixel");
      Assert.That(optimized.ExecutedInstructions, Is.LessThan(baseline.ExecutedInstructions),
        "compare emulator dispatch counts, not VGA bus wait states");
    });
  }

  [TestCase("PSET","11")]
  [TestCase("PRESET","4")]
  [TestCase("AND","3")]
  [TestCase("OR","11")]
  [TestCase("XOR","8")]
  public void Execute_GivenAlignedSpriteAndVgaLogicalVerb_ThenUsesExactBitplaneAlgebra(
      string verb, string expected) {
    var body = $"""
      LINE (8,8)-(23,8),11,BF
      GET (8,8)-(23,8),s%(0)
      LINE (32,20)-(47,20),3,BF
      PUT (32,20),s%(0),{verb}
      PRINT POINT(32,20);POINT(39,20);POINT(47,20)
      """;
    var optimized = Execute(body, true);
    var reference = Execute(body, false);
    Assert.Multiple(() => {
      Assert.That(optimized.Output, Is.EqualTo(reference.Output));
      Assert.That(optimized.Output.Trim(), Does.Contain($"{expected}  {expected}  {expected}"));
    });
  }

  [Test]
  [Category("Performance")]
  public void Measure_GivenAlignedPlanarPut_ThenWritesEightPixelBytesPlaneMajor() {
    const string body = """
      s%(0)=32
      s%(1)=2
      PUT (16,20),s%(0),PSET
      PRINT POINT(16,20);POINT(47,21)
      """;
    var optimized = Execute(body, true);
    var reference = Execute(body, false);
    Assert.Multiple(() => {
      Assert.That(optimized.Output, Is.EqualTo(reference.Output));
      Assert.That(optimized.ExecutedInstructions, Is.LessThan(reference.ExecutedInstructions));
      Assert.That(optimized.PortWrites.Count(p => p.Port is 0x3C4 or 0x3C5),
        Is.LessThan(40), "select each of four map masks once per row, not per sprite pixel");
    });
  }

  [Test]
  [Category("Performance")]
  public void Measure_GivenSixteenRowsOfAlignedGet_ThenHoistsReadMapSelection() {
    const string body = """
      GET (8,10)-(71,25),s%(0)
      PRINT s%(0);s%(1);s%(2);s%(3)
      """;
    var optimized = Execute(body, true, bufferWords: 300);
    var baseline = Execute(body, false, bufferWords: 300);
    var indexedPortWrites = optimized.PortWrites.Count(p => p.Port is 0x3CE or 0x3CF);
    Assert.Multiple(() => {
      Assert.That(optimized.Output, Is.EqualTo(baseline.Output));
      Assert.That(optimized.Output.Trim(), Does.Contain("64  16  0  0"));
      Assert.That(indexedPortWrites, Is.LessThan(40),
        "four read-map selections should be shared by all sixteen scanlines");
      Assert.That(optimized.ExecutedInstructions, Is.LessThan(baseline.ExecutedInstructions));
    });
    TestContext.Progress.WriteLine(
      $"64x16 GET: optimized GC OUT={indexedPortWrites}, "
      + $"instructions={optimized.ExecutedInstructions}, fetched bytes={optimized.FetchedInstructionBytes}; "
      + $"BIOS reference instructions={baseline.ExecutedInstructions}, "
      + $"fetched bytes={baseline.FetchedInstructionBytes}");
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
