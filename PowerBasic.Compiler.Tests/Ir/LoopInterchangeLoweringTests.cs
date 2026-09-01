using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// The hand-built O0122 fixtures keep dependence cases readable, but the pass has to match the CFG the
/// front end really emits too: FOR has separate body and increment blocks, and the inner exit is a
/// forwarding block before the outer increment. This fixture pins that structural contract.
/// </summary>
[TestFixture]
public sealed class LoopInterchangeLoweringTests {

  [Test]
  public void LoweredForNest_GivenOuterCounterHasSmallerAddressStride_ThenItInterchanges() {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize("""
      DIM src%(0 TO 31, 0 TO 31)
      DIM dst%(0 TO 31, 0 TO 31)
      DIM i%, j%
      FOR i% = 0 TO 31
        FOR j% = 0 TO 31
          dst%(j%, i%) = src%(j%, i%)
        NEXT j%
      NEXT i%
      """, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));

    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    var fn = module!.Functions.First(function => function.Name.Equals("main", StringComparison.OrdinalIgnoreCase));
    Mem2Reg.Run(fn);
    IntegerRecovery.Run(fn);
    Dce.Run(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(fn.Blocks.Count(block => block.Name.Contains("for.body", StringComparison.Ordinal)), Is.EqualTo(2),
      "real lowering keeps body blocks separate from FOR increment/latch blocks");

    Assert.That(LoopInterchange.Run(fn), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }
}
