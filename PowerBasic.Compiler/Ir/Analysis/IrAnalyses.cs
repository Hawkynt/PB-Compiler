namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>Shared identities for function analyses used across optimization passes.</summary>
public static class IrAnalyses {

  /// <summary>CFG dominators and dominance frontiers.</summary>
  public static IrAnalysisKey<IrDominators?> Dominators { get; } =
    new("dominators", static (function, _) => IrDominators.Build(function));
}
