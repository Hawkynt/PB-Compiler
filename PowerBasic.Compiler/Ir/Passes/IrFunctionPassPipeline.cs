using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Analysis-aware execution core for function transforms. Every registered transform receives the shared
/// function analysis manager and reports exactly which cached results remain valid after a mutation.
/// </summary>
public sealed class IrFunctionPassPipeline {

  private readonly List<(IrMiddleEndPhase Phase, string Name, Func<IrFunction, IrAnalysisManager, IrPassResult> Run)> _passes = [];
  private readonly List<IrPassDescriptor> _lastChangedPasses = [];

  /// <summary>The registered function-pass plan in execution order.</summary>
  public IReadOnlyList<IrPassDescriptor> Plan
    => this._passes.Select(pass => new IrPassDescriptor(pass.Phase, IrPassScope.Function, pass.Name)).ToArray();

  /// <summary>The most recent bounded fixed-point exhaustion, or null when the last run converged.</summary>
  public IrFixpointDiagnostic? LastFixpointDiagnostic { get; private set; }

  /// <summary>When true, verifies the function after every pass.</summary>
  public bool VerifyEachPass { get; set; }

  /// <summary>Adds an analysis-aware transform.</summary>
  public IrFunctionPassPipeline Add(string name, Func<IrFunction, IrAnalysisManager, IrPassResult> pass)
    => this.Add(IrMiddleEndPhase.Unspecified, name, pass);

  /// <summary>Adds an analysis-aware transform to an explicit production phase.</summary>
  public IrFunctionPassPipeline Add(
      IrMiddleEndPhase phase,
      string name,
      Func<IrFunction, IrAnalysisManager, IrPassResult> pass) {
    if (string.IsNullOrWhiteSpace(name))
      throw new ArgumentException("Pass name cannot be empty.", nameof(name));
    ArgumentNullException.ThrowIfNull(pass);
    this._passes.Add((phase, name, pass));
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
  public int RunToFixpoint(IrFunction function, int maxIterations = 16)
    => this.RunToFixpoint(function, moduleAnalyses: null, maxIterations: maxIterations);

  /// <summary>
  /// Module-owned fixed point. Function analyses may consume cached module facts; every function mutation
  /// conservatively invalidates those facts before the next transform can query them.
  /// </summary>
  internal int RunToFixpoint(
      IrFunction function,
      IrModuleAnalysisManager? moduleAnalyses,
      int maxIterations = 16) {
    ArgumentNullException.ThrowIfNull(function);
    this.LastFixpointDiagnostic = null;
    if (maxIterations <= 0 || function.HasErrorHandler || function.HasInlineAsm)
      return 0;

    var analyses = new IrAnalysisManager(function, moduleAnalyses);
    var total = 0;
    for (var i = 0; i < maxIterations; ++i) {
      var changes = this.Run(function, analyses);
      total += changes;
      if (changes == 0)
        return total;
      if (i == maxIterations - 1)
        this.LastFixpointDiagnostic = new(
          function.Name,
          maxIterations,
          i + 1,
          this._lastChangedPasses.ToArray());
    }
    return total;
  }

  private int Run(IrFunction function, IrAnalysisManager analyses) {
    if (function.HasErrorHandler || function.HasInlineAsm)
      return 0;

    var total = 0;
    this._lastChangedPasses.Clear();
    foreach (var (phase, name, run) in this._passes) {
      var result = run(function, analyses);
      total += result.Changes;
      if (result.Changes > 0) {
        this._lastChangedPasses.Add(new(phase, IrPassScope.Function, name));
        analyses.Invalidate(result.PreservedAnalyses);
        // Function transforms do not yet report cross-unit preservation. Invalidating module facts here
        // is conservative but prevents a later function pass from consuming a stale call graph/summary.
        analyses.ModuleAnalyses?.Invalidate(IrModulePreservedAnalyses.None);
      }

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
