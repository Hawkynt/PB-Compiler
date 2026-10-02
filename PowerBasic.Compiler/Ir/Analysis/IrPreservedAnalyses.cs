namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>
/// Describes which cached analyses remain valid after a transformation.
/// Unknown preservation is deliberately conservative: a changing pass preserves nothing unless it says otherwise.
/// </summary>
public sealed class IrPreservedAnalyses {

  private readonly HashSet<IrAnalysisKey> _analyses;
  private readonly HashSet<IrAnalysisSet> _sets;

  private IrPreservedAnalyses(
      bool preservesAll,
      IEnumerable<IrAnalysisKey>? analyses = null,
      IEnumerable<IrAnalysisSet>? sets = null) {
    this.PreservesAll = preservesAll;
    this._analyses = analyses is null ? [] : new HashSet<IrAnalysisKey>(analyses);
    this._sets = sets is null ? [] : new HashSet<IrAnalysisSet>(sets);
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

  /// <summary>Creates a preservation set containing every analysis belonging to the supplied named sets.</summary>
  public static IrPreservedAnalyses PreserveSets(params IrAnalysisSet[] sets) {
    ArgumentNullException.ThrowIfNull(sets);
    if (sets.Any(static set => set is null))
      throw new ArgumentException("Preserved analysis sets cannot contain null.", nameof(sets));
    return new IrPreservedAnalyses(false, sets: sets);
  }

  /// <summary>True when the given analysis remains valid.</summary>
  public bool IsPreserved(IrAnalysisKey analysis) {
    ArgumentNullException.ThrowIfNull(analysis);
    return this.PreservesAll
      || this._analyses.Contains(analysis)
      || this._sets.Any(set => set.Contains(analysis));
  }
}
