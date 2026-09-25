using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>Raised when <see cref="IrPassManager.VerifyEachPass"/> is on and a pass leaves the IR malformed.</summary>
public sealed class IrVerificationException(string pass, IReadOnlyList<string> errors)
  : Exception($"IR invalid after pass '{pass}': {string.Join("; ", errors)}") {
  public string Pass { get; } = pass;
  public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Execution engine for ordered IR transforms. Pipeline policy lives in <see cref="IrMiddleEndPipeline"/>;
/// this type owns pass execution, analysis invalidation, verification and fixed-point iteration only.
/// </summary>
public sealed class IrPassManager {

  private readonly IrFunctionPassPipeline _functionPasses = new();
  private readonly IrModulePassPipeline _earlyModulePasses = new(IrPassScope.EarlyModule);
  private readonly IrModulePassPipeline _modulePasses = new(IrPassScope.Module);
  private IrMiddleEndPhase _functionPhase = IrMiddleEndPhase.Unspecified;
  private IrMiddleEndPhase _earlyModulePhase = IrMiddleEndPhase.Unspecified;
  private IrMiddleEndPhase _modulePhase = IrMiddleEndPhase.Unspecified;

  /// <summary>The complete registered plan, grouped by execution scope but preserving order within each scope.</summary>
  public IReadOnlyList<IrPassDescriptor> PassPlan
    => [.. this._earlyModulePasses.Plan, .. this._functionPasses.Plan, .. this._modulePasses.Plan];

  /// <summary>The last function fixed-point exhaustion reported by the execution core.</summary>
  public IrFixpointDiagnostic? LastFixpointDiagnostic => this._functionPasses.LastFixpointDiagnostic;

  /// <summary>Bound on complete function-pipeline sweeps used by production module runs.</summary>
  public int FunctionFixpointIterationBudget { get; init; } = 16;

  /// <summary>When true, verifies the function after each pass and throws on any error.</summary>
  public bool VerifyEachPass {
    get => this._functionPasses.VerifyEachPass;
    set => this._functionPasses.VerifyEachPass = value;
  }

  /// <summary>The optimization objective this pipeline applies; propagated to the module for late passes.</summary>
  public bool OptimizeForSpeed { get; init; }

  /// <summary>Selects the phase assigned to subsequently registered function transforms.</summary>
  public IrPassManager InFunctionPhase(IrMiddleEndPhase phase) {
    this._functionPhase = phase;
    return this;
  }

  /// <summary>Selects the phase assigned to subsequently registered early module transforms.</summary>
  public IrPassManager InEarlyModulePhase(IrMiddleEndPhase phase) {
    this._earlyModulePhase = phase;
    return this;
  }

  /// <summary>Selects the phase assigned to subsequently registered interprocedural transforms.</summary>
  public IrPassManager InModulePhase(IrMiddleEndPhase phase) {
    this._modulePhase = phase;
    return this;
  }

  /// <summary>Adds a function pass that consumes the shared analysis manager and reports preservation.</summary>
  public IrPassManager AddAnalyzed(string name, Func<IrFunction, IrAnalysisManager, IrPassResult> pass) {
    this._functionPasses.Add(this._functionPhase, name, pass);
    return this;
  }

  /// <summary>Adds an analysis-aware function pass only when <paramref name="condition"/> holds.</summary>
  public IrPassManager AddAnalyzedWhen(bool condition, string name,
      Func<IrFunction, IrAnalysisManager, IrPassResult> pass)
    => condition ? this.AddAnalyzed(name, pass) : this;

  /// <summary>Adds an analysis-aware module pass that must run before function fixed points erase its proof shape.</summary>
  public IrPassManager AddEarlyModuleAnalyzed(
      string name,
      Func<IrModule, IrModuleAnalysisManager, IrModulePassResult> pass) {
    this._earlyModulePasses.Add(this._earlyModulePhase, name, pass);
    return this;
  }

  public IrPassManager AddEarlyModuleAnalyzedWhen(
      bool condition,
      string name,
      Func<IrModule, IrModuleAnalysisManager, IrModulePassResult> pass)
    => condition ? this.AddEarlyModuleAnalyzed(name, pass) : this;

  /// <summary>
  /// Adds an early module transform that has not yet adopted module analyses. A mutation conservatively
  /// invalidates every cached module analysis; unchanged transforms preserve the complete cache.
  /// </summary>
  public IrPassManager AddEarlyModuleConservative(string name, Func<IrModule, int> pass) {
    ArgumentNullException.ThrowIfNull(pass);
    return this.AddEarlyModuleAnalyzed(name, (module, _) => ModuleResult(pass(module)));
  }

  public IrPassManager AddEarlyModuleConservativeWhen(bool condition, string name, Func<IrModule, int> pass)
    => condition ? this.AddEarlyModuleConservative(name, pass) : this;

  /// <summary>Adds an analysis-aware interprocedural pass run around function fixed points.</summary>
  public IrPassManager AddModuleAnalyzed(
      string name,
      Func<IrModule, IrModuleAnalysisManager, IrModulePassResult> pass) {
    this._modulePasses.Add(this._modulePhase, name, pass);
    return this;
  }

  public IrPassManager AddModuleAnalyzedWhen(
      bool condition,
      string name,
      Func<IrModule, IrModuleAnalysisManager, IrModulePassResult> pass)
    => condition ? this.AddModuleAnalyzed(name, pass) : this;

  /// <summary>
  /// Adds an interprocedural transform that has not yet adopted module analyses. A mutation
  /// conservatively invalidates every cached module analysis.
  /// </summary>
  public IrPassManager AddModuleConservative(string name, Func<IrModule, int> pass) {
    ArgumentNullException.ThrowIfNull(pass);
    return this.AddModuleAnalyzed(name, (module, _) => ModuleResult(pass(module)));
  }

  public IrPassManager AddModuleConservativeWhen(bool condition, string name, Func<IrModule, int> pass)
    => condition ? this.AddModuleConservative(name, pass) : this;

  private static IrModulePassResult ModuleResult(int changes)
    => changes == 0 ? IrModulePassResult.Unchanged : IrModulePassResult.Changed(changes);

  /// <summary>Runs every function pass once.</summary>
  public int Run(IrFunction fn) => this._functionPasses.Run(fn);

  /// <summary>Repeats the function-pass set until a complete sweep changes nothing.</summary>
  public int RunToFixpoint(IrFunction fn, int maxIterations = 16)
    => this._functionPasses.RunToFixpoint(fn, maxIterations);

  /// <summary>
  /// Runs early module transforms, function fixed points, then interprocedural transforms. Every changing
  /// late module transform is followed by another function fixed point over all definitions it exposed.
  /// </summary>
  public void RunOnModule(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    module.OptimizeForSpeed = this.OptimizeForSpeed;
    var moduleAnalyses = new IrModuleAnalysisManager(module);
    this._earlyModulePasses.Run(module, moduleAnalyses);
    RunFunctions();
    this._modulePasses.Run(module, moduleAnalyses, RunFunctions);
    return;

    void RunFunctions() {
      foreach (var fn in module.Functions)
        if (!fn.IsDeclaration)
          this._functionPasses.RunToFixpoint(fn, moduleAnalyses, this.FunctionFixpointIterationBudget);
    }
  }
}
