namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>Identity of a lazily computed analysis over one IR module.</summary>
public abstract class IrModuleAnalysisKey {
  protected IrModuleAnalysisKey(string name) {
    if (string.IsNullOrWhiteSpace(name))
      throw new ArgumentException("Module analysis name cannot be empty.", nameof(name));
    this.Name = name;
  }

  /// <summary>Diagnostic name of the analysis.</summary>
  public string Name { get; }

  public override string ToString() => this.Name;
}

/// <summary>Typed identity and factory for one lazily computed module analysis.</summary>
public sealed class IrModuleAnalysisKey<TResult> : IrModuleAnalysisKey {
  private readonly Func<IrModule, IrModuleAnalysisManager, TResult> _compute;

  public IrModuleAnalysisKey(string name, Func<IrModule, IrModuleAnalysisManager, TResult> compute)
      : base(name) {
    ArgumentNullException.ThrowIfNull(compute);
    this._compute = compute;
  }

  internal TResult Compute(IrModule module, IrModuleAnalysisManager analyses)
    => this._compute(module, analyses);
}
