using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// <c>LINE</c> in every spelling, checked by reading the pixels back with <c>POINT</c> rather than by
/// looking at the instructions.
///
/// The statement is eight grammars, not one: with and without a start point, with a colour, with
/// <c>B</c>, with <c>BF</c>, with a style mask, and the shapes where a middle argument is elided but a
/// later one is present. Each combination reaches a different set of defaults, and the interesting
/// failures are the quiet ones - a box drawn as a diagonal, a style mask read as a colour, a segment
/// that starts at the origin instead of at the last point referenced.
/// </summary>
[TestFixture]
public sealed class LineStatementTests {

  private static string Run(string body) {
    var source = "SCREEN 13\n" + body + "\nEND\n";
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var cg = new CodeGenerator(model) { Optimize = true };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }

  [Test]
  public void Line_GivenAHorizontalSegment_ThenEveryPixelBetweenTheEndsIsSet() {
    // both ends inclusive, and one pixel past each end still clear
    Assert.That(Run("""
      LINE (10, 5)-(14, 5), 9
      PRINT POINT(9, 5); POINT(10, 5); POINT(12, 5); POINT(14, 5); POINT(15, 5)
      """), Is.EqualTo("0  9  9  9  0"));
  }

  [Test]
  public void Line_GivenAVerticalSegment_ThenEveryPixelBetweenTheEndsIsSet() =>
    Assert.That(Run("""
      LINE (7, 2)-(7, 6), 3
      PRINT POINT(7, 1); POINT(7, 2); POINT(7, 4); POINT(7, 6); POINT(7, 7)
      """), Is.EqualTo("0  3  3  3  0"));

  /// <summary>A 45-degree diagonal is the case that catches an error accumulator with the wrong sign.</summary>
  [Test]
  public void Line_GivenADiagonal_ThenItStepsOneForOne() =>
    Assert.That(Run("""
      LINE (0, 0)-(4, 4), 5
      PRINT POINT(0, 0); POINT(1, 1); POINT(2, 2); POINT(3, 3); POINT(4, 4); POINT(1, 0)
      """), Is.EqualTo("5  5  5  5  5  0"));

  /// <summary>Drawn right-to-left and bottom-to-top: the step directions have to be signed.</summary>
  [Test]
  public void Line_GivenAReversedSegment_ThenItDrawsTheSamePixels() =>
    Assert.That(Run("""
      LINE (14, 5)-(10, 5), 9
      PRINT POINT(10, 5); POINT(12, 5); POINT(14, 5); POINT(15, 5)
      """), Is.EqualTo("9  9  9  0"));

  /// <summary>
  /// No start point means "from the last point referenced", not "from the origin" - the thing that
  /// makes a polyline a polyline.
  /// </summary>
  [Test]
  public void Line_GivenNoStartPoint_ThenItContinuesFromTheLastPointDrawn() =>
    Assert.That(Run("""
      LINE (2, 3)-(6, 3), 4
      LINE -(6, 7), 4
      PRINT POINT(6, 5); POINT(6, 7); POINT(0, 5)
      """), Is.EqualTo("4  4  0"));

  [Test]
  public void Line_GivenNoColour_ThenItUsesTheDefaultForeground() =>
    Assert.That(Run("""
      LINE (1, 1)-(3, 1)
      PRINT POINT(2, 1)
      """), Is.EqualTo("15"));

  /// <summary>B draws the four edges and leaves the inside alone; BF fills it.</summary>
  [Test]
  public void Line_GivenB_ThenOnlyTheOutlineIsDrawn() =>
    Assert.That(Run("""
      LINE (2, 2)-(6, 6), 7, B
      PRINT POINT(2, 2); POINT(6, 2); POINT(2, 6); POINT(6, 6); POINT(4, 2); POINT(4, 4)
      """), Is.EqualTo("7  7  7  7  7  0"));

  [Test]
  public void Line_GivenBF_ThenTheInteriorIsFilledToo() =>
    Assert.That(Run("""
      LINE (2, 2)-(6, 6), 7, BF
      PRINT POINT(2, 2); POINT(6, 6); POINT(4, 4); POINT(3, 5); POINT(7, 7)
      """), Is.EqualTo("7  7  7  7  0"));

  [Test]
  public void Line_GivenAnElidedColourBeforeB_ThenTheBoxStillDrawsInTheDefault() =>
    Assert.That(Run("""
      LINE (2, 2)-(5, 5), , B
      PRINT POINT(2, 2); POINT(5, 5); POINT(3, 3)
      """), Is.EqualTo("15  15  0"));

  /// <summary>
  /// The style mask is consulted one bit per pixel and rotates as it goes, so &amp;HAAAA - alternating
  /// bits - leaves every second pixel clear. A mask read as a colour instead would paint solidly.
  /// </summary>
  [Test]
  public void Line_GivenAStyleMask_ThenItSkipsThePixelsTheMaskClears() {
    var got = Run("""
      LINE (0, 9)-(7, 9), 6, , &HAAAA
      PRINT POINT(0, 9); POINT(1, 9); POINT(2, 9); POINT(3, 9)
      """);

    // whichever phase the rotation starts on, the pattern has to alternate rather than be solid
    Assert.That(got, Is.EqualTo("6  0  6  0").Or.EqualTo("0  6  0  6"), $"not an alternating pattern: {got}");
  }

  [Test]
  public void Line_GivenASolidMaskExplicitly_ThenEveryPixelIsDrawn() =>
    Assert.That(Run("""
      LINE (0, 11)-(3, 11), 6, , &HFFFF
      PRINT POINT(0, 11); POINT(1, 11); POINT(2, 11); POINT(3, 11)
      """), Is.EqualTo("6  6  6  6"));

  [Test]
  public void Line_GivenAFilledBoxGivenBackwards_ThenItStillFills() =>
    Assert.That(Run("""
      LINE (6, 6)-(2, 2), 7, BF
      PRINT POINT(2, 2); POINT(4, 4); POINT(6, 6)
      """), Is.EqualTo("7  7  7"));

  /// <summary>
  /// PSET sets the LAST POINT REFERENCED, so the line that follows starts where the pixel was put.
  /// Before this it did not, and `PSET (x,y) : LINE -(a,b)` drew from wherever the previous graphics
  /// statement had finished - or from the origin, in a program where PSET was the first one.
  /// </summary>
  [Test]
  public void Pset_GivenAFollowingLineWithNoStart_ThenItBeginsWherePsetLeftOff() =>
    Assert.That(Run("""
      PSET (40, 40), 3
      LINE -(44, 40), 9
      PRINT POINT(42, 40); POINT(2, 0)
      """), Is.EqualTo("9  0"), "the segment runs from 40 to 44, not from the origin");
}
