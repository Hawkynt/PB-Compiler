using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// The current graphics foreground is shared by every primitive that omits its colour. QuickBASIC
/// defines COLOR as that state in SCREEN 12/13; keeping separate hard-coded 15 defaults makes the
/// statements agree only until the first COLOR.
/// </summary>
[TestFixture]
public sealed class GraphicsForegroundTests {
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
  public void Color_GivenScreen13_ThenEveryOmittedPrimitiveUsesTheCurrentForeground() =>
    Assert.That(Run("""
      COLOR 6
      PSET (1, 1)
      LINE (4, 4)-(8, 4)
      CIRCLE (20, 20), 3
      LINE (30, 30)-(40, 40), 6, B
      PAINT (35, 35)
      PSET (50, 50), 0
      DRAW "R4"
      PRINT POINT(1, 1); POINT(6, 4); POINT(23, 20); POINT(35, 35); POINT(54, 50)
      """), Is.EqualTo("6  6  6  6  6"));

  [Test]
  public void Screen_GivenAPreviousColor_ThenChangingModeRestoresItsDefaultForeground() =>
    Assert.That(Run("""
      COLOR 6
      PSET (1, 1)
      SCREEN 13
      PSET (2, 2)
      PRINT POINT(1, 1); POINT(2, 2)
      """), Is.EqualTo("0  15"),
      "SCREEN clears the framebuffer and re-establishes the mode's default foreground");

  [Test]
  public void Color_GivenAnExplicitPrimitiveColor_ThenExplicitColorStillWins() =>
    Assert.That(Run("""
      COLOR 6
      PSET (1, 1), 9
      LINE (4, 4)-(8, 4), 10
      CIRCLE (20, 20), 3, 11
      PRINT POINT(1, 1); POINT(6, 4); POINT(23, 20)
      """), Is.EqualTo("9  10  11"));
}
