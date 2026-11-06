using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// Independently calculated VGA register truth-table vectors. The expected
/// values come from four 1-bit pixel planes and the documented VGA write
/// modes, not from comparing the emulator with itself or reusing its code.
/// </summary>
[TestFixture]
public sealed class PlanarVgaRegisterVectorTests {
  private static Cpu8086 Execute(string body, bool optimize) {
    var source = "SCREEN 12\n" + body + "\nEND\n";
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "VGA-VECTORS.BAS", Dialect.Pb36),
      "VGA-VECTORS.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize, Container = DosContainer.Exe };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    return Cpu8086.Run(image);
  }

  [TestCase(0)]
  [TestCase(5)]
  [TestCase(10)]
  [TestCase(15)]
  public void WriteMode2_GivenFullByte_ThenBroadcastsAllFourColorBits(int color) {
    var body = $"""
      DEF SEG = &HA000
      OUT &H3CE,5: OUT &H3CF,2
      OUT &H3CE,3: OUT &H3CF,0
      OUT &H3CE,8: OUT &H3CF,255
      OUT &H3C4,2: OUT &H3C5,15
      POKE 0,{color}
      PRINT POINT(0,0);POINT(3,0);POINT(7,0);POINT(8,0)
      """;
    var expected = $"{color}  {color}  {color}  0";
    foreach (var optimize in new[] { false, true })
      Assert.That(Execute(body, optimize).Output.Trim(), Does.Contain(expected));
  }

  [Test]
  public void WriteMode2_GivenPartialBitMask_ThenPreservesUntouchedNeighborPixels() {
    // The eight original pixels are all color 9 (planes 0 and 3 set).
    // Bit Mask AAh selects x=0,2,4,6 for replacement with color 6
    // (planes 1 and 2 set). Each unselected pixel must retain color 9.
    const string body = """
      LINE (0,0)-(7,0),9,BF
      DEF SEG = &HA000
      OUT &H3CE,3: OUT &H3CF,0
      OUT &H3CE,8: OUT &H3CF,170
      OUT &H3CE,5: OUT &H3CF,2
      OUT &H3C4,2: OUT &H3C5,15
      z% = PEEK(0)
      POKE 0,6
      FOR x% = 0 TO 7: PRINT POINT(x%,0);: NEXT
      """;
    foreach (var optimize in new[] { false, true })
      Assert.That(Execute(body, optimize).Output.Trim(),
        Does.Contain("6  9  6  9  6  9  6  9"));
  }

  [Test]
  public void ReadMode0_GivenPlaneSelection_ThenReadsOnlyTheSelectedPlane() {
    // Color 4 consists solely of plane 2; all other plane bytes are zero.
    // Selecting read map 2 must return FFh, then selecting map 0 returns 00h.
    const string body = """
      LINE (0,0)-(7,0),4,BF
      DEF SEG = &HA000
      OUT &H3CE,5: OUT &H3CF,0
      OUT &H3CE,4: OUT &H3CF,2
      PRINT PEEK(0);
      OUT &H3CE,4: OUT &H3CF,0
      PRINT PEEK(0)
      """;
    foreach (var optimize in new[] { false, true })
      Assert.That(Execute(body, optimize).Output.Trim(), Does.Contain("255  0"));
  }

  [Test]
  public void WriteMode3_GivenBitMask_ThenOnlySelectedPixelsReceiveSetResetColor() {
    const string body = """
      LINE (0,0)-(7,0),9,BF
      DEF SEG = &HA000
      OUT &H3CE,0: OUT &H3CF,6
      OUT &H3CE,3: OUT &H3CF,0
      OUT &H3CE,8: OUT &H3CF,255
      OUT &H3CE,5: OUT &H3CF,3
      OUT &H3C4,2: OUT &H3C5,15
      z% = PEEK(0)
      POKE 0,&HF0
      PRINT POINT(0,0);POINT(3,0);POINT(4,0);POINT(7,0)
      """;
    foreach (var optimize in new[] { false, true })
      Assert.That(Execute(body, optimize).Output.Trim(), Does.Contain("6  6  9  9"));
  }

  [Test]
  public void MapMask_GivenOnlyPlaneZeroEnabled_ThenLeavesOtherThreePlanesUntouched() {
    const string body = """
      LINE (0,0)-(7,0),15,BF
      DEF SEG = &HA000
      OUT &H3CE,5: OUT &H3CF,0
      OUT &H3CE,1: OUT &H3CF,0
      OUT &H3CE,3: OUT &H3CF,0
      OUT &H3CE,8: OUT &H3CF,255
      OUT &H3C4,2: OUT &H3C5,1
      z% = PEEK(0)
      POKE 0,0
      PRINT POINT(0,0);POINT(4,0);POINT(7,0);POINT(8,0)
      """;
    foreach (var optimize in new[] { false, true })
      Assert.That(Execute(body, optimize).Output.Trim(), Does.Contain("14  14  14  0"));
  }

  [Test]
  public void WriteMode1_GivenLoadedSourceLatches_ThenCopiesEveryPlaneToDestination() {
    const string body = """
      LINE (0,0)-(7,0),5,BF
      LINE (0,1)-(7,1),2,BF
      DEF SEG = &HA000
      z% = PEEK(0)
      OUT &H3CE,5: OUT &H3CF,1
      OUT &H3C4,2: OUT &H3C5,15
      POKE 80,0
      PRINT POINT(0,0);POINT(3,0);POINT(0,1);POINT(7,1);POINT(8,1)
      """;
    foreach (var optimize in new[] { false, true })
      Assert.That(Execute(body, optimize).Output.Trim(), Does.Contain("5  5  5  5  0"));
  }

  [Test]
  public void ReadMode1_GivenColorCompareAndDontCareMask_ThenReturnsPixelMatchBits() {
    const string body = """
      LINE (0,0)-(7,0),5,BF
      DEF SEG = &HA000
      OUT &H3CE,2: OUT &H3CF,5
      OUT &H3CE,7: OUT &H3CF,15
      OUT &H3CE,5: OUT &H3CF,8
      PRINT PEEK(0)
      """;
    foreach (var optimize in new[] { false, true })
      Assert.That(Execute(body, optimize).Output.Trim(), Does.Contain("255"));
  }
}
