namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Control-flow graph cleanup. Safe, high-value canonicalizations tighten the many trivial blocks
/// the lowering and earlier optimization passes emit (if.next, do.latch, for.inc, ...). The pass
/// folds branches, removes dead/trivial SSA structure, threads proven branch edges, eliminates pure
/// forwarding blocks, and merges single-predecessor runs. It runs to an internal fixpoint and reports
/// the number of simplifications.
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
      total += ThreadBranchesThroughPhis(fn, ref changed);
      total += RemoveUnreachable(fn, ref changed);
      // Forwarding blocks go before trivial-phi removal: collapsing a successor phi first can
      // leave the bridge phi used by a non-phi, which permanently blocks the elision below and
      // strands the empty bridge block in the CFG.
      total += EliminateForwardingBlocks(fn, ref changed);
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

  /// <summary>
  /// Threads an unconditional predecessor around a phi-only branch block when that predecessor's
  /// incoming value makes the branch constant. This is deliberately edge-local: no instruction is
  /// speculated or duplicated, and a block containing anything between its phis and terminator is
  /// left for a stronger jump-threading pass.
  /// </summary>
  private static int ThreadBranchesThroughPhis(IrFunction fn, ref bool changed) {
    var threaded = 0;
    foreach (var block in fn.Blocks.ToList()) {
      if (block.Terminator is not IrCondBr cb
          || cb.Condition is not IrPhi condition
          || !ReferenceEquals(condition.Parent, block))
        continue;

      var phis = block.Phis.ToList();
      if (block.Instructions.Count != phis.Count + 1)
        continue;                                      // never bypass executable work

      foreach (var pred in block.Predecessors.ToList()) {
        if (pred.Terminator is not IrBr br || !ReferenceEquals(br.Target, block))
          continue;                                    // one edge only: no critical-edge surgery here
        if (condition.IncomingFrom(pred) is not IrConstantInt incoming)
          continue;

        var target = incoming.IsZero ? cb.IfFalse : cb.IfTrue;
        if (ReferenceEquals(target, block) || ReferenceEquals(target, pred))
          continue;                                    // keep loop/self-edge shapes for dedicated passes
        if (!TryTranslateSuccessorPhis(block, pred, target, out var translated))
          continue;

        // The old path was pred -> block -> target. A target phi therefore saw the value attached to
        // `block`; after threading it must see that same value as evaluated on `pred`. When that value
        // is itself one of block's phis, its pred-specific incoming is exactly that edge value.
        foreach (var (phi, value) in translated)
          phi.AddIncoming(value, pred);
        foreach (var phi in phis)
          phi.RemoveIncoming(pred);
        br.Target = target;

        ++threaded;
        changed = true;
      }
    }
    return threaded;
  }

  private static bool TryTranslateSuccessorPhis(
      IrBasicBlock block,
      IrBasicBlock pred,
      IrBasicBlock target,
      out List<(IrPhi Phi, IrValue Value)> translated) {
    translated = [];
    foreach (var phi in target.Phis) {
      if (phi.IncomingFrom(block) is not { } value)
        return false;                                  // malformed/unsupported edge: do not guess

      if (value is IrInstruction { Parent: { } valueBlock } && ReferenceEquals(valueBlock, block)) {
        if (value is not IrPhi sourcePhi || sourcePhi.IncomingFrom(pred) is not { } incoming)
          return false;                                // a non-phi local would have to be cloned
        value = incoming;
      }

      translated.Add((phi, value));
    }
    return true;
  }

  private static int RemoveUnreachable(IrFunction fn, ref bool changed) {
    var reachable = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance);
    var stack = new Stack<IrBasicBlock>();
    stack.Push(fn.Entry!);
    reachable.Add(fn.Entry!);
    // a block whose address is taken is a ROOT: something can jump there through an edge the graph
    // does not draw, which is the whole reason the address exists
    foreach (var addressed in fn.AddressTakenBlocks())
      if (addressed.Parent is not null && reachable.Add(addressed))
        stack.Push(addressed);
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

  /// <summary>
  /// Removes a block that contains only phis followed by an unconditional branch. Incoming edges are
  /// redirected to the successor, and successor phis are expanded to the predecessor-specific values
  /// that flowed through the removed block. The transform deliberately refuses edges/uses that would
  /// require critical-edge values, instruction cloning, or loop-header surgery.
  /// </summary>
  private static int EliminateForwardingBlocks(IrFunction fn, ref bool changed) {
    var eliminated = 0;
    var addressed = fn.AddressTakenBlocks();
    foreach (var block in fn.Blocks.ToList()) {
      if (block.Parent is null || ReferenceEquals(block, fn.Entry) || addressed.Contains(block)
          || block.Terminator is not IrBr br || ReferenceEquals(br.Target, block))
        continue;

      var phis = block.Phis.ToList();
      if (block.Instructions.Count != phis.Count + 1)
        continue;                                      // only phis plus the forwarding branch
      // A bridge that carries no phi is an EMPTY block, and removing one is MergeSingleSuccessorBlocks'
      // job, not this pass's. Doing it here also rewrites edges the loop passes still need: an empty
      // preheader or unroll stub folded away leaves a header whose phi the next unswitch/unroll clone
      // cannot remap, and it emits `%x = add %x, ...` - a value that is its own operand.
      if (phis.Count == 0)
        continue;

      var succ = br.Target;
      var preds = block.Predecessors.ToList();
      if (preds.Count == 0 || ReferenceEquals(succ, fn.Entry) || preds.Any(pred => ReferenceEquals(pred, succ)))
        continue;                                      // unreachable/entry/back-edge shapes stay canonical
      if (preds.Any(pred => !CanRetarget(pred.Terminator, block)))
        continue;                                      // switch/indirect edges need their own rewrite API
      if (preds.Any(pred => pred.Successors.Any(s => ReferenceEquals(s, succ))))
        continue;                                      // would create two edges from one predecessor to succ
      if (phis.Any(phi => phi.Users.Any(user => user is not IrPhi usePhi || !ReferenceEquals(usePhi.Parent, succ))))
        continue;                                      // a live bridge phi would have to move or be cloned
      if (!TryTranslateForwardedPhis(block, preds, succ, out var translated))
        continue;

      foreach (var (phi, pred, value) in translated)
        phi.AddIncoming(value, pred);
      foreach (var phi in succ.Phis)
        phi.RemoveIncoming(block);
      foreach (var pred in preds)
        Retarget(pred.Terminator!, block, succ);

      foreach (var inst in block.Instructions.ToList())
        inst.EraseFromParent();
      fn.RemoveBlock(block);

      ++eliminated;
      changed = true;
    }
    return eliminated;
  }

  private static bool TryTranslateForwardedPhis(
      IrBasicBlock block,
      IReadOnlyList<IrBasicBlock> preds,
      IrBasicBlock succ,
      out List<(IrPhi Phi, IrBasicBlock Pred, IrValue Value)> translated) {
    translated = [];
    foreach (var phi in succ.Phis) {
      if (phi.IncomingFrom(block) is not { } throughBlock)
        return false;

      foreach (var pred in preds) {
        var value = throughBlock;
        if (value is IrInstruction { Parent: { } valueBlock } && ReferenceEquals(valueBlock, block)) {
          if (value is not IrPhi bridgePhi || bridgePhi.IncomingFrom(pred) is not { } incoming)
            return false;
          value = incoming;
        }
        if (value is IrInstruction { Parent: { } translatedBlock } && ReferenceEquals(translatedBlock, block))
          return false;                                // bridge-phi cycles/chains need a stronger translator
        translated.Add((phi, pred, value));
      }
    }
    return true;
  }

  private static bool CanRetarget(IrInstruction? terminator, IrBasicBlock from) => terminator switch {
    IrBr br => ReferenceEquals(br.Target, from),
    IrCondBr cb => ReferenceEquals(cb.IfTrue, from) || ReferenceEquals(cb.IfFalse, from),
    _ => false,
  };

  private static void Retarget(IrInstruction terminator, IrBasicBlock from, IrBasicBlock to) {
    switch (terminator) {
      case IrBr br when ReferenceEquals(br.Target, from):
        br.Target = to;
        break;
      case IrCondBr cb:
        if (ReferenceEquals(cb.IfTrue, from))
          cb.IfTrue = to;
        if (ReferenceEquals(cb.IfFalse, from))
          cb.IfFalse = to;
        break;
      default:
        throw new InvalidOperationException("prechecked forwarding edge cannot be retargeted");
    }
  }

  private static int MergeSingleSuccessorBlocks(IrFunction fn, ref bool changed) {
    var merged = 0;
    // a block whose address something holds has to keep existing under its own label, whatever the
    // edges say - see IrFunction.AddressTakenBlocks
    var addressed = fn.AddressTakenBlocks();
    foreach (var block in fn.Blocks.ToList()) {
      if (block.Parent is null || block.Terminator is not IrBr br)
        continue;
      var succ = br.Target;
      if (ReferenceEquals(succ, block) || ReferenceEquals(succ, fn.Entry) || addressed.Contains(succ))
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
