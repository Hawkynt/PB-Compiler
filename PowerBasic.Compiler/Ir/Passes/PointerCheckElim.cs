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
    if (IrDominators.Build(fn) is not { } dom)
      return 0;

    var decided = new List<(IrCmp Cmp, bool Outcome)>();
    foreach (var block in dom.ReversePostorder)
      foreach (var cmp in block.Instructions.OfType<IrCmp>()) {
        if (cmp.HasNoUsers || !TryNullTest(cmp, out var value, out var trueMeansNull))
          continue;
        if (KnownNullness(value, block, dom) is not { } isNull)
          continue;
        decided.Add((cmp, isNull == trueMeansNull));
      }

    foreach (var (cmp, outcome) in decided)
      cmp.ReplaceAllUsesWith(IrBuilder.ConstBool(outcome));
    return decided.Count;
  }

  private static bool? KnownNullness(IrValue value, IrBasicBlock block, IrDominators dom) {
    for (var at = dom.ImmediateDominatorOf(block); at is not null; at = dom.ImmediateDominatorOf(at)) {
      if (at.Terminator is IrCondBr branch
          && branch.Condition is IrCmp guard
          && TryNullTest(guard, out var guarded, out var trueMeansNull)
          && ReferenceEquals(guarded, value)) {
        if (dom.Dominates(branch.IfTrue, block))
          return trueMeansNull;
        if (dom.Dominates(branch.IfFalse, block))
          return !trueMeansNull;
      }
      if (ReferenceEquals(at, dom.ImmediateDominatorOf(at)))
        break;
    }
    return null;
  }

  private static bool TryNullTest(IrCmp cmp, out IrValue value, out bool trueMeansNull) {
    value = null!;
    trueMeansNull = false;
    if (cmp.Pred is not (IrCmpPred.Eq or IrCmpPred.Ne))
      return false;

    if (cmp.Lhs.Type.IsPointer && cmp.Rhs is IrNullPtr) {
      value = cmp.Lhs;
      trueMeansNull = cmp.Pred == IrCmpPred.Eq;
      return true;
    }
    if (cmp.Rhs.Type.IsPointer && cmp.Lhs is IrNullPtr) {
      value = cmp.Rhs;
      trueMeansNull = cmp.Pred == IrCmpPred.Eq;
      return true;
    }
    return false;
  }
}
