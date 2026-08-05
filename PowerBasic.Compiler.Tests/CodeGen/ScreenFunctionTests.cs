using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// <c>SCREEN(row, col [, colour])</c> - the text page read back.
///
/// The page is 80 columns of two bytes at B800, character first and attribute second, with rows and
/// columns counting from one. Ordinary PRINT here goes through DOS rather than to that memory, so
/// the cells are written with POKE through DEF SEG - the same bytes the function reads, which makes
/// this a test of the addressing rather than a proxy for it.
///
/// The addressing is the whole of it, and the two places it goes wrong are the off-by-one on each
/// axis and the stride: a row is 160 bytes, not 80, because every cell carries its attribute. Both
/// mistakes read a plausible cell rather than crashing.
/// </summary>
[TestFixture]
public sealed class ScreenFunctionTests {

  private static string Run(string body) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(body + "\nEND\n", "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var cg = new CodeGenerator(model) { Optimize = true };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }

  /// <summary>The top-left cell is offset zero, so row and column both count from one.</summary>
  [Test]
  public void Screen_GivenTheFirstCell_ThenItReadsOffsetZero() =>
    Assert.That(Run("""
      DEF SEG = &HB800
      POKE 0, 65
      PRINT SCREEN(1, 1)
      """), Is.EqualTo("65"));

  /// <summary>A column step is two bytes, because each cell carries its attribute.</summary>
  [Test]
  public void Screen_GivenTheNextColumn_ThenItStepsTwoBytes() =>
    Assert.That(Run("""
      DEF SEG = &HB800
      POKE 2, 66
      PRINT SCREEN(1, 2)
      """), Is.EqualTo("66"));

  /// <summary>A row step is 160 bytes - eighty columns of two - and not eighty.</summary>
  [Test]
  public void Screen_GivenTheNextRow_ThenItStepsOneHundredAndSixty() =>
    Assert.That(Run("""
      DEF SEG = &HB800
      POKE 160, 67
      POKE 80, 99
      PRINT SCREEN(2, 1)
      """), Is.EqualTo("67"), "80 would be the answer if the attribute byte were forgotten");

  /// <summary>Both axes together, at a cell that is not on either edge.</summary>
  [Test]
  public void Screen_GivenAnInteriorCell_ThenBothAxesAreCounted() =>
    Assert.That(Run("""
      DEF SEG = &HB800
      POKE 322, 68
      PRINT SCREEN(3, 2)
      """), Is.EqualTo("68"), "row 3 is 320 bytes in, column 2 is 2 more");

  /// <summary>The third argument asks for the attribute, which sits after the character.</summary>
  [Test]
  public void Screen_GivenTheColourFlag_ThenItReadsTheAttributeByte() =>
    Assert.That(Run("""
      DEF SEG = &HB800
      POKE 0, 65
      POKE 1, 7
      PRINT SCREEN(1, 1); SCREEN(1, 1, 1)
      """), Is.EqualTo("65  7"));

  /// <summary>A false colour flag is the character, the same as leaving it out.</summary>
  [Test]
  public void Screen_GivenAFalseColourFlag_ThenItIsStillTheCharacter() =>
    Assert.That(Run("""
      DEF SEG = &HB800
      POKE 0, 65
      POKE 1, 7
      PRINT SCREEN(1, 1, 0)
      """), Is.EqualTo("65"));

  /// <summary>A high byte value comes back unsigned rather than as a negative.</summary>
  [Test]
  public void Screen_GivenAHighByte_ThenItIsNotSignExtended() =>
    Assert.That(Run("""
      DEF SEG = &HB800
      POKE 0, 200
      PRINT SCREEN(1, 1)
      """), Is.EqualTo("200"));

  /// <summary>The coordinates may be computed, not only written down.</summary>
  [Test]
  public void Screen_GivenComputedCoordinates_ThenItStillReadsTheRightCell() =>
    Assert.That(Run("""
      DIM r AS INTEGER
      DIM c AS INTEGER
      r = 3
      c = 2
      DEF SEG = &HB800
      POKE 322, 69
      PRINT SCREEN(r, c)
      """), Is.EqualTo("69"));
}
