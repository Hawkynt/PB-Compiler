using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// <c>PCOPY source, destination</c> - one text-mode video page copied over another.
///
/// A page is 4096 bytes at B800, of which 80x25x2 = 4000 are on screen and the rest is the slack
/// that puts the next page on a round boundary. There is nothing to read a screen back from here,
/// so the pages are written and read with POKE and PEEK through DEF SEG, which is the same memory
/// the copy moves and therefore a fair test of it.
///
/// Compiled as QuickBASIC, because PCOPY is Microsoft's: genuine PBC 3.0 and 3.5 answer it
/// "Undefined error", so no PowerBASIC dialect accepts the statement these tests are about.
/// </summary>
[TestFixture]
public sealed class PCopyTests {

  /// <summary>
  /// Every program here prints its answer, and the console driver puts what it prints into the very
  /// page these tests POKE - so the output is moved down to row 10 first, clear of the cells being
  /// read back. Without it a program overwrites its own subject between two PEEKs of one PRINT.
  /// </summary>
  private static string Run(string body) {
    var source = "LOCATE 10, 1\n" + body + "\nEND\n";
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Qb45), "T.BAS", Dialect.Qb45), Dialect.Qb45);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var cg = new CodeGenerator(model) { Optimize = true };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }

  /// <summary>A byte written to page 0 turns up at the same offset in page 1.</summary>
  [Test]
  public void PCopy_GivenAByteOnOnePage_ThenItAppearsAtTheSameOffsetOnTheOther() =>
    Assert.That(Run("""
      DEF SEG = &HB800
      POKE 0, 65
      POKE 1, 7
      POKE &H1000, 0
      PCOPY 0, 1
      PRINT PEEK(&H1000); PEEK(&H1001)
      """), Is.EqualTo("65  7"));

  /// <summary>
  /// The whole page travels, not just the first bytes: the last word of the 4000 on screen and a
  /// byte in the slack above it both arrive.
  /// </summary>
  [Test]
  public void PCopy_GivenBytesAcrossThePage_ThenTheWholePageTravels() =>
    Assert.That(Run("""
      DEF SEG = &HB800
      POKE &HF9E, 88
      POKE &HFFF, 99
      POKE &H1F9E, 0
      POKE &H1FFF, 0
      PCOPY 0, 1
      PRINT PEEK(&H1F9E); PEEK(&H1FFF)
      """), Is.EqualTo("88  99"));

  /// <summary>The source is left alone - this is a copy, not a move.</summary>
  [Test]
  public void PCopy_GivenACopy_ThenTheSourcePageIsUnchanged() =>
    Assert.That(Run("""
      DEF SEG = &HB800
      POKE 0, 65
      PCOPY 0, 2
      PRINT PEEK(0); PEEK(&H2000)
      """), Is.EqualTo("65  65"));

  /// <summary>
  /// Copying a page onto itself is a no-op rather than a corruption - REP MOVSW forwards over an
  /// exactly-overlapping range leaves it as it was.
  /// </summary>
  [Test]
  public void PCopy_GivenTheSamePageTwice_ThenNothingChanges() =>
    Assert.That(Run("""
      DEF SEG = &HB800
      POKE 0, 65
      POKE 1, 66
      PCOPY 1, 1
      PRINT PEEK(0); PEEK(1)
      """), Is.EqualTo("65  66"));

  /// <summary>The page numbers may be computed, not only written down.</summary>
  [Test]
  public void PCopy_GivenComputedPageNumbers_ThenItStillCopies() =>
    Assert.That(Run("""
      DIM a AS INTEGER
      DIM b AS INTEGER
      a = 0
      b = 3
      DEF SEG = &HB800
      POKE 0, 77
      POKE &H3000, 0
      PCOPY a, b
      PRINT PEEK(&H3000)
      """), Is.EqualTo("77"));
}
