namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>Identity of a lazily computed analysis over one IR function.</summary>
public abstract class IrAnalysisKey {

  private readonly HashSet<IrAnalysisSet> _sets;

  protected IrAnalysisKey(string name, params IrAnalysisSet[] sets) {
    if (string.IsNullOrWhiteSpace(name))
      throw new ArgumentException("Analysis name cannot be empty.", nameof(name));
    ArgumentNullException.ThrowIfNull(sets);
    if (sets.Any(static set => set is null))
      throw new ArgumentException("Analysis sets cannot contain null.", nameof(sets));

    this.Name = name;
    this._sets = [.. sets];
  }

  /// <summary>Diagnostic name of the analysis.</summary>
  public string Name { get; }

  internal bool BelongsTo(IrAnalysisSet set) => this._sets.Contains(set);

  public override string ToString() => this.Name;
}

/// <summary>Typed identity and factory for one lazily computed function analysis.</summary>
public sealed class IrAnalysisKey<TResult> : IrAnalysisKey {

  private readonly Func<IrFunction, IrAnalysisManager, TResult> _compute;

  public IrAnalysisKey(string name, Func<IrFunction, IrAnalysisManager, TResult> compute, params IrAnalysisSet[] sets)
      : base(name, sets) {
    ArgumentNullException.ThrowIfNull(compute);
    this._compute = compute;
  }

  internal TResult Compute(IrFunction function, IrAnalysisManager analyses)
    => this._compute(function, analyses);
}
