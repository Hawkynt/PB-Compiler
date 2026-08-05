using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// <c>REMOVE$(s$, match$)</c> - the source with every occurrence of the match cut out.
///
/// The result is at most as long as the source and its exact length is not known until the walk is
/// over, so the full length is allocated and the finished string trimmed. That ordering is the only
/// real trap in the routine: allocating may COMPACT the string heap, so every pointer is fetched
/// after the allocation. One read beforehand would still look plausible and would address whatever
/// the compaction had moved into its place - which is why the test with a heap busy enough to
/// compact is here and not just the obvious ones.
/// </summary>
[TestFixture]
public sealed class StringRemoveTests {

  private static string Run(string body) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(body + "\nEND\n", "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var cg = new CodeGenerator(model) { Optimize = true };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }

  [TestCase("REMOVE$(\"banana\", \"an\")", "ba")]
  [TestCase("REMOVE$(\"aaa\", \"a\")", "")]
  [TestCase("REMOVE$(\"abc\", \"xy\")", "abc")]
  [TestCase("REMOVE$(\"hello world\", \"o\")", "hell wrld")]
  [TestCase("REMOVE$(\"abcabc\", \"abc\")", "")]
  public void Remove_GivenAMatch_ThenEveryOccurrenceGoes(string call, string expected) =>
    Assert.That(Run($"PRINT \"[\"; {call}; \"]\""), Is.EqualTo($"[{expected}]"));

  /// <summary>An empty match removes nothing - and, more to the point, terminates.</summary>
  [Test]
  public void Remove_GivenAnEmptyMatch_ThenTheSourceComesBackUnchanged() =>
    Assert.That(Run("PRINT \"[\"; REMOVE$(\"abc\", \"\"); \"]\""), Is.EqualTo("[abc]"));

  /// <summary>An empty source is empty however it is asked.</summary>
  [Test]
  public void Remove_GivenAnEmptySource_ThenTheResultIsEmpty() =>
    Assert.That(Run("PRINT \"[\"; REMOVE$(\"\", \"a\"); \"]\""), Is.EqualTo("[]"));

  /// <summary>
  /// Overlapping candidates are consumed left to right: "aaa" less "aa" leaves one "a", because the
  /// match at position 1 is taken and the scan resumes past it rather than at position 2.
  /// </summary>
  [Test]
  public void Remove_GivenOverlappingCandidates_ThenTheyAreTakenLeftToRight() =>
    Assert.That(Run("PRINT \"[\"; REMOVE$(\"aaa\", \"aa\"); \"]\""), Is.EqualTo("[a]"));

  /// <summary>A match longer than the source cannot occur, and must not read past the end.</summary>
  [Test]
  public void Remove_GivenAMatchLongerThanTheSource_ThenNothingIsRemoved() =>
    Assert.That(Run("PRINT \"[\"; REMOVE$(\"ab\", \"abcdef\"); \"]\""), Is.EqualTo("[ab]"));

  /// <summary>A partial match at the very end is not a match, and is copied out whole.</summary>
  [Test]
  public void Remove_GivenAPartialMatchAtTheEnd_ThenItSurvives() =>
    Assert.That(Run("PRINT \"[\"; REMOVE$(\"xxab\", \"abc\"); \"]\""), Is.EqualTo("[xxab]"));

  /// <summary>Computed operands, not only literals - both arrive as handles either way.</summary>
  [Test]
  public void Remove_GivenComputedOperands_ThenItStillWorks() =>
    Assert.That(Run("""
      DIM a AS STRING
      DIM b AS STRING
      a = "mississippi"
      b = "ss"
      PRINT REMOVE$(a, b)
      """), Is.EqualTo("miiippi"));

  /// <summary>
  /// The heap busy enough to move. Two hundred iterations allocating and discarding keeps the
  /// allocator compacting, which is exactly when a pointer cached across the allocation inside
  /// REMOVE$ would start reading the wrong bytes - and the operands are freed, so the run also
  /// proves it does not leak.
  /// </summary>
  [Test]
  public void Remove_GivenABusyHeap_ThenItStillReadsTheRightBytes() =>
    Assert.That(Run("""
      DIM s AS STRING
      FOR i% = 1 TO 200
        s = REMOVE$("banana" + CHR$(65), "an")
      NEXT i%
      PRINT s; LEN(SPACE$(2000))
      """), Is.EqualTo("baA 2000"));
}
