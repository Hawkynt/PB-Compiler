using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>SimplifyCFG branch folding and unreachable-block removal.</summary>
[TestFixture]
public sealed class SimplifyCfgFoldTests {

  [Test]
  public void CondBrWithIdenticalTargets_BecomesUnconditional() {
    var fn = new IrFunction("f", IrType.Void, [new IrArgument(IrType.I1, 0, "c")]);
    var entry = fn.CreateBlock("entry");
    var join = fn.CreateBlock("join");
    new IrBuilder(entry).CondBr(fn.Parameters[0], join, join);   // both arms go to join
    new IrBuilder(join).Ret();

    SimplifyCfg.Run(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(fn.AllInstructions.OfType<IrCondBr>(), Is.Empty);
  }

  [Test]
  public void ConstantCondBr_FoldsAndDropsTheUnreachableArm() {
    var fn = new IrFunction("f", IrType.I32);
    var entry = fn.CreateBlock("entry");
    var t = fn.CreateBlock("t");
    var e = fn.CreateBlock("e");
    new IrBuilder(entry).CondBr(IrBuilder.ConstBool(true), t, e);
    new IrBuilder(t).Ret(IrBuilder.ConstI32(1));
    new IrBuilder(e).Ret(IrBuilder.ConstI32(2));

    SimplifyCfg.Run(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(fn.Blocks.Any(b => b.Label == "e"), Is.False);   // dead arm removed
    Assert.That(IrPrinter.Print(fn), Does.Not.Contain("ret i32 2"));
  }

  [Test]
  public void UnreachableBlock_IsRemovedAndPhiFixed() {
    var fn = new IrFunction("f", IrType.I32);
    var entry = fn.CreateBlock("entry");
    var orphan = fn.CreateBlock("orphan");   // nothing branches here
    var join = fn.CreateBlock("join");
    new IrBuilder(entry).Br(join);
    new IrBuilder(orphan).Br(join);
    var bj = new IrBuilder(join);
    var phi = bj.Phi(IrType.I32);
    phi.AddIncoming(IrBuilder.ConstI32(1), entry);
    phi.AddIncoming(IrBuilder.ConstI32(2), orphan);   // incoming from the unreachable block
    bj.Ret(phi);

    SimplifyCfg.Run(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(fn.Blocks.Any(b => b.Label == "orphan"), Is.False);
  }
}
