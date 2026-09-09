using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// O0276 — middle-end post-link layout planning. These tests pin the contract the eventual fragment
/// rewriter can consume without disassembling its own output: stable IDs, validated CFG edges and a
/// profile objective that never gets worse merely because a different order was available.
/// </summary>
[TestFixture]
public sealed class PostLinkOptimizationTests {

  [Test]
  public void Plan_GivenAHotPathInterruptedByAColdBlock_ThenItBuildsAHotFallthroughChain() {
    var fixture = BuildDiamond();
    var profile = new[] {
      Edge(fixture.Fn, 0, 2, 100),
      Edge(fixture.Fn, 0, 1, 1),
      Edge(fixture.Fn, 2, 3, 100),
      Edge(fixture.Fn, 1, 3, 1),
    };

    var plan = PostLinkOptimization.Plan(fixture.Fn, profile)!;

    Assert.Multiple(() => {
      Assert.That(plan.Fragments.Select(fragment => fragment.Block.Label), Is.EqualTo(new[] { "entry", "hot", "exit", "cold" }));
      Assert.That(plan.OriginalFallthroughWeight, Is.EqualTo(101));
      Assert.That(plan.PlannedFallthroughWeight, Is.EqualTo(200));
      Assert.That(plan.ProfiledEdgeWeight, Is.EqualTo(202));
      Assert.That(plan.ChangesLayout, Is.True);
      Assert.That(plan.WeightedFallthroughRatio, Is.EqualTo(200d / 202d).Within(1e-12));
      Assert.That(IrVerifier.Verify(fixture.Fn), Is.Empty);
    });
  }

  [Test]
  public void Plan_GivenAReorderedLayout_ThenFragmentIdsStillNameTheOriginalStructuralBlocks() {
    var fixture = BuildDiamond();

    var plan = PostLinkOptimization.Plan(fixture.Fn, [
      Edge(fixture.Fn, 0, 2, 100),
      Edge(fixture.Fn, 2, 3, 100),
      Edge(fixture.Fn, 0, 1, 1),
      Edge(fixture.Fn, 1, 3, 1),
    ])!;

    var hot = plan.Fragments.Single(fragment => fragment.Block.Label == "hot");
    var entry = plan.Fragments.Single(fragment => fragment.Block.Label == "entry");
    Assert.Multiple(() => {
      Assert.That(hot.Id, Is.EqualTo(new IrPostLinkBlockId(fixture.Fn.Name, 2)));
      Assert.That(entry.Id, Is.EqualTo(new IrPostLinkBlockId(fixture.Fn.Name, 0)));
      Assert.That(entry.Successors,
        Is.EquivalentTo(new[] { new IrPostLinkBlockId(fixture.Fn.Name, 2), new IrPostLinkBlockId(fixture.Fn.Name, 1) }));
      Assert.That(fixture.Fn.Blocks.Select(block => block.Label), Is.EqualTo(new[] { "entry", "cold", "hot", "exit" }),
        "planning is metadata-only; SSA block order must not become physical layout state");
    });
  }

  [Test]
  public void Plan_GivenTheExistingOrderScoresAtLeastAsWell_ThenItKeepsThatOrder() {
    var fixture = BuildDiamond();

    var plan = PostLinkOptimization.Plan(fixture.Fn, [
      Edge(fixture.Fn, 0, 1, 100),
      Edge(fixture.Fn, 2, 3, 100),
      Edge(fixture.Fn, 0, 2, 1),
      Edge(fixture.Fn, 1, 3, 1),
    ])!;

    Assert.Multiple(() => {
      Assert.That(plan.Fragments.Select(fragment => fragment.Block.Label), Is.EqualTo(new[] { "entry", "cold", "hot", "exit" }));
      Assert.That(plan.ChangesLayout, Is.False);
      Assert.That(plan.OriginalFallthroughWeight, Is.EqualTo(200));
      Assert.That(plan.PlannedFallthroughWeight, Is.EqualTo(200));
    });
  }

  [Test]
  public void Plan_GivenDuplicateSamplesForAnEdge_ThenItAggregatesThem() {
    var fn = new IrFunction("linear", IrType.Void);
    var entry = fn.CreateBlock("entry");
    var exit = fn.CreateBlock("exit");
    entry.Append(new IrBr(exit));
    exit.Append(new IrRet());

    var plan = PostLinkOptimization.Plan(fn, [
      Edge(fn, 0, 1, 7),
      Edge(fn, 0, 1, 11),
    ])!;

    Assert.Multiple(() => {
      Assert.That(plan.ProfiledEdgeWeight, Is.EqualTo(18));
      Assert.That(plan.OriginalFallthroughWeight, Is.EqualTo(18));
      Assert.That(plan.PlannedFallthroughWeight, Is.EqualTo(18));
    });
  }

  [Test]
  public void Plan_GivenAProfileEdgeThatIsNotInTheCurrentCfg_ThenItRejectsTheProfile() {
    var fixture = BuildDiamond();

    Assert.That(() => PostLinkOptimization.Plan(fixture.Fn, [Edge(fixture.Fn, 1, 2, 10)]),
      Throws.ArgumentException.With.Message.Contains("not present in the current CFG"));
  }

  [Test]
  public void Plan_GivenAProfileForAnotherFunction_ThenItRejectsTheProfile() {
    var fixture = BuildDiamond();
    var sample = new IrPostLinkEdgeSample(new("old-build", 0), new("old-build", 2), 10);

    Assert.That(() => PostLinkOptimization.Plan(fixture.Fn, [sample]),
      Throws.ArgumentException.With.Message.Contains("does not identify a block"));
  }

  [TestCase(true, false)]
  [TestCase(false, true)]
  public void Plan_GivenOpaqueControlFlow_ThenItDeclines(bool errorHandler, bool inlineAsm) {
    var fixture = BuildDiamond();
    fixture.Fn.HasErrorHandler = errorHandler;
    fixture.Fn.HasInlineAsm = inlineAsm;

    Assert.That(PostLinkOptimization.Plan(fixture.Fn, []), Is.Null);
  }

  [Test]
  public void Plan_GivenNullArguments_ThenItThrows() {
    var fixture = BuildDiamond();

    Assert.Multiple(() => {
      Assert.That(() => PostLinkOptimization.Plan(null!, []), Throws.ArgumentNullException);
      Assert.That(() => PostLinkOptimization.Plan(fixture.Fn, null!), Throws.ArgumentNullException);
    });
  }

  private static IrPostLinkEdgeSample Edge(IrFunction fn, int from, int to, ulong count)
    => new(new(fn.Name, from), new(fn.Name, to), count);

  private static Fixture BuildDiamond() {
    var fn = new IrFunction("diamond", IrType.Void);
    var entry = fn.CreateBlock("entry");
    var cold = fn.CreateBlock("cold");
    var hot = fn.CreateBlock("hot");
    var exit = fn.CreateBlock("exit");

    var condition = entry.Append(new IrCmp(IrCmpPred.Eq,
      new IrConstantInt(IrType.I16, 1), new IrConstantInt(IrType.I16, 1)));
    entry.Append(new IrCondBr(condition, hot, cold));
    cold.Append(new IrBr(exit));
    hot.Append(new IrBr(exit));
    exit.Append(new IrRet());

    return new(fn);
  }

  private sealed record Fixture(IrFunction Fn);
}
