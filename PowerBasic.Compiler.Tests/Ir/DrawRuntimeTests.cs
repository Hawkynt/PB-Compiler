using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Runtime;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class DrawRuntimeTests {
  [Test]
  public void Define_GivenAnOwnedParser_ThenPaintErrorsReturnForStringCleanupBeforeRaising() {
    var module = new IrModule("draw-runtime");
    var draw = module.AddFunction(new IrFunction("rt_draw", IrType.Void, [new IrArgument(IrType.Ptr, 0)]));
    var main = module.AddFunction(new IrFunction("main", IrType.Void));
    var block = main.CreateBlock("entry");
    block.Append(new IrCall(IrType.Void, draw, [new IrNullPtr()]));
    block.Append(new IrRet());
    Assert.That(DrawRuntime.Define(module), Is.GreaterThan(0));
    var calls = module.FindFunction("rt_draw_parse")!.AllInstructions.OfType<IrCall>().ToArray();
    Assert.That(calls.Select(call => call.Callee.Name), Does.Contain("rt_paint_status"));
    Assert.That(calls.Select(call => call.Callee.Name), Does.Not.Contain("rt_paint"));
    Assert.That(calls.Select(call => call.Callee.Name), Does.Not.Contain("rt_error"));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(DrawRuntime.Define(module), Is.Zero, "runtime definition is idempotent");
  }
}
