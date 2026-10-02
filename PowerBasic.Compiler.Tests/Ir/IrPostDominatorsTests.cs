using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class IrPostDominatorsTests {

  [Test]
  public void Build_GivenDiamondWithOneExit_ThenComputesImmediatePostDominatorsAndFrontiers() {
    var condition = new IrArgument(IrType.I1, 0, "condition");
    var function = new IrFunction("diamond", IrType.Void, [condition]);
    var entry = function.AddBlock(new IrBasicBlock("entry"));
    var left = function.AddBlock(new IrBasicBlock("left"));
    var right = function.AddBlock(new IrBasicBlock("right"));
    var join = function.AddBlock(new IrBasicBlock("join"));

    entry.Append(new IrCondBr(condition, left, right));
    left.Append(new IrBr(join));
    right.Append(new IrBr(join));
    join.Append(new IrRet());

    var postDominators = IrPostDominators.Build(function)!;

    Assert.Multiple(() => {
      Assert.That(postDominators.ImmediatePostDominatorOf(entry), Is.SameAs(join));
      Assert.That(postDominators.ImmediatePostDominatorOf(left), Is.SameAs(join));
      Assert.That(postDominators.ImmediatePostDominatorOf(right), Is.SameAs(join));
      Assert.That(postDominators.ImmediatePostDominatorOf(join), Is.Null);
      Assert.That(postDominators.PostDominates(join, entry), Is.True);
      Assert.That(postDominators.PostDominates(join, left), Is.True);
      Assert.That(postDominators.FrontierOf(left), Does.Contain(entry));
      Assert.That(postDominators.FrontierOf(right), Does.Contain(entry));
      Assert.That(postDominators.CanReachExit(entry), Is.True);
    });
  }

  [Test]
  public void Build_GivenTwoReturns_ThenVirtualExitKeepsEitherReturnFromPostDominatingTheBranch() {
    var condition = new IrArgument(IrType.I1, 0, "condition");
    var function = new IrFunction("twoExits", IrType.Void, [condition]);
    var entry = function.AddBlock(new IrBasicBlock("entry"));
    var leftExit = function.AddBlock(new IrBasicBlock("left.exit"));
    var rightExit = function.AddBlock(new IrBasicBlock("right.exit"));

    entry.Append(new IrCondBr(condition, leftExit, rightExit));
    leftExit.Append(new IrRet());
    rightExit.Append(new IrRet());

    var postDominators = IrPostDominators.Build(function)!;

    Assert.Multiple(() => {
      Assert.That(postDominators.ImmediatePostDominatorOf(entry), Is.Null);
      Assert.That(postDominators.ImmediatePostDominatorOf(leftExit), Is.Null);
      Assert.That(postDominators.ImmediatePostDominatorOf(rightExit), Is.Null);
      Assert.That(postDominators.PostDominates(leftExit, entry), Is.False);
      Assert.That(postDominators.PostDominates(rightExit, entry), Is.False);
      Assert.That(postDominators.Exits, Is.EquivalentTo(new[] { leftExit, rightExit }));
    });
  }

  [Test]
  public void Build_GivenAnInfiniteArm_ThenRealExitDoesNotPostDominateTheChoice() {
    var condition = new IrArgument(IrType.I1, 0, "condition");
    var function = new IrFunction("infiniteArm", IrType.Void, [condition]);
    var entry = function.AddBlock(new IrBasicBlock("entry"));
    var spin = function.AddBlock(new IrBasicBlock("spin"));
    var exit = function.AddBlock(new IrBasicBlock("exit"));

    entry.Append(new IrCondBr(condition, spin, exit));
    spin.Append(new IrBr(spin));
    exit.Append(new IrRet());

    var postDominators = IrPostDominators.Build(function)!;

    Assert.Multiple(() => {
      Assert.That(postDominators.CanReachExit(entry), Is.True);
      Assert.That(postDominators.CanReachExit(exit), Is.True);
      Assert.That(postDominators.CanReachExit(spin), Is.False);
      Assert.That(postDominators.ImmediatePostDominatorOf(entry), Is.Null);
      Assert.That(postDominators.PostDominates(exit, entry), Is.False);
      Assert.That(postDominators.PostDominates(spin, spin), Is.True);
    });
  }

  [Test]
  public void AnalysisManager_GivenPostDominatorsTwice_ThenReturnsTheCachedResult() {
    var function = new IrFunction("cached", IrType.Void);
    function.AddBlock(new IrBasicBlock("entry")).Append(new IrRet());
    var analyses = new IrAnalysisManager(function);

    var first = analyses.Get(IrAnalyses.PostDominators);
    var second = analyses.Get(IrAnalyses.PostDominators);

    Assert.That(second, Is.SameAs(first));
  }
}
