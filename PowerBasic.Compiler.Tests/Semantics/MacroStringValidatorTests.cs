using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Semantics;

/// <summary>
/// The compile-time check on <c>PLAY</c> and <c>DRAW</c> strings.
///
/// Both are macro languages read byte by byte at run time, where a typo is a runtime error at best.
/// PLAY is the worse of the two here: it binds, warns that it does nothing, and compiles, so before
/// this a malformed tune reached the executable with nothing said about it at all.
///
/// It is a WARNING and not an error, and the tests below pin that: the genuine compiler accepts
/// these strings and finds out later, so refusing a program it takes would be a bigger bug than the
/// one being fixed. The point is that the diagnostic exists, not that it stops the build.
/// </summary>
[TestFixture]
public sealed class MacroStringValidatorTests {

  private static List<string> Warnings(string body) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(body + "\nEND\n", "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    return model.Warnings.Select(w => w.Message).ToList();
  }

  // ---- PLAY -------------------------------------------------------------------------------------

  [TestCase("T120 O3 L4 CDEFGAB")]
  [TestCase("MB MS T180 O2 C#DE-")]
  [TestCase("C. D.. C4. P2 N60")]
  [TestCase("> < CDE")]
  [TestCase("")]
  public void Play_GivenAWellFormedTune_ThenNothingIsSaid(string tune) =>
    Assert.That(Warnings($"PLAY \"{tune}\""), Has.None.Contains("PLAY string"));

  [TestCase("CDQ", "'Q' is not a PLAY command")]
  [TestCase("T500 C", "T takes 32 to 255")]
  [TestCase("O9", "O takes 0 to 6")]
  [TestCase("L99", "L takes 1 to 64")]
  [TestCase("N99", "N takes 0 to 84")]
  [TestCase("MX", "M must be followed by N, L, S, F or B")]
  [TestCase("T", "T needs a number after it")]
  public void Play_GivenAMalformedTune_ThenItSaysWhatIsWrong(string tune, string expected) =>
    Assert.That(Warnings($"PLAY \"{tune}\""), Has.Some.Contains(expected));

  /// <summary>X runs another string, so nothing after it can be known from here - and it stops.</summary>
  [Test]
  public void Play_GivenAnExecuteCommand_ThenCheckingStopsRatherThanGuessing() =>
    Assert.That(Warnings("PLAY \"CDE XZZZ\""), Has.None.Contains("PLAY string"));

  // ---- DRAW -------------------------------------------------------------------------------------

  [TestCase("U10 R10 D10 L10")]
  [TestCase("BM10,20 C4 U5")]
  [TestCase("NU5 ND5 NL5 NR5")]
  [TestCase("E5F5G5H5")]
  [TestCase("M+10,-20")]
  [TestCase("A3 S4 TA-90 C15")]
  [TestCase("P1,2")]
  public void Draw_GivenAWellFormedPicture_ThenNothingIsSaid(string picture) =>
    Assert.That(Warnings($"DRAW \"{picture}\""), Has.None.Contains("DRAW string"));

  [TestCase("U10 Z5", "'Z' is not a DRAW command")]
  [TestCase("M10", "M needs a comma between its coordinates")]
  [TestCase("M10,", "M needs a second coordinate")]
  [TestCase("A7", "A takes 0 to 3")]
  [TestCase("B", "B is a prefix and must be followed by a movement")]
  [TestCase("TB90", "T must be TA (turn angle)")]
  [TestCase("P1", "P needs a border colour after the fill colour")]
  public void Draw_GivenAMalformedPicture_ThenItSaysWhatIsWrong(string picture, string expected) =>
    Assert.That(Warnings($"DRAW \"{picture}\""), Has.Some.Contains(expected));

  // ---- what is NOT checked ----------------------------------------------------------------------

  /// <summary>
  /// A computed string is nobody's business here: there is nothing to read. The check exists because
  /// a CONSTANT can be read, not because the statement is suspicious.
  /// </summary>
  [Test]
  public void Macro_GivenAComputedString_ThenItIsLeftAlone() =>
    Assert.That(Warnings("""
      DIM t AS STRING
      t = "ZZZ"
      PLAY t
      """), Has.None.Contains("PLAY string"));

  /// <summary>
  /// A malformed string is a warning, not an error - the genuine compiler takes these and finds out
  /// at run time, and refusing a program it accepts would be the larger bug.
  /// </summary>
  [Test]
  public void Macro_GivenAMalformedString_ThenItIsAWarningAndTheProgramStillBinds() {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize("PLAY \"ZZZ\"\nEND\n", "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.Multiple(() => {
      Assert.That(model.Errors, Is.Empty, "a bad tune must not fail the build");
      Assert.That(model.Warnings.Select(w => w.Message), Has.Some.Contains("PLAY string"));
    });
  }

  /// <summary>The position points at the offending character, counting from one.</summary>
  [Test]
  public void Macro_GivenAMalformedString_ThenThePositionNamesTheOffendingCharacter() =>
    Assert.That(Warnings("PLAY \"CDQ\""), Has.Some.Contains("at position 3"));
}
