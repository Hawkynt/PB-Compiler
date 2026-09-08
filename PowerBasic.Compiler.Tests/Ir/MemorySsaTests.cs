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
}
