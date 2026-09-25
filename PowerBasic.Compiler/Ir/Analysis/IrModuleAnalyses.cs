namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>First-class cached analyses over a complete IR module.</summary>
public static class IrModuleAnalyses {
  public static readonly IrModuleAnalysisKey<IrCallGraph> CallGraph = new(
    "call-graph",
    static (module, _) => IrCallGraph.Build(module));

  public static readonly IrModuleAnalysisKey<global::PowerBasic.Compiler.Ir.Passes.FunctionSummaries> FunctionSummaries = new(
    "function-summaries",
    static (module, analyses) =>
      global::PowerBasic.Compiler.Ir.Passes.FunctionSummaries.Compute(module, analyses.Get(CallGraph)));

  public static readonly IrModuleAnalysisKey<IrWholeProgramReachability> Reachability = new(
    "whole-program-reachability",
    static (module, analyses) => IrWholeProgramReachability.Build(module, analyses.Get(CallGraph)));

  public static readonly IrModuleAnalysisKey<IrFunctionTargetAnalysis> FunctionTargets = new(
    "function-targets",
    static (module, analyses) => new IrFunctionTargetAnalysis(module, analyses.Get(CallGraph)));
}
