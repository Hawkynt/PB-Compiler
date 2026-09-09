using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0330/O0339 — memory-loop recognition and constant-size specialization.</summary>
[TestFixture]
public sealed class LibraryAndMemoryIdiomTests {

  private static readonly TargetCost _baseline = new(CpuTier.I8086, CostObjective.Balanced);
  private static readonly TargetCost _speed286 = new(CpuTier.I80286, CostObjective.Speed);
  private static readonly TargetCost _speed386 = new(CpuTier.I80386, CostObjective.Speed);
  private static readonly TargetCost _speedP6 = new(CpuTier.P6, CostObjective.Speed);
  private static readonly TargetCost _sizeP6 = new(CpuTier.P6, CostObjective.Size);

  [Test]
  public void FillLoop_GivenAUnitStrideByteStore_ThenItBecomesMemset() {
    var module = new IrModule("test");
    var fn = module.AddFunction(new IrFunction("fill", IrType.Void));
    BuildFillLoop(fn, new IrConstantInt(IrType.I8, 0x5a), 12);

    Assert.That(LibraryCallRecognition.Run(module), Is.EqualTo(1));
    var call = fn.AllInstructions.OfType<IrCall>().Single();
    Assert.That((call.Callee as IrFunction)?.Name, Is.EqualTo("llvm.memset.p0.i32"));
    Assert.That(call.Args.ElementAt(2), Is.InstanceOf<IrConstantInt>());
    Assert.That(((IrConstantInt)call.Args.ElementAt(2)).Value, Is.EqualTo(12));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void CopyLoop_GivenDistinctAllocations_ThenItBecomesMemcpy() {
    var module = new IrModule("test");
    var fn = module.AddFunction(new IrFunction("copy", IrType.Void));
    var pre = fn.AddBlock(new IrBasicBlock("pre"));
    var source = pre.Append(new IrAlloca(IrType.I8) { Count = 6 });
    var target = pre.Append(new IrAlloca(IrType.I8) { Count = 6 });
    var (header, body, latch, exit, counter) = LoopSkeleton(fn, pre, 6);
    var sourceAt = body.Append(new IrGep(source, counter, IrType.I8));
    var value = body.Append(new IrLoad(IrType.I8, sourceAt));
    var targetAt = body.Append(new IrGep(target, counter, IrType.I8));
    body.Append(new IrStore(value, targetAt));
    body.Append(new IrBr(latch));
    FinishLoop(header, latch, exit, counter, pre, 6);

    Assert.That(LibraryCallRecognition.Run(module), Is.EqualTo(1));
    Assert.That((fn.AllInstructions.OfType<IrCall>().Single().Callee as IrFunction)?.Name,
      Is.EqualTo("llvm.memcpy.p0.p0.i32"));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void CopyLoop_GivenTheSameAllocationOnBothSides_ThenItIsNotGuessedNonOverlapping() {
    var module = new IrModule("test");
    var fn = module.AddFunction(new IrFunction("overlap", IrType.Void));
    var pre = fn.AddBlock(new IrBasicBlock("pre"));
    var buffer = pre.Append(new IrAlloca(IrType.I8) { Count = 6 });
    var (header, body, latch, exit, counter) = LoopSkeleton(fn, pre, 6);
    var at = body.Append(new IrGep(buffer, counter, IrType.I8));
    var value = body.Append(new IrLoad(IrType.I8, at));
    body.Append(new IrStore(value, at));
    body.Append(new IrBr(latch));
    FinishLoop(header, latch, exit, counter, pre, 6);

    Assert.That(LibraryCallRecognition.Run(module), Is.Zero);
    Assert.That(module.FindFunction("llvm.memcpy.p0.p0.i32"), Is.Null,
      "a declined match must not even mint an intrinsic declaration");
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Memcpy_GivenSixConstantBytesOn8086_ThenTheCallUsesThreeWordMoves() {
    var fn = MemcpyFunction(6);

    Assert.That(MemoryRoutineSpecialization.Run(fn, _baseline), Is.EqualTo(1));
    Assert.That(fn.AllInstructions.OfType<IrCall>(), Is.Empty);
    var loads = fn.AllInstructions.OfType<IrLoad>().ToList();
    var stores = fn.AllInstructions.OfType<IrStore>().ToList();
    Assert.That(loads.Count, Is.EqualTo(3));
    Assert.That(stores.Count, Is.EqualTo(3));
    Assert.That(loads.All(load => Equals(load.Type, IrType.I16)), Is.True);
    Assert.That(stores.All(store => Equals(store.Value.Type, IrType.I16)), Is.True);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Memcpy_GivenSevenConstantBytesOn8086_ThenTheRuntimeFormSurvives() {
    var fn = MemcpyFunction(7);

    Assert.That(MemoryRoutineSpecialization.Run(fn, _baseline), Is.Zero);
    Assert.That(fn.AllInstructions.OfType<IrCall>().Count(), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Memcpy_GivenTargetlessRun_ThenItUsesThe8086BaselinePolicy() {
    var fn = MemcpyFunction(7);

    Assert.That(MemoryRoutineSpecialization.Run(fn), Is.Zero,
      "the compatibility overload must not silently assume a newer target");
    Assert.That(fn.AllInstructions.OfType<IrCall>().Count(), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Memcpy_GivenEightBytesOn286_ThenFourWordMovesFitButNineBytesDoNot() {
    var accepted = MemcpyFunction(8);
    var refused = MemcpyFunction(9);

    Assert.That(MemoryRoutineSpecialization.Run(accepted, _speed286), Is.EqualTo(1));
    var loads = accepted.AllInstructions.OfType<IrLoad>().ToList();
    Assert.That(loads, Has.Count.EqualTo(4));
    Assert.That(loads.All(load => Equals(load.Type, IrType.I16)), Is.True);
    Assert.That(MemoryRoutineSpecialization.Run(refused, _speed286), Is.Zero);
    Assert.That(refused.AllInstructions.OfType<IrCall>(), Has.Exactly(1).Items);
    Assert.That(IrVerifier.Verify(accepted), Is.Empty);
    Assert.That(IrVerifier.Verify(refused), Is.Empty);
  }

  [Test]
  public void Memcpy_GivenSevenBytesOn386_ThenThePlanIsDwordWordByte() {
    var fn = MemcpyFunction(7);

    Assert.That(MemoryRoutineSpecialization.Run(fn, _speed386), Is.EqualTo(1));
    Assert.That(fn.AllInstructions.OfType<IrLoad>().Select(load => load.Type),
      Is.EqualTo(new[] { IrType.I32, IrType.I16, IrType.I8 }));
    Assert.That(fn.AllInstructions.OfType<IrStore>().Select(store => store.Value.Type),
      Is.EqualTo(new[] { IrType.I32, IrType.I16, IrType.I8 }));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Memcpy_Given386Boundary_ThenSixteenBytesFitAndSeventeenDoNot() {
    var accepted = MemcpyFunction(16);
    var refused = MemcpyFunction(17);

    Assert.That(MemoryRoutineSpecialization.Run(accepted, _speed386), Is.EqualTo(1));
    Assert.That(accepted.AllInstructions.OfType<IrLoad>(),
      Has.Count.EqualTo(4).And.All.Matches<IrLoad>(load => Equals(load.Type, IrType.I32)));
    Assert.That(MemoryRoutineSpecialization.Run(refused, _speed386), Is.Zero);
    Assert.That(refused.AllInstructions.OfType<IrCall>(), Has.Exactly(1).Items);
    Assert.That(IrVerifier.Verify(accepted), Is.Empty);
    Assert.That(IrVerifier.Verify(refused), Is.Empty);
  }

  [Test]
  public void Memcpy_GivenP6SpeedVersusSize_ThenTheSizeBudgetRefusesTheFifthDword() {
    var speed = MemcpyFunction(20);
    var size = MemcpyFunction(20);

    Assert.That(MemoryRoutineSpecialization.Run(speed, _speedP6), Is.EqualTo(1));
    Assert.That(speed.AllInstructions.OfType<IrLoad>(),
      Has.Count.EqualTo(5).And.All.Matches<IrLoad>(load => Equals(load.Type, IrType.I32)));
    Assert.That(MemoryRoutineSpecialization.Run(size, _sizeP6), Is.Zero);
    Assert.That(size.AllInstructions.OfType<IrCall>(), Has.Exactly(1).Items);
    Assert.That(IrVerifier.Verify(speed), Is.Empty);
    Assert.That(IrVerifier.Verify(size), Is.Empty);
  }

  [Test]
  public void Memcpy_GivenVolatileFlag_ThenTheRuntimeFormSurvives() {
    var fn = MemcpyFunction(2, isVolatile: true);

    Assert.That(MemoryRoutineSpecialization.Run(fn, _speed386), Is.Zero);
    Assert.That(fn.AllInstructions.OfType<IrCall>().Count(), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Memset_GivenEightConstantBytesOn8086_ThenTheCallUsesFourWordStores() {
    var fn = MemsetFunction(8, new IrConstantInt(IrType.I8, 0x5a));

    Assert.That(MemoryRoutineSpecialization.Run(fn, _baseline), Is.EqualTo(1));
    Assert.That(fn.AllInstructions.OfType<IrCall>(), Is.Empty);
    var stores = fn.AllInstructions.OfType<IrStore>().ToList();
    Assert.That(stores.Count, Is.EqualTo(4));
    Assert.That(stores.All(store => Equals(store.Value.Type, IrType.I16)), Is.True);
    Assert.That(stores.All(store => store.Value is IrConstantInt constant && constant.ZeroExtended == 0x5a5aUL), Is.True);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Memset_Given386Boundary_ThenThirtyTwoConstantBytesUseDwordsAndThirtyThreeRemainRuntime() {
    var accepted = MemsetFunction(32, new IrConstantInt(IrType.I8, 0x5a));
    var refused = MemsetFunction(33, new IrConstantInt(IrType.I8, 0x5a));

    Assert.That(MemoryRoutineSpecialization.Run(accepted, _speed386), Is.EqualTo(1));
    var stores = accepted.AllInstructions.OfType<IrStore>().ToList();
    Assert.That(stores, Has.Count.EqualTo(8));
    Assert.That(stores.All(store => Equals(store.Value.Type, IrType.I32)), Is.True);
    Assert.That(stores.All(store => store.Value is IrConstantInt constant
      && constant.ZeroExtended == 0x5a5a5a5aUL), Is.True);
    Assert.That(MemoryRoutineSpecialization.Run(refused, _speed386), Is.Zero);
    Assert.That(refused.AllInstructions.OfType<IrCall>(), Has.Exactly(1).Items);
    Assert.That(IrVerifier.Verify(accepted), Is.Empty);
    Assert.That(IrVerifier.Verify(refused), Is.Empty);
  }

  [Test]
  public void Memset_GivenFourDynamicBytesOnP6_ThenTheCallKeepsByteStores() {
    var (fn, fill) = DynamicMemsetFunction(4);

    Assert.That(MemoryRoutineSpecialization.Run(fn, _speedP6), Is.EqualTo(1));
    Assert.That(fn.AllInstructions.OfType<IrCall>(), Is.Empty);
    var stores = fn.AllInstructions.OfType<IrStore>().ToList();
    Assert.That(stores.Count, Is.EqualTo(4));
    Assert.That(stores.All(store => ReferenceEquals(store.Value, fill)), Is.True,
      "a dynamic byte must not grow arithmetic just to synthesize a repeated word or dword");
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Memset_GivenFiveDynamicBytesOnP6_ThenTheRuntimeFormSurvives() {
    var (fn, _) = DynamicMemsetFunction(5);

    Assert.That(MemoryRoutineSpecialization.Run(fn, _speedP6), Is.Zero,
      "the target's larger constant-fill budget must not leak into dynamic-byte fills");
    Assert.That(fn.AllInstructions.OfType<IrCall>().Count(), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  private static IrFunction MemcpyFunction(int size, bool isVolatile = false) {
    var memcpy = new IrFunction("llvm.memcpy.p0.p0.i32", IrType.Void, [
      new IrArgument(IrType.Ptr, 0), new IrArgument(IrType.Ptr, 1),
      new IrArgument(IrType.I32, 2), new IrArgument(IrType.I1, 3),
    ]);
    var fn = new IrFunction($"copy_{size}", IrType.Void);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var source = entry.Append(new IrAlloca(IrType.I8) { Count = size });
    var target = entry.Append(new IrAlloca(IrType.I8) { Count = size });
    entry.Append(new IrCall(IrType.Void, memcpy, [target, source,
      new IrConstantInt(IrType.I32, size), new IrConstantInt(IrType.I1, isVolatile ? 1 : 0)]));
    entry.Append(new IrRet());
    return fn;
  }

  private static IrFunction MemsetFunction(int size, IrValue fill) {
    var memset = new IrFunction("llvm.memset.p0.i32", IrType.Void, [
      new IrArgument(IrType.Ptr, 0), new IrArgument(IrType.I8, 1),
      new IrArgument(IrType.I32, 2), new IrArgument(IrType.I1, 3),
    ]);
    var fn = new IrFunction($"fill_{size}", IrType.Void);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var target = entry.Append(new IrAlloca(IrType.I8) { Count = size });
    entry.Append(new IrCall(IrType.Void, memset, [target, fill,
      new IrConstantInt(IrType.I32, size), new IrConstantInt(IrType.I1, 0)]));
    entry.Append(new IrRet());
    return fn;
  }

  private static (IrFunction Function, IrLoad Fill) DynamicMemsetFunction(int size) {
    var memset = new IrFunction("llvm.memset.p0.i32", IrType.Void, [
      new IrArgument(IrType.Ptr, 0), new IrArgument(IrType.I8, 1),
      new IrArgument(IrType.I32, 2), new IrArgument(IrType.I1, 3),
    ]);
    var fn = new IrFunction($"dynamic_fill_{size}", IrType.Void);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var fillCell = entry.Append(new IrAlloca(IrType.I8));
    var fill = entry.Append(new IrLoad(IrType.I8, fillCell));
    var target = entry.Append(new IrAlloca(IrType.I8) { Count = size });
    entry.Append(new IrCall(IrType.Void, memset, [target, fill,
      new IrConstantInt(IrType.I32, size), new IrConstantInt(IrType.I1, 0)]));
    entry.Append(new IrRet());
    return (fn, fill);
  }

  private static void BuildFillLoop(IrFunction fn, IrValue value, int trips) {
    var pre = fn.AddBlock(new IrBasicBlock("pre"));
    var target = pre.Append(new IrAlloca(IrType.I8) { Count = trips });
    var (header, body, latch, exit, counter) = LoopSkeleton(fn, pre, trips);
    var targetAt = body.Append(new IrGep(target, counter, IrType.I8));
    body.Append(new IrStore(value, targetAt));
    body.Append(new IrBr(latch));
    FinishLoop(header, latch, exit, counter, pre, trips);
  }

  private static (IrBasicBlock Header, IrBasicBlock Body, IrBasicBlock Latch, IrBasicBlock Exit, IrPhi Counter)
      LoopSkeleton(IrFunction fn, IrBasicBlock pre, int trips) {
    var header = fn.AddBlock(new IrBasicBlock("header"));
    var body = fn.AddBlock(new IrBasicBlock("body"));
    var latch = fn.AddBlock(new IrBasicBlock("latch"));
    var exit = fn.AddBlock(new IrBasicBlock("exit"));
    pre.Append(new IrBr(header));
    var counter = header.AppendPhi(new IrPhi(IrType.I16));
    var test = header.Append(new IrCmp(IrCmpPred.Slt, counter, new IrConstantInt(IrType.I16, trips)));
    header.Append(new IrCondBr(test, body, exit));
    return (header, body, latch, exit, counter);
  }

  private static void FinishLoop(IrBasicBlock header, IrBasicBlock latch, IrBasicBlock exit, IrPhi counter,
      IrBasicBlock pre, int trips) {
    var next = latch.Append(new IrBinary(IrBinaryOp.Add, counter, new IrConstantInt(IrType.I16, 1)));
    latch.Append(new IrBr(header));
    counter.AddIncoming(new IrConstantInt(IrType.I16, 0), pre);
    counter.AddIncoming(next, latch);
    exit.Append(new IrRet());
  }
}
