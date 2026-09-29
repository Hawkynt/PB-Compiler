using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Dead-code elimination: removes unused instructions whose central effect contract says they may
/// be discarded, cascading through operands as they become unused. Ordinary reads and effect-free
/// calls may disappear; writes, traps, lifetime changes, synchronization, IO and other observable
/// effects remain. Terminators are structural and are never removed here.
/// </summary>
public static class Dce {

  /// <summary>Removes dead instructions in place; returns how many were removed.</summary>
  public static int Run(IrFunction fn) {
    ArgumentNullException.ThrowIfNull(fn);
    return RunCore(fn);
  }

  /// <summary>
  /// Analysis-aware entry used by the middle end. DCE never removes terminators, so CFG-derived
  /// analyses remain valid while value-derived analyses are invalidated when an instruction dies.
  /// </summary>
  internal static IrPassResult Run(IrFunction fn, IrAnalysisManager analyses) {
    ArgumentNullException.ThrowIfNull(fn);
    ArgumentNullException.ThrowIfNull(analyses);
    var removed = RunCore(fn);
    return removed == 0
      ? IrPassResult.Unchanged
      : IrPassResult.ChangedPreservingSets(removed, IrAnalysisSets.Cfg);
  }

  private static int RunCore(IrFunction fn) {
    var removed = 0;
    var worklist = new Queue<IrInstruction>(fn.AllInstructions);
    while (worklist.Count > 0) {
      var inst = worklist.Dequeue();
      if (inst.Parent is null || !inst.HasNoUsers || inst.IsTerminator
          || !IrEffects.ForInstruction(inst).CanDiscard)
        continue;

      foreach (var operand in inst.Operands)         // operands may now be dead
        if (operand is IrInstruction producer)
          worklist.Enqueue(producer);
      inst.EraseFromParent();
      ++removed;
    }
    return removed;
  }

}
