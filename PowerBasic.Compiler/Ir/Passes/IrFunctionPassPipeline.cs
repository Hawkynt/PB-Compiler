using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Analysis-aware execution core for function transforms. Every registered transform receives the shared
/// function analysis manager and reports exactly which cached results remain valid after a mutation.
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
  /// Runs one analysis-aware pass outside a composed pipeline. Compatibility entry points use this
  /// instead of constructing their own analysis manager, keeping cache ownership in the executor.
  /// </summary>
  internal static int RunStandalone(
      IrFunction function,
      string name,
      Func<IrFunction, IrAnalysisManager, IrPassResult> pass)
    => new IrFunctionPassPipeline().Add(name, pass).Run(function);

  /// <summary>Runs the pipeline once with a fresh analysis cache.</summary>
  public int Run(IrFunction function) {
    ArgumentNullException.ThrowIfNull(function);
    return this.Run(function, new IrAnalysisManager(function));
  }

  /// <summary>Runs the pipeline to a bounded fixed point while retaining analyses that passes explicitly preserve.</summary>
  public int RunToFixpoint(IrFunction function, int maxIterations = 16) {
    ArgumentNullException.ThrowIfNull(function);
    if (maxIterations <= 0 || function.HasErrorHandler || function.HasInlineAsm)
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
      // Deliberately rebuild verifier analyses independently. Verification must be able to catch a pass
      // that incorrectly claims to preserve an analysis instead of trusting that preservation claim.
      var errors = IrVerifier.Verify(function);
      if (errors.Count > 0)
        throw new IrVerificationException(name, errors);
    }
    return total;
  }
}
