using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// SIN and COS below a 386, computed with instructions an 8087 actually has.
///
/// FSIN and FCOS arrived with the 387. Emitting them for an image whose declared target is an 8086
/// is what <c>rt_trig</c> replaces, and it is what the oracle does not do: genuine PBC 3.5 compiling
/// SIN, COS and TAN in one program emits zero FSIN, zero FCOS and exactly ONE FPTAN - one shared
/// routine that reduces the argument and derives both functions from the tangent.
///
/// The reduction is not optional, because the 8087's FPTAN is defined only for
/// 0 &lt;= x &lt;= pi/4. FPREM against pi/2 supplies both the remainder and, in its condition codes,
/// the quadrant; a remainder above pi/4 folds to pi/2 - r with sine and cosine swapping roles; the
/// quadrant then picks the signs, and sine alone carries the sign of the argument.
///
/// Which is why the values below range over all four quadrants, both signs and past 2*pi rather than
/// checking one angle: every one of those steps is a chance to pick the wrong function or the wrong
/// sign, and quadrant 0 alone would have passed while the other three were swapped.
/// </summary>
[TestFixture]
public sealed class EightySevenTrigTests {

  private static string Run(string body, string prologue = "") {
    var source = prologue + body + "\nEND\n";
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = true };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return Cpu8086.Run(image).Output.Trim();
  }

  /// <summary>Quadrant 0 through 3 and beyond 2*pi, to seven places.</summary>
  [TestCase("0.0", " 0")]
  [TestCase("0.5", " .4794255")]
  [TestCase("1.0", " .841471")]
  [TestCase("2.0", " .9092974")]
  [TestCase("3.0", " .14112")]
  [TestCase("4.0", "-.7568025")]
  [TestCase("5.0", "-.9589243")]
  [TestCase("6.0", "-.2794155")]
  [TestCase("7.0", " .6569866")]
  public void Sin_GivenAnAngle_ThenTheEightySevenRoutineAgreesWithTheRealValue(string angle, string expected) =>
    Assert.That(Run($"PRINT CSNG(SIN({angle}))"), Is.EqualTo(expected.Trim()));

  [TestCase("0.0", " 1")]
  [TestCase("0.5", " .8775826")]
  [TestCase("1.0", " .5403023")]
  [TestCase("2.0", "-.4161468")]
  [TestCase("3.0", "-.9899925")]
  [TestCase("4.0", "-.6536436")]
  [TestCase("5.0", " .2836622")]
  [TestCase("6.0", " .9601703")]
  [TestCase("7.0", " .7539023")]
  public void Cos_GivenAnAngle_ThenTheEightySevenRoutineAgreesWithTheRealValue(string angle, string expected) =>
    Assert.That(Run($"PRINT CSNG(COS({angle}))"), Is.EqualTo(expected.Trim()));

  /// <summary>Sine is odd and cosine is even - the sign the routine carries for one and not the other.</summary>
  [TestCase("SIN(-1.0)", "-.841471")]
  [TestCase("SIN(-2.0)", "-.9092974")]
  [TestCase("COS(-1.0)", " .5403023")]
  [TestCase("COS(-2.0)", "-.4161468")]
  public void Trig_GivenANegativeAngle_ThenParityIsRespected(string call, string expected) =>
    Assert.That(Run($"PRINT CSNG({call})"), Is.EqualTo(expected.Trim()));

  /// <summary>
  /// The identity that catches a wrong quadrant even where the individual values look plausible.
  /// </summary>
  [TestCase("1.0")]
  [TestCase("2.5")]
  [TestCase("4.0")]
  [TestCase("6.5")]
  public void Trig_GivenAnyAngle_ThenSinSquaredPlusCosSquaredIsOne(string angle) =>
    Assert.That(Run($"PRINT CSNG(SIN({angle}) * SIN({angle}) + COS({angle}) * COS({angle}))"), Is.EqualTo("1"));

  /// <summary>
  /// TAN went through the same routine for the same reason, and it is the case the FPTAN reading
  /// actually breaks on: <c>FPTAN; FSTP ST(0)</c> keeps the tangent on a 387 and keeps Y on an 8087.
  ///
  /// Its quadrant rule is not sine's. Tangent has period pi rather than 2*pi, so the sign is the
  /// quadrant's LOW bit where sine and cosine read the one above it - which is why quadrants 1 and 3
  /// are here, and why a rule copied from sine passes 0 and 2 while inverting the rest.
  /// </summary>
  [TestCase("0.0", "0")]
  [TestCase("0.5", ".5463025")]
  [TestCase("1.0", "1.557408")]
  [TestCase("2.0", "-2.18504")]
  [TestCase("3.0", "-.1425465")]
  [TestCase("4.0", "1.157821")]
  [TestCase("5.0", "-3.380515")]
  [TestCase("6.0", "-.2910062")]
  [TestCase("7.0", ".871448")]
  [TestCase("-1.0", "-1.557408")]
  [TestCase("-2.0", "2.18504")]
  public void Tan_GivenAnAngle_ThenTheEightySevenRoutineAgreesWithTheRealValue(string angle, string expected) =>
    Assert.That(Run($"PRINT CSNG(TAN({angle}))"), Is.EqualTo(expected));

  /// <summary>
  /// Tangent is the ratio of the other two - three separate paths through the routine agreeing.
  /// Compared after rounding to SINGLE rather than as a difference against zero: the two routes
  /// reach the same number by different arithmetic and land one double ulp apart (2.2E-16), which
  /// is a fact about the order of operations and not about the quadrant logic under test.
  /// </summary>
  [TestCase("1.0")]
  [TestCase("2.5")]
  [TestCase("4.0")]
  public void Tan_GivenAnAngle_ThenItIsSineOverCosine(string angle) =>
    Assert.That(Run($"PRINT CSNG(TAN({angle}))"), Is.EqualTo(Run($"PRINT CSNG(SIN({angle}) / COS({angle}))")));

  // That a 386 target still emits FSIN/FCOS is asserted in EightySixOnlyInstructionTests by scanning
  // the image, and not by running one: Cpu8086 refuses opcode 66, the 32-bit operand-size prefix, so
  // it cannot execute a 386 build at all. That limit predates this routine and is unrelated to it.
}
