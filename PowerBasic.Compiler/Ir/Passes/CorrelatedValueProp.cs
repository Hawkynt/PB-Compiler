namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Correlated value propagation: when a block ends in <c>condbr (icmp eq x, C), T, F</c>
/// and the true successor T is entered only through that edge, then x == C throughout
/// the region T dominates. Since x is an SSA value, every non-phi use of x in that
/// region can be replaced by the constant C, which then folds. This propagates facts
/// learned from a branch into the code guarded by it.
/// </summary>
public static class CorrelatedValueProp {

  public static int Run(IrFunction fn) {
    if (fn.Entry is null)
      return 0;
    var dom = IrDominators.Build(fn)!;
    var changed = 0;

    foreach (var block in fn.Blocks) {
      if (block.Terminator is not IrCondBr cb || cb.Condition is not IrCmp { Pred: IrCmpPred.Eq } cmp)
        continue;

      IrValue variable;
      IrConstant constant;
      if (cmp.Rhs is IrConstant rc && cmp.Lhs is not IrConstant) { variable = cmp.Lhs; constant = rc; }
      else if (cmp.Lhs is IrConstant lc && cmp.Rhs is not IrConstant) { variable = cmp.Rhs; constant = lc; }
      else continue;

      var t = cb.IfTrue;
      var preds = t.Predecessors.ToList();
      if (preds.Count != 1 || !ReferenceEquals(preds[0], block))
        continue;                                    // T must be reached only via the true edge

      foreach (var user in variable.Users.ToList()) {
        if (user is IrPhi || user.Parent is not { } ub)
          continue;                                  // phi operands are edge-based; skip them
        if (dom.Dominates(t, ub))
          if (ReplaceOperandIn(user, variable, constant))
            ++changed;
      }
    }
    return changed;
  }

  private static bool ReplaceOperandIn(IrInstruction inst, IrValue from, IrValue to) {
    var any = false;
    for (var i = 0; i < inst.Operands.Count; ++i)
      if (ReferenceEquals(inst.GetOperand(i), from)) {
        inst.SetOperand(i, to);
        any = true;
      }
    return any;
  }
}
