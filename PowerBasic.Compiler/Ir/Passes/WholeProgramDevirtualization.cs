using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0279 — whole-program devirtualization for indirect calls whose complete target set contains one
/// procedure.
///
/// <para>
/// This is deliberately a proof pass, not a prediction pass. It follows only value relations the IR
/// makes exact: a function value, pointer-preserving bitcasts, phi/select merges, closed local pointer
/// cells, and formal parameters of functions whose every caller is visible in this module. Anything
/// else is incomplete and stays indirect. In particular, a null-capable value or a set containing two
/// functions is not rewritten; guarded promotion belongs to O0271/O0307.
/// </para>
/// <para>
/// Parameters are where the whole-program part matters. If every visible caller passes the same
/// procedure for a callback parameter, a call through that parameter has one complete target even
/// though no local function pass can prove it. The complete target-set proof is shared through
/// <see cref="IrFunctionTargetAnalysis"/> and uses the cached call graph for the visibility promise.
/// </para>
/// </summary>
public static class WholeProgramDevirtualization {

  /// <summary>Replaces provably singleton indirect callees with their direct <see cref="IrFunction"/>.</summary>
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    return Run(module, new IrModuleAnalysisManager(module)).Changes;
  }

  /// <summary>
  /// Analysis-aware whole-program entry. Each successful rewrite exposes a new direct call edge, so
  /// the pass invalidates module facts immediately and restarts from a fresh cached call graph. The
  /// final no-change sweep leaves that graph valid and explicitly preserved for following module passes.
  /// </summary>
  public static IrModulePassResult Run(IrModule module, IrModuleAnalysisManager analyses) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(analyses);
    if (!ReferenceEquals(module, analyses.Module))
      throw new ArgumentException("Module analysis manager belongs to a different module.", nameof(analyses));

    var changed = 0;
    for (;;) {
      var targets = analyses.Get(IrModuleAnalyses.FunctionTargets);
      IrCall? rewrite = null;
      IrFunction? target = null;

      foreach (var function in module.Functions) {
        if (function.IsDeclaration || function.HasErrorHandler || function.HasInlineAsm)
          continue;

        foreach (var call in function.AllInstructions.OfType<IrCall>()) {
          if (call.Callee is IrFunction)
            continue;
          var candidate = targets.ResolveUnique(call.Callee);
          if (candidate is null || !SignatureMatches(call, candidate))
            continue;
          rewrite = call;
          target = candidate;
          break;
        }
        if (rewrite is not null)
          break;
      }

      if (rewrite is null)
        return changed == 0
          ? IrModulePassResult.Unchanged
          : IrModulePassResult.ChangedPreserving(
            changed,
            IrModuleAnalyses.CallGraph,
            IrModuleAnalyses.FunctionTargets);

      rewrite.SetOperand(0, target!);
      ++changed;
      analyses.Invalidate(IrModulePreservedAnalyses.None);
    }
  }

  /// <summary>
  /// Opaque pointers do not carry a signature, so target provenance alone is insufficient. The direct
  /// callee must agree with the call shape the indirect site already had; otherwise replacing the
  /// operand would turn an invalid/incompatible function-pointer cast into a different ABI call.
  /// </summary>
  private static bool SignatureMatches(IrCall call, IrFunction target) {
    if (!call.Type.SameStorage(target.ReturnType))
      return false;

    var arguments = call.Args.ToList();
    if (target.IsVarArgs ? arguments.Count < target.Parameters.Count : arguments.Count != target.Parameters.Count)
      return false;

    for (var i = 0; i < target.Parameters.Count; ++i)
      if (!arguments[i].Type.SameStorage(target.Parameters[i].Type))
        return false;
    return true;
  }


}
