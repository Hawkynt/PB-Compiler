using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0305 — path facts materialized as specialized copies of bounded reconverged regions.</summary>
[TestFixture]
public sealed class BasicBlockVersioningTests {

  [Test]
  public void Branch_GivenARepeatedGuardAfterReconvergence_ThenTheJoinIsVersioned() {
    var (fn, join, whenTrue, whenFalse) = VersionableDiamond(withPhi: true);

    Assert.That(BasicBlockVersioning.Run(fn), Is.EqualTo(1));

    var trueVersion = fn.Blocks.Single(block => block.Label == "bbv.t." + join.Label);
    var falseVersion = fn.Blocks.Single(block => block.Label == "bbv.f." + join.Label);
    Assert.Multiple(() => {
      Assert.That(join.Parent, Is.Null, "the two profitable guarded contexts exhaust the original join");
      Assert.That(((IrCondBr)trueVersion.Terminator!).Condition, Is.TypeOf<IrConstantInt>());
      Assert.That(((IrConstantInt)((IrCondBr)trueVersion.Terminator!).Condition).Value, Is.EqualTo(1));
      Assert.That(((IrConstantInt)((IrCondBr)falseVersion.Terminator!).Condition).Value, Is.Zero);
      Assert.That(trueVersion.Phis.Single().IncomingBlocks, Is.EqualTo(new[] { whenTrue }));
      Assert.That(falseVersion.Phis.Single().IncomingBlocks, Is.EqualTo(new[] { whenFalse }));
      Assert.That(IrVerifier.Verify(fn), Is.Empty, "versioning must preserve immediately valid SSA");
    });
  }

  [Test]
  public void Branch_GivenVersionedRepeatedGuards_ThenCleanupRemovesTheDuplicateChecks() {
    var (fn, _, _, _) = VersionableDiamond();
    Assume.That(BasicBlockVersioning.Run(fn), Is.EqualTo(1));

    SimplifyCfg.Run(fn);
    Dce.Run(fn);

    Assert.Multiple(() => {
      Assert.That(fn.AllInstructions.OfType<IrCmp>().Count(), Is.EqualTo(1),
        "only the original dispatch comparison should remain");
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void Branch_GivenNoReusableFactInTheJoin_ThenItIsNotVersioned() {
    var x = new IrArgument(IrType.I16, 0, "x");
    var y = new IrArgument(IrType.I16, 1, "y");
    var fn = new IrFunction("f", IrType.I16, [x, y]);
    var entry = fn.CreateBlock("entry");
    var whenTrue = fn.CreateBlock("when.true");
    var whenFalse = fn.CreateBlock("when.false");
    var join = fn.CreateBlock("join");
    var small = fn.CreateBlock("small");
    var large = fn.CreateBlock("large");

    var guard = entry.Append(new IrCmp(IrCmpPred.Slt, x, new IrConstantInt(IrType.I16, 256)));
    entry.Append(new IrCondBr(guard, whenTrue, whenFalse));
    whenTrue.Append(new IrBr(join));
    whenFalse.Append(new IrBr(join));
    var unrelated = join.Append(new IrCmp(IrCmpPred.Slt, y, new IrConstantInt(IrType.I16, 128)));
    join.Append(new IrCondBr(unrelated, small, large));
    small.Append(new IrRet(new IrConstantInt(IrType.I16, 1)));
    large.Append(new IrRet(new IrConstantInt(IrType.I16, 0)));

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(BasicBlockVersioning.Run(fn), Is.Zero,
      "duplicating code without a fact that decides a later guard is not profitable");
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Branch_GivenAnotherIncomingContext_ThenItRemainsTheGeneralFallback() {
    var x = new IrArgument(IrType.I16, 0, "x");
    var chooseGuard = new IrArgument(IrType.I1, 1, "chooseGuard");
    var fn = new IrFunction("f", IrType.I16, [x, chooseGuard]);
    var entry = fn.CreateBlock("entry");
    var guardBlock = fn.CreateBlock("guard");
    var fallback = fn.CreateBlock("fallback");
    var whenTrue = fn.CreateBlock("when.true");
    var whenFalse = fn.CreateBlock("when.false");
    var join = fn.CreateBlock("join");
    var small = fn.CreateBlock("small");
    var large = fn.CreateBlock("large");

    entry.Append(new IrCondBr(chooseGuard, guardBlock, fallback));
    var guard = guardBlock.Append(new IrCmp(IrCmpPred.Slt, x, new IrConstantInt(IrType.I16, 256)));
    guardBlock.Append(new IrCondBr(guard, whenTrue, whenFalse));
    fallback.Append(new IrBr(join));
    whenTrue.Append(new IrBr(join));
    whenFalse.Append(new IrBr(join));
    var phi = join.AppendPhi(new IrPhi(IrType.I16));
    phi.AddIncoming(new IrConstantInt(IrType.I16, 30), fallback);
    phi.AddIncoming(new IrConstantInt(IrType.I16, 10), whenTrue);
    phi.AddIncoming(new IrConstantInt(IrType.I16, 20), whenFalse);
    var repeated = join.Append(new IrCmp(IrCmpPred.Slt, x, new IrConstantInt(IrType.I16, 256)));
    join.Append(new IrCondBr(repeated, small, large));
    small.Append(new IrRet(new IrConstantInt(IrType.I16, 1)));
    large.Append(new IrRet(new IrConstantInt(IrType.I16, 0)));

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(BasicBlockVersioning.Run(fn), Is.EqualTo(1));

    var trueVersion = fn.Blocks.Single(block => block.Label == "bbv.t.join");
    var falseVersion = fn.Blocks.Single(block => block.Label == "bbv.f.join");
    Assert.Multiple(() => {
      Assert.That(join.Parent, Is.SameAs(fn), "the original block is the fallback for the third context");
      Assert.That(join.Predecessors, Is.EqualTo(new[] { fallback }));
      Assert.That(join.Phis.Single().IncomingBlocks, Is.EqualTo(new[] { fallback }));
      Assert.That(trueVersion.Phis.Single().IncomingBlocks, Is.EqualTo(new[] { whenTrue }));
      Assert.That(falseVersion.Phis.Single().IncomingBlocks, Is.EqualTo(new[] { whenFalse }));
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void Branch_GivenARangeImplicationOnOnlyOneOutcome_ThenOnlyThatVersionIsCreated() {
    var (fn, join, _, _) = VersionableDiamond(repeatedLimit: 128);

    Assert.That(BasicBlockVersioning.Run(fn), Is.EqualTo(1));

    var falseVersion = fn.Blocks.Single(block => block.Label == "bbv.f." + join.Label);
    Assert.Multiple(() => {
      Assert.That(fn.Blocks.Any(block => block.Label == "bbv.t." + join.Label), Is.False,
        "x < 256 does not decide whether x < 128 on the true edge");
      Assert.That(join.Parent, Is.SameAs(fn), "the undecided true context keeps using the general block");
      Assert.That(((IrCondBr)falseVersion.Terminator!).Condition, Is.TypeOf<IrConstantInt>());
      Assert.That(((IrConstantInt)((IrCondBr)falseVersion.Terminator!).Condition).IsZero, Is.True,
        "x >= 256 proves x < 128 false");
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void Branch_GivenAStrongerRangeGuard_ThenAWeakerGuardFoldsInTheSpecializedVersion() {
    var (fn, join, _, _) = VersionableDiamond(repeatedLimit: 512);

    Assert.That(BasicBlockVersioning.Run(fn), Is.EqualTo(1));

    var trueVersion = fn.Blocks.Single(block => block.Label == "bbv.t." + join.Label);
    Assert.Multiple(() => {
      Assert.That(fn.Blocks.Any(block => block.Label == "bbv.f." + join.Label), Is.False,
        "x >= 256 does not decide x < 512");
      Assert.That(((IrConstantInt)((IrCondBr)trueVersion.Terminator!).Condition).Value, Is.EqualTo(1),
        "x < 256 implies x < 512");
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void Branch_GivenAligned16_ThenAligned8IsKnownInTheTrueVersion() {
    var pointer = new IrArgument(IrType.Ptr, 0, "p");
    var fn = new IrFunction("f", IrType.I16, [pointer]);
    var entry = fn.CreateBlock("entry");
    var whenTrue = fn.CreateBlock("when.true");
    var whenFalse = fn.CreateBlock("when.false");
    var join = fn.CreateBlock("join");
    var aligned = fn.CreateBlock("aligned");
    var unaligned = fn.CreateBlock("unaligned");

    var guardBits = entry.Append(new IrCast(IrCastOp.PtrToInt, pointer, IrType.U16));
    var guardLow = entry.Append(new IrBinary(IrBinaryOp.And, guardBits, new IrConstantInt(IrType.U16, 15)));
    var guard = entry.Append(new IrCmp(IrCmpPred.Eq, guardLow, new IrConstantInt(IrType.U16, 0)));
    entry.Append(new IrCondBr(guard, whenTrue, whenFalse));
    whenTrue.Append(new IrBr(join));
    whenFalse.Append(new IrBr(join));

    var joinBits = join.Append(new IrCast(IrCastOp.PtrToInt, pointer, IrType.U16));
    var joinLow = join.Append(new IrBinary(IrBinaryOp.And, joinBits, new IrConstantInt(IrType.U16, 7)));
    var aligned8 = join.Append(new IrCmp(IrCmpPred.Eq, joinLow, new IrConstantInt(IrType.U16, 0)));
    join.Append(new IrCondBr(aligned8, aligned, unaligned));
    aligned.Append(new IrRet(new IrConstantInt(IrType.I16, 1)));
    unaligned.Append(new IrRet(new IrConstantInt(IrType.I16, 0)));

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(BasicBlockVersioning.Run(fn), Is.EqualTo(1));

    var trueVersion = fn.Blocks.Single(block => block.Label == "bbv.t.join");
    Assert.Multiple(() => {
      Assert.That(fn.Blocks.Any(block => block.Label == "bbv.f.join"), Is.False,
        "not 16-byte aligned does not imply not 8-byte aligned");
      Assert.That(((IrConstantInt)((IrCondBr)trueVersion.Terminator!).Condition).Value, Is.EqualTo(1));
      Assert.That(join.Parent, Is.SameAs(fn), "the undecided alignment context remains the fallback");
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void Branch_GivenTheProfitableGuardIsInASecondBlock_ThenTheWholeForwardRegionIsVersioned() {
    var x = new IrArgument(IrType.I16, 0, "x");
    var fn = new IrFunction("f", IrType.I16, [x]);
    var entry = fn.CreateBlock("entry");
    var whenTrue = fn.CreateBlock("when.true");
    var whenFalse = fn.CreateBlock("when.false");
    var join = fn.CreateBlock("join");
    var check = fn.CreateBlock("check");
    var small = fn.CreateBlock("small");
    var large = fn.CreateBlock("large");

    var guard = entry.Append(new IrCmp(IrCmpPred.Slt, x, new IrConstantInt(IrType.I16, 256)));
    entry.Append(new IrCondBr(guard, whenTrue, whenFalse));
    whenTrue.Append(new IrBr(join));
    whenFalse.Append(new IrBr(join));
    join.Append(new IrBr(check));
    var repeated = check.Append(new IrCmp(IrCmpPred.Slt, x, new IrConstantInt(IrType.I16, 256)));
    check.Append(new IrCondBr(repeated, small, large));
    small.Append(new IrRet(new IrConstantInt(IrType.I16, 1)));
    large.Append(new IrRet(new IrConstantInt(IrType.I16, 0)));

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(BasicBlockVersioning.Run(fn), Is.EqualTo(1));

    var trueJoin = fn.Blocks.Single(block => block.Label == "bbv.t.join");
    var falseJoin = fn.Blocks.Single(block => block.Label == "bbv.f.join");
    var trueCheck = fn.Blocks.Single(block => block.Label == "bbv.t.check");
    var falseCheck = fn.Blocks.Single(block => block.Label == "bbv.f.check");
    Assert.Multiple(() => {
      Assert.That(join.Parent, Is.Null);
      Assert.That(check.Parent, Is.Null);
      Assert.That(((IrBr)trueJoin.Terminator!).Target, Is.SameAs(trueCheck));
      Assert.That(((IrBr)falseJoin.Terminator!).Target, Is.SameAs(falseCheck));
      Assert.That(((IrConstantInt)((IrCondBr)trueCheck.Terminator!).Condition).Value, Is.EqualTo(1));
      Assert.That(((IrConstantInt)((IrCondBr)falseCheck.Terminator!).Condition).Value, Is.Zero);
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void Branch_GivenSuccessorPhis_ThenEachVersionAddsMappedIncomingValues() {
    var x = new IrArgument(IrType.I16, 0, "x");
    var fn = new IrFunction("f", IrType.I16, [x]);
    var entry = fn.CreateBlock("entry");
    var whenTrue = fn.CreateBlock("when.true");
    var whenFalse = fn.CreateBlock("when.false");
    var join = fn.CreateBlock("join");
    var small = fn.CreateBlock("small");
    var large = fn.CreateBlock("large");

    var guard = entry.Append(new IrCmp(IrCmpPred.Slt, x, new IrConstantInt(IrType.I16, 256)));
    entry.Append(new IrCondBr(guard, whenTrue, whenFalse));
    whenTrue.Append(new IrBr(join));
    whenFalse.Append(new IrBr(join));
    var payload = join.Append(new IrBinary(IrBinaryOp.Add,
      new IrConstantInt(IrType.I16, 20), new IrConstantInt(IrType.I16, 22)));
    var repeated = join.Append(new IrCmp(IrCmpPred.Slt, x, new IrConstantInt(IrType.I16, 256)));
    join.Append(new IrCondBr(repeated, small, large));

    var smallPhi = small.AppendPhi(new IrPhi(IrType.I16));
    smallPhi.AddIncoming(payload, join);
    small.Append(new IrRet(smallPhi));
    var largePhi = large.AppendPhi(new IrPhi(IrType.I16));
    largePhi.AddIncoming(payload, join);
    large.Append(new IrRet(largePhi));

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(BasicBlockVersioning.Run(fn), Is.EqualTo(1));

    var trueVersion = fn.Blocks.Single(block => block.Label == "bbv.t.join");
    var falseVersion = fn.Blocks.Single(block => block.Label == "bbv.f.join");
    Assert.Multiple(() => {
      Assert.That(join.Parent, Is.Null);
      Assert.That(smallPhi.IncomingBlocks, Is.EquivalentTo(new[] { trueVersion, falseVersion }));
      Assert.That(largePhi.IncomingBlocks, Is.EquivalentTo(new[] { trueVersion, falseVersion }));
      Assert.That(smallPhi.Operands, Has.All.Not.SameAs(payload), "successor phis must use cloned definitions");
      Assert.That(largePhi.Operands, Has.All.Not.SameAs(payload), "successor phis must use cloned definitions");
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void Branch_GivenAnEscapingValueAndUniqueExit_ThenTheExitMergesSpecializedDefinitions() {
    var x = new IrArgument(IrType.I16, 0, "x");
    var fn = new IrFunction("f", IrType.I16, [x]);
    var entry = fn.CreateBlock("entry");
    var whenTrue = fn.CreateBlock("when.true");
    var whenFalse = fn.CreateBlock("when.false");
    var join = fn.CreateBlock("join");
    var compute = fn.CreateBlock("compute");
    var exit = fn.CreateBlock("exit");

    var guard = entry.Append(new IrCmp(IrCmpPred.Slt, x, new IrConstantInt(IrType.I16, 256)));
    entry.Append(new IrCondBr(guard, whenTrue, whenFalse));
    whenTrue.Append(new IrBr(join));
    whenFalse.Append(new IrBr(join));
    join.Append(new IrBr(compute));
    var selected = compute.Append(new IrSelect(guard,
      new IrConstantInt(IrType.I16, 10), new IrConstantInt(IrType.I16, 20)));
    compute.Append(new IrBr(exit));
    var ret = exit.Append(new IrRet(selected));

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(BasicBlockVersioning.Run(fn), Is.EqualTo(1));

    var trueCompute = fn.Blocks.Single(block => block.Label == "bbv.t.compute");
    var falseCompute = fn.Blocks.Single(block => block.Label == "bbv.f.compute");
    var merge = exit.Phis.Single();
    Assert.Multiple(() => {
      Assert.That(join.Parent, Is.Null);
      Assert.That(compute.Parent, Is.Null);
      Assert.That(merge.IncomingBlocks, Is.EquivalentTo(new[] { trueCompute, falseCompute }));
      Assert.That(ret.Value, Is.SameAs(merge));
      Assert.That(((IrConstantInt)trueCompute.Instructions.OfType<IrSelect>().Single().Condition).Value, Is.EqualTo(1));
      Assert.That(((IrConstantInt)falseCompute.Instructions.OfType<IrSelect>().Single().Condition).Value, Is.Zero);
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void Branch_GivenOneProfitableContextAndAnEscapingValue_ThenTheExitMergesFallbackAndVersion() {
    var x = new IrArgument(IrType.I16, 0, "x");
    var fn = new IrFunction("f", IrType.I16, [x]);
    var entry = fn.CreateBlock("entry");
    var whenTrue = fn.CreateBlock("when.true");
    var whenFalse = fn.CreateBlock("when.false");
    var join = fn.CreateBlock("join");
    var compute = fn.CreateBlock("compute");
    var exit = fn.CreateBlock("exit");

    var guard = entry.Append(new IrCmp(IrCmpPred.Slt, x, new IrConstantInt(IrType.I16, 256)));
    entry.Append(new IrCondBr(guard, whenTrue, whenFalse));
    whenTrue.Append(new IrBr(join));
    whenFalse.Append(new IrBr(join));
    join.Append(new IrBr(compute));
    var repeated = compute.Append(new IrCmp(IrCmpPred.Slt, x, new IrConstantInt(IrType.I16, 128)));
    var selected = compute.Append(new IrSelect(repeated,
      new IrConstantInt(IrType.I16, 10), new IrConstantInt(IrType.I16, 20)));
    compute.Append(new IrBr(exit));
    var ret = exit.Append(new IrRet(selected));

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(BasicBlockVersioning.Run(fn), Is.EqualTo(1));

    var falseJoin = fn.Blocks.Single(block => block.Label == "bbv.f.join");
    var falseCompute = fn.Blocks.Single(block => block.Label == "bbv.f.compute");
    var merge = exit.Phis.Single();
    Assert.Multiple(() => {
      Assert.That(fn.Blocks.Any(block => block.Label == "bbv.t.join"), Is.False);
      Assert.That(join.Parent, Is.SameAs(fn), "the undecided true context stays on the generic region");
      Assert.That(((IrBr)whenTrue.Terminator!).Target, Is.SameAs(join));
      Assert.That(((IrBr)whenFalse.Terminator!).Target, Is.SameAs(falseJoin));
      Assert.That(merge.IncomingBlocks, Is.EquivalentTo(new[] { compute, falseCompute }));
      Assert.That(ret.Value, Is.SameAs(merge));
      Assert.That(((IrConstantInt)falseCompute.Instructions.OfType<IrSelect>().Single().Condition).IsZero, Is.True);
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void Branch_GivenAnEscapingValueAndMultipleExits_ThenItIsNotVersioned() {
    var x = new IrArgument(IrType.I16, 0, "x");
    var fn = new IrFunction("f", IrType.I16, [x]);
    var entry = fn.CreateBlock("entry");
    var whenTrue = fn.CreateBlock("when.true");
    var whenFalse = fn.CreateBlock("when.false");
    var join = fn.CreateBlock("join");
    var small = fn.CreateBlock("small");
    var large = fn.CreateBlock("large");

    var guard = entry.Append(new IrCmp(IrCmpPred.Slt, x, new IrConstantInt(IrType.I16, 256)));
    entry.Append(new IrCondBr(guard, whenTrue, whenFalse));
    whenTrue.Append(new IrBr(join));
    whenFalse.Append(new IrBr(join));
    var payload = join.Append(new IrBinary(IrBinaryOp.Add,
      new IrConstantInt(IrType.I16, 20), new IrConstantInt(IrType.I16, 22)));
    var repeated = join.Append(new IrCmp(IrCmpPred.Slt, x, new IrConstantInt(IrType.I16, 256)));
    join.Append(new IrCondBr(repeated, small, large));
    small.Append(new IrRet(payload));
    large.Append(new IrRet(payload));

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(BasicBlockVersioning.Run(fn), Is.Zero,
      "a direct SSA value crossing multiple versioned exits still needs a general SSA updater");
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Branch_GivenTheBlockExceedsTheGrowthBudget_ThenItIsNotVersioned() {
    var (fn, join, _, _) = VersionableDiamond(extraInstructions: 31);
    Assert.That(join.Instructions.Count, Is.GreaterThan(32));

    Assert.That(BasicBlockVersioning.Run(fn), Is.Zero);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Function_GivenAnArmedErrorHandler_ThenItIsSkipped() {
    var (fn, _, _, _) = VersionableDiamond();
    fn.HasErrorHandler = true;

    Assert.That(BasicBlockVersioning.Run(fn), Is.Zero);
  }

  private static (IrFunction Function, IrBasicBlock Join, IrBasicBlock WhenTrue, IrBasicBlock WhenFalse)
      VersionableDiamond(int repeatedLimit = 256, int extraInstructions = 0, bool withPhi = false) {
    var x = new IrArgument(IrType.I16, 0, "x");
    var fn = new IrFunction("f", IrType.I16, [x]);
    var entry = fn.CreateBlock("entry");
    var whenTrue = fn.CreateBlock("when.true");
    var whenFalse = fn.CreateBlock("when.false");
    var join = fn.CreateBlock("join");
    var small = fn.CreateBlock("small");
    var large = fn.CreateBlock("large");

    var guard = entry.Append(new IrCmp(IrCmpPred.Slt, x, new IrConstantInt(IrType.I16, 256)));
    entry.Append(new IrCondBr(guard, whenTrue, whenFalse));
    whenTrue.Append(new IrBr(join));
    whenFalse.Append(new IrBr(join));

    if (withPhi) {
      var phi = join.AppendPhi(new IrPhi(IrType.I16));
      phi.AddIncoming(new IrConstantInt(IrType.I16, 10), whenTrue);
      phi.AddIncoming(new IrConstantInt(IrType.I16, 20), whenFalse);
    }
    for (var i = 0; i < extraInstructions; ++i)
      join.Append(new IrBinary(IrBinaryOp.Add,
        new IrConstantInt(IrType.I16, i), new IrConstantInt(IrType.I16, i + 1)));

    var repeated = join.Append(new IrCmp(IrCmpPred.Slt, x, new IrConstantInt(IrType.I16, repeatedLimit)));
    join.Append(new IrCondBr(repeated, small, large));
    small.Append(new IrRet(new IrConstantInt(IrType.I16, 1)));
    large.Append(new IrRet(new IrConstantInt(IrType.I16, 0)));

    Assert.That(IrVerifier.Verify(fn), Is.Empty, "test setup must start from valid IR");
    return (fn, join, whenTrue, whenFalse);
  }
}
