using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Loop-invariant code motion. For each natural loop it identifies pure, speculatable instructions whose operands
/// are all defined outside the loop (transitively) and moves them to the loop's unique entering block, so they run
/// once instead of every iteration. Only non-trapping instructions are hoisted (integer/float division and loads
/// are left in place), so speculative execution before a non-canonical loop entry cannot introduce a fault the
/// original program would not have hit.
/// </summary>
public static class Licm {

  /// <summary>Hoists loop-invariant computations to unique loop-entry predecessors; returns how many were hoisted.</summary>
  public static int Run(IrFunction fn) {
    ArgumentNullException.ThrowIfNull(fn);
    return IrFunctionPassPipeline.RunStandalone(fn, "licm", Run);
  }

  /// <summary>Runs LICM using the shared loop-forest analysis.</summary>
  public static IrPassResult Run(IrFunction fn, IrAnalysisManager analyses) {
    ArgumentNullException.ThrowIfNull(fn);
    ArgumentNullException.ThrowIfNull(analyses);
    if (!ReferenceEquals(fn, analyses.Function))
      throw new ArgumentException("Analysis manager belongs to a different function.", nameof(analyses));
    if (fn.Entry is null)
      return IrPassResult.Unchanged;

    var loops = analyses.Get(IrAnalyses.Loops);
    var hoisted = 0;
    // Innermost first, so a value can climb out of nested loops over repeated runs.
    foreach (var loop in loops.Loops.OrderBy(loop => loop.Blocks.Count)) {
      var entering = loop.UniqueEnteringBlock;
      if (entering?.Terminator is null)
        continue;
      hoisted += Hoist(loop.Blocks, entering);
    }
    return hoisted == 0
      ? IrPassResult.Unchanged
      : IrPassResult.ChangedPreservingSets(hoisted, IrAnalysisSets.Cfg);
  }

  private static int Hoist(IReadOnlySet<IrBasicBlock> body, IrBasicBlock entering) {
    var invariant = ComputeInvariant(body, WritesNothing(body));
    var count = 0;
    bool progress;
    do {
      progress = false;
      foreach (var inst in invariant.ToList()) {
        if (!body.Contains(inst.Parent!))
          continue;                                  // already hoisted
        if (!AllOperandsOutside(inst, body))
          continue;                                  // wait until its invariant inputs are hoisted
        inst.Parent!.Remove(inst);
        entering.InsertBefore(inst, entering.Terminator!);
        ++count;
        progress = true;
      }
    } while (progress);
    return count;
  }

  private static List<IrInstruction> ComputeInvariant(IReadOnlySet<IrBasicBlock> body, bool readOnlyLoop) {
    var invariant = new HashSet<IrInstruction>(ReferenceEqualityComparer.Instance);
    var ordered = new List<IrInstruction>();
    bool changed;
    do {
      changed = false;
      foreach (var block in body)
        foreach (var inst in block.Instructions)
          if (!invariant.Contains(inst) && (IsSpeculatable(inst) || readOnlyLoop && IsHoistableRead(inst))
              && OperandsInvariant(inst, body, invariant)) {
            invariant.Add(inst);
            ordered.Add(inst);
            changed = true;
          }
    } while (changed);
    return ordered;
  }

  private static bool OperandsInvariant(IrInstruction inst, IReadOnlySet<IrBasicBlock> body,
      HashSet<IrInstruction> invariant) {
    foreach (var op in inst.Operands)
      if (op is IrInstruction def && def.Parent is { } b && body.Contains(b) && !invariant.Contains(def))
        return false;
    return true;
  }

  private static bool AllOperandsOutside(IrInstruction inst, IReadOnlySet<IrBasicBlock> body) {
    foreach (var op in inst.Operands)
      if (op is IrInstruction def && def.Parent is { } b && body.Contains(b))
        return false;
    return true;
  }

  /// <summary>
  /// Whether nothing in the loop writes memory, releases or allocates - so a deterministic read of an
  /// invariant operand answers the same on every iteration. A call or instruction with any stronger
  /// effect makes the loop a writer; loads and trap-only operations do not.
  /// </summary>
  private static bool WritesNothing(IReadOnlySet<IrBasicBlock> body)
    => body.SelectMany(block => block.Instructions).All(instruction =>
         (IrEffects.ForInstruction(instruction).Effects
          & (IrEffectKind.WritesMemory | IrEffectKind.MayRelease | IrEffectKind.MayAllocate | IrEffectKind.MayThrow)) == 0);

  /// <summary>
  /// A deterministic, trap-free call that only reads - LEN of a string's descriptor. In a loop that
  /// writes nothing it answers the same every iteration, and reading it once before the loop is
  /// harmless even when the loop does not run: its operand is live there.
  /// </summary>
  private static bool IsHoistableRead(IrInstruction inst)
    => inst is IrCall call && Gvn.IsDeterministicRead(call);

  /// <summary>Pure and trap-free: safe to execute unconditionally in the entering block.</summary>
  private static bool IsSpeculatable(IrInstruction inst) => inst switch {
    IrBinary or IrCmp or IrCast or IrGep or IrCall => IrEffects.ForInstruction(inst).CanSpeculate,
    _ => false,                                      // loads, stores, allocas, phis, terminators
  };
}
