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
  private readonly List<(string Name, Func<IrModule, int> Run)> _modulePasses = [];

  /// <summary>When true, verifies the function after each pass and throws on any error.</summary>
  public bool VerifyEachPass { get; set; }

  public IrPassManager Add(string name, Func<IrFunction, int> pass) {
    this._passes.Add((name, pass));
    return this;
  }

  /// <summary>Adds an interprocedural pass, run by <see cref="RunOnModule"/> around the function pipeline.</summary>
  public IrPassManager AddModulePass(string name, Func<IrModule, int> pass) {
    this._modulePasses.Add((name, pass));
    return this;
  }

  /// <summary>Runs every pass once over the function; returns the total number of changes.</summary>
  public int Run(IrFunction fn) {
    // a function with an armed error handler has control-flow edges the CFG does not show, so every
    // pass here would be reasoning from an incomplete graph - see IrFunction.HasErrorHandler
    if (fn.HasErrorHandler)
      return 0;
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

  /// <summary>
  /// Runs the pipeline over a module: the interprocedural passes first, then the function pipeline
  /// over each body, then the interprocedural passes once more.
  ///
  /// The order is the point. A pass that reasons across the call graph wants the bodies simplified —
  /// a return is only recognisably constant after the body's arithmetic has folded — while the
  /// function passes want the call-graph facts, because a parameter that turns out to be a literal is
  /// what makes a branch inside the body foldable. Neither can go first and be right, so both run,
  /// and the second interprocedural sweep is followed by another function sweep for what it exposed.
  /// </summary>
  public void RunOnModule(IrModule module) {
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

  /// <summary>
  /// The default optimization pipeline: promote memory to registers, then iterate
  /// simplification, conditional constant propagation, value numbering and dead-code
  /// elimination to a fixpoint.
  /// </summary>
  public static IrPassManager Standard() => new IrPassManager()
    .Add("mem2reg", Mem2Reg.Run)
    // unrolling goes early, right after values reach SSA: a fully unrolled loop turns its counter
    // into a constant in every copy, which is what gives the rest of the pipeline something to fold
    .Add("unroll", LoopUnroll.Run)
    .Add("instcombine", InstCombine.Run)
    .Add("sccp", Sccp.Run)
    .Add("correlate", CorrelatedValueProp.Run)
    // after SCCP, because a subscript is only constant once the index arithmetic has folded - and
    // before the value passes, so the elements it exposes get propagated like any other value
    .Add("sroa", ScalarReplaceArrays.Run)
    .Add("mem2reg2", Mem2Reg.Run)
    // canonicalizes associative chains so GVN hashes two equal expressions the same way; it must
    // come after SCCP (which supplies the constants it folds together) and before GVN (which is the
    // pass that benefits)
    .Add("reassociate", Reassociate.Run)
    .Add("gvn", Gvn.Run)
    .Add("memopt", RedundantMemory.Run)
    .Add("dse", DeadStoreElim.Run)
    .Add("licm", Licm.Run)
    .Add("dce", Dce.Run)
    .Add("ifconv", IfConversion.Run)
    .Add("simplifycfg", SimplifyCfg.Run)
    .AddModulePass("readonly-globals", ReadOnlyGlobals.Run)
    .AddModulePass("ipconstprop", IpConstantProp.Run);
}
