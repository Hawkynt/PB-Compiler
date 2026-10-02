namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>
/// Lazily computes and caches analyses for one module. Analysis-to-analysis queries are dependencies,
/// so invalidating a prerequisite transitively invalidates every cached result derived from it.
/// </summary>
public sealed class IrModuleAnalysisManager {
  private readonly Dictionary<IrModuleAnalysisKey, object?> _cache = new();
  private readonly Dictionary<IrModuleAnalysisKey, HashSet<IrModuleAnalysisKey>> _dependencies = new();
  private readonly Stack<IrModuleAnalysisKey> _computing = new();

  public IrModuleAnalysisManager(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    this.Module = module;
  }

  public IrModule Module { get; }

  public TResult Get<TResult>(IrModuleAnalysisKey<TResult> analysis) {
    ArgumentNullException.ThrowIfNull(analysis);

    if (this._computing.TryPeek(out var consumer) && !ReferenceEquals(consumer, analysis))
      (this._dependencies.TryGetValue(consumer, out var dependencies)
        ? dependencies
        : this._dependencies[consumer] = []).Add(analysis);

    if (this._cache.TryGetValue(analysis, out var cached))
      return (TResult)cached!;

    if (this._computing.Contains(analysis))
      throw new InvalidOperationException($"Module analysis dependency cycle detected at '{analysis.Name}'.");

    this._dependencies.Remove(analysis);
    this._computing.Push(analysis);
    try {
      var result = analysis.Compute(this.Module, this);
      this._cache.Add(analysis, result);
      return result;
    } finally {
      this._computing.Pop();
    }
  }

  public bool IsCached(IrModuleAnalysisKey analysis) {
    ArgumentNullException.ThrowIfNull(analysis);
    return this._cache.ContainsKey(analysis);
  }

  public void Invalidate(IrModulePreservedAnalyses preserved) {
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

  public void Clear() {
    this._cache.Clear();
    this._dependencies.Clear();
  }
}
