using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Reuse the proven Mode 13h volatile pixel-byte writer for POKE calls which
/// definitely address a visible byte of A000h. This does not change DEF SEG,
/// update graphics cursor state, or broaden generic raw-memory optimizations.
/// </summary>
public static class PokeRuntimeInlining {
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    if (module.FindFunction("rt_pset_mode13_offset") is { IsDeclaration: false })
      return 0; // Never substitute a consumer-supplied body for the runtime primitive.
    IrFunction? writer = null;
    var changed = 0;
    foreach (var function in module.Functions.Where(function => !function.IsDeclaration).ToArray())
      changed += new IrFunctionPassPipeline().Add("poke-mode13-inline", (fn, analyses) => {
        var segments = analyses.Get(IrAnalyses.DefaultSegments);
        var modes = analyses.Get(IrAnalyses.ScreenModes);
        var calls = fn.AllInstructions.OfType<IrCall>().Where(call =>
          modes.Before(call) == 13
          && IrDefaultSegmentAnalysis.IsPackedVgaPoke(call, segments.Before(call))).ToArray();
        if (calls.Length == 0)
          return IrPassResult.Unchanged;
        writer ??= module.FindFunction("rt_pset_mode13_offset")
          ?? module.AddFunction(new IrFunction("rt_pset_mode13_offset", IrType.Void,
            [new IrArgument(IrType.I16, 0), new IrArgument(IrType.I16, 1)]));
        foreach (var call in calls)
          call.SetOperand(0, writer);
        return IrPassResult.ChangedPreservingSets(calls.Length, IrAnalysisSets.Cfg);
      }).Run(function);
    return changed;
  }
}
