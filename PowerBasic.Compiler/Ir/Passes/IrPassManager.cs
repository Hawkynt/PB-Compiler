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
  private readonly List<(string Name, Func<IrModule, int> Run)> _earlyModulePasses = [];
  private readonly List<(string Name, Func<IrModule, int> Run)> _modulePasses = [];

  /// <summary>When true, verifies the function after each pass and throws on any error.</summary>
  public bool VerifyEachPass {
    get => this._functionPasses.VerifyEachPass;
    set => this._functionPasses.VerifyEachPass = value;
  }

  /// <summary>The optimization objective this pipeline applies; propagated to the module for late passes.</summary>
  public bool OptimizeForSpeed { get; init; }

  /// <summary>Adds a function pass that consumes the shared analysis manager and reports preservation.</summary>
  public IrPassManager AddAnalyzed(string name, Func<IrFunction, IrAnalysisManager, IrPassResult> pass) {
    this._functionPasses.Add(name, pass);
    return this;
  }

  /// <summary>Adds an analysis-aware function pass only when <paramref name="condition"/> holds.</summary>
  public IrPassManager AddAnalyzedWhen(bool condition, string name,
      Func<IrFunction, IrAnalysisManager, IrPassResult> pass)
    => condition ? this.AddAnalyzed(name, pass) : this;

  /// <summary>Adds a module pass that must run before function fixed points erase its proof shape.</summary>
  public IrPassManager AddEarlyModulePass(string name, Func<IrModule, int> pass) {
    this._earlyModulePasses.Add((name, pass));
    return this;
  }

  public IrPassManager AddEarlyModulePassWhen(bool condition, string name, Func<IrModule, int> pass)
    => condition ? this.AddEarlyModulePass(name, pass) : this;

  /// <summary>Adds an interprocedural pass run around function fixed points.</summary>
  public IrPassManager AddModulePass(string name, Func<IrModule, int> pass) {
    this._modulePasses.Add((name, pass));
    return this;
  }

  public IrPassManager AddModulePassWhen(bool condition, string name, Func<IrModule, int> pass)
    => condition ? this.AddModulePass(name, pass) : this;

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
    foreach (var (_, run) in this._earlyModulePasses)
      run(module);
    RunFunctions();
    foreach (var (_, run) in this._modulePasses)
      if (run(module) > 0)
        RunFunctions();
    return;

    void RunFunctions() {
      foreach (var fn in module.Functions)
        if (!fn.IsDeclaration)
          this.RunToFixpoint(fn);
    }
  }
}
