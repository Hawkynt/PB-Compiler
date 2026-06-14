namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>Raised when <see cref="IrPassManager.VerifyEachPass"/> is on and a pass leaves the IR malformed.</summary>
public sealed class IrVerificationException(string pass, IReadOnlyList<string> errors)
  : Exception($"IR invalid after pass '{pass}': {string.Join("; ", errors)}") {
  public string Pass { get; } = pass;
  public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Runs an ordered set of function passes, once or to a fixpoint. Each pass reports
/// how many changes it made; the fixpoint loop repeats the set until a full sweep
/// changes nothing. With <see cref="VerifyEachPass"/> on, the IR is verified after
/// every pass so a miscompiling pass is caught immediately - invaluable while the
/// middle-end grows.
/// </summary>
public sealed class IrPassManager {

  private readonly List<(string Name, Func<IrFunction, int> Run)> _passes = [];

  /// <summary>When true, verifies the function after each pass and throws on any error.</summary>
  public bool VerifyEachPass { get; set; }

  public IrPassManager Add(string name, Func<IrFunction, int> pass) {
    this._passes.Add((name, pass));
    return this;
  }

  /// <summary>Runs every pass once over the function; returns the total number of changes.</summary>
  public int Run(IrFunction fn) {
    var total = 0;
    foreach (var (name, run) in this._passes) {
      total += run(fn);
      if (this.VerifyEachPass) {
        var errors = IrVerifier.Verify(fn);
        if (errors.Count > 0)
          throw new IrVerificationException(name, errors);
      }
    }
    return total;
  }

  /// <summary>Repeats the pass set until it stops changing anything (or the iteration cap is hit).</summary>
  public int RunToFixpoint(IrFunction fn, int maxIterations = 16) {
    var total = 0;
    for (var i = 0; i < maxIterations; ++i) {
      var changes = this.Run(fn);
      total += changes;
      if (changes == 0)
        break;
    }
    return total;
  }

  /// <summary>Runs the pipeline over every defined function in a module.</summary>
  public void RunOnModule(IrModule module) {
    foreach (var fn in module.Functions)
      if (!fn.IsDeclaration)
        this.RunToFixpoint(fn);
  }

  /// <summary>
  /// The default optimization pipeline: promote memory to registers, then iterate
  /// simplification, conditional constant propagation, value numbering and dead-code
  /// elimination to a fixpoint.
  /// </summary>
  public static IrPassManager Standard() => new IrPassManager()
    .Add("mem2reg", Mem2Reg.Run)
    .Add("instcombine", InstCombine.Run)
    .Add("sccp", Sccp.Run)
    .Add("gvn", Gvn.Run)
    .Add("memopt", RedundantMemory.Run)
    .Add("dse", DeadStoreElim.Run)
    .Add("licm", Licm.Run)
    .Add("dce", Dce.Run)
    .Add("simplifycfg", SimplifyCfg.Run);
}
