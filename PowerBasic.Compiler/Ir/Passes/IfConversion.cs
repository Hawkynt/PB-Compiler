namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// If-conversion: turns a simple diamond into branchless <c>select</c>s. When a block
/// ends in <c>condbr c, T, E</c>, both T and E are empty (just a branch) and merge at a
/// common block M whose only predecessors are T and E, each phi in M becomes
/// <c>select c, valueFromT, valueFromE</c>, the diamond collapses to a straight edge to
/// M, and T and E are deleted. This removes two branches and a join - good for the
/// pattern <c>IF c THEN x = a ELSE x = b</c>, which mem2reg leaves as exactly this diamond.
/// </summary>
public static class IfConversion {

  public static int Run(IrFunction fn) {
    var converted = 0;
    foreach (var block in fn.Blocks.ToList()) {
      if (block.Parent is null || block.Terminator is not IrCondBr cb || ReferenceEquals(cb.IfTrue, cb.IfFalse))
        continue;
      if (!IsEmptyForward(cb.IfTrue, out var mergeFromT) || !IsEmptyForward(cb.IfFalse, out var mergeFromE))
        continue;
      if (!ReferenceEquals(mergeFromT, mergeFromE))
        continue;
      var (t, e, m) = (cb.IfTrue, cb.IfFalse, mergeFromT!);
      if (!SinglePred(t, block) || !SinglePred(e, block))
        continue;
      var mPreds = m.Predecessors.ToHashSet(ReferenceEqualityComparer.Instance);
      if (mPreds.Count != 2 || !mPreds.Contains(t) || !mPreds.Contains(e))
        continue;                                      // M must be reached only through the diamond

      foreach (var phi in m.Phis.ToList()) {
        var vt = phi.IncomingFrom(t);
        var ve = phi.IncomingFrom(e);
        if (vt is null || ve is null)
          continue;
        var select = new IrSelect(cb.Condition, vt, ve);
        m.InsertBefore(select, phi);
        phi.ReplaceAllUsesWith(select);
        phi.EraseFromParent();
      }

      cb.EraseFromParent();
      block.Append(new IrBr(m));
      foreach (var dead in new[] { t, e }) {
        foreach (var inst in dead.Instructions.ToList())
          inst.EraseFromParent();
        fn.RemoveBlock(dead);
      }
      ++converted;
    }
    return converted;
  }

  /// <summary>True if a block's only instruction is an unconditional branch; outputs its target.</summary>
  private static bool IsEmptyForward(IrBasicBlock block, out IrBasicBlock? target) {
    target = null;
    if (block.Instructions.Count == 1 && block.Terminator is IrBr br) {
      target = br.Target;
      return true;
    }
    return false;
  }

  private static bool SinglePred(IrBasicBlock block, IrBasicBlock expected) {
    var preds = block.Predecessors.ToList();
    return preds.Count == 1 && ReferenceEquals(preds[0], expected);
  }
}
