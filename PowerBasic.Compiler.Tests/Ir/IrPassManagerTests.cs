using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>The pass manager: the standard pipeline run to a verified fixpoint.</summary>
[TestFixture]
public sealed class IrPassManagerTests {

  [TestCase(false)]
  [TestCase(true)]
  public void AddModulePassWhen_GivenACondition_ThenRunsOnlyWhenEnabled(bool enabled) {
    var module = new IrModule("test");
    var calls = 0;
    var manager = new IrPassManager()
      .AddModulePassWhen(enabled, "probe", _ => {
        ++calls;
        return 0;
      });

    manager.RunOnModule(module);

    Assert.That(calls, Is.EqualTo(enabled ? 1 : 0));
  }

  private static IrFunction Lower(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    return IrLowering.TryLowerMainBody(Binder.Bind(unit, Dialect.Pb35))!;
  }

  [Test]
  public void Standard_OverLoweredProgram_OptimizesToAVerifiedFixpoint() {
    var fn = Lower(
      "a% = 2\nb% = 3\nc% = a% + b%\nIF c% > 4 THEN\n  d% = c% * 2\nELSE\n  d% = 0\nEND IF");
    var pm = IrPassManager.Standard();
    pm.VerifyEachPass = true;

    pm.RunToFixpoint(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    // a second run must make no further change - the pipeline has reached a fixpoint
    Assert.That(pm.RunToFixpoint(fn), Is.EqualTo(0));
  }

  [Test]
  public void Standard_FullyEvaluatesAConstantOnlyProgram() {
    // everything is compile-time constant and unused -> the body collapses to ret void
    var fn = Lower("a% = 10\nb% = 20\nc% = a% + b%");
    IrPassManager.Standard().RunToFixpoint(fn);

    Assert.That(IrPrinter.Print(fn), Is.EqualTo(
      "define void @main() {\n" +
      "entry:\n" +
      "  ret void\n" +
      "}\n"));
  }

  [Test]
  public void VerifyEachPass_ThrowsIfAPassWouldLeaveInvalidIr() {
    var fn = new IrFunction("bad", IrType.Void);
    fn.CreateBlock("entry").Append(new IrBinary(IrBinaryOp.Add, IrBuilder.ConstI32(1), IrBuilder.ConstI32(2)));  // no terminator
    var pm = new IrPassManager { VerifyEachPass = true }.Add("noop", _ => 0);

    Assert.That(() => pm.Run(fn), Throws.TypeOf<IrVerificationException>());
  }

  [Test]
  public void Standard_OverLoop_PromotesAndStaysVerifiable() {
    var fn = Lower("s% = 0\nFOR i% = 1 TO 10\n  s% = s% + i% * 2\nNEXT i%");
    var pm = IrPassManager.Standard();
    pm.VerifyEachPass = true;

    pm.RunToFixpoint(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(fn.AllInstructions.OfType<IrAlloca>().Count(), Is.EqualTo(0));   // fully promoted
  }
}
