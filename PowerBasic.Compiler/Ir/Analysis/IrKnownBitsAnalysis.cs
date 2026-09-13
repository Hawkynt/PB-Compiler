namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>Bits proven zero or one for an integer SSA value.</summary>
public readonly record struct IrKnownBits(int Width, ulong KnownZero, ulong KnownOne) {

  /// <summary>Mask containing every bit represented by this fact.</summary>
  public ulong Mask => Width >= 64 ? ulong.MaxValue : Width <= 0 ? 0 : (1UL << Width) - 1;

  /// <summary>Unknown facts for an integer of the supplied width.</summary>
  public static IrKnownBits Unknown(int width) => new(width, 0, 0);

  /// <summary>Exact facts for one integer bit pattern.</summary>
  public static IrKnownBits Constant(int width, ulong value) {
    var mask = width >= 64 ? ulong.MaxValue : width <= 0 ? 0 : (1UL << width) - 1;
    value &= mask;
    return new(width, (~value) & mask, value);
  }

  /// <summary>True when every bit selected by <paramref name="mask"/> is proven zero.</summary>
  public bool AreZero(ulong mask) => this.Width > 0 && (this.KnownZero & mask & this.Mask) == (mask & this.Mask);

  /// <summary>True when every bit selected by <paramref name="mask"/> is proven one.</summary>
  public bool AreOne(ulong mask) => this.Width > 0 && (this.KnownOne & mask & this.Mask) == (mask & this.Mask);

  internal IrKnownBits Normalize() {
    var mask = this.Mask;
    if ((this.KnownZero & this.KnownOne & mask) != 0)
      throw new InvalidOperationException("A bit cannot be known zero and known one simultaneously.");
    return new(this.Width, this.KnownZero & mask, this.KnownOne & mask);
  }
}

/// <summary>
/// Reusable target-independent known-bit queries for integer SSA values. Queries are intentionally recomputed from
/// the current graph rather than memoized inside the analysis object, so a value-rewriting transform can keep using
/// the manager-owned service during its own mutation loop without observing stale per-value facts.
/// </summary>
public sealed class IrKnownBitsAnalysis {

  private const int _MAX_DEPTH = 24;

  internal IrKnownBitsAnalysis(IrFunction function) {
    ArgumentNullException.ThrowIfNull(function);
  }

  /// <summary>Returns bits that are provably zero or one for <paramref name="value"/>.</summary>
  public IrKnownBits For(IrValue value) {
    ArgumentNullException.ThrowIfNull(value);
    if (!value.Type.IsInteger || value.Type.Bits is <= 0 or > 64)
      return default;
    return Compute(value, _MAX_DEPTH, new HashSet<IrValue>(ReferenceEqualityComparer.Instance)).Normalize();
  }

  private static IrKnownBits Compute(IrValue value, int depth, HashSet<IrValue> active) {
    var width = value.Type.Bits;
    if (!value.Type.IsInteger || width is <= 0 or > 64 || depth <= 0 || !active.Add(value))
      return IrKnownBits.Unknown(Math.Clamp(width, 0, 64));

    try {
      return value switch {
        IrConstantInt constant => IrKnownBits.Constant(width, unchecked((ulong)constant.Value)),
        IrBinary binary => Binary(binary, depth - 1, active),
        IrCast cast => Cast(cast, depth - 1, active),
        IrPhi phi => Meet(phi.Operands.Select(operand => Compute(operand, depth - 1, active)), width),
        IrSelect select => Meet([
          Compute(select.IfTrue, depth - 1, active),
          Compute(select.IfFalse, depth - 1, active),
        ], width),
        _ => IrKnownBits.Unknown(width),
      };
    } finally {
      active.Remove(value);
    }
  }

  private static IrKnownBits Binary(IrBinary binary, int depth, HashSet<IrValue> active) {
    var width = binary.Type.Bits;
    var left = Compute(binary.Lhs, depth, active);
    var right = Compute(binary.Rhs, depth, active);
    return binary.Op switch {
      IrBinaryOp.And => new(width,
        left.KnownZero | right.KnownZero,
        left.KnownOne & right.KnownOne),
      IrBinaryOp.Or => new(width,
        left.KnownZero & right.KnownZero,
        left.KnownOne | right.KnownOne),
      IrBinaryOp.Xor => new(width,
        (left.KnownZero & right.KnownZero) | (left.KnownOne & right.KnownOne),
        (left.KnownZero & right.KnownOne) | (left.KnownOne & right.KnownZero)),
      _ => IrKnownBits.Unknown(width),
    };
  }

  private static IrKnownBits Cast(IrCast cast, int depth, HashSet<IrValue> active) {
    if (!cast.Value.Type.IsInteger || !cast.Type.IsInteger)
      return IrKnownBits.Unknown(cast.Type.Bits);

    var source = Compute(cast.Value, depth, active);
    var targetWidth = cast.Type.Bits;
    var targetMask = targetWidth >= 64 ? ulong.MaxValue : (1UL << targetWidth) - 1;
    var sourceMask = source.Width >= 64 ? ulong.MaxValue : (1UL << source.Width) - 1;
    return cast.Op switch {
      IrCastOp.Trunc => new(targetWidth, source.KnownZero & targetMask, source.KnownOne & targetMask),
      IrCastOp.ZExt => new(targetWidth,
        (source.KnownZero & sourceMask) | (targetMask & ~sourceMask),
        source.KnownOne & sourceMask),
      IrCastOp.SExt => SignExtend(source, targetWidth),
      _ => IrKnownBits.Unknown(targetWidth),
    };
  }

  private static IrKnownBits SignExtend(IrKnownBits source, int targetWidth) {
    if (source.Width <= 0 || targetWidth <= source.Width)
      return new(targetWidth, source.KnownZero, source.KnownOne);

    var sourceMask = source.Width >= 64 ? ulong.MaxValue : (1UL << source.Width) - 1;
    var targetMask = targetWidth >= 64 ? ulong.MaxValue : (1UL << targetWidth) - 1;
    var high = targetMask & ~sourceMask;
    var sign = 1UL << (source.Width - 1);
    return new(targetWidth,
      (source.KnownZero & sourceMask) | (source.AreZero(sign) ? high : 0),
      (source.KnownOne & sourceMask) | (source.AreOne(sign) ? high : 0));
  }

  private static IrKnownBits Meet(IEnumerable<IrKnownBits> values, int width) {
    var mask = width >= 64 ? ulong.MaxValue : (1UL << width) - 1;
    var zero = mask;
    var one = mask;
    var any = false;
    foreach (var value in values) {
      any = true;
      zero &= value.KnownZero;
      one &= value.KnownOne;
    }
    return any ? new(width, zero, one) : IrKnownBits.Unknown(width);
  }
}
