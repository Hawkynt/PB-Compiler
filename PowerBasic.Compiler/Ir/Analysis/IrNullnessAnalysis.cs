namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>Pointer nullness established without assuming that address zero faults on the PB memory model.</summary>
public enum IrNullness {
  Unknown,
  Null,
  NonNull,
}

/// <summary>
/// Branch-refined pointer nullness. Only explicit dominating <c>ptr == null</c>/<c>ptr != null</c>
/// edges contribute contextual facts; dereferences deliberately do not, because address zero is not
/// intrinsically trapping on the DOS memory model.
/// </summary>
public sealed class IrNullnessAnalysis {
  private readonly IrDominators? _dominators;

  internal IrNullnessAnalysis(IrDominators? dominators) => this._dominators = dominators;

  public IrNullness At(IrValue value, IrBasicBlock block) {
    ArgumentNullException.ThrowIfNull(value);
    ArgumentNullException.ThrowIfNull(block);

    if (value is IrNullPtr)
      return IrNullness.Null;
    if (!value.Type.IsPointer || this._dominators is null)
      return IrNullness.Unknown;

    var dom = this._dominators;
    for (var at = dom.ImmediateDominatorOf(block); at is not null; at = dom.ImmediateDominatorOf(at)) {
      if (at.Terminator is IrCondBr branch
          && branch.Condition is IrCmp guard
          && TryNullTest(guard, out var guarded, out var trueMeansNull)
          && ReferenceEquals(guarded, value)) {
        if (dom.EdgeDominates(at, branch.IfTrue, block))
          return trueMeansNull ? IrNullness.Null : IrNullness.NonNull;
        if (dom.EdgeDominates(at, branch.IfFalse, block))
          return trueMeansNull ? IrNullness.NonNull : IrNullness.Null;
      }
      if (ReferenceEquals(at, dom.ImmediateDominatorOf(at)))
        break;
    }
    return IrNullness.Unknown;
  }

  internal static bool TryNullTest(IrCmp comparison, out IrValue value, out bool trueMeansNull) {
    value = null!;
    trueMeansNull = false;
    if (comparison.Pred is not (IrCmpPred.Eq or IrCmpPred.Ne))
      return false;

    if (comparison.Lhs.Type.IsPointer && comparison.Rhs is IrNullPtr) {
      value = comparison.Lhs;
      trueMeansNull = comparison.Pred == IrCmpPred.Eq;
      return true;
    }
    if (comparison.Rhs.Type.IsPointer && comparison.Lhs is IrNullPtr) {
      value = comparison.Rhs;
      trueMeansNull = comparison.Pred == IrCmpPred.Eq;
      return true;
    }
    return false;
  }
}
