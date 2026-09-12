namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>
/// Describes which cached analyses remain valid after a transformation.
/// Unknown preservation is deliberately conservative: a changing pass preserves nothing unless it says otherwise.
/// </summary>
public sealed class IrPreservedAnalyses {

  private readonly HashSet<IrAnalysisKey> _analyses;

  private IrPreservedAnalyses(bool preservesAll, IEnumerable<IrAnalysisKey>? analyses = null) {
    this.PreservesAll = preservesAll;
    this._analyses = analyses is null ? [] : new HashSet<IrAnalysisKey>(analyses);
  }

  /// <summary>All analyses remain valid.</summary>
  public static IrPreservedAnalyses All { get; } = new(true);

  /// <summary>No cached analysis is known to remain valid.</summary>
  public static IrPreservedAnalyses None { get; } = new(false);

  /// <summary>True when every cached analysis is preserved.</summary>
  public bool PreservesAll { get; }

  /// <summary>Creates a preservation set containing exactly the supplied analyses.</summary>
  public static IrPreservedAnalyses Preserve(params IrAnalysisKey[] analyses) {
    ArgumentNullException.ThrowIfNull(analyses);
    if (analyses.Any(static analysis => analysis is null))
      throw new ArgumentException("Preserved analyses cannot contain null.", nameof(analyses));
    return new IrPreservedAnalyses(false, analyses);
  }

  /// <summary>True when the given analysis remains valid.</summary>
  public bool IsPreserved(IrAnalysisKey analysis) {
    ArgumentNullException.ThrowIfNull(analysis);
    return this.PreservesAll || this._analyses.Contains(analysis);
  }
}
