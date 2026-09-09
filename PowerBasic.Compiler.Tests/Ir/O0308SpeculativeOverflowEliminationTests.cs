using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class O0308SpeculativeOverflowEliminationTests {

  [Test]
  public void GivenNarrowLoopValueAndInvariantAddend_WhenRun_ThenOnePreheaderGuardSelectsAnUncheckedClone() {
    var fixture = BuildCheckedLoop(IrBinaryOp.Add);

    var changed = SpeculativeOverflowElimination.Run(fixture.Function);

    Assert.That(changed, Is.EqualTo(1));
    Assert.That(fixture.Preheader.Terminator, Is.TypeOf<IrCondBr>());
    var chooser = (IrCondBr)fixture.Preheader.Terminator!;
    Assert.That(chooser.IfFalse, Is.SameAs(fixture.Header));
    Assert.That(fixture.Header.Label, Does.StartWith("ovf.slow."));
    Assert.That(chooser.IfTrue.Label, Does.StartWith("ovf.fast."));

    var bound = fixture.Preheader.Instructions.OfType<IrCmp>().Single();
    Assert.That(bound.Pred, Is.EqualTo(IrCmpPred.Sle));
    Assert.That(bound.Lhs, Is.SameAs(fixture.Invariant));
    Assert.That(((IrConstantInt)bound.Rhs).Value, Is.EqualTo(int.MaxValue - 3L));

    Assert.That(fixture.Guard.Terminator, Is.TypeOf<IrCondBr>(), "the fallback must keep the original Error 6 check");
    var fastGuard = fixture.Function.Blocks.Single(block => block.Label == "ovf.fast.guard");
    Assert.That(fastGuard.Terminator, Is.TypeOf<IrBr>(), "the guarded clone must bypass the per-iteration overflow branch");

    var joined = fixture.Exit.Instructions.OfType<IrPhi>().Single();
    Assert.That(joined.IncomingFrom(fixture.Header), Is.SameAs(fixture.Counter));
    Assert.That(joined.IncomingFrom(chooser.IfTrue), Is.Not.Null, "the fast-loop counter must join the value visible after the loop");
    Assert.That(SpeculativeOverflowElimination.Run(fixture.Function), Is.Zero, "the pass-manager fixpoint must not version either copy again");
    Assert.That(IrVerifier.Verify(fixture.Function), Is.Empty);
  }

  [Test]
  public void GivenInvariantMinuendAndNarrowLoopSubtrahend_WhenRun_ThenTheLowerSafetyBoundIsHoisted() {
    var fixture = BuildCheckedLoop(IrBinaryOp.Sub, invariantOnLeft: true);

    var changed = SpeculativeOverflowElimination.Run(fixture.Function);

    Assert.That(changed, Is.EqualTo(1));
    var bound = fixture.Preheader.Instructions.OfType<IrCmp>().Single();
    Assert.That(bound.Pred, Is.EqualTo(IrCmpPred.Sge));
    Assert.That(bound.Lhs, Is.SameAs(fixture.Invariant));
    Assert.That(((IrConstantInt)bound.Rhs).Value, Is.EqualTo(int.MinValue + 3L));
    Assert.That(IrVerifier.Verify(fixture.Function), Is.Empty);
  }

  [Test]
  public void GivenStaticallySafeConstantAddend_WhenRun_ThenO0219IsLeftToRemoveTheCheckWithoutCloning() {
    var fixture = BuildCheckedLoop(IrBinaryOp.Add, constantInvariant: 1);

    var changed = SpeculativeOverflowElimination.Run(fixture.Function);

    Assert.That(changed, Is.Zero);
    Assert.That(fixture.Preheader.Terminator, Is.TypeOf<IrBr>());
    Assert.That(fixture.Guard.Terminator, Is.TypeOf<IrCondBr>());
    Assert.That(fixture.Function.Blocks.Any(block => block.Label.StartsWith("ovf.fast.", StringComparison.Ordinal)), Is.False);
    Assert.That(IrVerifier.Verify(fixture.Function), Is.Empty);
  }

  [Test]
  public void GivenAnEarlyExitInsideTheLoop_WhenRun_ThenTheLoopIsNotVersioned() {
    var breakNow = new IrArgument(IrType.I1, 1, "breakNow");
    var fixture = BuildCheckedLoop(IrBinaryOp.Add, extraArgument: breakNow);
    fixture.Continuation.Terminator!.EraseFromParent();
    fixture.Continuation.Append(new IrCondBr(breakNow, fixture.Exit, fixture.Latch));

    var changed = SpeculativeOverflowElimination.Run(fixture.Function);

    Assert.That(changed, Is.Zero);
    Assert.That(fixture.Preheader.Terminator, Is.TypeOf<IrBr>());
    Assert.That(IrVerifier.Verify(fixture.Function), Is.Empty);
  }

  private sealed record Fixture(
    IrFunction Function,
    IrBasicBlock Preheader,
    IrBasicBlock Header,
    IrBasicBlock Guard,
    IrBasicBlock Continuation,
    IrBasicBlock Latch,
    IrBasicBlock Exit,
    IrPhi Counter,
    IrValue Invariant);

  private static Fixture BuildCheckedLoop(IrBinaryOp op, bool invariantOnLeft = false,
      long? constantInvariant = null, IrArgument? extraArgument = null) {
    var invariantArgument = constantInvariant is null ? new IrArgument(IrType.I32, 0, "offset") : null;
    var parameters = new List<IrArgument>();
    if (invariantArgument is not null)
      parameters.Add(invariantArgument);
    if (extraArgument is not null)
      parameters.Add(extraArgument);

    var fn = new IrFunction("f", IrType.I16, parameters);
    var error = new IrFunction("rt_error", IrType.Void, [new IrArgument(IrType.I32, 0)]);
    var preheader = fn.CreateBlock("preheader");
    var header = fn.CreateBlock("header");
    var guard = fn.CreateBlock("guard");
    var trap = fn.CreateBlock("trap");
    var continuation = fn.CreateBlock("continue");
    var latch = fn.CreateBlock("latch");
    var exit = fn.CreateBlock("exit");

    new IrBuilder(preheader).Br(header);
    var bh = new IrBuilder(header);
    var counter = bh.Phi(IrType.I16);
    bh.CondBr(bh.Cmp(IrCmpPred.Slt, counter, new IrConstantInt(IrType.I16, 4)), guard, exit);

    var bg = new IrBuilder(guard);
    var varying = bg.Cast(IrCastOp.SExt, counter, IrType.I32);
    IrValue invariant = constantInvariant is { } constant
      ? new IrConstantInt(IrType.I32, constant)
      : invariantArgument!;
    var left = invariantOnLeft ? invariant : varying;
    var right = invariantOnLeft ? varying : invariant;
    var arithmetic = bg.Binary(op, left, right);
    var operandSigns = bg.Xor(left, right);
    IrValue interesting = op == IrBinaryOp.Add
      ? bg.Xor(operandSigns, new IrConstantInt(IrType.I32, -1))
      : operandSigns;
    var movedAway = bg.Xor(arithmetic, left);
    var overflowBits = bg.And(interesting, movedAway);
    var overflowed = bg.Cmp(IrCmpPred.Slt, overflowBits, new IrConstantInt(IrType.I32, 0));
    bg.CondBr(overflowed, trap, continuation);

    var bt = new IrBuilder(trap);
    bt.Call(IrType.Void, error, IrBuilder.ConstI32(6));
    bt.Br(continuation);
    new IrBuilder(continuation).Br(latch);
    var bl = new IrBuilder(latch);
    var next = bl.Add(counter, new IrConstantInt(IrType.I16, 1));
    bl.Br(header);
    counter.AddIncoming(new IrConstantInt(IrType.I16, 0), preheader);
    counter.AddIncoming(next, latch);
    new IrBuilder(exit).Ret(counter);

    return new(fn, preheader, header, guard, continuation, latch, exit, counter, invariant);
  }
}
