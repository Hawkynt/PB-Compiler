using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// <c>BSAVE</c> and <c>BLOAD</c> - a block of <c>DEF SEG</c> written to a file and read back.
///
/// The file is QuickBASIC's: a seven-byte header (&amp;HFD, then the segment, offset and length as
/// words) followed by the bytes. Storing the offset is what lets <c>BLOAD</c> put the block back
/// where it came from without the program remembering, so the round trip is the headline test.
///
/// The transfer is also the one place in the runtime where DS stops pointing at the runtime's own
/// data - INT 21h reads and writes through DS:DX while the block lives in DEF SEG - so a test that
/// only checked the bytes arrived would miss the failure mode where the routine reads its own
/// length or offset after the swap and transfers garbage.
/// </summary>
[TestFixture]
public sealed class BSaveBLoadTests {

  private static string Run(string body) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(body + "\nEND\n", "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var cg = new CodeGenerator(model) { Optimize = true };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }

  /// <summary>Saved, overwritten, loaded back - and the file's own offset put it where it started.</summary>
  [Test]
  public void BSaveBLoad_GivenABlock_WhenReloaded_ThenTheBytesReturnToTheirOwnOffset() =>
    Assert.That(Run("""
      DIM b%(8)
      DEF SEG = VARSEG(b%(1))
      POKE VARPTR(b%(1)), 65
      POKE VARPTR(b%(1)) + 1, 66
      POKE VARPTR(b%(1)) + 2, 67
      BSAVE "T.BSV", VARPTR(b%(1)), 3
      POKE VARPTR(b%(1)), 0
      POKE VARPTR(b%(1)) + 1, 0
      POKE VARPTR(b%(1)) + 2, 0
      BLOAD "T.BSV"
      PRINT PEEK(VARPTR(b%(1))); PEEK(VARPTR(b%(1)) + 1); PEEK(VARPTR(b%(1)) + 2)
      """), Is.EqualTo("65  66  67"));

  /// <summary>A stated offset overrides the file's, which is how a block is relocated on load.</summary>
  [Test]
  public void BLoad_GivenAnOffset_ThenTheBlockLandsThereInsteadOfWhereItWasSaved() =>
    Assert.That(Run("""
      DIM b%(8)
      DEF SEG = VARSEG(b%(0))
      POKE VARPTR(b%(0)), 77
      POKE VARPTR(b%(0)) + 1, 78
      BSAVE "T.BSV", VARPTR(b%(0)), 2
      PRINT PEEK(VARPTR(b%(4))); PEEK(VARPTR(b%(4)) + 1);
      BLOAD "T.BSV", VARPTR(b%(4))
      PRINT PEEK(VARPTR(b%(4))); PEEK(VARPTR(b%(4)) + 1)
      """), Is.EqualTo("0  0  77  78"));

  /// <summary>
  /// The length is honoured rather than the whole rest of the block being written: the byte just
  /// past the saved run must come back untouched.
  /// </summary>
  [Test]
  public void BSave_GivenALength_ThenOnlyThatManyBytesTravel() =>
    Assert.That(Run("""
      DIM b%(8)
      DEF SEG = VARSEG(b%(1))
      POKE VARPTR(b%(1)), 11
      POKE VARPTR(b%(1)) + 1, 22
      BSAVE "T.BSV", VARPTR(b%(1)), 1
      POKE VARPTR(b%(1)), 0
      POKE VARPTR(b%(1)) + 1, 99
      BLOAD "T.BSV"
      PRINT PEEK(VARPTR(b%(1))); PEEK(VARPTR(b%(1)) + 1)
      """), Is.EqualTo("11  99"));

  /// <summary>A file that is not there is not an error, exactly as KILL and CHDIR report nothing.</summary>
  [Test]
  public void BLoad_GivenAMissingFile_ThenItIsSilentAndTheProgramCarriesOn() =>
    Assert.That(Run("""
      DIM b%(8)
      DEF SEG = VARSEG(b%(1))
      POKE VARPTR(b%(1)), 5
      BLOAD "NOSUCH.BSV"
      PRINT PEEK(VARPTR(b%(1)))
      """), Is.EqualTo("5"));
}
