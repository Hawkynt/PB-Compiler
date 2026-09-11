using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class ColdCodeOutliningTests {

  [Test]
  public void Run_OutlinesZeroEqualityTerminalArmAndPassesLiveValue() {
    var module = new IrModule("T");
    var sink = Declare(module, "sink", IrType.Void, IrType.I32);
    var x = new IrArgument(IrType.I32, 0, "x");
    var function = module.AddFunction(new IrFunction("main", IrType.Void, [x]));
    var entry = function.CreateBlock("entry");
    var cold = function.CreateBlock("cold");
    var hot = function.CreateBlock("hot");

    BranchOnZero(entry, x, cold, hot);
    Call(cold, sink, x);
    Call(cold, sink, x);
    cold.Append(new IrRet());
    for (var i = 0; i < 4; ++i)
      Call(hot, sink, x);
    hot.Append(new IrRet());

    Assert.That(ColdCodeOutlining.Run(module), Is.EqualTo(1));

    var helper = module.Functions.Single(f => f.Name.StartsWith("main__cold_", StringComparison.Ordinal));
    Assert.Multiple(() => {
      Assert.That(helper.NoInline, Is.True);
      Assert.That(helper.ReturnType, Is.EqualTo(IrType.Void));
      Assert.That(helper.Parameters, Has.Count.EqualTo(1));
      Assert.That(helper.Parameters[0].Type, Is.EqualTo(IrType.I32));
      Assert.That(helper.AllInstructions.OfType<IrCall>().Count(), Is.EqualTo(2));
      Assert.That(cold.Instructions, Has.Count.EqualTo(2));
      Assert.That(cold.Instructions[0], Is.TypeOf<IrCall>());
      Assert.That(((IrCall)cold.Instructions[0]).Callee, Is.SameAs(helper));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });

    // The outlining call must survive the inliner that follows O0275 in the SPEED pipeline.
    Inliner.Run(module, optimizeForSpeed: true);
    Assert.That(cold.Instructions.OfType<IrCall>().Single().Callee, Is.SameAs(helper));
  }

  [Test]
  public void Run_PreservesNonVoidEarlyReturnThroughHelperCall() {
    var module = new IrModule("T");
    var sink = Declare(module, "sink", IrType.Void, IrType.I32);
    var x = new IrArgument(IrType.I32, 0, "x");
    var function = module.AddFunction(new IrFunction("value", IrType.I32, [x]));
    var entry = function.CreateBlock("entry");
    var cold = function.CreateBlock("cold");
    var hot = function.CreateBlock("hot");

    BranchOnZero(entry, x, cold, hot);
    Call(cold, sink, x);
    Call(cold, sink, x);
    cold.Append(new IrRet(x));
    for (var i = 0; i < 4; ++i)
      Call(hot, sink, x);
    hot.Append(new IrRet(new IrConstantInt(IrType.I32, 42)));

    Assert.That(ColdCodeOutlining.Run(module), Is.EqualTo(1));

    var helper = module.Functions.Single(f => f.Name.StartsWith("value__cold_", StringComparison.Ordinal));
    var call = cold.Instructions.OfType<IrCall>().Single();
    var ret = (IrRet)cold.Terminator!;
    Assert.Multiple(() => {
      Assert.That(helper.ReturnType, Is.EqualTo(IrType.I32));
      Assert.That(call.Type, Is.EqualTo(IrType.I32));
      Assert.That(ret.Value, Is.SameAs(call));
      Assert.That(((IrRet)helper.Blocks.Single().Terminator!).Value, Is.SameAs(helper.Parameters[0]));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void Run_OutlinesMultiBlockTerminalRegion() {
    var module = new IrModule("T");
    var sink = Declare(module, "sink", IrType.Void, IrType.I32);
    var x = new IrArgument(IrType.I32, 0, "x");
    var function = module.AddFunction(new IrFunction("main", IrType.Void, [x]));
    var entry = function.CreateBlock("entry");
    var coldHead = function.CreateBlock("cold.head");
    var coldTail = function.CreateBlock("cold.tail");
    var hot = function.CreateBlock("hot");

    BranchOnZero(entry, x, coldHead, hot);
    Call(coldHead, sink, x);
    coldHead.Append(new IrBr(coldTail));
    Call(coldTail, sink, x);
    coldTail.Append(new IrRet());
    for (var i = 0; i < 5; ++i)
      Call(hot, sink, x);
    hot.Append(new IrRet());

    Assert.That(ColdCodeOutlining.Run(module), Is.EqualTo(1));

    var helper = module.Functions.Single(f => f.Name.StartsWith("main__cold_", StringComparison.Ordinal));
    Assert.Multiple(() => {
      Assert.That(helper.Blocks, Has.Count.EqualTo(2));
      Assert.That(function.Blocks, Does.Not.Contain(coldTail));
      Assert.That(coldHead.Instructions.OfType<IrCall>().Single().Callee, Is.SameAs(helper));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void Run_DoesNotOutlineArmThatRejoinsHotControlFlow() {
    var module = new IrModule("T");
    var sink = Declare(module, "sink", IrType.Void, IrType.I32);
    var x = new IrArgument(IrType.I32, 0, "x");
    var function = module.AddFunction(new IrFunction("main", IrType.Void, [x]));
    var entry = function.CreateBlock("entry");
    var left = function.CreateBlock("left");
    var right = function.CreateBlock("right");
    var merge = function.CreateBlock("merge");

    BranchOnZero(entry, x, left, right);
    Call(left, sink, x);
    Call(left, sink, x);
    left.Append(new IrBr(merge));
    for (var i = 0; i < 4; ++i)
      Call(right, sink, x);
    right.Append(new IrBr(merge));
    merge.Append(new IrRet());

    Assert.Multiple(() => {
      Assert.That(ColdCodeOutlining.Run(module), Is.Zero);
      Assert.That(module.Functions, Has.Count.EqualTo(2));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void Run_DoesNotGuessColdArmFromSizeAlone() {
    var module = new IrModule("T");
    var sink = Declare(module, "sink", IrType.Void, IrType.I32);
    var x = new IrArgument(IrType.I32, 0, "x");
    var function = module.AddFunction(new IrFunction("main", IrType.Void, [x]));
    var entry = function.CreateBlock("entry");
    var smaller = function.CreateBlock("smaller");
    var larger = function.CreateBlock("larger");

    var cmp = entry.Append(new IrCmp(IrCmpPred.Eq, x, new IrConstantInt(IrType.I32, 7)));
    entry.Append(new IrCondBr(cmp, smaller, larger));
    Call(smaller, sink, x);
    Call(smaller, sink, x);
    smaller.Append(new IrRet());
    for (var i = 0; i < 5; ++i)
      Call(larger, sink, x);
    larger.Append(new IrRet());

    Assert.Multiple(() => {
      Assert.That(ColdCodeOutlining.Run(module), Is.Zero);
      Assert.That(module.Functions, Has.Count.EqualTo(2));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void Run_DoesNotOutlineRegionWithTooManyLiveIns() {
    var module = new IrModule("T");
    var sink = Declare(module, "sink5", IrType.Void, IrType.I32, IrType.I32, IrType.I32, IrType.I32, IrType.I32);
    var args = Enumerable.Range(0, 5).Select(i => new IrArgument(IrType.I32, i, $"x{i}")).ToArray();
    var function = module.AddFunction(new IrFunction("main", IrType.Void, args));
    var entry = function.CreateBlock("entry");
    var cold = function.CreateBlock("cold");
    var hot = function.CreateBlock("hot");

    BranchOnZero(entry, args[0], cold, hot);
    Call(cold, sink, args);
    Call(cold, sink, args);
    cold.Append(new IrRet());
    for (var i = 0; i < 4; ++i)
      Call(hot, sink, args);
    hot.Append(new IrRet());

    Assert.Multiple(() => {
      Assert.That(ColdCodeOutlining.Run(module), Is.Zero);
      Assert.That(module.Functions, Has.Count.EqualTo(2));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void Run_PrefersUnreachableTerminalArmEvenWhenItIsLarger() {
    var module = new IrModule("T");
    var sink = Declare(module, "sink", IrType.Void, IrType.I32);
    var x = new IrArgument(IrType.I32, 0, "x");
    var function = module.AddFunction(new IrFunction("main", IrType.Void, [x]));
    var entry = function.CreateBlock("entry");
    var normal = function.CreateBlock("normal");
    var impossible = function.CreateBlock("impossible");

    BranchOnZero(entry, x, normal, impossible);
    Call(normal, sink, x);
    Call(normal, sink, x);
    normal.Append(new IrRet());
    for (var i = 0; i < 4; ++i)
      Call(impossible, sink, x);
    impossible.Append(new IrUnreachable());

    Assert.That(ColdCodeOutlining.Run(module), Is.EqualTo(1));

    var helper = module.Functions.Single(f => f.Name.StartsWith("main__cold_", StringComparison.Ordinal));
    Assert.Multiple(() => {
      Assert.That(helper.AllInstructions.OfType<IrUnreachable>().Count(), Is.EqualTo(1));
      Assert.That(impossible.Instructions.OfType<IrCall>().Single().Callee, Is.SameAs(helper));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void Run_RepeatedSweepDoesNotOutlineGeneratedHelperAgain() {
    var module = new IrModule("T");
    var sink = Declare(module, "sink", IrType.Void, IrType.I32);
    var x = new IrArgument(IrType.I32, 0, "x");
    var function = module.AddFunction(new IrFunction("main", IrType.Void, [x]));
    var entry = function.CreateBlock("entry");
    var cold = function.CreateBlock("cold");
    var hot = function.CreateBlock("hot");

    BranchOnZero(entry, x, cold, hot);
    Call(cold, sink, x);
    Call(cold, sink, x);
    cold.Append(new IrRet());
    for (var i = 0; i < 4; ++i)
      Call(hot, sink, x);
    hot.Append(new IrRet());

    Assert.That(ColdCodeOutlining.Run(module), Is.EqualTo(1));
    var countAfterFirstSweep = module.Functions.Count;

    Assert.Multiple(() => {
      Assert.That(ColdCodeOutlining.Run(module), Is.Zero);
      Assert.That(module.Functions, Has.Count.EqualTo(countAfterFirstSweep));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void Standard_SpeedPipelineRunsOutliningBeforeInlining() {
    var module = new IrModule("T");
    var sink = Declare(module, "sink", IrType.Void, IrType.I32);
    var x = new IrArgument(IrType.I32, 0, "x");
    var function = module.AddFunction(new IrFunction("main", IrType.Void, [x]));
    var entry = function.CreateBlock("entry");
    var cold = function.CreateBlock("cold");
    var hot = function.CreateBlock("hot");

    BranchOnZero(entry, x, cold, hot);
    Call(cold, sink, x);
    Call(cold, sink, x);
    cold.Append(new IrRet());
    for (var i = 0; i < 4; ++i)
      Call(hot, sink, x);
    hot.Append(new IrRet());

    IrPassManager.Standard(optimizeForSpeed: true).RunOnModule(module);

    var helper = module.Functions.Single(f => f.Name.StartsWith("main__cold_", StringComparison.Ordinal));
    Assert.Multiple(() => {
      Assert.That(helper.NoInline, Is.True);
      Assert.That(function.AllInstructions.OfType<IrCall>().Any(call => ReferenceEquals(call.Callee, helper)), Is.True);
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  /// <summary>
  /// Outlining is speculative: a consumer that cannot compile the helper - the native x86 routing
  /// declines one whose live ranges will not register-allocate - has to be able to put the region
  /// back, or it loses the whole caller instead of just the helper.
  /// </summary>
  [Test]
  public void Reinline_PutsTheRegionBackAndRemovesTheHelper() {
    var module = new IrModule("T");
    var sink = Declare(module, "sink", IrType.Void, IrType.I32);
    var x = new IrArgument(IrType.I32, 0, "x");
    var function = module.AddFunction(new IrFunction("value", IrType.I32, [x]));
    var entry = function.CreateBlock("entry");
    var cold = function.CreateBlock("cold");
    var hot = function.CreateBlock("hot");

    BranchOnZero(entry, x, cold, hot);
    Call(cold, sink, x);
    Call(cold, sink, x);
    cold.Append(new IrRet(x));
    for (var i = 0; i < 4; ++i)
      Call(hot, sink, x);
    hot.Append(new IrRet(new IrConstantInt(IrType.I32, 42)));

    Assert.That(ColdCodeOutlining.Run(module), Is.EqualTo(1));
    var helper = module.Functions.Single(f => f.Name.StartsWith("value__cold_", StringComparison.Ordinal));

    Assert.That(ColdCodeOutlining.Reinline(module, helper), Is.True);

    var restored = cold.Successors.Single();
    Assert.Multiple(() => {
      Assert.That(module.Functions, Does.Not.Contain(helper));
      Assert.That(function.AllInstructions.OfType<IrCall>().Select(call => call.Callee),
        Has.None.SameAs(helper), "no call may survive the function it named");
      Assert.That(cold.Terminator, Is.TypeOf<IrBr>());
      // the region is back with its live-in bound to the value the call passed, not to a parameter
      Assert.That(restored.Instructions.OfType<IrCall>().First().Args.Single(), Is.SameAs(x));
      Assert.That(((IrRet)restored.Terminator!).Value, Is.SameAs(x));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  /// <summary>A helper is only ever put back into the shape the outliner itself left behind.</summary>
  [Test]
  public void Reinline_RefusesACallSiteTheOutlinerDidNotCreate() {
    var module = new IrModule("T");
    var sink = Declare(module, "sink", IrType.Void, IrType.I32);
    var x = new IrArgument(IrType.I32, 0, "x");
    var function = module.AddFunction(new IrFunction("main", IrType.Void, [x]));
    var entry = function.CreateBlock("entry");
    var cold = function.CreateBlock("cold");
    var hot = function.CreateBlock("hot");

    BranchOnZero(entry, x, cold, hot);
    Call(cold, sink, x);
    Call(cold, sink, x);
    cold.Append(new IrRet());
    for (var i = 0; i < 4; ++i)
      Call(hot, sink, x);
    hot.Append(new IrRet());

    Assert.That(ColdCodeOutlining.Run(module), Is.EqualTo(1));
    var helper = module.Functions.Single(f => f.Name.StartsWith("main__cold_", StringComparison.Ordinal));

    // a second, ordinary call site - this block is not "the call, and the return of its result"
    hot.InsertBefore(new IrCall(helper.ReturnType, helper, [x]), hot.Terminator!);

    Assert.Multiple(() => {
      Assert.That(ColdCodeOutlining.Reinline(module, helper), Is.False);
      Assert.That(module.Functions, Does.Contain(helper));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  private static IrFunction Declare(IrModule module, string name, IrType returnType, params IrType[] parameters)
    => module.AddFunction(new IrFunction(name, returnType,
      parameters.Select((type, index) => new IrArgument(type, index))));

  private static void BranchOnZero(IrBasicBlock block, IrValue value, IrBasicBlock ifTrue, IrBasicBlock ifFalse) {
    var cmp = block.Append(new IrCmp(IrCmpPred.Eq, value, new IrConstantInt(value.Type, 0)));
    block.Append(new IrCondBr(cmp, ifTrue, ifFalse));
  }

  private static void Call(IrBasicBlock block, IrFunction callee, params IrValue[] arguments)
    => block.Append(new IrCall(callee.ReturnType, callee, arguments));
}
