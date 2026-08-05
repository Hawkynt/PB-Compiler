using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// <c>MIN$</c> and <c>MAX$</c>, two more of the built-ins the intrinsic census found binding and
/// generating nothing.
///
/// The comparison is ordinary, but the handle bookkeeping is the whole difficulty: StrCmp consumes
/// both operands and these have to give one back, so the runtime duplicates the pair, compares and
/// discards the copies, and frees whichever original lost. A leak here would not show up as a wrong
/// answer - it shows up as a program that runs out of string heap after enough iterations, which is
/// why one test below calls MIN$ two hundred times and then checks the heap still works.
/// </summary>
[TestFixture]
public sealed class StringMinMaxTests {

  private static string Run(string body) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(body + "\nEND\n", "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var cg = new CodeGenerator(model) { Optimize = true };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }

  [TestCase("MIN$(\"pear\", \"apple\")", "apple")]
  [TestCase("MAX$(\"pear\", \"apple\")", "pear")]
  [TestCase("MIN$(\"apple\", \"pear\")", "apple")]
  [TestCase("MAX$(\"apple\", \"pear\")", "pear")]
  public void StringMinMax_GivenTwoStrings_ThenItPicksTheRightOne(string call, string expected) =>
    Assert.That(Run($"PRINT {call}"), Is.EqualTo(expected));

  /// <summary>
  /// The comparison is bytewise, so it is case-SENSITIVE and upper case sorts first: "Z" is below
  /// "a" because 'Z' is 90 and 'a' is 97. A comparison that folded case would put them the other way.
  /// </summary>
  [Test]
  public void StringMinMax_GivenMixedCase_ThenItComparesByteWiseRatherThanFoldingCase() =>
    Assert.That(Run("PRINT MIN$(\"a\", \"Z\"); \"/\"; MAX$(\"a\", \"Z\")"), Is.EqualTo("Z/a"));

  /// <summary>A prefix sorts before the longer string that contains it.</summary>
  [Test]
  public void StringMinMax_GivenAPrefix_ThenTheShorterOneIsTheSmaller() =>
    Assert.That(Run("PRINT MIN$(\"ab\", \"abc\"); \"/\"; MAX$(\"ab\", \"abc\")"), Is.EqualTo("ab/abc"));

  /// <summary>The empty string is below everything, and is returned rather than skipped.</summary>
  [Test]
  public void StringMinMax_GivenAnEmptyString_ThenItIsTheSmallest() =>
    Assert.That(Run("PRINT \"[\"; MIN$(\"\", \"a\"); \"]\"; MAX$(\"\", \"a\")"), Is.EqualTo("[]a"));

  /// <summary>Equal operands give that value back, from whichever side it is taken.</summary>
  [Test]
  public void StringMinMax_GivenEqualStrings_ThenThatValueComesBack() =>
    Assert.That(Run("PRINT MIN$(\"same\", \"same\"); \"/\"; MAX$(\"same\", \"same\")"), Is.EqualTo("same/same"));

  /// <summary>It works on computed strings, not only literals - the operands are handles either way.</summary>
  [Test]
  public void StringMinMax_GivenComputedOperands_ThenItStillPicksTheRightOne() =>
    Assert.That(Run("""
      DIM a AS STRING
      DIM b AS STRING
      a = "pe" + "ar"
      b = UCASE$("apple")
      PRINT MIN$(a, b); "/"; MAX$(a, b)
      """), Is.EqualTo("APPLE/pear"));

  /// <summary>
  /// The loser's handle is freed. Two hundred calls that each discard one operand would exhaust the
  /// string heap if they did not, and the allocation afterwards is what notices.
  /// </summary>
  [Test]
  public void StringMinMax_GivenManyCalls_ThenTheDiscardedOperandsAreFreed() =>
    Assert.That(Run("""
      DIM s AS STRING
      FOR i% = 1 TO 200
        s = MIN$("pear" + CHR$(65), "apple" + CHR$(66))
      NEXT i%
      PRINT s; LEN(SPACE$(2000))
      """), Is.EqualTo("appleB 2000"));
}
