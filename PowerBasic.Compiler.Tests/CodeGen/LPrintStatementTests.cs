using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// <c>LPRINT</c>, which is <c>PRINT</c> with the output pointed at DOS handle 4 (PRN).
///
/// There is no printer to read back here, so what these check is the half that goes wrong silently:
/// that the redirect happens at all, and - more importantly - that it is UNDONE. An LPRINT that
/// leaves rt_curout pointing at the printer sends the rest of the program's output there too, and
/// the symptom is a program that prints nothing rather than one that crashes.
/// </summary>
[TestFixture]
public sealed class LPrintStatementTests {

  private static string Run(string body) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(body + "\nEND\n", "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var cg = new CodeGenerator(model) { Optimize = true };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }

  [Test]
  public void LPrint_GivenText_ThenItDoesNotReachTheScreen() =>
    Assert.That(Run("""
      LPRINT "printer"
      PRINT "screen"
      """), Is.EqualTo("screen"));

  [Test]
  public void LPrint_GivenAFollowingPrint_ThenTheScreenGetsItsOutputBack() =>
    Assert.That(Run("""
      PRINT "before"
      LPRINT "printer"
      PRINT "after"
      """), Is.EqualTo("before|after"));

  /// <summary>Every item shape the ordinary PRINT path has, since LPRINT reuses all of it.</summary>
  [Test]
  public void LPrint_GivenItemsSeparatorsAndUsing_ThenNoneOfItReachesTheScreen() =>
    Assert.That(Run("""
      DIM n AS INTEGER
      n = 42
      LPRINT "a", n; TAB(10); "b"
      LPRINT USING "###.##"; 3.5
      LPRINT
      PRINT "screen"
      """), Is.EqualTo("screen"));

  /// <summary>
  /// LPOS reports the printer's column, and it moves as the printer is written to - which is the
  /// only observable the printer has here, there being nothing to read the paper back from.
  /// </summary>
  [Test]
  public void LPos_GivenPrinterOutput_ThenItTracksThePrinterColumnAndNotTheScreens() =>
    Assert.That(Run("""
      PRINT LPOS(0);
      LPRINT "abcde";
      PRINT LPOS(0); POS(0)
      """), Is.EqualTo("1  6  7"),
      "LPOS counts the five printer characters; POS counts the six screen ones PRINT itself emitted");

  /// <summary>
  /// The printer's column is its own. A comma zone on the printer must not shift the screen's,
  /// which is why LPOS and POS are separate functions - so POS(0) is unmoved by an LPRINT.
  /// </summary>
  [Test]
  public void LPrint_GivenACommaZone_ThenTheScreenColumnIsUnmoved() =>
    Assert.That(Run("""
      PRINT "ab";
      LPRINT "xxxxxxxxxxxxxxxx",
      PRINT POS(0)
      """), Is.EqualTo("ab 3"));
}
