using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>Focused CFG edge-dominance regressions for O0351 pointer-check elimination.</summary>
[TestFixture]
public sealed class O0351PointerCheckElimTests {

  [Test]
  public void PointerCheckElim_GivenOppositeEdgeCanReconverge_WhenRun_ThenTheRepeatedTestStaysUnknown() {
    var pointer = new IrArgument(IrType.Ptr, 0, "p");
    var fn = new IrFunction("f", IrType.Void, [pointer]);
    var entry = fn.CreateBlock("entry");
    var detour = fn.CreateBlock("detour");
    var checkedBlock = fn.CreateBlock("checked");
    var yes = fn.CreateBlock("yes");
    var no = fn.CreateBlock("no");

    var be = new IrBuilder(entry);
    be.CondBr(be.Cmp(IrCmpPred.Ne, pointer, new IrNullPtr()), checkedBlock, detour);
    new IrBuilder(detour).Br(checkedBlock);
    var bc = new IrBuilder(checkedBlock);
    var repeated = bc.Cmp(IrCmpPred.Ne, pointer, new IrNullPtr());
    bc.CondBr(repeated, yes, no);
    new IrBuilder(yes).Ret();
    new IrBuilder(no).Ret();

    Assert.That(PointerCheckElim.Run(fn), Is.Zero);
    Assert.That(((IrCondBr)checkedBlock.Terminator!).Condition, Is.SameAs(repeated));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void PointerCheckElim_GivenBothGuardEdgesReachSameBlock_WhenRun_ThenTheRepeatedTestStaysUnknown() {
    var pointer = new IrArgument(IrType.Ptr, 0, "p");
    var fn = new IrFunction("f", IrType.Void, [pointer]);
    var entry = fn.CreateBlock("entry");
    var checkedBlock = fn.CreateBlock("checked");
    var yes = fn.CreateBlock("yes");
    var no = fn.CreateBlock("no");

    var be = new IrBuilder(entry);
    be.CondBr(be.Cmp(IrCmpPred.Ne, pointer, new IrNullPtr()), checkedBlock, checkedBlock);
    var bc = new IrBuilder(checkedBlock);
    var repeated = bc.Cmp(IrCmpPred.Ne, pointer, new IrNullPtr());
    bc.CondBr(repeated, yes, no);
    new IrBuilder(yes).Ret();
    new IrBuilder(no).Ret();

    Assert.That(PointerCheckElim.Run(fn), Is.Zero);
    Assert.That(((IrCondBr)checkedBlock.Terminator!).Condition, Is.SameAs(repeated));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void PointerCheckElim_GivenGuardedLoopHeaderWithBackedge_WhenRun_ThenTheRepeatedTestStillFolds() {
    var pointer = new IrArgument(IrType.Ptr, 0, "p");
    var loopAgain = new IrArgument(IrType.I1, 1, "again");
    var fn = new IrFunction("f", IrType.Void, [pointer, loopAgain]);
    var entry = fn.CreateBlock("entry");
    var header = fn.CreateBlock("header");
    var body = fn.CreateBlock("body");
    var exit = fn.CreateBlock("exit");

    var be = new IrBuilder(entry);
    be.CondBr(be.Cmp(IrCmpPred.Ne, pointer, new IrNullPtr()), header, exit);
    var bh = new IrBuilder(header);
    var repeated = bh.Cmp(IrCmpPred.Ne, pointer, new IrNullPtr());
    bh.CondBr(repeated, body, exit);
    new IrBuilder(body).CondBr(loopAgain, header, exit);
    new IrBuilder(exit).Ret();

    Assert.That(PointerCheckElim.Run(fn), Is.EqualTo(1));
    Assert.That(((IrCondBr)header.Terminator!).Condition, Is.InstanceOf<IrConstantInt>());
    Assert.That(((IrConstantInt)((IrCondBr)header.Terminator!).Condition).Value, Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }
}
