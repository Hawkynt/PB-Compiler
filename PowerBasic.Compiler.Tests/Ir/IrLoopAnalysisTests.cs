using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class IrLoopAnalysisTests {

  [Test]
  public void Build_GivenNestedNaturalLoops_ThenCreatesLoopForest() {
    var outerCondition = new IrArgument(IrType.I1, 0, "outer");
    var innerCondition = new IrArgument(IrType.I1, 1, "inner");
    var function = new IrFunction("nested", IrType.Void, [outerCondition, innerCondition]);
    var entry = function.AddBlock(new IrBasicBlock("entry"));
    var outerHeader = function.AddBlock(new IrBasicBlock("outer.header"));
    var innerHeader = function.AddBlock(new IrBasicBlock("inner.header"));
    var innerBody = function.AddBlock(new IrBasicBlock("inner.body"));
    var outerLatch = function.AddBlock(new IrBasicBlock("outer.latch"));
    var exit = function.AddBlock(new IrBasicBlock("exit"));

    entry.Append(new IrBr(outerHeader));
    outerHeader.Append(new IrCondBr(outerCondition, innerHeader, exit));
    innerHeader.Append(new IrCondBr(innerCondition, innerBody, outerLatch));
    innerBody.Append(new IrBr(innerHeader));
    outerLatch.Append(new IrBr(outerHeader));
    exit.Append(new IrRet());

    var analyses = new IrAnalysisManager(function);
    var loops = analyses.Get(IrAnalyses.Loops);
    var outer = loops.Loops.Single(loop => ReferenceEquals(loop.Header, outerHeader));
    var inner = loops.Loops.Single(loop => ReferenceEquals(loop.Header, innerHeader));

    Assert.Multiple(() => {
      Assert.That(loops.Loops, Has.Count.EqualTo(2));
      Assert.That(loops.TopLevelLoops, Is.EqualTo(new[] { outer }));
      Assert.That(outer.Parent, Is.Null);
      Assert.That(outer.SubLoops, Is.EqualTo(new[] { inner }));
      Assert.That(outer.Depth, Is.EqualTo(1));
      Assert.That(inner.Parent, Is.SameAs(outer));
      Assert.That(inner.Depth, Is.EqualTo(2));
      Assert.That(outer.Preheader, Is.SameAs(entry));
      Assert.That(inner.UniqueEnteringBlock, Is.SameAs(outerHeader));
      Assert.That(inner.Preheader, Is.Null, "the conditional outer header is not a canonical preheader");
      Assert.That(outer.Latches, Is.EqualTo(new[] { outerLatch }));
      Assert.That(inner.Latches, Is.EqualTo(new[] { innerBody }));
      Assert.That(outer.ExitBlocks, Does.Contain(exit));
      Assert.That(inner.ExitBlocks, Does.Contain(outerLatch));
      Assert.That(loops.LoopFor(innerBody), Is.SameAs(inner));
      Assert.That(loops.LoopFor(outerLatch), Is.SameAs(outer));
      Assert.That(loops.LoopFor(entry), Is.Null);
      Assert.That(outer.IsLoopSimplifyForm, Is.True);
      Assert.That(inner.IsLoopSimplifyForm, Is.False);
    });
  }

  [Test]
  public void Invalidate_GivenLoopForestPreservedWithoutDominators_ThenDropsDependentLoopAnalysis() {
    var function = new IrFunction("loop", IrType.Void);
    var entry = function.AddBlock(new IrBasicBlock("entry"));
    entry.Append(new IrBr(entry));
    var analyses = new IrAnalysisManager(function);

    _ = analyses.Get(IrAnalyses.Loops);
    analyses.Invalidate(IrPreservedAnalyses.Preserve(IrAnalyses.Loops));

    Assert.Multiple(() => {
      Assert.That(analyses.IsCached(IrAnalyses.Dominators), Is.False);
      Assert.That(analyses.IsCached(IrAnalyses.Loops), Is.False);
    });
  }
}
