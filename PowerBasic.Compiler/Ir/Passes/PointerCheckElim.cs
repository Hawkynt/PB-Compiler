using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Eliminates a pointer/handle null test whose result is already established by an explicit
/// dominating null test.
///
/// <para>
/// The fact is attached to the SSA VALUE, not to memory. A later load from the same slot is a
/// different value and receives no fact, so a call or store that changes a string handle cannot make
/// this pass reuse stale knowledge. Conversely, a call cannot invalidate an SSA pointer argument or
/// an earlier loaded value: that value's bits do not change merely because memory does.
/// </para>
///
/// <para>
/// A dereference is intentionally not evidence. On the DOS memory model a null near pointer can read
/// segment zero rather than faulting, so the usual hosted-language rule "we got past the load,
/// therefore p != null" would be a miscompile here. Only an explicit <c>p == null</c> or
/// <c>p != null</c> branch contributes a fact.
/// </para>
/// </summary>
public static class PointerCheckElim {

  /// <summary>Replaces decided pointer-null comparisons; returns how many comparisons were decided.</summary>
  public static int Run(IrFunction fn) {
    ArgumentNullException.ThrowIfNull(fn);
    return IrFunctionPassPipeline.RunStandalone(fn, "ptrcheck", Run);
  }

  /// <summary>Runs null-check elimination through the shared program-point fact facade.</summary>
  public static IrPassResult Run(IrFunction fn, IrAnalysisManager analyses) {
    ArgumentNullException.ThrowIfNull(fn);
    ArgumentNullException.ThrowIfNull(analyses);
    if (!ReferenceEquals(fn, analyses.Function))
      throw new ArgumentException("Analysis manager belongs to a different function.", nameof(analyses));

    var facts = analyses.Get(IrAnalyses.Facts);
    var decided = new List<(IrCmp Cmp, bool Outcome)>();
    foreach (var block in fn.Blocks)
      foreach (var cmp in block.Instructions.OfType<IrCmp>()) {
        if (cmp.HasNoUsers || !IrNullnessAnalysis.TryNullTest(cmp, out _, out _))
          continue;
        if (facts.Decide(cmp, block) is not { } outcome)
          continue;
        decided.Add((cmp, outcome));
      }

    foreach (var (cmp, outcome) in decided)
      cmp.ReplaceAllUsesWith(IrBuilder.ConstBool(outcome));
    return decided.Count == 0
      ? IrPassResult.Unchanged
      : IrPassResult.ChangedPreservingSets(decided.Count, IrAnalysisSets.Cfg);
  }

}
