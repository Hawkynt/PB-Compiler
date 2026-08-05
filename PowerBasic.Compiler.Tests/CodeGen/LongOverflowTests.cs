using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// Where a LONG <c>+</c>/<c>-</c> overflow wraps, and where it does not.
///
/// PB computes <c>+</c>, <c>-</c> and <c>*</c> in a WIDE type and narrows only at a store. The two
/// halves of that are separately observable and they disagree, which is what makes the pair worth
/// pinning: printing <c>a&amp; + b&amp;</c> shows the full 2147484000, while storing that same sum
/// into a LONG and printing the variable shows -2147483296.
///
/// Two readings each get one half right and are refuted by the other. Taking the STORED case alone
/// says a 4-byte <c>+</c>/<c>-</c> is simply integral and wraps - true of the value, wrong about
/// where the wrap comes from, and it makes the direct PRINT wrap too. Taking the x87 alone says the
/// narrowing store writes the integer-indefinite pattern 8000_0000h, since a 32-bit FISTP cannot
/// represent an out-of-range value - which answers -2147483648. Genuine PBC does neither: it
/// narrows through a 64-bit store and keeps the low half, so the wrap is the store's and the
/// addition stays wide.
///
/// All values below are verified against genuine PBC 3.50; tests/diff/DIFF25.BAS covers the wide
/// half and tests/diff/DIFF113.BAS the stored one. Both run only with the toolchain key, so the
/// same facts are asserted here where they gate every build.
/// </summary>
[TestFixture]
public sealed class LongOverflowTests {

  /// <summary>
  /// A BYREF argument to a SUB the inliner may not touch: the value cannot be tracked past it, so
  /// the arithmetic below is emitted rather than folded. Without this the constant folder answers
  /// instead of the code, and the runtime path goes untested.
  /// </summary>
  private const string Barrier = "\nSUB Opaque(v AS LONG) NOINLINE\nEND SUB\n";

  private static string Run(string body, bool optimize) {
    var source = "DECLARE SUB Opaque(v AS LONG)\nDIM a AS LONG, b AS LONG, r AS LONG\n" + body + "\nEND\n" + Barrier;
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }

  /// <summary>The sum used unstored keeps its full value - there is no LONG to wrap it into yet.</summary>
  [TestCase(true)]
  [TestCase(false)]
  public void LongAdd_GivenAnOverflowingSumPrintedDirectly_ThenItIsNotWrapped(bool optimize) =>
    Assert.That(Run("""
      a = 2147483000 : b = 1000
      CALL Opaque(a) : CALL Opaque(b)
      PRINT a + b
      """, optimize), Is.EqualTo("2147484000"));

  /// <summary>The same sum stored into a LONG first: the store keeps the low 32 bits.</summary>
  [TestCase(true)]
  [TestCase(false)]
  public void LongAdd_GivenAnOverflowingSumStoredFirst_ThenTheStoreWrapsIt(bool optimize) =>
    Assert.That(Run("""
      a = 2147483000 : b = 1000
      CALL Opaque(a) : CALL Opaque(b)
      r = a + b
      PRINT r
      """, optimize), Is.EqualTo("-2147483296"), "not -2147483648: the store wraps, it does not saturate");

  /// <summary>Subtraction below the range wraps the same way, and upwards.</summary>
  [TestCase(true)]
  [TestCase(false)]
  public void LongSubtract_GivenAnUnderflowingDifferenceStored_ThenItWrapsPositive(bool optimize) =>
    Assert.That(Run("""
      a = -2147483000 : b = 1000
      CALL Opaque(a) : CALL Opaque(b)
      r = a - b
      PRINT r
      """, optimize), Is.EqualTo("2147483296"));

  /// <summary>
  /// The case that tells a wrap from a saturation: 2147483647 + 2147483647 is 4294967294, whose low
  /// 32 bits are -2. A saturating store would answer -2147483648 here, as it would for every other
  /// overflow - which is why this one carries the distinction and the edge case below does not.
  /// </summary>
  [TestCase(true)]
  [TestCase(false)]
  public void LongAdd_GivenTwoMaximaStored_ThenItWrapsToMinusTwo(bool optimize) =>
    Assert.That(Run("""
      a = 2147483647
      CALL Opaque(a)
      r = a + a
      PRINT r
      """, optimize), Is.EqualTo("-2"), "a saturating store would answer -2147483648");

  /// <summary>
  /// Folded rather than emitted - no barrier, so the value is a compile-time constant and the
  /// optimizer answers. It has to agree with the code it replaced, which is the whole point.
  /// </summary>
  [Test]
  public void LongAdd_GivenTwoMaximaTheOptimizerCanSee_ThenTheFoldWrapsToo() =>
    Assert.That(Run("""
      a = 2147483647
      r = a + a
      PRINT r
      """, optimize: true), Is.EqualTo("-2"));

  /// <summary>
  /// One past the top, where the wrap and the sentinel happen to coincide at 8000_0000h. It passes
  /// under either reading, so it is here to show the fixture is not merely asserting "never
  /// -2147483648" - that value is a legal answer, just not to the sums above.
  /// </summary>
  [TestCase(true)]
  [TestCase(false)]
  public void LongAdd_GivenOnePastTheLargest_ThenTheWrapLandsOnTheSmallest(bool optimize) =>
    Assert.That(Run("""
      a = 2147483646 : b = 1
      CALL Opaque(a) : CALL Opaque(b)
      r = a + b + b
      PRINT r
      """, optimize), Is.EqualTo("-2147483648"));

  /// <summary>A sum that stays in range is unaffected by any of this.</summary>
  [TestCase(true)]
  [TestCase(false)]
  public void LongAdd_GivenASumThatFits_ThenItIsExact(bool optimize) =>
    Assert.That(Run("""
      a = 2147483646 : b = 1
      CALL Opaque(a) : CALL Opaque(b)
      r = a + b
      PRINT r
      """, optimize), Is.EqualTo("2147483647"));
}
