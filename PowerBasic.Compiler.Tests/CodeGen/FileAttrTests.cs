using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// <c>FILEATTR(n, 1)</c> - the mode a file was opened in.
///
/// The runtime keeps its own numbering in <c>rt_fmode</c> (0 INPUT, 1 OUTPUT, 2 APPEND, 3 RANDOM,
/// 4 BINARY) and BASIC answers with a different one (1, 2, 8, 4, 32). The two orders are not the
/// same - APPEND is 2 internally and 8 outside, RANDOM is 3 internally and 4 outside - so this is a
/// translation rather than a load, and the pair that crosses over is exactly what these tests pin.
/// A mapping that simply doubled the internal value would answer correctly for INPUT and OUTPUT and
/// wrongly for the other three.
///
/// <c>FILEATTR(n, 2)</c>, the DOS handle, is what the statement already did and set the convention
/// this follows.
/// </summary>
[TestFixture]
public sealed class FileAttrTests {

  private static string Run(string body) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(body + "\nEND\n", "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var cg = new CodeGenerator(model) { Optimize = true };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }

  [Test]
  public void FileAttr_GivenAnOutputFile_ThenTheModeIsTwo() =>
    Assert.That(Run("""
      OPEN "A.TXT" FOR OUTPUT AS #1
      PRINT FILEATTR(1, 1)
      CLOSE
      """), Is.EqualTo("2"));

  [Test]
  public void FileAttr_GivenAnInputFile_ThenTheModeIsOne() =>
    Assert.That(Run("""
      OPEN "A.TXT" FOR OUTPUT AS #1
      CLOSE #1
      OPEN "A.TXT" FOR INPUT AS #1
      PRINT FILEATTR(1, 1)
      CLOSE
      """), Is.EqualTo("1"));

  /// <summary>
  /// APPEND and RANDOM are the pair whose order differs between the two numberings - 2 and 3
  /// internally, 8 and 4 outside. Either one alone could pass a translation that had them swapped.
  /// </summary>
  [Test]
  public void FileAttr_GivenAppendAndRandom_ThenTheyCrossOverRatherThanFollowTheInternalOrder() =>
    Assert.That(Run("""
      OPEN "A.TXT" FOR OUTPUT AS #1
      CLOSE #1
      OPEN "A.TXT" FOR APPEND AS #1
      OPEN "B.DAT" FOR RANDOM AS #2 LEN = 16
      PRINT FILEATTR(1, 1); FILEATTR(2, 1)
      CLOSE
      """), Is.EqualTo("8  4"));

  [Test]
  public void FileAttr_GivenABinaryFile_ThenTheModeIsThirtyTwo() =>
    Assert.That(Run("""
      OPEN "A.TXT" FOR BINARY AS #1
      PRINT FILEATTR(1, 1)
      CLOSE
      """), Is.EqualTo("32"));

  /// <summary>Two files at once keep their own modes - the table is indexed, not a single cell.</summary>
  [Test]
  public void FileAttr_GivenTwoOpenFiles_ThenEachReportsItsOwnMode() =>
    Assert.That(Run("""
      OPEN "A.TXT" FOR OUTPUT AS #1
      OPEN "B.TXT" FOR OUTPUT AS #2
      CLOSE #2
      OPEN "B.TXT" FOR INPUT AS #2
      PRINT FILEATTR(1, 1); FILEATTR(2, 1)
      CLOSE
      """), Is.EqualTo("2  1"));

  /// <summary>The handle form still answers, and is not the mode.</summary>
  [Test]
  public void FileAttr_GivenTheHandleAttribute_ThenItIsStillTheDosHandle() =>
    Assert.That(Run("""
      OPEN "A.TXT" FOR OUTPUT AS #1
      PRINT FILEATTR(1, 2) > 4
      CLOSE
      """), Is.EqualTo("-1"), "DOS hands out handles above the five it opens for every program");

  /// <summary>An attribute that is neither is refused rather than answered with one of them.</summary>
  [Test]
  public void FileAttr_GivenAnUnknownAttribute_ThenItIsRefused() {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize("OPEN \"A.TXT\" FOR OUTPUT AS #1\nPRINT FILEATTR(1, 3)\nEND\n", "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    var cg = new CodeGenerator(model);
    cg.EmitExecutable();
    Assert.That(cg.Errors.Select(e => e.Message), Has.Some.Contains("FILEATTR attribute"));
  }
}
