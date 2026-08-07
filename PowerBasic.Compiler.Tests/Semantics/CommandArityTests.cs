using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Semantics;

/// <summary>
/// How many arguments each command takes, enforced against what the genuine compilers accept.
///
/// The commands were previously bound without any count check at all, so <c>BEEP 1, 2, 3</c>,
/// <c>CLS 1, 2, 3, 4</c> and <c>RANDOMIZE 1, 2, 3</c> compiled silently. Every limit here was probed
/// at its boundary against PBC 3.5 and, where the column exists, BC 4.50 - the largest accepted count
/// and the first refused one - because a limit invented in the compiler is worse than no limit: it
/// rejects programs the real compiler builds.
///
/// Two results shaped the table and neither was guessable:
///
/// The families DISAGREE. PBC 3.5 accepts <c>BEEP 1</c> and BC 4.50 refuses it; BC accepts a bare
/// <c>COLOR</c> and <c>LOCATE</c> where PBC demands an argument. One number per command would have
/// been wrong for one of the two whichever way it was written.
///
/// PALETTE is not a range. 0 and 2 are accepted, 1 and 3 refused - so the table holds SETS, and a
/// min/max pair would have admitted <c>PALETTE 1</c>.
/// </summary>
[TestFixture]
public sealed class CommandArityTests {

  private static IReadOnlyList<string> Errors(string body, Dialect dialect) {
    var unit = Parser.Parse(Lexer.Tokenize(body + "\nEND\n", "T.BAS", dialect), "T.BAS", dialect);
    return [.. Binder.Bind(unit, dialect).Errors.Select(e => e.Message)];
  }

  private static void Accepts(string body, Dialect dialect) =>
    Assert.That(Errors(body, dialect), Is.Empty, $"{dialect} should accept: {body}");

  private static void Refuses(string body, Dialect dialect) =>
    Assert.That(Errors(body, dialect), Has.Some.Contains("argument(s), not"), $"{dialect} should refuse: {body}");

  /// <summary>The counts PBC 3.5 accepts, each verified against it.</summary>
  [TestCase("BEEP")]
  [TestCase("BEEP 1")]
  [TestCase("CLS")]
  [TestCase("CLS 1")]
  [TestCase("COLOR 1")]
  [TestCase("COLOR 1, 2, 3")]
  [TestCase("LOCATE 1")]
  [TestCase("LOCATE 1, 1, 1, 1, 1")]
  [TestCase("RANDOMIZE")]
  [TestCase("RANDOMIZE 1")]
  [TestCase("SLEEP")]
  [TestCase("SLEEP 1")]
  [TestCase("WIDTH 80")]
  [TestCase("WIDTH 80, 25")]
  [TestCase("PALETTE")]
  [TestCase("PALETTE 1, 2")]
  [TestCase("SCREEN")]
  [TestCase("SCREEN 1, 0, 0, 0")]
  public void Bind_GivenACountPbcAccepts_WhenPb35_ThenNoDiagnostic(string body) => Accepts(body, Dialect.Pb35);

  /// <summary>And the first count past each boundary, which PBC 3.5 refuses.</summary>
  [TestCase("BEEP 1, 2")]
  [TestCase("CLS 1, 2")]
  [TestCase("COLOR 1, 2, 3, 4")]
  [TestCase("LOCATE 1, 1, 1, 1, 1, 1")]
  [TestCase("RANDOMIZE 1, 2")]
  [TestCase("SLEEP 1, 2")]
  [TestCase("WIDTH 80, 25, 3")]
  [TestCase("PALETTE 1, 2, 3")]
  [TestCase("SCREEN 1, 0, 0, 0, 0")]
  public void Bind_GivenTooManyArguments_WhenPb35_ThenRefused(string body) => Refuses(body, Dialect.Pb35);

  /// <summary>
  /// PBC 3.5 requires an argument for these three; a bare keyword is a diagnostic, not a default.
  /// This is the half that differs from the Microsoft family below.
  /// </summary>
  [TestCase("COLOR")]
  [TestCase("LOCATE")]
  [TestCase("WIDTH")]
  public void Bind_GivenABareKeywordPbcRequiresAnArgumentFor_WhenPb35_ThenRefused(string body) =>
    Refuses(body, Dialect.Pb35);

  /// <summary>
  /// The same two under QuickBASIC, where BC 4.50 accepts them. The split is the point: a table with
  /// one column would have had to be wrong here or wrong above.
  /// </summary>
  [TestCase("COLOR")]
  [TestCase("LOCATE")]
  public void Bind_GivenABareKeywordBcAccepts_WhenQb45_ThenNoDiagnostic(string body) =>
    Accepts(body, Dialect.Qb45);

  /// <summary>Where the two families agree, they are still both enforced.</summary>
  [TestCase("COLOR 1, 2, 3, 4")]
  [TestCase("LOCATE 1, 1, 1, 1, 1, 1")]
  [TestCase("PALETTE 1")]
  public void Bind_GivenACountBothFamiliesRefuse_WhenQb45_ThenRefused(string body) =>
    Refuses(body, Dialect.Qb45);

  /// <summary>
  /// PALETTE takes none or two, never one - the case a min/max pair could not express, and the reason
  /// the table stores sets.
  /// </summary>
  [Test]
  public void Bind_GivenPaletteWithOneArgument_ThenRefusedThoughZeroAndTwoAreFine() {
    Assert.Multiple(() => {
      Assert.That(Errors("PALETTE", Dialect.Pb35), Is.Empty);
      Assert.That(Errors("PALETTE 1", Dialect.Pb35), Has.Some.Contains("argument(s), not 1"));
      Assert.That(Errors("PALETTE 1, 2", Dialect.Pb35), Is.Empty);
    });
  }

  /// <summary>
  /// A command with no measured entry is left alone rather than guessed at - SHELL, KILL and the rest
  /// keep whatever the parser already enforced.
  /// </summary>
  [TestCase("SHELL \"x\"")]
  [TestCase("KILL \"x\"")]
  [TestCase("CHDIR \"x\"")]
  public void Bind_GivenAnUnmeasuredCommand_ThenNoArityDiagnostic(string body) =>
    Assert.That(Errors(body, Dialect.Pb35), Has.None.Contains("argument(s), not"));
}
