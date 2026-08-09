using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// <c>HEX$</c>, <c>OCT$</c> and <c>BIN$</c> in the IR lowering.
///
/// Their two-argument form sets a MINIMUM digit count - <c>HEX$(n, 4)</c> zero-pads to four, and a
/// value needing more still prints them all - which is a different string, not a formatting nicety.
/// Taking argument 0 alone would silently produce the unpadded answer, so the counted form used to
/// decline. It no longer has to: the runtime reads the whole conversion from one word,
/// <c>(minimum digits &lt;&lt; 8) | bits-per-digit</c>, and the lowering packs it - which is where a
/// constant folds away for free.
///
/// A NON-constant count still declines, deliberately: the direct emitter refuses it too, and
/// accepting it here would put the IR path ahead of the reference it is checked against.
/// </summary>
[TestFixture]
public sealed class RadixIntrinsicLoweringTests {

  private static IrModule? Lower(string source, out string? why) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return IrLowering.TryLowerModule(model, out why);
  }

  private static List<IrCall> CallsOf(IrModule module) =>
    module.Functions.SelectMany(f => f.Blocks).SelectMany(b => b.Instructions).OfType<IrCall>().ToList();

  [TestCase("HEX$", "rt_str_hex")]
  [TestCase("OCT$", "rt_str_oct")]
  [TestCase("BIN$", "rt_str_bin")]
  public void Lower_GivenTheOneArgumentForm_ThenCallsItsRuntimeEntry(string intrinsic, string routine) {
    var module = Lower($"""
      DIM n AS LONG
      n = 26
      PRINT {intrinsic}(n)
      """, out var why);

    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    Assert.That(CallsOf(module!).Select(c => (c.Callee as IrFunction)?.Name), Does.Contain(routine));
  }

  /// <summary>
  /// The count reaches the runtime as the packed word it reads, carrying the bits-per-digit the
  /// base implies: 4 for HEX$, 3 for OCT$, 1 for BIN$.
  /// </summary>
  [TestCase("HEX$", 4)]
  [TestCase("OCT$", 3)]
  [TestCase("BIN$", 1)]
  public void Lower_GivenAConstantDigitCount_ThenPacksItWithTheBitsPerDigit(string intrinsic, int bits) {
    var module = Lower($"""
      DIM n AS LONG
      n = 26
      PRINT {intrinsic}(n, 4)
      """, out var why);

    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    var radix = CallsOf(module!).SingleOrDefault(c => (c.Callee as IrFunction)?.Name == "rt_str_radix");
    Assert.That(radix, Is.Not.Null, "the counted form should route through rt_str_radix");
    Assert.That(radix!.Args.Last(), Is.InstanceOf<IrConstantInt>());
    Assert.That(((IrConstantInt)radix.Args.Last()).Value, Is.EqualTo((4 << 8) | bits));
  }

  /// <summary>The direct emitter clamps the count to 1..32, so this clamps identically.</summary>
  [TestCase(0, 1)]
  [TestCase(99, 32)]
  public void Lower_GivenAnOutOfRangeDigitCount_ThenClampsTheSameWayTheDirectEmitterDoes(int written, int clamped) {
    var module = Lower($"""
      DIM n AS LONG
      n = 26
      PRINT HEX$(n, {written})
      """, out var why);

    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    var radix = CallsOf(module!).Single(c => (c.Callee as IrFunction)?.Name == "rt_str_radix");
    Assert.That(((IrConstantInt)radix.Args.Last()).Value, Is.EqualTo((clamped << 8) | 4));
  }

  [TestCase("HEX$")]
  [TestCase("BIN$")]
  public void Lower_GivenANonConstantDigitCount_ThenDeclinesRatherThanDropIt(string intrinsic) {
    Lower($"""
      DIM n AS LONG
      DIM d AS INTEGER
      n = 26
      d = 4
      PRINT {intrinsic}(n, d)
      """, out var why);

    Assert.That(why, Does.Contain("non-constant digit count"));
  }
}
