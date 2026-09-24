using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>Function-local memory SSA construction and alias-aware clobber walking.</summary>
[TestFixture]
public sealed class MemorySsaTests {

  [Test]
  public void Build_PlacesPhiAtDiamondJoin() {
    var condition = new IrArgument(IrType.I1, 0, "c");
    var fn = new IrFunction("f", IrType.I16, [condition]);
    var entry = fn.CreateBlock("entry");
    var left = fn.CreateBlock("left");
    var right = fn.CreateBlock("right");
    var join = fn.CreateBlock("join");

    var b = new IrBuilder(entry);
    var slot = b.Alloca(IrType.I16);
    b.CondBr(condition, left, right);

    b.Position(left);
    var leftStore = b.Store(IrBuilder.ConstInt(IrType.I16, 1), slot);
    b.Br(join);

    b.Position(right);
    var rightStore = b.Store(IrBuilder.ConstInt(IrType.I16, 2), slot);
    b.Br(join);

    b.Position(join);
    var load = b.Load(IrType.I16, slot);
    b.Ret(load);

    var memory = IrMemorySsa.Build(fn);
    var phi = memory.PhiFor(join);

    Assert.That(phi, Is.Not.Null);
    Assert.That(phi!.Incoming, Has.Count.EqualTo(2));
    Assert.That(((IrMemoryDef)phi.IncomingFrom(left)!).Instruction, Is.SameAs(leftStore));
    Assert.That(((IrMemoryDef)phi.IncomingFrom(right)!).Instruction, Is.SameAs(rightStore));
    Assert.That(((IrMemoryUse)memory.AccessFor(load)!).DefiningAccess, Is.SameAs(phi));
    Assert.That(memory.GetClobberingAccess(load), Is.SameAs(phi));
  }

  [Test]
  public void ClobberWalker_SkipsNonAliasingLoopCarriedStore() {
    var condition = new IrArgument(IrType.I1, 0, "again");
    var fn = new IrFunction("f", IrType.I16, [condition]);
    var entry = fn.CreateBlock("entry");
    var header = fn.CreateBlock("header");
    var latch = fn.CreateBlock("latch");
    var exit = fn.CreateBlock("exit");

    var b = new IrBuilder(entry);
    var observed = b.Alloca(IrType.I16);
    var other = b.Alloca(IrType.I16);
    var initialStore = b.Store(IrBuilder.ConstInt(IrType.I16, 7), observed);
    b.Br(header);

    b.Position(header);
    var load = b.Load(IrType.I16, observed);
    b.CondBr(condition, latch, exit);

    b.Position(latch);
    b.Store(IrBuilder.ConstInt(IrType.I16, 9), other);
    b.Br(header);

    b.Position(exit);
    b.Ret(load);

    var memory = IrMemorySsa.Build(fn);
    var phi = memory.PhiFor(header);
    var clobber = memory.GetClobberingAccess(load);

    Assert.That(phi, Is.Not.Null, "the back-edge write must create a memory merge");
    Assert.That(((IrMemoryUse)memory.AccessFor(load)!).DefiningAccess, Is.SameAs(phi));
    Assert.That(clobber, Is.TypeOf<IrMemoryDef>());
    Assert.That(((IrMemoryDef)clobber).Instruction, Is.SameAs(initialStore));
  }

  [Test]
  public void ClobberWalker_KeepsPhiForAliasingLoopCarriedStore() {
    var condition = new IrArgument(IrType.I1, 0, "again");
    var fn = new IrFunction("f", IrType.I16, [condition]);
    var entry = fn.CreateBlock("entry");
    var header = fn.CreateBlock("header");
    var latch = fn.CreateBlock("latch");
    var exit = fn.CreateBlock("exit");

    var b = new IrBuilder(entry);
    var observed = b.Alloca(IrType.I16);
    b.Store(IrBuilder.ConstInt(IrType.I16, 7), observed);
    b.Br(header);

    b.Position(header);
    var load = b.Load(IrType.I16, observed);
    b.CondBr(condition, latch, exit);

    b.Position(latch);
    b.Store(IrBuilder.ConstInt(IrType.I16, 9), observed);
    b.Br(header);

    b.Position(exit);
    b.Ret(load);

    var memory = IrMemorySsa.Build(fn);
    var phi = memory.PhiFor(header);

    Assert.That(phi, Is.Not.Null);
    Assert.That(memory.GetClobberingAccess(load), Is.SameAs(phi));
  }
  [Test]
  public void Build_GivenEffectFreeCall_ThenItDoesNotVersionMemory() {
    var fn = new IrFunction("f", IrType.I16);
    var sqrt = new IrFunction("llvm.sqrt.f64", IrType.F64, [new IrArgument(IrType.F64, 0)]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    var slot = b.Alloca(IrType.I16);
    var store = b.Store(new IrConstantInt(IrType.I16, 7), slot);
    var call = b.Call(IrType.F64, sqrt, new IrConstantFloat(IrType.F64, 4));
    var load = b.Load(IrType.I16, slot);
    b.Ret(load);

    var memory = IrMemorySsa.Build(fn);
    var storeAccess = memory.AccessFor(store);

    Assert.Multiple(() => {
      Assert.That(call.Parent, Is.Not.Null);
      Assert.That(memory.AccessFor(call), Is.Null);
      Assert.That(storeAccess, Is.TypeOf<IrMemoryDef>());
      Assert.That(((IrMemoryUse)memory.AccessFor(load)!).DefiningAccess, Is.SameAs(storeAccess));
      Assert.That(memory.GetClobberingAccess(load), Is.SameAs(storeAccess));
    });
  }

  [Test]
  public void Build_GivenOpaqueCall_ThenItVersionsMemory() {
    var fn = new IrFunction("f", IrType.I16);
    var opaque = new IrFunction("opaque", IrType.Void);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    var slot = b.Alloca(IrType.I16);
    b.Store(new IrConstantInt(IrType.I16, 7), slot);
    var call = b.Call(IrType.Void, opaque);
    var load = b.Load(IrType.I16, slot);
    b.Ret(load);

    var memory = IrMemorySsa.Build(fn);
    var callAccess = memory.AccessFor(call);

    Assert.Multiple(() => {
      Assert.That(callAccess, Is.TypeOf<IrMemoryDef>());
      Assert.That(((IrMemoryUse)memory.AccessFor(load)!).DefiningAccess, Is.SameAs(callAccess));
      Assert.That(memory.GetClobberingAccess(load), Is.SameAs(callAccess));
    });
  }

  [Test]
  public void Build_GivenBorrowedAndConsumingStringQueries_ThenOwnershipDefinesMemoryOnlyForTheConsumer() {
    var handle = new IrArgument(IrType.Ptr, 0, "handle");
    var borrow = new IrFunction("rt_str_len_borrow", IrType.I32, [new IrArgument(IrType.Ptr, 0)]);
    var consume = new IrFunction("rt_str_len", IrType.I32, [new IrArgument(IrType.Ptr, 0)]);
    var fn = new IrFunction("f", IrType.Void, [handle]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    var borrowedLength = b.Call(IrType.I32, borrow, handle);
    var consumedLength = b.Call(IrType.I32, consume, handle);
    b.Ret();

    var memory = IrMemorySsa.Build(fn);

    Assert.Multiple(() => {
      Assert.That(memory.AccessFor(borrowedLength), Is.TypeOf<IrMemoryUse>(),
        "borrowed LEN reads the descriptor but keeps the stable handle alive");
      Assert.That(memory.AccessFor(consumedLength), Is.TypeOf<IrMemoryDef>(),
        "ordinary LEN releases the owned handle and therefore changes memory lifetime");
      Assert.That(((IrMemoryDef)memory.AccessFor(consumedLength)!).DefiningAccess,
        Is.SameAs(((IrMemoryUse)memory.AccessFor(borrowedLength)!).DefiningAccess));
    });
  }

  [Test]
  public void Build_GivenNonVolatileMemcpy_ThenItStillCreatesAModRefDefinitionWithoutAVolatileBarrierFlag() {
    var memcpy = new IrFunction("llvm.memcpy.p0.p0.i32", IrType.Void, [
      new IrArgument(IrType.Ptr, 0), new IrArgument(IrType.Ptr, 1),
      new IrArgument(IrType.I32, 2), new IrArgument(IrType.I1, 3),
    ]);
    var fn = new IrFunction("f", IrType.Void);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    var dst = fn.Entry!.Append(new IrAlloca(IrType.I8) { Count = 4 });
    var src = fn.Entry!.Append(new IrAlloca(IrType.I8) { Count = 4 });
    var copy = b.Call(IrType.Void, memcpy,
      dst, src, new IrConstantInt(IrType.I32, 4), IrBuilder.ConstBool(false));
    b.Ret();

    var effects = IrEffects.ForInstruction(copy);
    var memory = IrMemorySsa.Build(fn);

    Assert.Multiple(() => {
      Assert.That(effects.Effects, Is.EqualTo(IrEffectKind.ReadsMemory | IrEffectKind.WritesMemory));
      Assert.That(memory.AccessFor(copy), Is.TypeOf<IrMemoryDef>());
    });
  }


  [Test]
  public void SharedMemorySsa_GivenInternalPureAndReadOnlyCalls_ThenUsesCachedModuleModRefFacts() {
    var module = new IrModule("modref");
    var global = module.AddGlobal(new IrGlobalVariable("g", IrType.I16));

    var pure = module.AddFunction(new IrFunction("pure", IrType.I16));
    var pureBuilder = new IrBuilder(pure.CreateBlock("entry"));
    pureBuilder.Ret(new IrConstantInt(IrType.I16, 3));

    var reader = module.AddFunction(new IrFunction("reader", IrType.I16));
    var readerBuilder = new IrBuilder(reader.CreateBlock("entry"));
    readerBuilder.Ret(readerBuilder.Load(IrType.I16, global));

    var writer = module.AddFunction(new IrFunction("writer", IrType.Void));
    var writerBuilder = new IrBuilder(writer.CreateBlock("entry"));
    writerBuilder.Store(new IrConstantInt(IrType.I16, 9), global);
    writerBuilder.Ret();

    var caller = module.AddFunction(new IrFunction("caller", IrType.I16));
    var b = new IrBuilder(caller.CreateBlock("entry"));
    var initialStore = b.Store(new IrConstantInt(IrType.I16, 7), global);
    var pureCall = b.Call(IrType.I16, pure);
    var readCall = b.Call(IrType.I16, reader);
    var loadBeforeWrite = b.Load(IrType.I16, global);
    var writeCall = b.Call(IrType.Void, writer);
    var loadAfterWrite = b.Load(IrType.I16, global);
    b.Ret(loadAfterWrite);

    var moduleAnalyses = new IrModuleAnalysisManager(module);
    var functionAnalyses = new IrAnalysisManager(caller, moduleAnalyses);
    var memory = functionAnalyses.Get(IrAnalyses.MemorySsa);

    Assert.Multiple(() => {
      Assert.That(memory.AccessFor(pureCall), Is.Null,
        "a direct internal function proven memory-free is not a MemorySSA wall");
      Assert.That(memory.AccessFor(readCall), Is.TypeOf<IrMemoryUse>(),
        "an internal read-only function observes memory without defining a new version");
      Assert.That(memory.AccessFor(writeCall), Is.TypeOf<IrMemoryDef>(),
        "an internal writer still creates a memory definition");
      Assert.That(memory.GetClobberingAccess(loadBeforeWrite), Is.SameAs(memory.AccessFor(initialStore)));
      Assert.That(memory.GetClobberingAccess(loadAfterWrite), Is.SameAs(memory.AccessFor(writeCall)));
      Assert.That(moduleAnalyses.IsCached(IrModuleAnalyses.FunctionSummaries), Is.True);
    });
  }

  [Test]
  public void StandaloneMemorySsa_GivenInternalCallWithoutModuleOwnership_ThenKeepsItOpaque() {
    var callee = new IrFunction("internal", IrType.Void);
    new IrBuilder(callee.CreateBlock("entry")).Ret();
    var caller = new IrFunction("caller", IrType.Void);
    var b = new IrBuilder(caller.CreateBlock("entry"));
    var call = b.Call(IrType.Void, callee);
    b.Ret();

    var memory = IrMemorySsa.Build(caller);

    Assert.That(memory.AccessFor(call), Is.TypeOf<IrMemoryDef>(),
      "a standalone function analysis has no closed module proof and must fail conservative");
  }


}
