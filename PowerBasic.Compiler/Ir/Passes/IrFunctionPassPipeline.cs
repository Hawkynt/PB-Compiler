using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Analysis-aware execution core for function transforms. The production <see cref="IrPassManager"/> can migrate to
/// this runner without forcing every existing pass to adopt the new contract at once.
/// </summary>
public sealed class IrFunctionPassPipeline {

  private readonly List<(string Name, Func<IrFunction, IrAnalysisManager, IrPassResult> Run)> _passes = [];

  /// <summary>When true, verifies the function after every pass.</summary>
  public bool VerifyEachPass { get; set; }

  /// <summary>Adds an analysis-aware transform.</summary>
  public IrFunctionPassPipeline Add(string name, Func<IrFunction, IrAnalysisManager, IrPassResult> pass) {
    if (string.IsNullOrWhiteSpace(name))
      throw new ArgumentException("Pass name cannot be empty.", nameof(name));
    ArgumentNullException.ThrowIfNull(pass);
    this._passes.Add((name, pass));
    return this;
  }

  /// <summary>
  /// Adds an existing integer-result pass. A changing legacy pass conservatively invalidates all cached analyses.
  /// </summary>
  public IrFunctionPassPipeline AddLegacy(string name, Func<IrFunction, int> pass) {
    ArgumentNullException.ThrowIfNull(pass);
    return this.Add(name, (function, _) => {
      var changes = pass(function);
      return changes == 0 ? IrPassResult.Unchanged : IrPassResult.Changed(changes);
    });
  }

  /// <summary>Runs the pipeline once with a fresh analysis cache.</summary>
  public int Run(IrFunction function) {
    ArgumentNullException.ThrowIfNull(function);
    return this.Run(function, new IrAnalysisManager(function));
  }

  /// <summary>Runs the pipeline to a bounded fixed point while retaining analyses that passes explicitly preserve.</summary>
  public int RunToFixpoint(IrFunction function, int maxIterations = 16) {
    ArgumentNullException.ThrowIfNull(function);
    if (maxIterations <= 0)
      throw new ArgumentOutOfRangeException(nameof(maxIterations));
    if (function.HasErrorHandler || function.HasInlineAsm)
      return 0;

    var analyses = new IrAnalysisManager(function);
    var total = 0;
    for (var i = 0; i < maxIterations; ++i) {
      var changes = this.Run(function, analyses);
      total += changes;
      if (changes == 0)
        break;
    }
    return total;
  }

  private int Run(IrFunction function, IrAnalysisManager analyses) {
    if (function.HasErrorHandler || function.HasInlineAsm)
      return 0;

    var total = 0;
    foreach (var (name, run) in this._passes) {
      var result = run(function, analyses);
      total += result.Changes;
      if (result.Changes > 0)
        analyses.Invalidate(result.PreservedAnalyses);

      if (!this.VerifyEachPass)
        continue;
      var errors = IrVerifier.Verify(function);
      if (errors.Count > 0)
        throw new IrVerificationException(name, errors);
    }
    return total;
  }
}
