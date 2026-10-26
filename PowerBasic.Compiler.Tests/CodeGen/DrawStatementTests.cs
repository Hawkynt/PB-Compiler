using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// <c>DRAW</c> with a written-down string, checked by reading the pixels back with <c>POINT</c>.
///
/// Known commands and the runtime parser share movement operations and persistent DRAW state.
///
/// The prefixes are what these tests are mostly about. B moves without drawing and N draws without
/// moving, and both are easy to get backwards in a way that looks right on a closed shape: a square
/// drawn with R D L U ends where it started whether or not the position tracking works at all.
/// </summary>
[TestFixture]
public sealed class DrawStatementTests {

  private static string Run(string body) {
    var source = "SCREEN 13\n" + body + "\nEND\n";
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var cg = new CodeGenerator(model) { Optimize = true };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }

  /// <summary>A square: each side drawn, and the inside left alone.</summary>
  [Test]
  public void Draw_GivenASquare_ThenTheSidesArePlottedAndTheInsideIsNot() =>
    Assert.That(Run("""
      PSET (10, 10), 0
      DRAW "C15 R5 D5 L5 U5"
      PRINT POINT(15, 10); POINT(15, 15); POINT(10, 15); POINT(12, 10); POINT(12, 12)
      """), Is.EqualTo("15  15  15  15  0"), "the four corners and a mid-side are set; the middle is not");

  /// <summary>The eight movements go where their letters say.</summary>
  [TestCase("R4", 44, 40)]
  [TestCase("L4", 36, 40)]
  [TestCase("D4", 40, 44)]
  [TestCase("U4", 40, 36)]
  [TestCase("E4", 44, 36)]
  [TestCase("F4", 44, 44)]
  [TestCase("G4", 36, 44)]
  [TestCase("H4", 36, 36)]
  public void Draw_GivenAMovement_ThenItEndsWhereItsLetterSays(string move, int x, int y) =>
    Assert.That(Run($"""
      PSET (40, 40), 0
      DRAW "C9 {move}"
      PRINT POINT({x}, {y})
      """), Is.EqualTo("9"));

  /// <summary>A movement with no count is one step.</summary>
  [Test]
  public void Draw_GivenAMovementWithNoCount_ThenItIsOneStep() =>
    Assert.That(Run("""
      PSET (40, 40), 0
      DRAW "C9 R"
      PRINT POINT(41, 40); POINT(42, 40)
      """), Is.EqualTo("9  0"));

  /// <summary>B moves without drawing: the far end is set, the path to it is not.</summary>
  [Test]
  public void Draw_GivenTheBlankPrefix_ThenItMovesWithoutDrawing() =>
    Assert.That(Run("""
      PSET (10, 20), 0
      DRAW "C12 BR10 R2"
      PRINT POINT(15, 20); POINT(21, 20)
      """), Is.EqualTo("0  12"), "nothing along the blank move; the drawn move after it lands at 20..22");

  /// <summary>
  /// N draws and returns: the line appears, and the NEXT movement starts from where the N began
  /// rather than from where it ended.
  /// </summary>
  [Test]
  public void Draw_GivenTheNoUpdatePrefix_ThenItDrawsAndComesBack() =>
    Assert.That(Run("""
      PSET (10, 30), 0
      DRAW "C11 NR5 D3"
      PRINT POINT(14, 30); POINT(10, 33); POINT(15, 33)
      """), Is.EqualTo("11  11  0"), "the arm is drawn, and the D starts from x=10 rather than x=15");

  /// <summary>C changes the colour for everything after it and nothing before.</summary>
  [Test]
  public void Draw_GivenAColourChange_ThenItAppliesFromThatPointOn() =>
    Assert.That(Run("""
      PSET (10, 40), 0
      DRAW "C3 R4 C13 R4"
      PRINT POINT(12, 40); POINT(17, 40)
      """), Is.EqualTo("3  13"));

  /// <summary>M with a signed pair is relative; without, it is absolute.</summary>
  [Test]
  public void Draw_GivenMoveTo_ThenSignedIsRelativeAndUnsignedIsAbsolute() =>
    Assert.That(Run("""
      PSET (10, 50), 0
      DRAW "C7 M+6,+0 M30,50"
      PRINT POINT(13, 50); POINT(20, 50)
      """), Is.EqualTo("7  7"), "the relative move draws to x=16, then the absolute one carries on to x=30");

  [TestCase("S4 R10", 50, 40)]
  [TestCase("A1 R6", 40, 35)]
  [TestCase("TA90 R6", 40, 35)]
  [TestCase("TA-90 R6", 40, 45)]
  public void Draw_GivenScaleOrRotation_ThenExecutesItInsteadOfIgnoringIt(string picture, int x, int y) =>
    Assert.That(Run($"PSET (40,40),0\nDRAW \"C9 {picture}\"\nPRINT POINT({x},{y})"), Is.EqualTo("9"));

  [Test]
  public void Draw_GivenAComputedString_ThenParsesItAtRuntime() =>
    Assert.That(Run("READ p$\nDATA \"C9 R5\"\nPSET (10,10),0\nDRAW p$\nPRINT POINT(15,10);POINT(16,10)"),
      Is.EqualTo("9  0"));

  [Test]
  public void Draw_GivenAPaintCommand_ThenFillsWithoutChangingItsDrawingColour() =>
    Assert.That(Run("""
      LINE (5,5)-(15,15),4,B
      PSET (10,10),0
      DRAW "C3 P15,4 BR10 R2"
      PRINT POINT(10,10);POINT(6,6);POINT(5,5);POINT(21,10)
      """), Is.EqualTo("15  15  4  3"));
}
