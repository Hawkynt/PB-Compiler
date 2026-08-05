using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// <c>ROUND</c>, the last of the census's built-ins that looked like an oversight rather than a
/// decision - and the one that could not be written the easy way.
///
/// CEIL and FIX and INT are all FRNDINT under a rounding mode, and ROUND is not, because the mode
/// BASIC wants does not exist on the x87. BASIC rounds a half AWAY FROM ZERO: 2.5 is 3 and 3.5 is
/// 4. The x87's nearest mode rounds halves to EVEN: 2.5 would be 2 and 3.5 would be 4. Those agree
/// on 3.5 and differ on 2.5, so a test that only tried one of them would pass either way - both are
/// here for that reason.
///
/// So it is done by hand: scale by ten to the requested places, take the magnitude, add a half,
/// truncate toward zero, put the sign back, unscale.
/// </summary>
[TestFixture]
public sealed class RoundIntrinsicTests {

  private static string Run(string body) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(body + "\nEND\n", "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var cg = new CodeGenerator(model) { Optimize = true };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }

  /// <summary>
  /// The pair that separates away-from-zero from to-even. Round-to-even gives 2 for 2.5 and 4 for
  /// 3.5; away-from-zero gives 3 and 4. Only 2.5 tells them apart.
  /// </summary>
  [TestCase("ROUND(2.5)", "3")]
  [TestCase("ROUND(3.5)", "4")]
  [TestCase("ROUND(-2.5)", "-3")]
  [TestCase("ROUND(-3.5)", "-4")]
  public void Round_GivenAHalf_ThenItGoesAwayFromZeroRatherThanToEven(string call, string expected) =>
    Assert.That(Run($"PRINT {call}"), Is.EqualTo(expected));

  [TestCase("ROUND(2.4)", "2")]
  [TestCase("ROUND(2.6)", "3")]
  [TestCase("ROUND(-2.4)", "-2")]
  [TestCase("ROUND(-2.6)", "-3")]
  [TestCase("ROUND(0.0)", "0")]
  public void Round_GivenAnOrdinaryValue_ThenItGoesToTheNearest(string call, string expected) =>
    Assert.That(Run($"PRINT {call}"), Is.EqualTo(expected));

  [TestCase("ROUND(3.14159, 2)", "3.14")]
  [TestCase("ROUND(3.14159, 4)", "3.1416")]
  [TestCase("ROUND(-3.14159, 2)", "-3.14")]
  [TestCase("ROUND(2.5, 0)", "3")]
  public void Round_GivenDecimalPlaces_ThenItRoundsThere(string call, string expected) =>
    Assert.That(Run($"PRINT {call}"), Is.EqualTo(expected));

  /// <summary>
  /// 1.005 is the classic case, and the answer is 1 rather than 1.01 - not a bug in the rounding
  /// but in the premise: the nearest double to 1.005 is a shade BELOW it, so scaled by a hundred it
  /// is 100.4999..., and a half added to that still truncates to 100. Any implementation working in
  /// binary answers the same, and the test says so rather than leaving it to look wrong.
  /// </summary>
  [Test]
  public void Round_GivenAValueThatIsNotExactlyRepresentable_ThenItRoundsWhatTheDoubleActuallyIs() =>
    Assert.That(Run("PRINT ROUND(1.005, 2)"), Is.EqualTo("1"));

  /// <summary>An integer is already round, and does not go near the x87 to find out.</summary>
  [Test]
  public void Round_GivenAnInteger_ThenItIsTheIdentity() =>
    Assert.That(Run("""
      DIM n AS INTEGER
      n = -7
      PRINT ROUND(n)
      """), Is.EqualTo("-7"));

  /// <summary>The places may be computed, not only written down.</summary>
  [Test]
  public void Round_GivenARuntimePlaceCount_ThenItStillRoundsThere() =>
    Assert.That(Run("""
      DIM p AS INTEGER
      p = 3
      PRINT ROUND(2.718281828, p)
      """), Is.EqualTo("2.718"));
}
