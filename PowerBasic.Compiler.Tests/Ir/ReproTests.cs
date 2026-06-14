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
    var unit = Parser.Parse(Lexer.Tokenize(
      "DECLARE FUNCTION sq%(BYVAL n%)\nDIM a%(0 TO 4)\nFOR i% = 0 TO 4\n  a%(i%) = sq%(i%)\nNEXT i%\n\nFUNCTION sq%(BYVAL n%)\n  sq% = n% OR n%\nEND FUNCTION", "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var module = IrLowering.TryLowerModule(Binder.Bind(unit, Dialect.Pb35))!;
    var pm = IrPassManager.Standard();
    pm.VerifyEachPass = true;

    pm.RunOnModule(module);
    Inliner.Run(module);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    pm.RunOnModule(module);   // would throw IrVerificationException if a pass saw invalid IR

    var main = module.FindFunction("main")!;
    Assert.That(IrVerifier.Verify(main), Is.Empty);
    Assert.That(main.AllInstructions.OfType<IrCall>(), Is.Empty);                 // sq inlined
    Assert.That(main.AllInstructions.OfType<IrPhi>().Count(), Is.GreaterThanOrEqualTo(1));  // loop counter survives
    Assert.That(main.AllInstructions.OfType<IrStore>().Count(), Is.GreaterThanOrEqualTo(1));// array store survives
  }
}
