using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Analysis;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class IrAnalysisManagerTests {

  [Test]
  public void Get_GivenTheSameAnalysisTwice_ThenComputesItOnce() {
    var function = new IrFunction("test", IrType.Void);
    var computations = 0;
    var analysis = new IrAnalysisKey<int>("probe", (fn, _) => {
      ++computations;
      return fn.Blocks.Count;
    });
    var manager = new IrAnalysisManager(function);

    var first = manager.Get(analysis);
    var second = manager.Get(analysis);

    Assert.Multiple(() => {
      Assert.That(first, Is.EqualTo(0));
      Assert.That(second, Is.EqualTo(0));
      Assert.That(computations, Is.EqualTo(1));
      Assert.That(manager.IsCached(analysis), Is.True);
    });
  }

  [Test]
  public void Invalidate_GivenOnePreservedAnalysis_ThenDropsOnlyTheOthers() {
    var function = new IrFunction("test", IrType.Void);
    var kept = new IrAnalysisKey<int>("kept", static (_, _) => 1);
    var dropped = new IrAnalysisKey<int>("dropped", static (_, _) => 2);
    var manager = new IrAnalysisManager(function);
    manager.Get(kept);
    manager.Get(dropped);

    manager.Invalidate(IrPreservedAnalyses.Preserve(kept));

    Assert.Multiple(() => {
      Assert.That(manager.IsCached(kept), Is.True);
      Assert.That(manager.IsCached(dropped), Is.False);
    });
  }

  [Test]
  public void Invalidate_GivenPreservedDependentWithInvalidatedPrerequisite_ThenDropsItTransitively() {
    var function = new IrFunction("test", IrType.Void);
    var rootComputations = 0;
    var middleComputations = 0;
    var leafComputations = 0;
    var root = new IrAnalysisKey<int>("root", (_, _) => ++rootComputations);
    var middle = new IrAnalysisKey<int>("middle", (_, analyses) => analyses.Get(root) + ++middleComputations);
    var leaf = new IrAnalysisKey<int>("leaf", (_, analyses) => analyses.Get(middle) + ++leafComputations);
    var manager = new IrAnalysisManager(function);

    manager.Get(root); // make sure dependencies are recorded even when the prerequisite is already cached
    manager.Get(leaf);
    manager.Invalidate(IrPreservedAnalyses.Preserve(middle, leaf));

    Assert.Multiple(() => {
      Assert.That(manager.IsCached(root), Is.False);
      Assert.That(manager.IsCached(middle), Is.False);
      Assert.That(manager.IsCached(leaf), Is.False);
    });

    manager.Get(leaf);
    Assert.Multiple(() => {
      Assert.That(rootComputations, Is.EqualTo(2));
      Assert.That(middleComputations, Is.EqualTo(2));
      Assert.That(leafComputations, Is.EqualTo(2));
    });
  }

  [Test]
  public void Run_GivenAChangingPassThatPreservesAnAnalysis_ThenReusesItsCachedResult() {
    var function = new IrFunction("test", IrType.Void);
    var computations = 0;
    var analysis = new IrAnalysisKey<int>("probe", (_, _) => ++computations);
    var pipeline = new IrFunctionPassPipeline()
      .Add("read-before", (_, analyses) => {
        analyses.Get(analysis);
        return IrPassResult.Unchanged;
      })
      .Add("operand-rewrite", (_, _) => IrPassResult.ChangedPreserving(1, analysis))
      .Add("read-after", (_, analyses) => {
        analyses.Get(analysis);
        return IrPassResult.Unchanged;
      });

    var changes = pipeline.Run(function);

    Assert.Multiple(() => {
      Assert.That(changes, Is.EqualTo(1));
      Assert.That(computations, Is.EqualTo(1));
    });
  }

  [Test]
  public void Run_GivenAChangingLegacyPass_ThenConservativelyRecomputesAnalyses() {
    var function = new IrFunction("test", IrType.Void);
    var computations = 0;
    var analysis = new IrAnalysisKey<int>("probe", (_, _) => ++computations);
    var pipeline = new IrFunctionPassPipeline()
      .Add("read-before", (_, analyses) => {
        analyses.Get(analysis);
        return IrPassResult.Unchanged;
      })
      .AddLegacy("legacy", _ => 1)
      .Add("read-after", (_, analyses) => {
        analyses.Get(analysis);
        return IrPassResult.Unchanged;
      });

    var changes = pipeline.Run(function);

    Assert.Multiple(() => {
      Assert.That(changes, Is.EqualTo(1));
      Assert.That(computations, Is.EqualTo(2));
    });
  }
}
