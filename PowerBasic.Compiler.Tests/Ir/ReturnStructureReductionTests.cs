using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class ReturnStructureReductionTests {

  [Test]
  public void GivenOnlyFirstResultFieldIsRead_WhenRun_ThenSecondFieldStoreIsRemoved() {
    var module = new IrModule("retstruct");
    var callee = AddSretFunction(module, "Make", out var sret);
    var cb = new IrBuilder(callee.CreateBlock("entry"));
    var firstStore = cb.Store(IrBuilder.ConstI32(10), sret);
    var second = cb.Gep(sret, IrBuilder.ConstI32(4));
    var secondStore = cb.Store(IrBuilder.ConstI32(20), second);
    cb.Ret();

    AddCaller(module, "caller", callee, 8, 0);

    Assert.That(ReturnStructureReduction.Run(module), Is.EqualTo(1));
    Assert.Multiple(() => {
      Assert.That(firstStore.Parent, Is.Not.Null, "the observed field must still be produced");
      Assert.That(secondStore.Parent, Is.Null, "the unobserved field store is dead");
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void GivenDifferentCallersReadDifferentFields_WhenRun_ThenTheUnionIsRetained() {
    var module = new IrModule("retstruct");
    var callee = AddSretFunction(module, "Make", out var sret);
    var cb = new IrBuilder(callee.CreateBlock("entry"));
    var firstStore = cb.Store(IrBuilder.ConstI32(10), sret);
    var second = cb.Gep(sret, IrBuilder.ConstI32(4));
    var secondStore = cb.Store(IrBuilder.ConstI32(20), second);
    cb.Ret();

    AddCaller(module, "caller.first", callee, 8, 0);
    AddCaller(module, "caller.second", callee, 8, 4);

    Assert.That(ReturnStructureReduction.Run(module), Is.Zero);
    Assert.Multiple(() => {
      Assert.That(firstStore.Parent, Is.Not.Null);
      Assert.That(secondStore.Parent, Is.Not.Null);
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void GivenResultBufferEscapesToAnotherCall_WhenRun_ThenCandidateIsRejected() {
    var module = new IrModule("retstruct");
    var callee = AddSretFunction(module, "Make", out var sret);
    var cb = new IrBuilder(callee.CreateBlock("entry"));
    cb.Store(IrBuilder.ConstI32(10), sret);
    var second = cb.Gep(sret, IrBuilder.ConstI32(4));
    var secondStore = cb.Store(IrBuilder.ConstI32(20), second);
    cb.Ret();

    var sinkArg = new IrArgument(IrType.Ptr, 0, "p");
    var sink = module.AddFunction(new IrFunction("Sink", IrType.Void, [sinkArg]));
    var (_, slot, builder) = AddCaller(module, "caller", callee, 8, 0, terminate: false);
    builder.Call(IrType.Void, sink, slot);
    builder.Ret();

    Assert.That(ReturnStructureReduction.Run(module), Is.Zero);
    Assert.Multiple(() => {
      Assert.That(secondStore.Parent, Is.Not.Null, "an escaped result may be read by the unknown callee");
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void GivenUnusedFieldComputationHasACall_WhenRunAndDce_ThenCallSurvives() {
    var module = new IrModule("retstruct");
    var callee = AddSretFunction(module, "Make", out var sret);
    var effect = module.AddFunction(new IrFunction("Effect", IrType.I32));
    var cb = new IrBuilder(callee.CreateBlock("entry"));
    cb.Store(IrBuilder.ConstI32(10), sret);
    var sideEffect = cb.Call(IrType.I32, effect);
    var second = cb.Gep(sret, IrBuilder.ConstI32(4));
    var deadStore = cb.Store(sideEffect, second);
    cb.Ret();

    AddCaller(module, "caller", callee, 8, 0);

    Assert.That(ReturnStructureReduction.Run(module), Is.EqualTo(1));
    Dce.Run(callee);
    Assert.Multiple(() => {
      Assert.That(deadStore.Parent, Is.Null);
      Assert.That(sideEffect.Parent, Is.Not.Null, "calls are observable even when their returned field is not");
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void GivenUnusedFieldComputationIsPure_WhenRunAndDce_ThenProducerChainDies() {
    var module = new IrModule("retstruct");
    var callee = AddSretFunction(module, "Make", out var sret);
    var cb = new IrBuilder(callee.CreateBlock("entry"));
    cb.Store(IrBuilder.ConstI32(10), sret);
    var pure = cb.Add(IrBuilder.ConstI32(20), IrBuilder.ConstI32(1));
    var second = cb.Gep(sret, IrBuilder.ConstI32(4));
    cb.Store(pure, second);
    cb.Ret();

    AddCaller(module, "caller", callee, 8, 0);

    Assert.That(ReturnStructureReduction.Run(module), Is.EqualTo(1));
    Dce.Run(callee);
    Assert.Multiple(() => {
      Assert.That(pure.Parent, Is.Null, "ordinary DCE should collect pure work exposed by O0281");
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void GivenCalleeReadsAFieldItWrites_WhenRun_ThenThatFieldStoreIsRetained() {
    var module = new IrModule("retstruct");
    var callee = AddSretFunction(module, "Make", out var sret);
    var effectArg = new IrArgument(IrType.I32, 0, "value");
    var effect = module.AddFunction(new IrFunction("Observe", IrType.Void, [effectArg]));
    var cb = new IrBuilder(callee.CreateBlock("entry"));
    cb.Store(IrBuilder.ConstI32(10), sret);
    var second = cb.Gep(sret, IrBuilder.ConstI32(4));
    var secondStore = cb.Store(IrBuilder.ConstI32(20), second);
    var readBack = cb.Load(IrType.I32, second);
    cb.Call(IrType.Void, effect, readBack);
    cb.Ret();

    AddCaller(module, "caller", callee, 8, 0);

    Assert.That(ReturnStructureReduction.Run(module), Is.Zero);
    Assert.Multiple(() => {
      Assert.That(secondStore.Parent, Is.Not.Null, "callee-internal observation makes the write live");
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void GivenDynamicResultOffset_WhenRun_ThenCandidateIsRejected() {
    var module = new IrModule("retstruct");
    var index = new IrArgument(IrType.I32, 0, "offset");
    var sret = new IrArgument(IrType.Ptr, 1, "$sret");
    var callee = module.AddFunction(new IrFunction("Make", IrType.Void, [index, sret]));
    var cb = new IrBuilder(callee.CreateBlock("entry"));
    var dynamicField = cb.Gep(sret, index);
    var store = cb.Store(IrBuilder.ConstI32(20), dynamicField);
    cb.Ret();

    var caller = module.AddFunction(new IrFunction("caller", IrType.Void));
    var entry = caller.CreateBlock("entry");
    var builder = new IrBuilder(entry);
    var slot = entry.Append(new IrAlloca(IrType.I8) { Count = 8, Name = "result" });
    builder.Call(IrType.Void, callee, IrBuilder.ConstI32(4), slot);
    builder.Load(IrType.I32, slot);
    builder.Ret();

    Assert.That(ReturnStructureReduction.Run(module), Is.Zero);
    Assert.Multiple(() => {
      Assert.That(store.Parent, Is.Not.Null);
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void GivenStandardPipeline_WhenRunOnModule_ThenReturnReductionIsApplied() {
    var module = new IrModule("retstruct");
    var callee = AddSretFunction(module, "Make", out var sret);
    var cb = new IrBuilder(callee.CreateBlock("entry"));
    cb.Store(IrBuilder.ConstI32(10), sret);
    var second = cb.Gep(sret, IrBuilder.ConstI32(4));
    var secondStore = cb.Store(IrBuilder.ConstI32(20), second);
    cb.Ret();

    AddCaller(module, "caller", callee, 8, 0);

    IrPassManager.Standard().RunOnModule(module);

    Assert.Multiple(() => {
      Assert.That(secondStore.Parent, Is.Null, "O0281 is part of the standard whole-module pipeline");
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  private static IrFunction AddSretFunction(IrModule module, string name, out IrArgument sret) {
    sret = new IrArgument(IrType.Ptr, 0, "$sret");
    return module.AddFunction(new IrFunction(name, IrType.Void, [sret]));
  }

  private static (IrFunction Caller, IrAlloca Slot, IrBuilder Builder) AddCaller(
      IrModule module,
      string name,
      IrFunction callee,
      int resultSize,
      long observedOffset,
      bool terminate = true) {
    var caller = module.AddFunction(new IrFunction(name, IrType.Void));
    var entry = caller.CreateBlock("entry");
    var builder = new IrBuilder(entry);
    var slot = entry.Append(new IrAlloca(IrType.I8) { Count = resultSize, Name = "result" });
    builder.Call(IrType.Void, callee, slot);
    var field = observedOffset == 0 ? (IrValue)slot : builder.Gep(slot, IrBuilder.ConstI32(observedOffset));
    builder.Load(IrType.I32, field);
    if (terminate)
      builder.Ret();
    return (caller, slot, builder);
  }
}
