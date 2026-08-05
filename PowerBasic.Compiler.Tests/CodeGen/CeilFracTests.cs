using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// <c>CEIL</c> and <c>FRAC</c>, two of the built-ins the intrinsic census found binding and
/// generating nothing.
///
/// Both are the x87 doing the work under a rounding mode, exactly as INT and FIX beside them do:
/// CEIL is FRNDINT toward +infinity, and FRAC is what FIX leaves behind - x minus FIX(x), computed
/// by duplicating the value, truncating the copy and subtracting.
///
/// The negatives are the half worth testing. CEIL(-2.1) is -2, not -3, because ceiling goes toward
/// +infinity and not away from zero, and FRAC(-2.25) is -0.25 rather than 0.75 because it follows
/// FIX rather than INT. Getting either backwards gives the right answer for positives and the wrong
/// one for everything else.
/// </summary>
[TestFixture]
public sealed class CeilFracTests {

  private static string Run(string body) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(body + "\nEND\n", "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var cg = new CodeGenerator(model) { Optimize = true };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }

  [TestCase("CEIL(2.1)", "3")]
  [TestCase("CEIL(2.9)", "3")]
  [TestCase("CEIL(3.0)", "3")]
  [TestCase("CEIL(-2.1)", "-2")]
  [TestCase("CEIL(-2.9)", "-2")]
  [TestCase("CEIL(0.0)", "0")]
  public void Ceil_GivenAValue_ThenItRoundsTowardPositiveInfinity(string call, string expected) =>
    Assert.That(Run($"PRINT {call}"), Is.EqualTo(expected));

  [TestCase("FRAC(2.25)", ".25")]
  [TestCase("FRAC(-2.25)", "-.25")]
  [TestCase("FRAC(4.0)", "0")]
  public void Frac_GivenAValue_ThenItIsWhatFixLeavesBehind(string call, string expected) =>
    Assert.That(Run($"PRINT {call}"), Is.EqualTo(expected));

  /// <summary>
  /// An integer is already whole, so CEIL returns it and FRAC is zero - and neither goes near the
  /// x87 to find that out.
  /// </summary>
  [Test]
  public void CeilAndFrac_GivenAnInteger_ThenTheyAreTheIdentityAndZero() =>
    Assert.That(Run("""
      DIM n AS INTEGER
      n = -7
      PRINT CEIL(n); FRAC(n)
      """), Is.EqualTo("-7  0"));

  /// <summary>CEIL agrees with -INT(-x), which is the identity it is standing in for.</summary>
  [Test]
  public void Ceil_GivenAValue_ThenItAgreesWithTheNegatedFloorOfTheNegation() =>
    Assert.That(Run("""
      DIM x AS DOUBLE
      x = -2.1
      PRINT CEIL(x); -INT(-x)
      """), Is.EqualTo("-2 -2"));
}
