using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// <c>HEX$</c>, <c>OCT$</c> and <c>BIN$</c> in the IR lowering. Their two-argument form fixes the
/// digit count - <c>HEX$(n, 4)</c> pads or truncates to four - which is a different string, not a
/// formatting nicety. Taking argument 0 alone (what the lowering did) silently produced the
/// unpadded answer, so the counted form declines instead until it is really implemented.
/// </summary>
[TestFixture]
public sealed class RadixIntrinsicLoweringTests {

  private static IrModule? Lower(string source, out string? why) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return IrLowering.TryLowerModule(model, out why);
  }

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
    var callees = module!.Functions.SelectMany(f => f.Blocks).SelectMany(b => b.Instructions)
      .OfType<IrCall>().Select(c => (c.Callee as IrFunction)?.Name).ToList();
    Assert.That(callees, Does.Contain(routine));
  }

  [TestCase("HEX$")]
  [TestCase("BIN$")]
  public void Lower_GivenADigitCount_ThenDeclinesRatherThanDropIt(string intrinsic) {
    Lower($"""
      DIM n AS LONG
      n = 26
      PRINT {intrinsic}(n, 4)
      """, out var why);

    Assert.That(why, Does.Contain("digit count"));
  }
}
