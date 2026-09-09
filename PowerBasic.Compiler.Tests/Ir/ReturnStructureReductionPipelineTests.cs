using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class ReturnStructureReductionPipelineTests {

  [Test]
  public void GivenObservedAndDeadResultFields_WhenStandardPipelineRuns_ThenOnlyDeadFieldStoreIsRemoved() {
    var module = new IrModule("retstruct-pipeline");
    var sret = new IrArgument(IrType.Ptr, 0, "$sret");
    var callee = module.AddFunction(new IrFunction("Make", IrType.Void, [sret]));
    var calleeBuilder = new IrBuilder(callee.CreateBlock("entry"));
    var observedStore = calleeBuilder.Store(IrBuilder.ConstI32(10), sret);
    var deadAddress = calleeBuilder.Gep(sret, IrBuilder.ConstI32(4));
    var deadStore = calleeBuilder.Store(IrBuilder.ConstI32(20), deadAddress);
    calleeBuilder.Ret();

    var consumeArgument = new IrArgument(IrType.I32, 0, "value");
    var consume = module.AddFunction(new IrFunction("Consume", IrType.Void, [consumeArgument]));
    var caller = module.AddFunction(new IrFunction("caller", IrType.Void));
    var entry = caller.CreateBlock("entry");
    var callerBuilder = new IrBuilder(entry);
    var result = entry.Append(new IrAlloca(IrType.I8) { Count = 8, Name = "result" });
    callerBuilder.Call(IrType.Void, callee, result);
    var observed = callerBuilder.Load(IrType.I32, result);
    callerBuilder.Call(IrType.Void, consume, observed);
    callerBuilder.Ret();

    IrPassManager.Standard().RunOnModule(module);

    Assert.Multiple(() => {
      Assert.That(observedStore.Parent, Is.Not.Null, "the externally observed result region stays live");
      Assert.That(deadStore.Parent, Is.Null, "the unobserved result region is removed by O0281");
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }
}
