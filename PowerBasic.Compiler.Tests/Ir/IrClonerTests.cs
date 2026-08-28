using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>The general block-cloning utility, including SSA back-edges (loop phis).</summary>
[TestFixture]
public sealed class IrClonerTests {

  [Test]
  public void Clone_ALoopWithAPhiBackEdge_ProducesVerifiableSsa() {
    // source: entry -> head ; head: i = phi[0,entry],[next,head] ; next = i+1 ; i<10 ? head : exit
    var src = new IrFunction("src", IrType.Void);
    var entry = src.CreateBlock("entry");
    var head = src.CreateBlock("head");
    var exit = src.CreateBlock("exit");
    new IrBuilder(entry).Br(head);
    var bh = new IrBuilder(head);
    var i = bh.Phi(IrType.I32);
    var next = bh.Add(i, IrBuilder.ConstI32(1));
    var cond = bh.Cmp(IrCmpPred.Slt, next, IrBuilder.ConstI32(10));
    bh.CondBr(cond, head, exit);
    i.AddIncoming(IrBuilder.ConstI32(0), entry);
    i.AddIncoming(next, head);
    new IrBuilder(exit).Ret();

    var dest = new IrFunction("dest", IrType.Void);
    var map = IrCloner.Clone(dest, src.Blocks.ToList(), new(ReferenceEqualityComparer.Instance), "c.");

    Assert.That(dest.Blocks, Has.Count.EqualTo(3));
    Assert.That(IrVerifier.Verify(dest), Is.Empty);                       // back-edge phi cloned correctly
    // the cloned phi's back-edge value is the cloned 'next', not the original
    var clonedHead = map[head];
    var clonedPhi = clonedHead.Phis.Single();
    Assert.That(clonedPhi.IncomingFrom(clonedHead), Is.Not.SameAs(next)); // remapped to the clone
    Assert.That(clonedPhi.IncomingFrom(clonedHead), Is.InstanceOf<IrBinary>());
  }

  [Test]
  public void Clone_RemapsSeededValuesAndLeavesTheOriginalIntact() {
    var p = new IrArgument(IrType.I32, 0, "p");
    var src = new IrFunction("src", IrType.I32, [p]);
    var b = new IrBuilder(src.CreateBlock("entry"));
    var doubled = b.Add(p, p);
    b.Ret(doubled);

    var replacement = new IrConstantInt(IrType.I32, 21);
    var dest = new IrFunction("dest", IrType.I32);
    var seed = new Dictionary<IrValue, IrValue>(ReferenceEqualityComparer.Instance) { [p] = replacement };
    IrCloner.Clone(dest, src.Blocks.ToList(), seed, "c.");

    Assert.That(IrVerifier.Verify(dest), Is.Empty);
    Assert.That(IrPrinter.Print(dest), Does.Contain("add i32 21, 21"));   // seeded operand used
    Assert.That(IrPrinter.Print(src), Does.Contain("add i32 %p, %p"));    // original untouched
  }

  [Test]
  public void Clone_GivenNonDefaultCallConvention_ThenPreservesItsAbiIdentity() {
    var callee = new IrFunction("foreign", IrType.Void);
    var src = new IrFunction("src", IrType.Void);
    var entry = src.CreateBlock("entry");
    entry.Append(new IrCall(IrType.Void, callee, [], IrCallConvention.Cdecl));
    entry.Append(new IrRet());
    var dest = new IrFunction("dest", IrType.Void);

    IrCloner.Clone(dest, src.Blocks.ToList(), new(ReferenceEqualityComparer.Instance), "c.");

    Assert.That(dest.Blocks.Single().Instructions.OfType<IrCall>().Single().Convention,
      Is.EqualTo(IrCallConvention.Cdecl));
    Assert.That(IrPrinter.Print(dest), Does.Contain("call cdecl void @foreign()"));
  }
}
