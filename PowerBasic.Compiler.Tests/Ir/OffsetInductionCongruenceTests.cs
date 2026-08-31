using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class OffsetInductionCongruenceTests {

  private sealed record LoopFixture(
    IrFunction Fn,
    IrBasicBlock Header,
    IrBasicBlock Body,
    IrBasicBlock Exit,
    IrPhi Leader,
    IrPhi Offset,
    IrBinary NextLeader,
    IrBinary NextOffset);

  private static LoopFixture CreateLoop(long leaderStart, long offsetStart, long leaderStep = 1, long offsetStep = 1, IrType? type = null) {
    type ??= IrType.I16;
    var fn = new IrFunction("f", IrType.Void);
    var entry = fn.CreateBlock("entry");
    var head = fn.CreateBlock("head");
    var body = fn.CreateBlock("body");
    var exit = fn.CreateBlock("exit");

    entry.Append(new IrBr(head));
    var i = head.AppendPhi(new IrPhi(type) { Name = "i" });
    var j = head.AppendPhi(new IrPhi(type) { Name = "j" });
    var keepGoing = head.Append(new IrCmp(IrCmpPred.Ne, i, new IrConstantInt(type, 3)));
    head.Append(new IrCondBr(keepGoing, body, exit));

    var nextI = body.Append(new IrBinary(IrBinaryOp.Add, i, new IrConstantInt(type, leaderStep)));
    var nextJ = body.Append(new IrBinary(IrBinaryOp.Add, j, new IrConstantInt(type, offsetStep)));
    body.Append(new IrBr(head));
    exit.Append(new IrRet());

    i.AddIncoming(new IrConstantInt(type, leaderStart), entry);
    i.AddIncoming(nextI, body);
    j.AddIncoming(new IrConstantInt(type, offsetStart), entry);
    j.AddIncoming(nextJ, body);

    return new(fn, head, body, exit, i, j, nextI, nextJ);
  }

  [Test]
  public void Phis_GivenConstantOffsetAndEqualStep_ThenOffsetUseCancelsBackToLeader() {
    var loop = CreateLoop(leaderStart: 0, offsetStart: 100);
    var sink = new IrArgument(IrType.Ptr, 0, "sink");
    // IrFunction arguments are immutable, so use a global-shaped pointer value as the observable sink.
    var sinkValue = new IrGlobal("sink", IrType.I16);
    var normalized = new IrBinary(IrBinaryOp.Sub, loop.Offset, new IrConstantInt(IrType.I16, 100));
    loop.Body.InsertBefore(normalized, loop.NextLeader);
    var store = new IrStore(normalized, sinkValue);
    loop.Body.InsertBefore(store, loop.NextLeader);

    Assert.That(IrVerifier.Verify(loop.Fn), Is.Empty);

    Assert.That(PhiCongruence.Run(loop.Fn), Is.EqualTo(1));
    InstCombine.Run(loop.Fn);
    Dce.Run(loop.Fn);

    Assert.That(IrVerifier.Verify(loop.Fn), Is.Empty);
    Assert.That(loop.Offset.Parent, Is.Null);
    Assert.That(store.Value, Is.SameAs(loop.Leader), "(j - 100) should become i once j is represented as i + 100");
    Assert.That(loop.Fn.AllInstructions.OfType<IrPhi>(), Has.Count.EqualTo(1));
  }

  [Test]
  public void Phis_GivenOffsetValueReadAfterLoop_ThenDerivedHeaderValuePreservesExitValue() {
    var fn = new IrFunction("f", IrType.I16);
    var entry = fn.CreateBlock("entry");
    var head = fn.CreateBlock("head");
    var body = fn.CreateBlock("body");
    var exit = fn.CreateBlock("exit");

    entry.Append(new IrBr(head));
    var i = head.AppendPhi(new IrPhi(IrType.I16) { Name = "i" });
    var j = head.AppendPhi(new IrPhi(IrType.I16) { Name = "j" });
    var test = head.Append(new IrCmp(IrCmpPred.Slt, i, new IrConstantInt(IrType.I16, 3)));
    head.Append(new IrCondBr(test, body, exit));
    var nextI = body.Append(new IrBinary(IrBinaryOp.Add, i, new IrConstantInt(IrType.I16, 1)));
    var nextJ = body.Append(new IrBinary(IrBinaryOp.Add, j, new IrConstantInt(IrType.I16, 1)));
    body.Append(new IrBr(head));
    var ret = exit.Append(new IrRet(j));

    i.AddIncoming(new IrConstantInt(IrType.I16, 0), entry);
    i.AddIncoming(nextI, body);
    j.AddIncoming(new IrConstantInt(IrType.I16, 100), entry);
    j.AddIncoming(nextJ, body);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);

    Assert.That(PhiCongruence.Run(fn), Is.EqualTo(1));
    Dce.Run(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(j.Parent, Is.Null);
    var derived = ret.Value as IrBinary;
    Assert.That(derived, Is.Not.Null);
    Assert.That(derived!.Parent, Is.SameAs(head), "the replacement must dominate both loop and exit uses");
    Assert.That(derived.Lhs, Is.SameAs(i));
    Assert.That(((IrConstantInt)derived.Rhs).Value, Is.EqualTo(100));
    Assert.That(nextJ.Parent, Is.Null, "the second loop-carried update becomes dead");
  }

  [Test]
  public void Phis_GivenStartsAcrossSignedWrap_ThenOffsetIsComputedModuloBitWidth() {
    var loop = CreateLoop(leaderStart: 32760, offsetStart: -32766, leaderStep: 5, offsetStep: 5);

    Assert.That(IrVerifier.Verify(loop.Fn), Is.Empty);

    Assert.That(PhiCongruence.Run(loop.Fn), Is.EqualTo(1));

    Assert.That(IrVerifier.Verify(loop.Fn), Is.Empty);
    Assert.That(loop.Offset.Parent, Is.Null);
    var derived = loop.Header.Instructions.OfType<IrBinary>()
      .Single(b => ReferenceEquals(b.Lhs, loop.Leader) && b.Rhs is IrConstantInt);
    Assert.That(((IrConstantInt)derived.Rhs).ZeroExtended, Is.EqualTo(10),
      "32770 - 32760 is ten in the i16 bit domain even though 32770 is represented as -32766");
  }

  [Test]
  public void Phis_GivenOffsetPhiFeedsAnotherPhi_ThenLocalPassRefusesEdgeRewrite() {
    var loop = CreateLoop(leaderStart: 0, offsetStart: 100);
    var exitPhi = loop.Exit.InsertPhi(new IrPhi(IrType.I16));
    exitPhi.AddIncoming(loop.Offset, loop.Header);

    Assert.That(IrVerifier.Verify(loop.Fn), Is.Empty);

    Assert.That(PhiCongruence.Run(loop.Fn), Is.Zero);

    Assert.That(IrVerifier.Verify(loop.Fn), Is.Empty);
    Assert.That(loop.Offset.Parent, Is.SameAs(loop.Header));
  }
}
