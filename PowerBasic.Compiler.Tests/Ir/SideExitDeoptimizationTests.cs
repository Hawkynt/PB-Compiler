using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// O0310 — side exits are mostly a state-reconstruction problem. These tests build the shape directly
/// so they can pin the exact boundary: a store and accumulator update happen before the guard, then the
/// failed iteration must resume in the generic suffix without replaying either.
/// </summary>
[TestFixture]
public sealed class SideExitDeoptimizationTests {

  private sealed record Fixture(
    IrFunction Function,
    IrBasicBlock Entry,
    IrBasicBlock Header,
    IrBasicBlock Prefix,
    IrBasicBlock GuardBlock,
    IrBasicBlock Fast,
    IrBasicBlock Slow,
    IrBasicBlock Latch,
    IrBasicBlock Exit,
    IrCondBr Guard,
    IrPhi Counter,
    IrPhi Sum);

  [TestCase(true, "fast", 1)]
  [TestCase(false, "slow", 0)]
  public void Loop_GivenAPerIterationAssumption_ThenTheSelectedVersionMaterializesIt(
      bool assumedValue, string selectedLabel, long expected) {
    var fixture = Build();
    var before = fixture.Function.Blocks.Count;

    Assert.That(SideExitDeoptimization.TryVersionLoop(
      fixture.Function, fixture.Header, fixture.Guard, assumedValue, out var version), Is.True);
    Assert.That(version, Is.Not.Null);
    Assert.That(fixture.Function.Blocks.Count, Is.GreaterThan(before));

    var selected = selectedLabel == "fast" ? fixture.Fast : fixture.Slow;
    var selectedClone = version!.FastBlocks[selected];
    var select = selectedClone.Instructions.OfType<IrSelect>().Single();
    Assert.That(select.Condition, Is.TypeOf<IrConstantInt>());
    Assert.That(((IrConstantInt)select.Condition).Value, Is.EqualTo(expected));

    Assert.That(IrVerifier.Verify(fixture.Function), Is.Empty);
  }

  [Test]
  public void Loop_GivenTheGuardFailsAfterASideEffect_ThenResumeDoesNotReplayThePrefix() {
    var fixture = Build();
    Assert.That(SideExitDeoptimization.TryVersionLoop(
      fixture.Function, fixture.Header, fixture.Guard, assumedValue: true, out var version), Is.True);

    var resumeBlocks = version!.ResumeBlocks.Values.ToList();
    Assert.That(resumeBlocks.SelectMany(b => b.Instructions).OfType<IrStore>(), Is.Empty,
      "the store before the guard already happened on the fast path and must not run again");
    Assert.That(version.FastBlocks[fixture.Prefix].Instructions.OfType<IrStore>().Count(), Is.EqualTo(1),
      "the fast iteration still performs the original side effect once");

    var resumeLatch = version.ResumeBlocks[fixture.Latch];
    Assert.That(fixture.Sum.IncomingFrom(fixture.Entry), Is.Null,
      "the generic loop is no longer entered with the original initial state");
    Assert.That(fixture.Sum.IncomingFrom(resumeLatch), Is.Not.Null,
      "the deopt suffix reconstructs the accumulator for the next generic iteration");
    Assert.That(fixture.Counter.IncomingFrom(resumeLatch), Is.Not.Null,
      "the deopt suffix reconstructs the induction variable at the same iteration boundary");

    Assert.That(((IrBr)resumeLatch.Terminator!).Target, Is.SameAs(fixture.Header),
      "after completing the failed iteration, execution stays in the generic loop");
    Assert.That(IrVerifier.Verify(fixture.Function), Is.Empty);
  }

  [Test]
  public void Loop_GivenFastAndGenericNaturalExits_ThenEscapingStateIsJoined() {
    var fixture = Build();
    var originalReturn = (IrRet)fixture.Exit.Terminator!;

    Assert.That(SideExitDeoptimization.TryVersionLoop(
      fixture.Function, fixture.Header, fixture.Guard, assumedValue: true, out var version), Is.True);

    Assert.That(originalReturn.Value, Is.TypeOf<IrPhi>());
    var joined = (IrPhi)originalReturn.Value!;
    Assert.That(joined.IncomingFrom(fixture.Header), Is.SameAs(fixture.Sum));
    Assert.That(joined.IncomingFrom(version!.FastHeader), Is.SameAs(version.FastValues[fixture.Sum]));
    Assert.That(IrVerifier.Verify(fixture.Function), Is.Empty);
  }

  [Test]
  public void Loop_GivenASecondExitInsideTheBody_ThenItIsDeclinedWithoutMutation() {
    var fixture = Build(fallbackExitsLoop: true);
    var blocks = fixture.Function.Blocks.ToArray();
    var entryTarget = ((IrBr)fixture.Entry.Terminator!).Target;

    Assert.That(SideExitDeoptimization.TryVersionLoop(
      fixture.Function, fixture.Header, fixture.Guard, assumedValue: true, out var version), Is.False);
    Assert.That(version, Is.Null);
    Assert.That(fixture.Function.Blocks, Is.EqualTo(blocks));
    Assert.That(((IrBr)fixture.Entry.Terminator!).Target, Is.SameAs(entryTarget));
    Assert.That(IrVerifier.Verify(fixture.Function), Is.Empty);
  }

  private static Fixture Build(bool fallbackExitsLoop = false) {
    var n = new IrArgument(IrType.I16, 0, "n");
    var pointer = new IrArgument(IrType.Ptr, 1, "p");
    var fn = new IrFunction("f", IrType.I16, [n, pointer]);

    var entry = fn.CreateBlock("entry");
    var header = fn.CreateBlock("header");
    var prefix = fn.CreateBlock("prefix");
    var guardBlock = fn.CreateBlock("guard");
    var fast = fn.CreateBlock("fast");
    var slow = fn.CreateBlock("slow");
    var latch = fn.CreateBlock("latch");
    var exit = fn.CreateBlock("exit");

    entry.Append(new IrBr(header));

    var counter = header.AppendPhi(new IrPhi(IrType.I16) { Name = "i" });
    var sum = header.AppendPhi(new IrPhi(IrType.I16) { Name = "sum" });
    counter.AddIncoming(new IrConstantInt(IrType.I16, 0), entry);
    sum.AddIncoming(new IrConstantInt(IrType.I16, 0), entry);
    var loopTest = header.Append(new IrCmp(IrCmpPred.Slt, counter, n));
    header.Append(new IrCondBr(loopTest, prefix, exit));

    // Observable work BEFORE the assumption. A side exit that restarted the generic loop at its
    // header would execute this store and increment twice for the failed iteration.
    prefix.Append(new IrStore(sum, pointer));
    var advancedSum = prefix.Append(new IrBinary(IrBinaryOp.Add, sum, new IrConstantInt(IrType.I16, 1)));
    prefix.Append(new IrBr(guardBlock));

    var condition = guardBlock.Append(new IrCmp(
      IrCmpPred.Slt, advancedSum, new IrConstantInt(IrType.I16, 10)));
    var guard = guardBlock.Append(new IrCondBr(condition, fast, slow));

    var fastValue = fast.Append(new IrSelect(
      condition, advancedSum, new IrConstantInt(IrType.I16, -1)));
    fast.Append(new IrBr(latch));

    var slowValue = slow.Append(new IrSelect(
      condition, new IrConstantInt(IrType.I16, -1), advancedSum));
    if (fallbackExitsLoop)
      slow.Append(new IrBr(exit));
    else
      slow.Append(new IrBr(latch));

    var carried = latch.AppendPhi(new IrPhi(IrType.I16) { Name = "carried" });
    carried.AddIncoming(fastValue, fast);
    if (!fallbackExitsLoop)
      carried.AddIncoming(slowValue, slow);
    var next = latch.Append(new IrBinary(IrBinaryOp.Add, counter, new IrConstantInt(IrType.I16, 1)));
    latch.Append(new IrBr(header));

    counter.AddIncoming(next, latch);
    sum.AddIncoming(carried, latch);

    exit.Append(new IrRet(sum));

    Assert.That(IrVerifier.Verify(fn), Is.Empty, "test fixture must start as valid SSA");
    return new(fn, entry, header, prefix, guardBlock, fast, slow, latch, exit, guard, counter, sum);
  }
}
