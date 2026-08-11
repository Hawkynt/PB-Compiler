namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Control-flow graph cleanup. Two safe, high-value transforms that tighten the many
/// trivial blocks the lowering emits (if.next, do.latch, for.inc, ...):
///   1. trivial-phi elimination - a phi whose inputs are all the same value is that value;
///   2. single-predecessor merge - a block ending in an unconditional branch to a successor
///      that has only this predecessor is spliced into it, deleting the edge and the block.
/// Runs to an internal fixpoint and reports the number of simplifications.
/// </summary>
public static class SimplifyCfg {

  public static int Run(IrFunction fn) {
    if (fn.Entry is null)
      return 0;
    var total = 0;
    bool changed;
    do {
      changed = false;
      total += FoldBranches(fn, ref changed);
      total += RemoveUnreachable(fn, ref changed);
      total += RemoveTrivialPhis(fn, ref changed);
      total += MergeSingleSuccessorBlocks(fn, ref changed);
    } while (changed);
    return total;
  }

  private static int FoldBranches(IrFunction fn, ref bool changed) {
    var folded = 0;
    foreach (var block in fn.Blocks.ToList()) {
      IrBasicBlock? target = null;
      switch (block.Terminator) {
        case IrCondBr cb when ReferenceEquals(cb.IfTrue, cb.IfFalse):
          target = cb.IfTrue;                           // condbr c, X, X -> br X
          break;
        case IrCondBr { Condition: IrConstantInt c } cb:
          target = c.IsZero ? cb.IfFalse : cb.IfTrue;   // constant condition
          break;
        case IrSwitch { Condition: IrConstantInt s } sw:
          target = sw.TargetFor(s.Value);                    // fixed-width constant selector
          break;
      }
      if (target is null)
        continue;
      block.Terminator!.EraseFromParent();
      block.Append(new IrBr(target));
      ++folded;
      changed = true;
    }
    return folded;
  }

  private static int RemoveUnreachable(IrFunction fn, ref bool changed) {
    var reachable = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance);
    var stack = new Stack<IrBasicBlock>();
    stack.Push(fn.Entry!);
    reachable.Add(fn.Entry!);
    while (stack.Count > 0)
      foreach (var s in stack.Pop().Successors)
        if (reachable.Add(s))
          stack.Push(s);

    var dead = fn.Blocks.Where(b => !reachable.Contains(b)).ToList();
    if (dead.Count == 0)
      return 0;

    foreach (var block in reachable)
      foreach (var phi in block.Phis.ToList())
        foreach (var pred in phi.IncomingBlocks.ToList())
          if (!reachable.Contains(pred))
            phi.RemoveIncoming(pred);

    foreach (var block in dead) {
      foreach (var inst in block.Instructions.ToList())
        inst.EraseFromParent();
      fn.RemoveBlock(block);
    }
    changed = true;
    return dead.Count;
  }

  private static int RemoveTrivialPhis(IrFunction fn, ref bool changed) {
    var removed = 0;
    foreach (var block in fn.Blocks.ToList())
      foreach (var phi in block.Phis.ToList())
        if (TrivialValue(phi) is { } value) {
          phi.ReplaceAllUsesWith(value);
          phi.EraseFromParent();
          ++removed;
          changed = true;
        }
    return removed;
  }

  /// <summary>The single distinct value a phi resolves to (ignoring self-references), or null.</summary>
  private static IrValue? TrivialValue(IrPhi phi) {
    IrValue? only = null;
    foreach (var op in phi.Operands) {
      if (ReferenceEquals(op, phi))
        continue;                                    // self-reference does not count
      if (only is null)
        only = op;
      else if (!ReferenceEquals(only, op))
        return null;                                 // two distinct inputs: a real merge
    }
    return only;
  }

  private static int MergeSingleSuccessorBlocks(IrFunction fn, ref bool changed) {
    var merged = 0;
    foreach (var block in fn.Blocks.ToList()) {
      if (block.Parent is null || block.Terminator is not IrBr br)
        continue;
      var succ = br.Target;
      if (ReferenceEquals(succ, block) || ReferenceEquals(succ, fn.Entry))
        continue;
      var preds = succ.Predecessors.ToList();
      if (preds.Count != 1 || !ReferenceEquals(preds[0], block))
        continue;                                    // succ must be reached only from here
      if (succ.Successors.Any(s => ReferenceEquals(s, block)))
        continue;                                    // back-edge to us: leave the loop shape alone

      // succ's phis are trivial (single predecessor): fold them to their lone input
      foreach (var phi in succ.Phis.ToList()) {
        phi.ReplaceAllUsesWith(phi.GetOperand(0));
        phi.EraseFromParent();
      }

      br.EraseFromParent();                          // drop block's branch to succ
      foreach (var inst in succ.Instructions.ToList()) {
        succ.Remove(inst);
        block.Append(inst);
      }
      // any phi in a successor of the moved terminator that named succ must now name block
      foreach (var after in block.Successors)
        foreach (var phi in after.Phis)
          phi.RenameIncomingBlock(succ, block);

      fn.RemoveBlock(succ);
      ++merged;
      changed = true;
    }
    return merged;
  }
}
