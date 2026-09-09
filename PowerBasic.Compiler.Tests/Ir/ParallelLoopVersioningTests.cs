using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class ParallelLoopVersioningTests {

  private sealed record Fixture(
    IrModule Module,
    IrFunction Function,
    IrBasicBlock Preheader,
    IrBasicBlock Header,
    IrBasicBlock Body,
    IrBasicBlock Exit,
    IrPhi Counter,
    IrGlobalVariable Source,
    IrGlobalVariable Destination);

  private static Fixture CreateLoop(
      int trips = 2048,
      bool loopCarriedDependence = false,
      bool captureLocal = false,
      IrType? counterType = null) {
    counterType ??= IrType.I16;
    var module = new IrModule("parallel-test");
    var source = module.AddGlobal(new IrGlobalVariable("source", IrType.I8) { Count = trips + 8 });
    var destination = loopCarriedDependence
      ? source
      : module.AddGlobal(new IrGlobalVariable("destination", IrType.I8) { Count = trips + 8 });
    var fn = module.AddFunction(new IrFunction("f", IrType.Void));
    var entry = fn.CreateBlock("entry");
    var header = fn.CreateBlock("head");
    var body = fn.CreateBlock("body");
    var exit = fn.CreateBlock("exit");

    IrValue? captured = null;
    if (captureLocal)
      captured = entry.Append(new IrAlloca(IrType.I8));
    entry.Append(new IrBr(header));

    var counter = header.AppendPhi(new IrPhi(counterType) { Name = "i" });
    var test = header.Append(new IrCmp(IrCmpPred.Slt, counter, new IrConstantInt(counterType, trips)));
    header.Append(new IrCondBr(test, body, exit));

    var builder = new IrBuilder(body);
    IrValue sourceIndex = counter;
    if (loopCarriedDependence)
      sourceIndex = builder.Sub(counter, new IrConstantInt(counterType, 1));
    IrValue value = builder.Load(IrType.I8, builder.Gep(source, sourceIndex));
    if (captured is not null) {
      var extra = builder.Load(IrType.I8, captured);
      value = builder.Add(value, extra);
    }
    builder.Store(value, builder.Gep(destination, counter));
    var next = builder.Add(counter, new IrConstantInt(counterType, 1));
    builder.Br(header);

    counter.AddIncoming(new IrConstantInt(counterType, loopCarriedDependence ? 1 : 0), entry);
    counter.AddIncoming(next, body);
    new IrBuilder(exit).Ret();

    Assert.That(IrVerifier.Verify(module), Is.Empty);
    return new(module, fn, entry, header, body, exit, counter, source, destination);
  }

  [Test]
  public void IndependentLargeLoop_GivenOptInPass_ThenKeepsSequentialVersionAndAddsParallelPath() {
    var fixture = CreateLoop();

    Assert.That(ParallelLoopVersioning.Run(fixture.Module), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fixture.Module), Is.Empty);

    Assert.That(fixture.Preheader.Terminator, Is.TypeOf<IrCondBr>());
    var branch = (IrCondBr)fixture.Preheader.Terminator!;
    Assert.That(branch.IfFalse, Is.SameAs(fixture.Header), "the original loop is the sequential version");
    Assert.That(branch.IfTrue.Instructions.OfType<IrCall>().Single().Callee,
      Is.SameAs(fixture.Module.FindFunction("rt_parallel_for")));
    Assert.That(branch.IfTrue.Terminator, Is.TypeOf<IrBr>());
    Assert.That(((IrBr)branch.IfTrue.Terminator!).Target, Is.SameAs(fixture.Exit));

    var helper = fixture.Module.Functions.Single(function => function.Name.StartsWith("pb_parallel_body_", StringComparison.Ordinal));
    Assert.That(helper.NoInline, Is.True);
    Assert.That(helper.Parameters, Has.Count.EqualTo(1));
    Assert.That(helper.Parameters[0].Type, Is.EqualTo(IrType.I64));
  }

  [Test]
  public void IndependentLargeLoop_WhenPassRunsAgain_ThenItIsNotVersionedTwice() {
    var fixture = CreateLoop();

    Assert.That(ParallelLoopVersioning.Run(fixture.Module), Is.EqualTo(1));
    Assert.That(ParallelLoopVersioning.Run(fixture.Module), Is.Zero);
    Assert.That(fixture.Module.Functions.Count(function => function.Name == "rt_parallel_for"), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fixture.Module), Is.Empty);
  }

  [Test]
  public void LoopCarriedFlowDependence_GivenPreviousElementRead_ThenParallelizationDeclines() {
    var fixture = CreateLoop(loopCarriedDependence: true);

    Assert.That(ParallelLoopVersioning.Run(fixture.Module), Is.Zero);
    Assert.That(fixture.Module.FindFunction("rt_parallel_for"), Is.Null);
    Assert.That(IrVerifier.Verify(fixture.Module), Is.Empty);
  }

  [Test]
  public void SmallIndependentLoop_GivenTripCountBelowThreshold_ThenParallelizationDeclines() {
    var fixture = CreateLoop(trips: ParallelLoopVersioning.MinimumTrips - 1);

    Assert.That(ParallelLoopVersioning.Run(fixture.Module), Is.Zero);
    Assert.That(fixture.Module.FindFunction("rt_parallel_for"), Is.Null);
  }

  [Test]
  public void IndependentLoop_GivenCapturedStackStorage_ThenOutliningDeclines() {
    var fixture = CreateLoop(captureLocal: true);

    Assert.That(ParallelLoopVersioning.Run(fixture.Module), Is.Zero);
    Assert.That(fixture.Module.FindFunction("rt_parallel_for"), Is.Null);
    Assert.That(IrVerifier.Verify(fixture.Module), Is.Empty);
  }

  [Test]
  public void IndependentLargeLoop_GivenSignedI64Counter_ThenParallelizationDeclines() {
    var fixture = CreateLoop(counterType: IrType.I64);

    Assert.That(ParallelLoopVersioning.Run(fixture.Module), Is.Zero);
    Assert.That(fixture.Module.FindFunction("rt_parallel_should_run"), Is.Null);
    Assert.That(fixture.Module.FindFunction("rt_parallel_for"), Is.Null);
    Assert.That(IrVerifier.Verify(fixture.Module), Is.Empty);
  }

  [Test]
  public void VersionedLoop_WhenEmittedToC_ThenRuntimeCallUsesPortableVarargsCallback() {
    var fixture = CreateLoop();
    Assert.That(ParallelLoopVersioning.Run(fixture.Module), Is.EqualTo(1));

    var emitted = CEmitter.Emit(fixture.Module);

    Assert.That(emitted, Does.Contain("extern void rt_parallel_for(int64_t p0, int64_t p1, int64_t p2, ...);"));
    Assert.That(emitted, Does.Match(@"rt_parallel_for\([^;]*pb_parallel_body_0\);"));
  }

  [Test]
  public void VersionedLoop_WhenEmittedToLlvm_ThenVariadicRuntimeDeclarationCarriesHelperPointer() {
    var fixture = CreateLoop();
    Assert.That(ParallelLoopVersioning.Run(fixture.Module), Is.EqualTo(1));

    var emitted = LlvmEmitter.Emit(fixture.Module);

    Assert.That(emitted, Does.Contain("declare void @rt_parallel_for(i64 %first, i64 %step, i64 %trips, ...)"));
    Assert.That(emitted, Does.Match(@"call void \(i64, i64, i64, \.\.\.\) @rt_parallel_for\(i64 0, i64 1, i64 2048, ptr @pb_parallel_body_0\)"));
  }
}
