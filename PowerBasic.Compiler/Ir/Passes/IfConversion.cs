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
      if (TryConvertAbs(fn, block, cb)) {
        ++converted;
        continue;
      }
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

  /// <summary>
  /// Converts <c>IF x &lt; 0 THEN x = -x</c>'s one-instruction diamond to the canonical
  /// <c>mask = x &gt;&gt;s 15; (x XOR mask) - mask</c> form. A general non-empty arm is not safe to
  /// speculate; this exact wrapping integer negation is, and overflow-checked lowering has extra
  /// control flow so it deliberately cannot match.
  /// </summary>
  private static bool TryConvertAbs(IrFunction fn, IrBasicBlock block, IrCondBr branch) {
    if (branch.Condition is not IrCmp {
          Pred: IrCmpPred.Slt,
          Lhs: { Type: { IsInteger: true, Signed: true, Bits: 16 } } source,
          Rhs: IrConstantInt { Value: 0 },
        })
      return false;
    if (branch.IfTrue.Instructions is not
        [IrBinary { Op: IrBinaryOp.Sub, Lhs: IrConstantInt { Value: 0 } } negated, IrBr negativeJump]
        || !ReferenceEquals(negated.Rhs, source)
        || branch.IfFalse.Instructions is not [IrBr nonNegativeJump]
        || !ReferenceEquals(negativeJump.Target, nonNegativeJump.Target))
      return false;

    var merge = negativeJump.Target;
    if (!SinglePred(branch.IfTrue, block) || !SinglePred(branch.IfFalse, block))
      return false;
    var predecessors = merge.Predecessors.ToHashSet(ReferenceEqualityComparer.Instance);
    if (predecessors.Count != 2 || !predecessors.Contains(branch.IfTrue) || !predecessors.Contains(branch.IfFalse))
      return false;
    if (merge.Phis.ToList() is not [var phi]
        || !ReferenceEquals(phi.IncomingFrom(branch.IfTrue), negated)
        || !ReferenceEquals(phi.IncomingFrom(branch.IfFalse), source))
      return false;

    var mask = block.InsertBefore(
      new IrBinary(IrBinaryOp.AShr, source, new IrConstantInt(source.Type, 15)), branch);
    var inverted = block.InsertBefore(new IrBinary(IrBinaryOp.Xor, source, mask), branch);
    var absolute = block.InsertBefore(new IrBinary(IrBinaryOp.Sub, inverted, mask), branch);
    phi.ReplaceAllUsesWith(absolute);
    phi.EraseFromParent();
    branch.EraseFromParent();
    block.Append(new IrBr(merge));
    foreach (var dead in new[] { branch.IfTrue, branch.IfFalse }) {
      foreach (var instruction in dead.Instructions.ToList())
        instruction.EraseFromParent();
      fn.RemoveBlock(dead);
    }
    return true;
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
