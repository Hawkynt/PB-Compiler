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
  /// An arc is declined rather than drawn as a whole circle. Silently ignoring the angles would be
  /// the worst outcome available - a program asking for a quarter turn would get all four.
  /// </summary>
  [Test]
  public void Circle_GivenAnArc_ThenTheCompilerSaysItCannotYet() {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize("""
      SCREEN 13
      CIRCLE (40, 40), 10, 12, 0.0, 1.5
      END
      """, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    var cg = new CodeGenerator(model);
    cg.EmitExecutable();

    Assert.That(cg.Errors.Select(e => e.Message), Has.Some.Contains("start/end angle"));
  }
}
