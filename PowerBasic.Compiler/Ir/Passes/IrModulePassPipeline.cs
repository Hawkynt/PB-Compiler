using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>Ordered module-pass executor with one shared cached analysis manager.</summary>
public sealed class IrModulePassPipeline {
  private readonly List<(string Name, Func<IrModule, IrModuleAnalysisManager, IrModulePassResult> Run)> _passes = [];

  public IrModulePassPipeline Add(
      string name,
      Func<IrModule, IrModuleAnalysisManager, IrModulePassResult> pass) {
    if (string.IsNullOrWhiteSpace(name))
      throw new ArgumentException("Pass name cannot be empty.", nameof(name));
    ArgumentNullException.ThrowIfNull(pass);
    this._passes.Add((name, pass));
    return this;
  }

  /// <summary>
  /// Runs every pass once. <paramref name="afterChange"/> runs after invalidation for each mutating pass;
  /// callers use it to re-run function fixed points exposed by interprocedural changes.
  /// </summary>
  public int Run(
      IrModule module,
      IrModuleAnalysisManager analyses,
      Action? afterChange = null) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(analyses);
    if (!ReferenceEquals(module, analyses.Module))
      throw new ArgumentException("Module analysis manager belongs to a different module.", nameof(analyses));

    var total = 0;
    foreach (var (_, run) in this._passes) {
      var result = run(module, analyses);
      total += result.Changes;
      if (result.Changes == 0)
        continue;
      analyses.Invalidate(result.PreservedAnalyses);
      afterChange?.Invoke();
    }
    return total;
  }
}
