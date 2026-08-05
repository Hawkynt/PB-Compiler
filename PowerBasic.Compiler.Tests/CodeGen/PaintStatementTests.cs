using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// <c>PAINT</c>, checked by reading the pixels back with <c>POINT</c>.
///
/// A flood fill has two ways to be wrong that a single sample cannot tell apart: it can stop early,
/// leaving part of the region unfilled, or it can leak past the border and fill the outside. Every
/// test therefore samples inside AND outside the shape it fills, and the leak checks matter more -
/// a fill that escapes its boundary on a 320x200 screen will happily paint all 64000 pixels.
///
/// The fill is a scanline one, so the interesting shapes are those where a row is reached only
/// through a row above or below it: a U keeps its two arms connected solely through its base, and a
/// fill that fails to seed the row above from the row it just filled leaves one arm empty.
/// </summary>
[TestFixture]
public sealed class PaintStatementTests {

  private static string Run(string body) {
    var source = "SCREEN 13\n" + body + "\nEND\n";
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var cg = new CodeGenerator(model) { Optimize = true };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }

  /// <summary>Inside is filled, the border keeps its own colour, and the outside is untouched.</summary>
  [Test]
  public void Paint_GivenABorderedBox_ThenTheInteriorFillsAndNothingEscapes() =>
    Assert.That(Run("""
      LINE (5, 5)-(15, 15), 4, B
      PAINT (10, 10), 15, 4
      PRINT POINT(10, 10); POINT(6, 6); POINT(14, 14); POINT(5, 5); POINT(4, 4); POINT(16, 16)
      """), Is.EqualTo("15  15  15  4  0  0"));

  /// <summary>Every pixel of the interior, not merely the one that was seeded.</summary>
  [Test]
  public void Paint_GivenABorderedBox_ThenTheWholeInteriorIsCovered() =>
    Assert.That(Run("""
      DIM n AS INTEGER
      LINE (5, 5)-(15, 15), 4, B
      PAINT (10, 10), 15, 4
      FOR y% = 6 TO 14
        FOR x% = 6 TO 14
          IF POINT(x%, y%) = 15 THEN n = n + 1
        NEXT x%
      NEXT y%
      PRINT n
      """), Is.EqualTo("81"));

  /// <summary>
  /// A U-shape: the two arms meet only through the base, so the fill has to travel down one arm,
  /// across and back up the other. A scanline fill that never seeds upward fills one arm and stops.
  /// </summary>
  [Test]
  public void Paint_GivenAShapeReachableOnlyThroughAnotherRow_ThenTheFillTravelsBothWays() =>
    Assert.That(Run("""
      LINE (10, 10)-(30, 30), 4, B
      LINE (15, 10)-(25, 25), 4, BF
      PAINT (12, 12), 15, 4
      PRINT POINT(12, 12); POINT(12, 28); POINT(28, 28); POINT(28, 12); POINT(20, 20)
      """), Is.EqualTo("15  15  15  15  4"));

  /// <summary>
  /// With no border colour the boundary IS the paint colour, so the fill spreads over everything
  /// reachable that is not already that colour and stops only where it meets it. A box drawn in some
  /// other colour is therefore painted straight over - it is not a boundary - while one drawn in the
  /// paint colour holds the fill out.
  /// </summary>
  [Test]
  public void Paint_GivenNoBorderColour_ThenOnlyItsOwnColourStopsIt() {
    Assert.That(Run("""
      LINE (5, 5)-(15, 15), 3, B
      PAINT (0, 0), 3
      PRINT POINT(0, 0); POINT(319, 199); POINT(5, 5); POINT(10, 10)
      """), Is.EqualTo("3  3  3  0"), "a box in the paint colour bounds the fill; its interior stays clear");

    Assert.That(Run("""
      LINE (5, 5)-(15, 15), 7, BF
      PAINT (0, 0), 3
      PRINT POINT(0, 0); POINT(10, 10)
      """), Is.EqualTo("3  3"), "a box in any other colour is not a boundary and is painted over");
  }

  /// <summary>A seed already sitting on the border colour has nothing to do.</summary>
  [Test]
  public void Paint_GivenASeedOnTheBorder_ThenNothingIsFilled() =>
    Assert.That(Run("""
      LINE (5, 5)-(15, 15), 4, B
      PAINT (5, 5), 15, 4
      PRINT POINT(5, 5); POINT(10, 10)
      """), Is.EqualTo("4  0"));

  /// <summary>A seed already the paint colour is likewise done before it starts - and must terminate.</summary>
  [Test]
  public void Paint_GivenASeedAlreadyPainted_ThenItTerminates() =>
    Assert.That(Run("""
      PSET (10, 10), 15
      PAINT (10, 10), 15, 4
      PRINT POINT(10, 10); POINT(11, 10)
      """), Is.EqualTo("15  0"));
}
