using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0330/O0339 — memory-loop recognition and constant-size specialization.</summary>
[TestFixture]
public sealed class LibraryAndMemoryIdiomTests {

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
  public void FillLoop_GivenConditionalStore_ThenItIsNotPromotedToMemset() {
    var module = new IrModule("test");
    var condition = new IrArgument(IrType.I1, 0);
    var fn = module.AddFunction(new IrFunction("fill_if", IrType.Void, [condition]));
    var pre = fn.AddBlock(new IrBasicBlock("pre"));
    var target = pre.Append(new IrAlloca(IrType.I8) { Count = 6 });
    var (header, body, latch, exit, counter) = LoopSkeleton(fn, pre, 6);
    var storeBlock = fn.AddBlock(new IrBasicBlock("store"));
    var skipBlock = fn.AddBlock(new IrBasicBlock("skip"));
    body.Append(new IrCondBr(condition, storeBlock, skipBlock));
    var targetAt = storeBlock.Append(new IrGep(target, counter, IrType.I8));
    storeBlock.Append(new IrStore(new IrConstantInt(IrType.I8, 0x5a), targetAt));
    storeBlock.Append(new IrBr(latch));
    skipBlock.Append(new IrBr(latch));
    FinishLoop(header, latch, exit, counter, pre, 6);

    Assert.That(LibraryCallRecognition.Run(module), Is.Zero);
    Assert.That(module.FindFunction("llvm.memset.p0.i32"), Is.Null);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void FillLoop_GivenAnEarlyExit_ThenItIsNotPromotedToMemset() {
    var module = new IrModule("test");
    var condition = new IrArgument(IrType.I1, 0);
    var fn = module.AddFunction(new IrFunction("fill_until", IrType.Void, [condition]));
    var pre = fn.AddBlock(new IrBasicBlock("pre"));
    var target = pre.Append(new IrAlloca(IrType.I8) { Count = 6 });
    var (header, body, latch, exit, counter) = LoopSkeleton(fn, pre, 6);
    var storeBlock = fn.AddBlock(new IrBasicBlock("store"));
    body.Append(new IrCondBr(condition, storeBlock, exit));
    var targetAt = storeBlock.Append(new IrGep(target, counter, IrType.I8));
    storeBlock.Append(new IrStore(new IrConstantInt(IrType.I8, 0x5a), targetAt));
    storeBlock.Append(new IrBr(latch));
    FinishLoop(header, latch, exit, counter, pre, 6);

    Assert.That(LibraryCallRecognition.Run(module), Is.Zero);
    Assert.That(module.FindFunction("llvm.memset.p0.i32"), Is.Null);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void FillLoop_GivenAnInnerCycle_ThenItIsNotPromotedToMemset() {
    var module = new IrModule("test");
    var repeat = new IrArgument(IrType.I1, 0);
    var fn = module.AddFunction(new IrFunction("fill_repeat", IrType.Void, [repeat]));
    var pre = fn.AddBlock(new IrBasicBlock("pre"));
    var target = pre.Append(new IrAlloca(IrType.I8) { Count = 6 });
    var (header, body, latch, exit, counter) = LoopSkeleton(fn, pre, 6);
    var targetAt = body.Append(new IrGep(target, counter, IrType.I8));
    body.Append(new IrStore(new IrConstantInt(IrType.I8, 0x5a), targetAt));
    body.Append(new IrCondBr(repeat, body, latch));
    FinishLoop(header, latch, exit, counter, pre, 6);

    Assert.That(LibraryCallRecognition.Run(module), Is.Zero);
    Assert.That(module.FindFunction("llvm.memset.p0.i32"), Is.Null);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void FillLoop_GivenAStoreInTheTestHeader_ThenItIsNotPromotedToMemset() {
    var module = new IrModule("test");
    var fn = module.AddFunction(new IrFunction("header_store", IrType.Void));
    var pre = fn.AddBlock(new IrBasicBlock("pre"));
    var target = pre.Append(new IrAlloca(IrType.I8) { Count = 7 });
    var (header, body, latch, exit, counter) = LoopSkeleton(fn, pre, 6);
    var targetAt = header.InsertBefore(new IrGep(target, counter, IrType.I8), header.Terminator!);
    header.InsertBefore(new IrStore(new IrConstantInt(IrType.I8, 0x5a), targetAt), header.Terminator!);
    body.Append(new IrBr(latch));
    FinishLoop(header, latch, exit, counter, pre, 6);

    Assert.That(LibraryCallRecognition.Run(module), Is.Zero);
    Assert.That(module.FindFunction("llvm.memset.p0.i32"), Is.Null);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void FillLoop_GivenAnInternalBranchAfterTheStore_ThenItStillBecomesMemset() {
    var module = new IrModule("test");
    var condition = new IrArgument(IrType.I1, 0);
    var fn = module.AddFunction(new IrFunction("fill_branch", IrType.Void, [condition]));
    var pre = fn.AddBlock(new IrBasicBlock("pre"));
    var target = pre.Append(new IrAlloca(IrType.I8) { Count = 6 });
    var (header, body, latch, exit, counter) = LoopSkeleton(fn, pre, 6);
    var left = fn.AddBlock(new IrBasicBlock("left"));
    var right = fn.AddBlock(new IrBasicBlock("right"));
    var targetAt = body.Append(new IrGep(target, counter, IrType.I8));
    body.Append(new IrStore(new IrConstantInt(IrType.I8, 0x5a), targetAt));
    body.Append(new IrCondBr(condition, left, right));
    left.Append(new IrBr(latch));
    right.Append(new IrBr(latch));
    FinishLoop(header, latch, exit, counter, pre, 6);

    Assert.That(LibraryCallRecognition.Run(module), Is.EqualTo(1));
    Assert.That((fn.AllInstructions.OfType<IrCall>().Single().Callee as IrFunction)?.Name,
      Is.EqualTo("llvm.memset.p0.i32"));
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
  public void CopyLoop_GivenTheSourceLoadInTheTestHeader_ThenItIsNotPromotedToMemcpy() {
    var module = new IrModule("test");
    var fn = module.AddFunction(new IrFunction("header_load", IrType.Void));
    var pre = fn.AddBlock(new IrBasicBlock("pre"));
    var source = pre.Append(new IrAlloca(IrType.I8) { Count = 7 });
    var target = pre.Append(new IrAlloca(IrType.I8) { Count = 6 });
    var (header, body, latch, exit, counter) = LoopSkeleton(fn, pre, 6);
    var sourceAt = header.InsertBefore(new IrGep(source, counter, IrType.I8), header.Terminator!);
    var value = header.InsertBefore(new IrLoad(IrType.I8, sourceAt), header.Terminator!);
    var targetAt = body.Append(new IrGep(target, counter, IrType.I8));
    body.Append(new IrStore(value, targetAt));
    body.Append(new IrBr(latch));
    FinishLoop(header, latch, exit, counter, pre, 6);

    Assert.That(LibraryCallRecognition.Run(module), Is.Zero);
    Assert.That(module.FindFunction("llvm.memcpy.p0.p0.i32"), Is.Null);
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
  public void Memcpy_GivenSixConstantBytes_ThenTheCallUsesThreeWordMoves() {
    var memcpy = new IrFunction("llvm.memcpy.p0.p0.i32", IrType.Void, [
      new IrArgument(IrType.Ptr, 0), new IrArgument(IrType.Ptr, 1),
      new IrArgument(IrType.I32, 2), new IrArgument(IrType.I1, 3),
    ]);
    var fn = new IrFunction("tiny", IrType.Void);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var source = entry.Append(new IrAlloca(IrType.I8) { Count = 6 });
    var target = entry.Append(new IrAlloca(IrType.I8) { Count = 6 });
    entry.Append(new IrCall(IrType.Void, memcpy, [target, source,
      new IrConstantInt(IrType.I32, 6), new IrConstantInt(IrType.I1, 0)]));
    entry.Append(new IrRet());

    Assert.That(MemoryRoutineSpecialization.Run(fn), Is.EqualTo(1));
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
  public void Memcpy_GivenSevenConstantBytes_ThenTheRuntimeFormSurvives() {
    var memcpy = new IrFunction("llvm.memcpy.p0.p0.i32", IrType.Void, [
      new IrArgument(IrType.Ptr, 0), new IrArgument(IrType.Ptr, 1),
      new IrArgument(IrType.I32, 2), new IrArgument(IrType.I1, 3),
    ]);
    var fn = new IrFunction("medium", IrType.Void);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var source = entry.Append(new IrAlloca(IrType.I8) { Count = 7 });
    var target = entry.Append(new IrAlloca(IrType.I8) { Count = 7 });
    entry.Append(new IrCall(IrType.Void, memcpy, [target, source,
      new IrConstantInt(IrType.I32, 7), new IrConstantInt(IrType.I1, 0)]));
    entry.Append(new IrRet());

    Assert.That(MemoryRoutineSpecialization.Run(fn), Is.Zero);
    Assert.That(fn.AllInstructions.OfType<IrCall>().Count(), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Memcpy_GivenVolatileFlag_ThenTheRuntimeFormSurvives() {
    var memcpy = new IrFunction("llvm.memcpy.p0.p0.i32", IrType.Void, [
      new IrArgument(IrType.Ptr, 0), new IrArgument(IrType.Ptr, 1),
      new IrArgument(IrType.I32, 2), new IrArgument(IrType.I1, 3),
    ]);
    var fn = new IrFunction("volatile_copy", IrType.Void);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var source = entry.Append(new IrAlloca(IrType.I8) { Count = 2 });
    var target = entry.Append(new IrAlloca(IrType.I8) { Count = 2 });
    entry.Append(new IrCall(IrType.Void, memcpy, [target, source,
      new IrConstantInt(IrType.I32, 2), new IrConstantInt(IrType.I1, 1)]));
    entry.Append(new IrRet());

    Assert.That(MemoryRoutineSpecialization.Run(fn), Is.Zero);
    Assert.That(fn.AllInstructions.OfType<IrCall>().Count(), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Memset_GivenEightConstantBytes_ThenTheCallUsesFourWordStores() {
    var memset = new IrFunction("llvm.memset.p0.i32", IrType.Void, [
      new IrArgument(IrType.Ptr, 0), new IrArgument(IrType.I8, 1),
      new IrArgument(IrType.I32, 2), new IrArgument(IrType.I1, 3),
    ]);
    var fn = new IrFunction("constant_fill", IrType.Void);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var target = entry.Append(new IrAlloca(IrType.I8) { Count = 8 });
    entry.Append(new IrCall(IrType.Void, memset, [target, new IrConstantInt(IrType.I8, 0x5a),
      new IrConstantInt(IrType.I32, 8), new IrConstantInt(IrType.I1, 0)]));
    entry.Append(new IrRet());

    Assert.That(MemoryRoutineSpecialization.Run(fn), Is.EqualTo(1));
    Assert.That(fn.AllInstructions.OfType<IrCall>(), Is.Empty);
    var stores = fn.AllInstructions.OfType<IrStore>().ToList();
    Assert.That(stores.Count, Is.EqualTo(4));
    Assert.That(stores.All(store => Equals(store.Value.Type, IrType.I16)), Is.True);
    Assert.That(stores.All(store => store.Value is IrConstantInt constant && constant.ZeroExtended == 0x5a5aUL), Is.True);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Memset_GivenFourDynamicBytes_ThenTheCallKeepsByteStores() {
    var memset = new IrFunction("llvm.memset.p0.i32", IrType.Void, [
      new IrArgument(IrType.Ptr, 0), new IrArgument(IrType.I8, 1),
      new IrArgument(IrType.I32, 2), new IrArgument(IrType.I1, 3),
    ]);
    var fn = new IrFunction("dynamic_fill", IrType.Void);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var fillCell = entry.Append(new IrAlloca(IrType.I8));
    var fill = entry.Append(new IrLoad(IrType.I8, fillCell));
    var target = entry.Append(new IrAlloca(IrType.I8) { Count = 4 });
    entry.Append(new IrCall(IrType.Void, memset, [target, fill,
      new IrConstantInt(IrType.I32, 4), new IrConstantInt(IrType.I1, 0)]));
    entry.Append(new IrRet());

    Assert.That(MemoryRoutineSpecialization.Run(fn), Is.EqualTo(1));
    Assert.That(fn.AllInstructions.OfType<IrCall>(), Is.Empty);
    var stores = fn.AllInstructions.OfType<IrStore>().ToList();
    Assert.That(stores.Count, Is.EqualTo(4));
    Assert.That(stores.All(store => ReferenceEquals(store.Value, fill)), Is.True,
      "a dynamic byte must not grow arithmetic just to synthesize a repeated word");
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Memset_GivenFiveDynamicBytes_ThenTheRuntimeFormSurvives() {
    var memset = new IrFunction("llvm.memset.p0.i32", IrType.Void, [
      new IrArgument(IrType.Ptr, 0), new IrArgument(IrType.I8, 1),
      new IrArgument(IrType.I32, 2), new IrArgument(IrType.I1, 3),
    ]);
    var fn = new IrFunction("dynamic_fill", IrType.Void);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var fillCell = entry.Append(new IrAlloca(IrType.I8));
    var fill = entry.Append(new IrLoad(IrType.I8, fillCell));
    var target = entry.Append(new IrAlloca(IrType.I8) { Count = 5 });
    entry.Append(new IrCall(IrType.Void, memset, [target, fill,
      new IrConstantInt(IrType.I32, 5), new IrConstantInt(IrType.I1, 0)]));
    entry.Append(new IrRet());

    Assert.That(MemoryRoutineSpecialization.Run(fn), Is.Zero);
    Assert.That(fn.AllInstructions.OfType<IrCall>().Count(), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
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
