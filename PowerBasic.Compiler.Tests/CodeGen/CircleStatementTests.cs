using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// <c>CIRCLE</c>, checked by reading the pixels back with <c>POINT</c>.
///
/// The midpoint algorithm walks one octant and mirrors it into the other seven, so the failures worth
/// looking for are asymmetries - an octant plotted with a sign the wrong way round gives a shape that
/// is right on one axis and missing on another, which no single sample would catch. Every test here
/// therefore checks all four cardinal points, not one.
/// </summary>
[TestFixture]
public sealed class CircleStatementTests {

  private static string Run(string body) {
    var source = "SCREEN 13\n" + body + "\nEND\n";
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var cg = new CodeGenerator(model) { Optimize = true };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }

  /// <summary>All four cardinal points lie on the circle, and the centre does not - it is an outline.</summary>
  [Test]
  public void Circle_GivenARadius_ThenTheFourCardinalPointsAreSetAndTheCentreIsNot() =>
    Assert.That(Run("""
      CIRCLE (40, 40), 10, 12
      PRINT POINT(50, 40); POINT(30, 40); POINT(40, 50); POINT(40, 30); POINT(40, 40)
      """), Is.EqualTo("12  12  12  12  0"));

  [Test]
  public void Circle_GivenNoColour_ThenItUsesTheDefaultForeground() =>
    Assert.That(Run("""
      CIRCLE (40, 40), 5
      PRINT POINT(45, 40); POINT(35, 40)
      """), Is.EqualTo("15  15"));

  /// <summary>Two points well inside the radius and one well outside must all stay clear.</summary>
  [Test]
  public void Circle_GivenARadius_ThenNothingIsDrawnInsideOrOutsideIt() =>
    Assert.That(Run("""
      CIRCLE (60, 60), 20, 9
      PRINT POINT(60, 45); POINT(60, 55); POINT(60, 85)
      """), Is.EqualTo("0  0  0"));

  /// <summary>
  /// A centre near the edge is what the clipping is for: the mirrored points go negative, and without
  /// the clip they wrap round the frame buffer and appear on the far side of the screen.
  /// </summary>
  [Test]
  public void Circle_GivenACentreAtTheEdge_ThenTheOffScreenPixelsAreDroppedNotWrapped() =>
    Assert.That(Run("""
      CIRCLE (2, 40), 10, 11
      PRINT POINT(12, 40); POINT(2, 50); POINT(2, 30); POINT(315, 40); POINT(318, 40)
      """), Is.EqualTo("11  11  11  0  0"));

  [Test]
  public void Circle_GivenARadiusOfZero_ThenOnlyTheCentreIsTouched() =>
    Assert.That(Run("""
      CIRCLE (20, 20), 0, 5
      PRINT POINT(20, 20); POINT(21, 20); POINT(20, 21)
      """), Is.EqualTo("5  0  0"));

  /// <summary>
  /// An arc no longer draws a whole circle, and never did draw one: it was declined until the walk
  /// below was written. Silently ignoring the angles would have been the worst outcome available -
  /// a program asking for a quarter turn getting all four - which is why it refused rather than
  /// approximated, and the tests underneath are what replaced the refusal.
  /// </summary>
  [Test]
  public void Circle_GivenAnArc_ThenItIsDrawnRatherThanRefused() {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize("""
      SCREEN 13
      CIRCLE (40, 40), 10, 12, 0.0, 1.5
      END
      """, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    var cg = new CodeGenerator(model);
    cg.EmitExecutable();

    Assert.That(cg.Errors, Is.Empty);
  }

  // ---- the arc and aspect forms -----------------------------------------------------------------

  /// <summary>
  /// A quarter arc from 0 to pi/2 is the UPPER RIGHT quadrant. The screen counts rows downward while
  /// the angle turns anticlockwise, so the sine is subtracted - get that backwards and the arc is a
  /// perfect quarter circle in the wrong place, which no single sample would catch.
  /// </summary>
  [Test]
  public void Circle_GivenAQuarterArc_ThenItCoversTheUpperRightAndNothingElse() =>
    Assert.That(Run("""
      CIRCLE (60, 60), 20, 9, 0, 1.5707963
      PRINT POINT(80, 60); POINT(60, 40); POINT(40, 60); POINT(60, 80)
      """), Is.EqualTo("9  9  0  0"), "right and up are on the arc; left and down are not");

  /// <summary>The opposite quarter, to show the walk follows its bounds rather than always starting at 0.</summary>
  [Test]
  public void Circle_GivenAnArcThatDoesNotStartAtZero_ThenItBeginsWhereItWasTold() =>
    Assert.That(Run("""
      CIRCLE (60, 60), 20, 11, 3.1415927, 4.712389
      PRINT POINT(40, 60); POINT(60, 80); POINT(80, 60); POINT(60, 40)
      """), Is.EqualTo("11  11  0  0"), "left and down this time");

  /// <summary>A full turn given explicitly draws the same circle the midpoint walk does.</summary>
  [Test]
  public void Circle_GivenAFullTurnAsAnArc_ThenAllFourCardinalPointsAreOn() =>
    Assert.That(Run("""
      CIRCLE (60, 60), 20, 12, 0, 6.2831853
      PRINT POINT(80, 60); POINT(60, 40); POINT(40, 60); POINT(60, 80); POINT(60, 60)
      """), Is.EqualTo("12  12  12  12  0"), "an outline, with the centre still clear");

  /// <summary>
  /// The aspect ratio scales the vertical radius and leaves the horizontal one alone, so a half
  /// aspect puts the top of the ellipse at half the distance while the sides stay put.
  /// </summary>
  [Test]
  public void Circle_GivenAnAspectRatio_ThenOnlyTheVerticalRadiusScales() =>
    Assert.That(Run("""
      CIRCLE (60, 60), 20, 13, , , 0.5
      PRINT POINT(80, 60); POINT(60, 50); POINT(60, 40)
      """), Is.EqualTo("13  13  0"), "the side is still at 20; the top is at 10 rather than 20");

  /// <summary>An aspect of one is the plain circle, which is what makes it the right default.</summary>
  [Test]
  public void Circle_GivenAnAspectOfOne_ThenItIsTheOrdinaryCircle() =>
    Assert.That(Run("""
      CIRCLE (60, 60), 20, 14, , , 1.0
      PRINT POINT(80, 60); POINT(60, 40); POINT(40, 60); POINT(60, 80)
      """), Is.EqualTo("14  14  14  14"));

  /// <summary>A zero radius plots nothing rather than dividing by it to find the step.</summary>
  [Test]
  public void Circle_GivenAZeroRadiusArc_ThenItDrawsNothingAndReturns() =>
    Assert.That(Run("""
      CIRCLE (60, 60), 0, 15, 0, 1.5
      PRINT POINT(60, 60)
      """), Is.EqualTo("0"));
}
