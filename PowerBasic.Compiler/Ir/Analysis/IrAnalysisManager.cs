namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>
/// Lazily computes and caches analyses for one function. Analysis-to-analysis queries are tracked as
/// dependencies so invalidating a prerequisite also invalidates every cached result derived from it.
/// </summary>
public sealed class IrAnalysisManager {

  private readonly Dictionary<IrAnalysisKey, object?> _cache = new();
  private readonly Dictionary<IrAnalysisKey, HashSet<IrAnalysisKey>> _dependencies = new();
  private readonly Stack<IrAnalysisKey> _computing = new();

  public IrAnalysisManager(IrFunction function, IrModuleAnalysisManager? moduleAnalyses = null) {
    ArgumentNullException.ThrowIfNull(function);
    this.Function = function;
    this.ModuleAnalyses = moduleAnalyses;
  }

  /// <summary>The function whose analyses are owned by this manager.</summary>
  public IrFunction Function { get; }

  /// <summary>
  /// Module analysis owner enclosing this function-analysis lifetime, when the pipeline is executing
  /// as part of a module. Standalone function entry points deliberately leave this null.
  /// </summary>
  public IrModuleAnalysisManager? ModuleAnalyses { get; }

  /// <summary>Gets a cached analysis result, computing and caching it on first use.</summary>
  public TResult Get<TResult>(IrAnalysisKey<TResult> analysis) {
    ArgumentNullException.ThrowIfNull(analysis);

    if (this._computing.TryPeek(out var consumer) && !ReferenceEquals(consumer, analysis))
      (this._dependencies.TryGetValue(consumer, out var dependencies)
        ? dependencies
        : this._dependencies[consumer] = []).Add(analysis);

    if (this._cache.TryGetValue(analysis, out var cached))
      return (TResult)cached!;

    if (this._computing.Contains(analysis))
      throw new InvalidOperationException($"Analysis dependency cycle detected at '{analysis.Name}'.");

    this._dependencies.Remove(analysis);
    this._computing.Push(analysis);
    try {
      var result = analysis.Compute(this.Function, this);
      this._cache.Add(analysis, result);
      return result;
    } finally {
      this._computing.Pop();
    }
  }

  /// <summary>True when the analysis has already been computed and remains valid.</summary>
  public bool IsCached(IrAnalysisKey analysis) {
    ArgumentNullException.ThrowIfNull(analysis);
    return this._cache.ContainsKey(analysis);
  }

  /// <summary>
  /// Invalidates every cached result not covered by the preservation set, then transitively invalidates
  /// preserved analyses whose recorded prerequisites became invalid.
  /// </summary>
  public void Invalidate(IrPreservedAnalyses preserved) {
    ArgumentNullException.ThrowIfNull(preserved);
    if (preserved.PreservesAll)
      return;

    var invalidated = this._cache.Keys
      .Where(key => !preserved.IsPreserved(key))
      .ToHashSet();

    bool changed;
    do {
      changed = false;
      foreach (var analysis in this._cache.Keys) {
        if (invalidated.Contains(analysis)
            || !this._dependencies.TryGetValue(analysis, out var dependencies)
            || !dependencies.Overlaps(invalidated))
          continue;
        invalidated.Add(analysis);
        changed = true;
      }
    } while (changed);

    foreach (var analysis in invalidated) {
      this._cache.Remove(analysis);
      this._dependencies.Remove(analysis);
    }

    foreach (var dependencies in this._dependencies.Values)
      dependencies.RemoveWhere(invalidated.Contains);
  }

  /// <summary>Drops all cached analysis results and dependency metadata for the function.</summary>
  public void Clear() {
    this._cache.Clear();
    this._dependencies.Clear();
  }
}
