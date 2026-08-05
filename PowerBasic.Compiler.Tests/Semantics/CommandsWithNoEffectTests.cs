using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Semantics;

/// <summary>
/// The statements this runtime accepts and then does nothing with.
///
/// COLOR, PALETTE, WIDTH, VIEW, WINDOW, KEY and PLAY all parse, bind and emit no code, and the whole
/// event system - ON KEY/TIMER/COM/PEN/STRIG/PLAY and the KEY ON|OFF|STOP that arms it - is bound,
/// has its handler label resolved, and is then never dispatched.
///
/// Refusing any of them is not an option: the genuine compiler takes every one, and the sibling
/// graphics corpus uses PALETTE and WIDTH, so an error would turn programs that compile today into
/// programs that do not. But accepting them in silence means a program runs with its colours quietly
/// not applied and its timer handler quietly never called, with nothing anywhere saying so. A warning
/// is the honest middle - it changes no bytes and rejects nothing.
///
/// Note that "no effect" means no effect at RUN time, not that the statement is free: a program of
/// `END` alone is two bytes (int 20h) and `COLOR 4 : END` is 819, because a command the emitter has
/// no case for disqualifies the tiny-binary path. The cost is real even though the statement is not.
/// </summary>
[TestFixture]
public sealed class CommandsWithNoEffectTests {

  private static (List<string> Errors, List<string> Warnings) Bind(string body) {
    var source = "DIM pal%(16)\n" + body + "\nPRINT \"x\"\nEND\n";
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    return (model.Errors.Select(e => e.Message).ToList(), model.Warnings.Select(w => w.Message).ToList());
  }

  /// <summary>
  /// One spelling per entry of <see cref="Binder.CommandsWithNoEffect"/>. Written out here rather
  /// than taken from the statement-surface table because the surface has no form for four of them -
  /// VIEW PRINT, VIEW SCREEN, VIEW TEXT and PALETTE USING - and a test that quietly skipped the
  /// entries nothing else covers would be testing the wrong half of the list.
  /// </summary>
  private static readonly (string Keyword, string Statement)[] _spellings = [
    ("COLOR", "COLOR 7, 0"),
    ("WIDTH", "WIDTH 80"),
    ("KEY", "KEY 1, \"abc\""),
    ("VIEW", "VIEW (0, 0)-(319, 199)"),
    // VIEW PRINT's row range is spelled `1 TO 20` in real programs, which this parser reads as an
    // expression list and refuses ("unknown SUB TO") - a separate gap, so the comma form is used here
    ("VIEW PRINT", "VIEW PRINT 1, 20"),
    ("VIEW TEXT", "VIEW TEXT 1, 20"),
    ("VIEW SCREEN", "VIEW SCREEN (0, 0)-(10, 10)"),
    ("WINDOW", "WINDOW (0, 0)-(319, 199)"),
    ("PALETTE", "PALETTE 1, 2"),
    ("PALETTE USING", "PALETTE USING pal%(0)"),
    ("PLAY", "PLAY \"CDE\""),
  ];

  [TestCaseSource(nameof(_spellings))]
  public void Commands_GivenOneThatDoesNothing_WhenBound_ThenItIsAcceptedAndWarnedAbout((string Keyword, string Statement) form) {
    var (errors, warnings) = Bind(form.Statement);
    Assert.Multiple(() => {
      Assert.That(errors, Is.Empty, $"{form.Keyword}: refusing it would break programs the genuine compiler takes");
      Assert.That(warnings, Has.Some.Contains($"{form.Keyword} is accepted but has no effect"), $"{form.Keyword}: accepted in silence");
    });
  }

  /// <summary>
  /// Every name on the list has a spelling above. An entry nothing can produce would sit there
  /// looking like a known gap while warning about nothing, and one added later without a spelling
  /// would go untested - so the two are compared rather than trusted.
  /// </summary>
  [Test]
  public void Commands_GivenTheList_ThenEveryEntryHasASpellingUnderTest() {
    Assert.That(_spellings.Select(s => s.Keyword), Is.EquivalentTo(Binder.CommandsWithNoEffect),
      "the list of commands with no effect and the spellings that exercise it have drifted apart");
  }

  [TestCase("ON TIMER(1) GOSUB Tick", "ON TIMER")]
  [TestCase("ON KEY(1) GOSUB Tick", "ON KEY")]
  [TestCase("ON PEN GOSUB Tick", "ON PEN")]
  public void Events_GivenAHandler_WhenBound_ThenItIsWarnedAboutRatherThanSilentlyNeverCalled(string statement, string expected) {
    // The quietest failure in the compiler: the handler binds, its label resolves, and it never runs
    var (errors, warnings) = Bind($"{statement}\nGOTO Done\nTick:\nRETURN\nDone:");
    Assert.That(errors, Is.Empty);
    Assert.That(warnings, Has.Some.Contains($"{expected} is accepted but has no effect"));
  }

  [TestCase("KEY OFF", "KEY OFF")]
  [TestCase("KEY ON", "KEY ON")]
  public void Events_GivenAnArmingStatement_WhenBound_ThenItIsWarnedAbout(string statement, string expected) {
    var (errors, warnings) = Bind(statement);
    Assert.That(errors, Is.Empty);
    Assert.That(warnings, Has.Some.Contains($"{expected} is accepted but has no effect"));
  }

  [Test]
  public void Commands_GivenOnesThatWork_WhenBound_ThenNothingIsWarnedAbout() {
    // The warning has to be worth reading, which means it must not fire on statements that work
    var (errors, warnings) = Bind("CLS\nLOCATE 1, 1\nBEEP\nSOUND 440, 1");
    Assert.That(errors, Is.Empty);
    Assert.That(warnings, Has.None.Contains("has no effect"));
  }
}
