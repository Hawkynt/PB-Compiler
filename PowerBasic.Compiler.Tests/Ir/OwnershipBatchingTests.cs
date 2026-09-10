using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0292: batching repeated dynamic-string ownership operations.</summary>
[TestFixture]
public sealed class OwnershipBatchingTests {

  private static IrFunction DupFunction()
    => new("rt_str_dup", IrType.Ptr, [new IrArgument(IrType.Ptr, 0)]);

  private static IrFunction FreeFunction()
    => new("rt_str_free", IrType.Void, [new IrArgument(IrType.Ptr, 0)]);

  [Test]
  public void Run_CollapsesRepeatedStraightLineAssignmentOfSameHandle() {
    var source = new IrArgument(IrType.Ptr, 0, "source");
    var initial = new IrArgument(IrType.Ptr, 1, "initial");
    var fn = new IrFunction("f", IrType.Void, [source, initial]);
    var entry = fn.CreateBlock("entry");
    var b = new IrBuilder(entry);
    var dupFn = DupFunction();
    var freeFn = FreeFunction();

    var first = b.Call(IrType.Ptr, dupFn, source);
    b.Call(IrType.Void, freeFn, initial);
    var second = b.Call(IrType.Ptr, dupFn, source);
    var freeFirst = b.Call(IrType.Void, freeFn, first);
    var finalFree = b.Call(IrType.Void, freeFn, second);
    b.Ret();

    Assert.That(OwnershipBatching.Run(fn), Is.EqualTo(1));
    Assert.That(first.Parent, Is.SameAs(entry));
    Assert.That(second.Parent, Is.Null);
    Assert.That(freeFirst.Parent, Is.Null);
    Assert.That(finalFree.GetOperand(1), Is.SameAs(first));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Run_DoesNotCollapseStraightLineAssignmentWhenIntermediateOwnerIsRead() {
    var source = new IrArgument(IrType.Ptr, 0, "source");
    var initial = new IrArgument(IrType.Ptr, 1, "initial");
    var fn = new IrFunction("f", IrType.Void, [source, initial]);
    var entry = fn.CreateBlock("entry");
    var b = new IrBuilder(entry);
    var dupFn = DupFunction();
    var freeFn = FreeFunction();

    var first = b.Call(IrType.Ptr, dupFn, source);
    b.Call(IrType.Void, freeFn, initial);
    var observed = b.Call(IrType.Ptr, dupFn, first);
    b.Call(IrType.Void, freeFn, observed);
    var second = b.Call(IrType.Ptr, dupFn, source);
    b.Call(IrType.Void, freeFn, first);
    b.Call(IrType.Void, freeFn, second);
    b.Ret();

    Assert.That(OwnershipBatching.Run(fn), Is.EqualTo(0));
    Assert.That(first.Parent, Is.SameAs(entry));
    Assert.That(second.Parent, Is.SameAs(entry));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Run_DoesNotCollapseStraightLineAssignmentAcrossOpaqueSourceUse() {
    var source = new IrArgument(IrType.Ptr, 0, "source");
    var initial = new IrArgument(IrType.Ptr, 1, "initial");
    var fn = new IrFunction("f", IrType.Void, [source, initial]);
    var entry = fn.CreateBlock("entry");
    var b = new IrBuilder(entry);
    var dupFn = DupFunction();
    var freeFn = FreeFunction();
    var opaque = new IrFunction("opaque", IrType.Void, [new IrArgument(IrType.Ptr, 0)]);

    var first = b.Call(IrType.Ptr, dupFn, source);
    b.Call(IrType.Void, freeFn, initial);
    b.Call(IrType.Void, opaque, source);              // could mutate/consume the handle behind the pointer
    var second = b.Call(IrType.Ptr, dupFn, source);
    b.Call(IrType.Void, freeFn, first);
    b.Call(IrType.Void, freeFn, second);
    b.Ret();

    Assert.That(OwnershipBatching.Run(fn), Is.EqualTo(0));
    Assert.That(first.Parent, Is.SameAs(entry));
    Assert.That(second.Parent, Is.SameAs(entry));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Run_DoesNotCollapseWhenRawHandleIdentityIsObservedThroughPhi() {
    var source = new IrArgument(IrType.Ptr, 0, "source");
    var initial = new IrArgument(IrType.Ptr, 1, "initial");
    var fn = new IrFunction("f", IrType.I16, [source, initial]);
    var entry = fn.CreateBlock("entry");
    var join = fn.CreateBlock("join");
    var b = new IrBuilder(entry);
    var dupFn = DupFunction();
    var freeFn = FreeFunction();

    var first = b.Call(IrType.Ptr, dupFn, source);
    b.Call(IrType.Void, freeFn, initial);
    var second = b.Call(IrType.Ptr, dupFn, source);
    b.Call(IrType.Void, freeFn, first);
    b.Br(join);

    var bj = new IrBuilder(join);
    var carried = bj.Phi(IrType.Ptr);
    carried.AddIncoming(second, entry);
    var raw = bj.Cast(IrCastOp.PtrToInt, carried, IrType.I16);
    bj.Call(IrType.Void, freeFn, carried);
    bj.Ret(raw);

    Assert.That(OwnershipBatching.Run(fn), Is.EqualTo(0));
    Assert.That(first.Parent, Is.SameAs(entry));
    Assert.That(second.Parent, Is.SameAs(entry));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Run_HoistsInvariantLoopOwnershipIntoPreheader() {
    var loop = BuildLoop(initialCounter: 0, limit: 4);

    Assert.That(OwnershipBatching.Run(loop.Fn), Is.EqualTo(1));

    Assert.That(loop.Duplicate.Parent, Is.SameAs(loop.Entry));
    Assert.That(loop.Release.Parent, Is.SameAs(loop.Entry));
    Assert.That(loop.Release.GetOperand(1), Is.SameAs(loop.InitialOwner));
    Assert.That(loop.OwnerPhi.Parent, Is.Null);
    Assert.That(loop.ExitRelease.GetOperand(1), Is.SameAs(loop.Duplicate));
    Assert.That(IndexIn(loop.Entry, loop.Duplicate), Is.LessThan(IndexIn(loop.Entry, loop.Release)));
    Assert.That(IndexIn(loop.Entry, loop.Release), Is.LessThan(IndexIn(loop.Entry, loop.Entry.Terminator!)));
    Assert.That(IrVerifier.Verify(loop.Fn), Is.Empty);
  }

  [Test]
  public void Run_DoesNotHoistOwnershipFromZeroTripLoop() {
    var loop = BuildLoop(initialCounter: 4, limit: 4);

    Assert.That(OwnershipBatching.Run(loop.Fn), Is.EqualTo(0));
    Assert.That(loop.Duplicate.Parent, Is.SameAs(loop.Body));
    Assert.That(loop.Release.Parent, Is.SameAs(loop.Body));
    Assert.That(loop.OwnerPhi.Parent, Is.SameAs(loop.Header));
    Assert.That(IrVerifier.Verify(loop.Fn), Is.Empty);
  }

  [Test]
  public void Run_DoesNotHoistOwnershipAcrossSideExit() {
    var source = new IrArgument(IrType.Ptr, 0, "source");
    var initialOwner = new IrArgument(IrType.Ptr, 1, "initial");
    var leave = new IrArgument(IrType.I1, 2, "leave");
    var fn = new IrFunction("f", IrType.Void, [source, initialOwner, leave]);
    var entry = fn.CreateBlock("entry");
    var header = fn.CreateBlock("header");
    var body = fn.CreateBlock("body");
    var latch = fn.CreateBlock("latch");
    var exit = fn.CreateBlock("exit");
    var dupFn = DupFunction();
    var freeFn = FreeFunction();

    new IrBuilder(entry).Br(header);
    var bh = new IrBuilder(header);
    var counter = bh.Phi(IrType.I16);
    var owner = bh.Phi(IrType.Ptr);
    bh.CondBr(bh.Cmp(IrCmpPred.Slt, counter, new IrConstantInt(IrType.I16, 4)), body, exit);

    var bb = new IrBuilder(body);
    var duplicate = bb.Call(IrType.Ptr, dupFn, source);
    var release = bb.Call(IrType.Void, freeFn, owner);
    bb.CondBr(leave, exit, latch);                    // EXIT FOR-shaped side exit

    var bl = new IrBuilder(latch);
    var next = bl.Add(counter, new IrConstantInt(IrType.I16, 1));
    bl.Br(header);
    counter.AddIncoming(new IrConstantInt(IrType.I16, 0), entry);
    counter.AddIncoming(next, latch);
    owner.AddIncoming(initialOwner, entry);
    owner.AddIncoming(duplicate, latch);

    new IrBuilder(exit).Ret();

    Assert.That(OwnershipBatching.Run(fn), Is.EqualTo(0));
    Assert.That(duplicate.Parent, Is.SameAs(body));
    Assert.That(release.Parent, Is.SameAs(body));
    Assert.That(owner.Parent, Is.SameAs(header));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  private sealed record LoopFixture(
    IrFunction Fn,
    IrBasicBlock Entry,
    IrBasicBlock Header,
    IrBasicBlock Body,
    IrValue InitialOwner,
    IrPhi OwnerPhi,
    IrCall Duplicate,
    IrCall Release,
    IrCall ExitRelease);

  private static LoopFixture BuildLoop(long initialCounter, long limit) {
    var source = new IrArgument(IrType.Ptr, 0, "source");
    var initialOwner = new IrArgument(IrType.Ptr, 1, "initial");
    var fn = new IrFunction("f", IrType.Void, [source, initialOwner]);
    var entry = fn.CreateBlock("entry");
    var header = fn.CreateBlock("header");
    var body = fn.CreateBlock("body");
    var exit = fn.CreateBlock("exit");
    var dupFn = DupFunction();
    var freeFn = FreeFunction();

    new IrBuilder(entry).Br(header);

    var bh = new IrBuilder(header);
    var counter = bh.Phi(IrType.I16);
    var owner = bh.Phi(IrType.Ptr);
    bh.CondBr(bh.Cmp(IrCmpPred.Slt, counter, new IrConstantInt(IrType.I16, limit)), body, exit);

    var bb = new IrBuilder(body);
    var duplicate = bb.Call(IrType.Ptr, dupFn, source);
    var release = bb.Call(IrType.Void, freeFn, owner);
    var next = bb.Add(counter, new IrConstantInt(IrType.I16, 1));
    bb.Br(header);

    counter.AddIncoming(new IrConstantInt(IrType.I16, initialCounter), entry);
    counter.AddIncoming(next, body);
    owner.AddIncoming(initialOwner, entry);
    owner.AddIncoming(duplicate, body);

    var be = new IrBuilder(exit);
    var exitRelease = be.Call(IrType.Void, freeFn, owner);
    be.Ret();

    return new(fn, entry, header, body, initialOwner, owner, duplicate, release, exitRelease);
  }

  private static int IndexIn(IrBasicBlock block, IrInstruction instruction) {
    for (var i = 0; i < block.Instructions.Count; ++i)
      if (ReferenceEquals(block.Instructions[i], instruction))
        return i;
    return -1;
  }
}
