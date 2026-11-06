using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// Validates two distinct ways of filling a Mode 12h VGA byte. A plane-major
/// traversal is a useful candidate for planar sprite sources, but hardware
/// write mode 2 already updates all four planes in one host byte operation
/// when every pixel of the byte has the same four-bit color.
/// </summary>
[TestFixture]
public sealed class PlanarSchedulingComparisonTests {
  private static Cpu8086 Run(string render, string probe = "PRINT POINT(0,0);POINT(3,0);POINT(7,0);POINT(8,0)") {
    var source = $"""
      SCREEN 12
      FOR x% = 0 TO 7 : PSET (x%,0),6 : NEXT
      DEF SEG = &HA000
      {render}
      {probe}
      END
      """;
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "PLANE.BAS", Dialect.Pb36),
      "PLANE.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = true, Container = DosContainer.Exe };
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    return Cpu8086.Run(exe);
  }

  [Test]
  public void Execute_GivenEightDifferentColors_ThenFourPlaneByteMasksMatchBiosPixels() {
    // Eight differently colored pixels are four independent *bytes* when
    // represented plane-major: no GC index switch is needed per pixel.
    // Leftmost pixel occupies bit 7, hence masks 55, 33, 0F, 00.
    const string byPlane = """
      OUT &H3CE, 5: OUT &H3CF, 0
      OUT &H3CE, 1: OUT &H3CF, 0
      OUT &H3CE, 3: OUT &H3CF, 0
      OUT &H3CE, 8: OUT &H3CF, 255
      OUT &H3C4, 2: OUT &H3C5, 1: POKE 0, &H55
      OUT &H3C4, 2: OUT &H3C5, 2: POKE 0, &H33
      OUT &H3C4, 2: OUT &H3C5, 4: POKE 0, &H0F
      OUT &H3C4, 2: OUT &H3C5, 8: POKE 0, 0
      """;
    const string byPixel = """
      FOR x% = 0 TO 7: PSET (x%,0),x%: NEXT
      """;
    const string probes = """
      PRINT POINT(0,0);POINT(1,0);POINT(2,0);POINT(3,0);POINT(4,0);POINT(5,0);POINT(6,0);POINT(7,0)
      """;
    var batched = Run(byPlane, probes);
    var baseline = Run(byPixel, probes);
    // The common setup initializes eight pixels using now-optimized PSET.
    // Subtract its register traffic to measure only the four plane switches.
    var initialization = Run("", probes);
    Assert.Multiple(() => {
      Assert.That(batched.Output, Is.EqualTo(baseline.Output));
      Assert.That(batched.Output.Trim(), Does.Contain("0  1  2  3  4  5  6  7"));
      Assert.That(batched.PortWrites.Count(e => e.Port is 0x3C4 or 0x3C5)
        - initialization.PortWrites.Count(e => e.Port is 0x3C4 or 0x3C5), Is.EqualTo(8),
        "four map-mask selections use two sequencer ports each");
    });
  }

  [TestCase(0)]
  [TestCase(3)]
  [TestCase(5)]
  [TestCase(10)]
  [TestCase(15)]
  public void Execute_GivenPlaneMajorAndBroadcastFills_ThenPixelValuesAgree(int color) {
    // Explicitly overwrite every plane, including the zero bits; otherwise
    // the initial color 6 would leave stale bits in planes 1 and 2.
    var planeMajor = $"""
      OUT &H3CE, 5: OUT &H3CF, 0
      OUT &H3CE, 1: OUT &H3CF, 0
      OUT &H3CE, 3: OUT &H3CF, 0
      OUT &H3CE, 8: OUT &H3CF, 255
      OUT &H3C4, 2: OUT &H3C5, 1
      POKE 0, {((color & 1) != 0 ? 255 : 0)}
      OUT &H3C4, 2: OUT &H3C5, 2
      POKE 0, {((color & 2) != 0 ? 255 : 0)}
      OUT &H3C4, 2: OUT &H3C5, 4
      POKE 0, {((color & 4) != 0 ? 255 : 0)}
      OUT &H3C4, 2: OUT &H3C5, 8
      POKE 0, {((color & 8) != 0 ? 255 : 0)}
      """;
    var broadcast = $"""
      OUT &H3C4, 2: OUT &H3C5, 15
      OUT &H3CE, 3: OUT &H3CF, 0
      OUT &H3CE, 8: OUT &H3CF, 255
      OUT &H3CE, 5: OUT &H3CF, 2
      POKE 0, {color}
      """;
    var byPlane = Run(planeMajor);
    var byBroadcast = Run(broadcast);
    Assert.Multiple(() => {
      Assert.That(byPlane.Output, Is.EqualTo(byBroadcast.Output));
      Assert.That(byPlane.Output.Trim(), Does.Contain($"{color}  {color}  {color}  0"));
      Assert.That(byPlane.PortWrites.Count(e => e.Port is 0x3C4 or 0x3C5),
        Is.GreaterThan(byBroadcast.PortWrites.Count(e => e.Port is 0x3C4 or 0x3C5)),
        "four plane-major visits require extra map-mask selections for this uniform run");
    });
  }
}
