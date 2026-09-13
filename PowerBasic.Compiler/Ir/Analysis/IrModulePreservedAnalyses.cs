namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>Describes which cached module analyses remain valid after an interprocedural transform.</summary>
public sealed class IrModulePreservedAnalyses {
  private readonly HashSet<IrModuleAnalysisKey> _analyses;

  private IrModulePreservedAnalyses(bool preservesAll, IEnumerable<IrModuleAnalysisKey>? analyses = null) {
    this.PreservesAll = preservesAll;
    this._analyses = analyses is null ? [] : new HashSet<IrModuleAnalysisKey>(analyses);
  }

  public static IrModulePreservedAnalyses All { get; } = new(true);
  public static IrModulePreservedAnalyses None { get; } = new(false);

  public bool PreservesAll { get; }

  public static IrModulePreservedAnalyses Preserve(params IrModuleAnalysisKey[] analyses) {
    ArgumentNullException.ThrowIfNull(analyses);
    if (analyses.Any(static analysis => analysis is null))
      throw new ArgumentException("Preserved module analyses cannot contain null.", nameof(analyses));
    return new(false, analyses);
  }

  public bool IsPreserved(IrModuleAnalysisKey analysis) {
    ArgumentNullException.ThrowIfNull(analysis);
    return this.PreservesAll || this._analyses.Contains(analysis);
  }
}
