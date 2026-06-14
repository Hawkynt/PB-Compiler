using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>Dominator tree and dominance frontiers over the IR CFG.</summary>
[TestFixture]
public sealed class IrDominatorsTests {

  /// <summary>entry -> {a, b} -> merge (a classic diamond).</summary>
  private static (IrFunction Fn, IrBasicBlock Entry, IrBasicBlock A, IrBasicBlock B, IrBasicBlock Merge) Diamond() {
    var fn = new IrFunction("d", IrType.Void);
    var entry = fn.CreateBlock("entry");
    var a = fn.CreateBlock("a");
    var b = fn.CreateBlock("b");
    var merge = fn.CreateBlock("merge");
    new IrBuilder(entry).CondBr(IrBuilder.ConstBool(true), a, b);
    new IrBuilder(a).Br(merge);
    new IrBuilder(b).Br(merge);
    new IrBuilder(merge).Ret();
    return (fn, entry, a, b, merge);
  }

  [Test]
  public void Idom_OverDiamond_AllArmsDominatedByEntry() {
    var (fn, entry, a, b, merge) = Diamond();
    var dom = IrDominators.Build(fn)!;

    Assert.That(dom.ImmediateDominatorOf(a), Is.SameAs(entry));
    Assert.That(dom.ImmediateDominatorOf(b), Is.SameAs(entry));
    Assert.That(dom.ImmediateDominatorOf(merge), Is.SameAs(entry));
    Assert.That(dom.ImmediateDominatorOf(entry), Is.SameAs(entry));
  }

  [Test]
  public void Dominates_OverDiamond_ReflectsReachabilityThroughChokePoints() {
    var (fn, entry, a, _, merge) = Diamond();
    var dom = IrDominators.Build(fn)!;

    Assert.That(dom.Dominates(entry, merge), Is.True);
    Assert.That(dom.Dominates(entry, a), Is.True);
    Assert.That(dom.Dominates(a, merge), Is.False);    // the b arm bypasses a
    Assert.That(dom.Dominates(a, a), Is.True);         // a block dominates itself
  }

  [Test]
  public void Frontier_OverDiamond_IsTheMergeForBothArms() {
    var (fn, _, a, b, merge) = Diamond();
    var dom = IrDominators.Build(fn)!;

    Assert.That(dom.FrontierOf(a), Is.EquivalentTo(new[] { merge }));
    Assert.That(dom.FrontierOf(b), Is.EquivalentTo(new[] { merge }));
    Assert.That(dom.FrontierOf(merge), Is.Empty);
  }

  [Test]
  public void Frontier_OverLoop_HeaderIsItsOwnFrontier() {
    // entry -> header -> body -> header (back-edge) ; header -> exit
    var fn = new IrFunction("loop", IrType.Void);
    var entry = fn.CreateBlock("entry");
    var header = fn.CreateBlock("header");
    var body = fn.CreateBlock("body");
    var exit = fn.CreateBlock("exit");
    new IrBuilder(entry).Br(header);
    new IrBuilder(header).CondBr(IrBuilder.ConstBool(true), body, exit);
    new IrBuilder(body).Br(header);
    new IrBuilder(exit).Ret();
    var dom = IrDominators.Build(fn)!;

    Assert.That(dom.FrontierOf(body), Is.EquivalentTo(new[] { header }));
    Assert.That(dom.ImmediateDominatorOf(body), Is.SameAs(header));
    Assert.That(dom.ImmediateDominatorOf(exit), Is.SameAs(header));
  }
}
