namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Loop-invariant code motion. For each natural loop (found from CFG back-edges via
/// the dominator tree) it identifies pure, speculatable instructions whose operands
/// are all defined outside the loop (transitively) and sinks them into the loop's
/// preheader, so they run once instead of every iteration. Only non-trapping
/// instructions are hoisted (integer/float division and loads are left in place), so
/// speculative execution in the preheader can never introduce a fault the original
/// program would not have hit.
/// </summary>
public static class Licm {

  /// <summary>Hoists loop-invariant computations to loop preheaders; returns how many were hoisted.</summary>
  public static int Run(IrFunction fn) {
    if (fn.Entry is null)
      return 0;
    var dom = IrDominators.Build(fn)!;
    var loops = DetectLoops(fn, dom);
    var hoisted = 0;
    // innermost first, so a value can climb out of nested loops over repeated runs
    foreach (var loop in loops.OrderBy(l => l.Body.Count)) {
      var preheader = UniquePreheader(loop.Header, loop.Body);
      if (preheader is null)
        continue;
      hoisted += Hoist(loop.Body, preheader);
    }
    return hoisted;
  }

  private readonly record struct Loop(IrBasicBlock Header, HashSet<IrBasicBlock> Body);

  private static List<Loop> DetectLoops(IrFunction fn, IrDominators dom) {
    var byHeader = new Dictionary<IrBasicBlock, HashSet<IrBasicBlock>>(ReferenceEqualityComparer.Instance);
    foreach (var block in fn.Blocks) {
      if (!dom.IsReachable(block))
        continue;
      foreach (var succ in block.Successors)
        if (dom.Dominates(succ, block)) {            // back-edge block -> succ (header)
          if (!byHeader.TryGetValue(succ, out var body))
            byHeader[succ] = body = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance) { succ };
          var stack = new Stack<IrBasicBlock>();
          stack.Push(block);
          while (stack.Count > 0) {
            var n = stack.Pop();
            if (body.Add(n))
              foreach (var pred in n.Predecessors)
                stack.Push(pred);                    // stops at the header (already in body)
          }
        }
    }
    return byHeader.Select(kv => new Loop(kv.Key, kv.Value)).ToList();
  }

  private static IrBasicBlock? UniquePreheader(IrBasicBlock header, HashSet<IrBasicBlock> body) {
    IrBasicBlock? preheader = null;
    foreach (var pred in header.Predecessors) {
      if (body.Contains(pred))
        continue;                                    // the back-edge source
      if (preheader is not null)
        return null;                                 // more than one entry: no single preheader
      preheader = pred;
    }
    return preheader?.Terminator is not null ? preheader : null;
  }

  private static int Hoist(HashSet<IrBasicBlock> body, IrBasicBlock preheader) {
    var invariant = ComputeInvariant(body);
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
        preheader.InsertBefore(inst, preheader.Terminator!);
        ++count;
        progress = true;
      }
    } while (progress);
    return count;
  }

  private static List<IrInstruction> ComputeInvariant(HashSet<IrBasicBlock> body) {
    var invariant = new HashSet<IrInstruction>(ReferenceEqualityComparer.Instance);
    var ordered = new List<IrInstruction>();
    bool changed;
    do {
      changed = false;
      foreach (var block in body)
        foreach (var inst in block.Instructions)
          if (!invariant.Contains(inst) && IsSpeculatable(inst) && OperandsInvariant(inst, body, invariant)) {
            invariant.Add(inst);
            ordered.Add(inst);
            changed = true;
          }
    } while (changed);
    return ordered;
  }

  private static bool OperandsInvariant(IrInstruction inst, HashSet<IrBasicBlock> body, HashSet<IrInstruction> invariant) {
    foreach (var op in inst.Operands)
      if (op is IrInstruction def && def.Parent is { } b && body.Contains(b) && !invariant.Contains(def))
        return false;
    return true;
  }

  private static bool AllOperandsOutside(IrInstruction inst, HashSet<IrBasicBlock> body) {
    foreach (var op in inst.Operands)
      if (op is IrInstruction def && def.Parent is { } b && body.Contains(b))
        return false;
    return true;
  }

  /// <summary>Pure and trap-free: safe to execute unconditionally in the preheader.</summary>
  private static bool IsSpeculatable(IrInstruction inst) => inst switch {
    IrBinary b => b.Op is not (IrBinaryOp.SDiv or IrBinaryOp.UDiv or IrBinaryOp.SRem or IrBinaryOp.URem or IrBinaryOp.FDiv),
    IrCmp or IrCast or IrGep => true,
    // A call is a wall except for the short checked list of runtime entries that are a function of
    // their arguments and cannot fault - see FunctionSummaries.IsSpeculatableExternal for the
    // argument per row, and for what is deliberately kept off it.
    IrCall { Callee: IrFunction callee } => FunctionSummaries.IsSpeculatableExternal(callee.Name),
    _ => false,                                      // loads, stores, allocas, phis, terminators
  };
}
