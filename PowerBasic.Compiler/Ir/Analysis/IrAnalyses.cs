namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>Shared identities for function analyses used across optimization passes.</summary>
public static class IrAnalyses {

  /// <summary>CFG dominators and dominance frontiers.</summary>
  public static IrAnalysisKey<IrDominators?> Dominators { get; } =
    new("dominators", static (function, _) => IrDominators.Build(function), IrAnalysisSets.Cfg);

  /// <summary>CFG post-dominators and post-dominance frontiers.</summary>
  public static IrAnalysisKey<IrPostDominators?> PostDominators { get; } =
    new("post-dominators", static (function, _) => IrPostDominators.Build(function), IrAnalysisSets.Cfg);

  /// <summary>Natural-loop forest derived from the function CFG and dominators.</summary>
  public static IrAnalysisKey<IrLoopAnalysis> Loops { get; } =
    new("loops", static (function, analyses) =>
      IrLoopAnalysis.Build(function, analyses.Get(Dominators)), IrAnalysisSets.Cfg);

  /// <summary>Additive loop recurrences and exact canonical trip counts.</summary>
  public static IrAnalysisKey<IrScalarEvolution> ScalarEvolution { get; } =
    new("scalar-evolution", static (function, analyses) =>
      IrScalarEvolution.Build(function, analyses.Get(Loops)));

  /// <summary>Branch-refined integer range facts derived from SSA and dominance.</summary>
  public static IrAnalysisKey<IrRangeAnalysis?> Ranges { get; } =
    new("ranges", static (function, analyses) =>
      IrRangeAnalysis.Build(function, analyses.Get(Dominators)));

  /// <summary>Floating domains adapted from the shared integer range facts.</summary>
  public static IrAnalysisKey<FpDomainAnalysis?> FpDomains { get; } =
    new("fp-domains", static (function, analyses) =>
      FpDomainAnalysis.Build(function, analyses.Get(Ranges)));

  /// <summary>Known-zero/known-one facts for integer SSA values.</summary>
  public static IrAnalysisKey<IrKnownBitsAnalysis> KnownBits { get; } =
    new("known-bits", static (function, _) => new IrKnownBitsAnalysis(function));

  /// <summary>Explicit-guard pointer nullness derived from CFG dominance.</summary>
  public static IrAnalysisKey<IrNullnessAnalysis> Nullness { get; } =
    new("nullness", static (_, analyses) => new IrNullnessAnalysis(analyses.Get(Dominators)));

  /// <summary>Common program-point facade over range, known-bits and nullness domains.</summary>
  public static IrAnalysisKey<IrValueFacts> Facts { get; } =
    new("value-facts", static (_, analyses) => new IrValueFacts(
      analyses.Get(Ranges),
      analyses.Get(KnownBits),
      analyses.Get(Nullness)));

  /// <summary>
  /// Shared memory read/write projection. Module-owned runs refine direct internal calls through cached
  /// function summaries; standalone function runs conservatively keep those calls opaque.
  /// </summary>
  public static IrAnalysisKey<IrModRefAnalysis> ModRef { get; } =
    new("mod-ref", static (_, analyses) => new IrModRefAnalysis(
      analyses.ModuleAnalyses?.Get(IrModuleAnalyses.FunctionSummaries)));

  /// <summary>SSA overlay for memory definitions, uses and merge points.</summary>
  public static IrAnalysisKey<IrMemorySsa> MemorySsa { get; } =
    new("memory-ssa", static (function, analyses) =>
      IrMemorySsa.Build(function, analyses.Get(Dominators), analyses.Get(ModRef)));
}
