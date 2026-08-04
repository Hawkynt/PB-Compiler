using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>Regression tests for inlining interactions that previously produced invalid IR.</summary>
[TestFixture]
public sealed class InlineRegressionTests {

  /// <summary>
  /// Inlining a call inside a loop body splits the body block; the moved back-edge
  /// terminator must repoint the loop-header phi to the new continuation block, or the
  /// phi's predecessor set goes stale and the loop is miscompiled (an infinite empty loop).
  /// </summary>
  [Test]
  public void InlineCallInsideLoop_KeepsTheLoopWellFormed() {
    // the trip count is read at run time, so the loop survives to be inlined INTO - a constant-trip
    // loop is unrolled away first, which would make this a test about something else
    var unit = Parser.Parse(Lexer.Tokenize(
      "DECLARE FUNCTION sq%(BYVAL n%)\nDIM a%(0 TO 4)\nINPUT k%\nFOR i% = 0 TO k%\n  a%(i%) = sq%(i%)\nNEXT i%\n\nFUNCTION sq%(BYVAL n%)\n  sq% = n% OR n%\nEND FUNCTION", "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var module = IrLowering.TryLowerModule(Binder.Bind(unit, Dialect.Pb35))!;
    var pm = IrPassManager.Standard();
    pm.VerifyEachPass = true;

    pm.RunOnModule(module);
    Inliner.Run(module);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    pm.RunOnModule(module);   // would throw IrVerificationException if a pass saw invalid IR

    var main = module.FindFunction("main")!;
    Assert.That(IrVerifier.Verify(main), Is.Empty);
    // the runtime calls INPUT emits are not the point; what must be gone is the call to sq
    Assert.That(main.AllInstructions.OfType<IrCall>()
      .Where(c => (c.Callee as IrFunction)?.Name.Equals("sq", StringComparison.OrdinalIgnoreCase) == true), Is.Empty);
    Assert.That(main.AllInstructions.OfType<IrPhi>().Count(), Is.GreaterThanOrEqualTo(1));  // loop counter survives
    Assert.That(main.AllInstructions.OfType<IrStore>().Count(), Is.GreaterThanOrEqualTo(1));// array store survives
  }
}
