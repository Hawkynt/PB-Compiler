namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>Identity of a lazily computed analysis over one IR function.</summary>
public abstract class IrAnalysisKey {

  protected IrAnalysisKey(string name) {
    if (string.IsNullOrWhiteSpace(name))
      throw new ArgumentException("Analysis name cannot be empty.", nameof(name));
    this.Name = name;
  }

  /// <summary>Diagnostic name of the analysis.</summary>
  public string Name { get; }

  public override string ToString() => this.Name;
}

/// <summary>Typed identity and factory for one lazily computed function analysis.</summary>
public sealed class IrAnalysisKey<TResult> : IrAnalysisKey {

  private readonly Func<IrFunction, IrAnalysisManager, TResult> _compute;

  public IrAnalysisKey(string name, Func<IrFunction, IrAnalysisManager, TResult> compute)
      : base(name) {
    ArgumentNullException.ThrowIfNull(compute);
    this._compute = compute;
  }

  internal TResult Compute(IrFunction function, IrAnalysisManager analyses)
    => this._compute(function, analyses);
}
