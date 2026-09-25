using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Analysis;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class IrModuleAnalysesTests {

  private static IrFunction VoidFunction(IrModule module, string name) {
    var function = module.AddFunction(new IrFunction(name, IrType.Void));
    return function;
  }

  [Test]
  public void CachedModuleFacts_GivenAClosedDirectCallGraph_ThenShareDependenciesAndReachability() {
    var module = new IrModule("closed") { OwnsProcedureAbi = true };
    var main = VoidFunction(module, "main");
    var helper = VoidFunction(module, "helper");
    var deadA = VoidFunction(module, "dead-a");
    var deadB = VoidFunction(module, "dead-b");

    var helperBuilder = new IrBuilder(helper.CreateBlock("entry"));
    helperBuilder.Ret();

    var mainBuilder = new IrBuilder(main.CreateBlock("entry"));
    mainBuilder.Call(IrType.Void, helper);
    mainBuilder.Ret();

    var aBuilder = new IrBuilder(deadA.CreateBlock("entry"));
    aBuilder.Call(IrType.Void, deadB);
    aBuilder.Ret();
    var bBuilder = new IrBuilder(deadB.CreateBlock("entry"));
    bBuilder.Call(IrType.Void, deadA);
    bBuilder.Ret();

    var analyses = new IrModuleAnalysisManager(module);
    var graph = analyses.Get(IrModuleAnalyses.CallGraph);
    var summaries = analyses.Get(IrModuleAnalyses.FunctionSummaries);
    var reachability = analyses.Get(IrModuleAnalyses.Reachability);

    Assert.Multiple(() => {
      Assert.That(graph.DirectCalleesOf(main), Is.EqualTo(new[] { helper }));
      Assert.That(graph.IsFullyVisible(helper), Is.True);
      Assert.That(summaries.For(helper).CanDiscard, Is.True);
      Assert.That(reachability.IsComplete, Is.True);
      Assert.That(reachability.IsReachable(main), Is.True);
      Assert.That(reachability.IsReachable(helper), Is.True);
      Assert.That(reachability.IsReachable(deadA), Is.False);
      Assert.That(reachability.IsReachable(deadB), Is.False);
      Assert.That(analyses.IsCached(IrModuleAnalyses.CallGraph), Is.True);
      Assert.That(analyses.IsCached(IrModuleAnalyses.FunctionSummaries), Is.True);
      Assert.That(analyses.IsCached(IrModuleAnalyses.Reachability), Is.True);
    });

    // Both higher-level facts queried CallGraph while being computed. Preserving them while invalidating
    // their prerequisite must therefore invalidate them transitively.
    analyses.Invalidate(IrModulePreservedAnalyses.Preserve(
      IrModuleAnalyses.FunctionSummaries,
      IrModuleAnalyses.Reachability));

    Assert.Multiple(() => {
      Assert.That(analyses.IsCached(IrModuleAnalyses.CallGraph), Is.False);
      Assert.That(analyses.IsCached(IrModuleAnalyses.FunctionSummaries), Is.False);
      Assert.That(analyses.IsCached(IrModuleAnalyses.Reachability), Is.False);
    });
  }

  [Test]
  public void Reachability_GivenAnIndirectCall_ThenFailsClosed() {
    var target = new IrArgument(IrType.Ptr, 0, "target");
    var module = new IrModule("indirect") { OwnsProcedureAbi = true };
    var main = module.AddFunction(new IrFunction("main", IrType.Void, [target]));
    var otherwiseDead = VoidFunction(module, "otherwise-dead");
    new IrBuilder(otherwiseDead.CreateBlock("entry")).Ret();

    var builder = new IrBuilder(main.CreateBlock("entry"));
    builder.Call(IrType.Void, target);
    builder.Ret();

    var reachability = new IrModuleAnalysisManager(module).Get(IrModuleAnalyses.Reachability);

    Assert.Multiple(() => {
      Assert.That(reachability.IsComplete, Is.False);
      Assert.That(reachability.IsReachable(main), Is.True);
      Assert.That(reachability.IsReachable(otherwiseDead), Is.True,
        "an incomplete whole-program proof must not classify an arbitrary function as dead");
    });
  }


  [Test]
  public void CallGraph_GivenACalleeAlsoPassedAsData_ThenTheFunctionIsNotFullyVisible() {
    var callback = new IrArgument(IrType.Ptr, 0, "callback");
    var module = new IrModule("escaped") { OwnsProcedureAbi = true };
    var target = module.AddFunction(new IrFunction("target", IrType.Void, [callback]));
    new IrBuilder(target.CreateBlock("entry")).Ret();

    var main = VoidFunction(module, "main");
    var builder = new IrBuilder(main.CreateBlock("entry"));
    builder.Call(IrType.Void, target, target);
    builder.Ret();

    var graph = new IrModuleAnalysisManager(module).Get(IrModuleAnalyses.CallGraph);

    Assert.That(graph.IsFullyVisible(target), Is.False,
      "the direct call does not make the simultaneous data use disappear");
  }

  [Test]
  public void AnalysisAwareConsumers_GivenSharedManager_ThenPopulateTheModuleCache() {
    var module = new IrModule("consumers");
    var calc = module.AddFunction(new IrFunction("calc", IrType.I16));
    var calcBuilder = new IrBuilder(calc.CreateBlock("entry"));
    calcBuilder.Ret(new IrConstantInt(IrType.I16, 7));

    var main = VoidFunction(module, "main");
    var mainBuilder = new IrBuilder(main.CreateBlock("entry"));
    var call = mainBuilder.Call(IrType.I16, calc);
    mainBuilder.Ret();

    var analyses = new IrModuleAnalysisManager(module);
    var result = FunctionSummaries.RemoveDeadPureCalls(module, analyses);

    Assert.Multiple(() => {
      Assert.That(result.Changes, Is.EqualTo(1));
      Assert.That(call.Parent, Is.Null);
      Assert.That(analyses.IsCached(IrModuleAnalyses.CallGraph), Is.True);
      Assert.That(analyses.IsCached(IrModuleAnalyses.FunctionSummaries), Is.True);
    });

    var second = new IrModuleAnalysisManager(module);
    IpConstantProp.Run(module, second);
    Assert.That(second.IsCached(IrModuleAnalyses.CallGraph), Is.True);
  }
}
