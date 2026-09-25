namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>
/// Guaranteed low-bit alignment of pointer values, expressed as a power-of-two byte multiple.
///
/// <para>
/// This is a fact about the IR pointer/integer representation. It is intentionally not target load/store
/// metadata: a backend whose physical address model differs from the integer pointer representation must
/// still apply its own legality rules before turning this fact into an aligned machine access.
/// </para>
/// <para>
/// The domain combines explicit pointer arithmetic with dominating canonical guards of the form
/// <c>(ptrtoint p &amp; (2^k - 1)) == 0</c>. A false/non-zero guard does not manufacture a positive
/// alignment guarantee, but it can still imply another alignment test through
/// <see cref="DecideUnder"/>.
/// </para>
/// </summary>
public sealed class IrAlignmentAnalysis {
  private readonly IrDominators? _dominators;

  private readonly record struct AlignmentTest(IrValue Pointer, ulong Mask, bool IsZero) {
    public ulong Alignment => this.Mask + 1;
  }

  internal IrAlignmentAnalysis(IrDominators? dominators) => this._dominators = dominators;

  /// <summary>
  /// Minimum power-of-two alignment known for <paramref name="value"/> at <paramref name="block"/>.
  /// One means no useful alignment guarantee.
  /// </summary>
  public ulong MinimumAt(IrValue value, IrBasicBlock block) {
    ArgumentNullException.ThrowIfNull(value);
    ArgumentNullException.ThrowIfNull(block);
    if (!value.Type.IsPointer)
      return 1;

    var alignment = StructuralAlignment(value, new HashSet<IrValue>(ReferenceEqualityComparer.Instance));
    if (this._dominators is not { } dom)
      return alignment;

    for (var at = dom.ImmediateDominatorOf(block); at is not null; at = dom.ImmediateDominatorOf(at)) {
      if (at.Terminator is IrCondBr branch
          && branch.Condition is IrCmp guard
          && TryAlignmentTest(guard, out var test)
          && ReferenceEquals(test.Pointer, value)) {
        bool? outcome = null;
        if (dom.EdgeDominates(at, branch.IfTrue, block))
          outcome = true;
        else if (dom.EdgeDominates(at, branch.IfFalse, block))
          outcome = false;

        if (outcome is { } selected && test.IsZero == selected)
          alignment = Math.Max(alignment, test.Alignment);
      }

      var parent = dom.ImmediateDominatorOf(at);
      if (ReferenceEquals(parent, at))
        break;
    }
    return alignment;
  }

  /// <summary>Decides a canonical alignment comparison from facts already valid at a program point.</summary>
  public bool? Decide(IrCmp comparison, IrBasicBlock block) {
    ArgumentNullException.ThrowIfNull(comparison);
    ArgumentNullException.ThrowIfNull(block);
    if (!TryAlignmentTest(comparison, out var test))
      return null;

    return this.MinimumAt(test.Pointer, block) >= test.Alignment
      ? test.IsZero
      : null;
  }

  /// <summary>
  /// Decides <paramref name="comparison"/> with one additional branch assumption. This is used by
  /// versioning/speculation before the assumed path has become a dominating CFG fact.
  /// </summary>
  public bool? DecideUnder(
      IrCmp comparison,
      IrCmp assumption,
      bool assumptionOutcome,
      IrBasicBlock block) {
    ArgumentNullException.ThrowIfNull(comparison);
    ArgumentNullException.ThrowIfNull(assumption);
    ArgumentNullException.ThrowIfNull(block);

    if (this.Decide(comparison, block) is { } alreadyKnown)
      return alreadyKnown;

    if (!TryAlignmentTest(assumption, out var premise)
        || !TryAlignmentTest(comparison, out var candidate)
        || !ReferenceEquals(premise.Pointer, candidate.Pointer))
      return null;

    var premiseSaysZero = premise.IsZero == assumptionOutcome;
    if (premiseSaysZero) {
      if ((candidate.Mask & ~premise.Mask) != 0)
        return null; // only a subset of the known-zero low bits is also guaranteed zero
      return candidate.IsZero;
    }

    if ((premise.Mask & ~candidate.Mask) != 0)
      return null;   // the bit known non-zero may have been excluded by the candidate mask
    return !candidate.IsZero;
  }

  private static ulong StructuralAlignment(IrValue value, HashSet<IrValue> active) {
    if (!active.Add(value))
      return 1;

    try {
      switch (value) {
        case IrCast { Op: IrCastOp.BitCast } cast when cast.Type.IsPointer && cast.Value.Type.IsPointer:
          return StructuralAlignment(cast.Value, active);

        case IrGep gep when TryRoundedUpAlignment(gep, out var rounded):
          return rounded;

        case IrGep { ByteOffset: IrConstantInt offset } gep:
          var baseAlignment = StructuralAlignment(gep.BasePtr, active);
          if (offset.IsZero)
            return baseAlignment;
          return Math.Min(baseAlignment, PowerOfTwoDivisor(offset.ZeroExtended));

        case IrSelect select when select.Type.IsPointer:
          return Math.Min(
            StructuralAlignment(select.IfTrue, active),
            StructuralAlignment(select.IfFalse, active));

        case IrPhi phi when phi.Type.IsPointer && phi.Operands.Count > 0:
          var alignment = ulong.MaxValue;
          foreach (var incoming in phi.Operands)
            alignment = Math.Min(alignment, StructuralAlignment(incoming, active));
          return alignment == ulong.MaxValue ? 1 : alignment;

        default:
          return 1;
      }
    } finally {
      active.Remove(value);
    }
  }

  private static bool TryRoundedUpAlignment(IrGep gep, out ulong alignment) {
    alignment = 1;
    if (gep.ByteOffset is not IrBinary { Op: IrBinaryOp.And } and)
      return false;

    IrValue adjustmentSource;
    IrConstantInt maskConstant;
    if (and.Rhs is IrConstantInt right) {
      adjustmentSource = and.Lhs;
      maskConstant = right;
    } else if (and.Lhs is IrConstantInt left) {
      adjustmentSource = and.Rhs;
      maskConstant = left;
    } else {
      return false;
    }

    if (adjustmentSource is not IrBinary {
          Op: IrBinaryOp.Sub,
          Lhs: IrConstantInt { IsZero: true },
          Rhs: IrCast { Op: IrCastOp.PtrToInt } ptrToInt
        }
        || !ReferenceEquals(ptrToInt.Value, gep.BasePtr))
      return false;

    var mask = maskConstant.ZeroExtended;
    if (!IsAlignmentMask(mask))
      return false;

    alignment = mask + 1;
    return true;
  }

  private static bool TryAlignmentTest(IrCmp comparison, out AlignmentTest test) {
    test = default;
    if (comparison.Pred is not (IrCmpPred.Eq or IrCmpPred.Ne))
      return false;

    IrValue masked;
    if (comparison.Rhs is IrConstantInt { IsZero: true })
      masked = comparison.Lhs;
    else if (comparison.Lhs is IrConstantInt { IsZero: true })
      masked = comparison.Rhs;
    else
      return false;

    if (masked is not IrBinary { Op: IrBinaryOp.And } and)
      return false;

    IrValue bits;
    IrConstantInt maskConstant;
    if (and.Rhs is IrConstantInt right) {
      bits = and.Lhs;
      maskConstant = right;
    } else if (and.Lhs is IrConstantInt left) {
      bits = and.Rhs;
      maskConstant = left;
    } else {
      return false;
    }

    if (bits is not IrCast { Op: IrCastOp.PtrToInt } ptrToInt)
      return false;

    var mask = maskConstant.ZeroExtended;
    if (!IsAlignmentMask(mask))
      return false;

    test = new(ptrToInt.Value, mask, comparison.Pred == IrCmpPred.Eq);
    return true;
  }

  private static bool IsAlignmentMask(ulong mask)
    => mask != 0 && mask != ulong.MaxValue && (mask & (mask + 1)) == 0;

  private static ulong PowerOfTwoDivisor(ulong value) {
    if (value == 0)
      return ulong.MaxValue;
    return value & unchecked(0UL - value);
  }
}
