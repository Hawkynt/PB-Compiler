namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>
/// Lazily computes and caches analyses for one function. A transform invalidates every cached result it does not
/// explicitly preserve, making analysis lifetime part of the pass contract instead of private pass folklore.
/// </summary>
public sealed class IrAnalysisManager {

  private readonly Dictionary<IrAnalysisKey, object?> _cache = new();

  public IrAnalysisManager(IrFunction function) {
    ArgumentNullException.ThrowIfNull(function);
    this.Function = function;
  }

  /// <summary>The function whose analyses are owned by this manager.</summary>
  public IrFunction Function { get; }

  /// <summary>Gets a cached analysis result, computing and caching it on first use.</summary>
  public TResult Get<TResult>(IrAnalysisKey<TResult> analysis) {
    ArgumentNullException.ThrowIfNull(analysis);
    if (this._cache.TryGetValue(analysis, out var cached))
      return (TResult)cached!;

    var result = analysis.Compute(this.Function, this);
    this._cache.Add(analysis, result);
    return result;
  }

  /// <summary>True when the analysis has already been computed and remains valid.</summary>
  public bool IsCached(IrAnalysisKey analysis) {
    ArgumentNullException.ThrowIfNull(analysis);
    return this._cache.ContainsKey(analysis);
  }

  /// <summary>Invalidates every cached result not covered by the preservation set.</summary>
  public void Invalidate(IrPreservedAnalyses preserved) {
    ArgumentNullException.ThrowIfNull(preserved);
    if (preserved.PreservesAll)
      return;

    foreach (var analysis in this._cache.Keys.Where(key => !preserved.IsPreserved(key)).ToList())
      this._cache.Remove(analysis);
  }

  /// <summary>Drops all cached analysis results for the function.</summary>
  public void Clear() => this._cache.Clear();
}
