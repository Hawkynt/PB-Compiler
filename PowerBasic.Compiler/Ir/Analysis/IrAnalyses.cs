namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>Shared identities for function analyses used across optimization passes.</summary>
public static class IrAnalyses {

  /// <summary>CFG dominators and dominance frontiers.</summary>
  public static IrAnalysisKey<IrDominators?> Dominators { get; } =
    new("dominators", static (function, _) => IrDominators.Build(function));

  /// <summary>SSA overlay for memory definitions, uses and merge points.</summary>
  public static IrAnalysisKey<IrMemorySsa> MemorySsa { get; } =
    new("memory-ssa", static (function, analyses) =>
      IrMemorySsa.Build(function, analyses.Get(Dominators)));
}
