namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>
/// Conservative known-zero/known-one facts for fixed-width integer SSA values.
///
/// <para>
/// This is a deliberately small abstract domain rather than a second constant folder. It answers the
/// question bitwise transforms actually ask: which result bits are guaranteed zero or one for every
/// execution? The bootstrap domain understands exact constants, bitwise AND/OR/XOR, integer
/// truncation/extension, selects and cycle-safe phi meets. Unsupported operations remain unknown.
/// </para>
/// </summary>
public sealed class IrKnownBitsAnalysis {

  /// <summary>Bit facts for one integer value. Known-zero and known-one masks are always disjoint.</summary>
  public readonly record struct KnownBits(int Width, ulong Zero, ulong One) {
    /// <summary>True when every bit selected by <paramref name="mask"/> is known zero.</summary>
    public bool AreZero(ulong mask)
      => this.Width > 0 && (this.Zero & (mask & Mask(this.Width))) == (mask & Mask(this.Width));

    /// <summary>True when every bit selected by <paramref name="mask"/> is known one.</summary>
    public bool AreOne(ulong mask)
      => this.Width > 0 && (this.One & (mask & Mask(this.Width))) == (mask & Mask(this.Width));
  }

  private const int _MAX_DEPTH = 64;
  private readonly Dictionary<IrValue, KnownBits> _cache = new(ReferenceEqualityComparer.Instance);
  private readonly HashSet<IrValue> _computing = new(ReferenceEqualityComparer.Instance);

  /// <summary>Creates an empty known-bits cache. The analysis is value-driven and does not need a CFG upfront.</summary>
  public IrKnownBitsAnalysis() { }

  /// <summary>Compatibility constructor for callers that conceptually bind analyses to one function.</summary>
  public IrKnownBitsAnalysis(IrFunction function) {
    ArgumentNullException.ThrowIfNull(function);
  }

  /// <summary>Returns conservative known-zero/known-one masks for <paramref name="value"/>.</summary>
  public KnownBits For(IrValue value) {
    ArgumentNullException.ThrowIfNull(value);
    if (!value.Type.IsInteger || value.Type.Bits is <= 0 or > 64)
      return default;
    if (this._cache.TryGetValue(value, out var cached))
      return cached;
    return this.Compute(value, _MAX_DEPTH);
  }

  private KnownBits Compute(IrValue value, int depth) {
    if (this._cache.TryGetValue(value, out var cached))
      return cached;
    var width = value.Type.IsInteger ? value.Type.Bits : 0;
    if (width is <= 0 or > 64 || depth <= 0 || !this._computing.Add(value))
      return Unknown(width);

    try {
      var result = value switch {
        IrConstantInt constant => Exact(constant),
        IrBinary binary => this.Binary(binary, depth - 1),
        IrCast cast => this.Cast(cast, depth - 1),
        IrSelect select => Meet(this.Compute(select.IfTrue, depth - 1), this.Compute(select.IfFalse, depth - 1)),
        IrPhi phi => this.Phi(phi, depth - 1),
        _ => Unknown(width),
      };
      this._cache[value] = result;
      return result;
    } finally {
      this._computing.Remove(value);
    }
  }

  private KnownBits Binary(IrBinary binary, int depth) {
    var left = this.Compute(binary.Lhs, depth);
    var right = this.Compute(binary.Rhs, depth);
    var mask = Mask(binary.Type.Bits);
    return binary.Op switch {
      IrBinaryOp.And => new(binary.Type.Bits,
        (left.Zero | right.Zero) & mask,
        (left.One & right.One) & mask),
      IrBinaryOp.Or => new(binary.Type.Bits,
        (left.Zero & right.Zero) & mask,
        (left.One | right.One) & mask),
      IrBinaryOp.Xor => new(binary.Type.Bits,
        ((left.Zero & right.Zero) | (left.One & right.One)) & mask,
        ((left.Zero & right.One) | (left.One & right.Zero)) & mask),
      _ => Unknown(binary.Type.Bits),
    };
  }

  private KnownBits Cast(IrCast cast, int depth) {
    if (!cast.Value.Type.IsInteger || !cast.Type.IsInteger)
      return Unknown(cast.Type.Bits);
    var source = this.Compute(cast.Value, depth);
    var sourceMask = Mask(cast.Value.Type.Bits);
    var targetMask = Mask(cast.Type.Bits);
    return cast.Op switch {
      IrCastOp.Trunc => new(cast.Type.Bits, source.Zero & targetMask, source.One & targetMask),
      IrCastOp.ZExt => new(cast.Type.Bits,
        (source.Zero | (targetMask & ~sourceMask)) & targetMask,
        source.One & sourceMask),
      IrCastOp.SExt when cast.Value.Type.Bits > 0 => SignExtend(source, cast.Value.Type.Bits, cast.Type.Bits),
      IrCastOp.BitCast when cast.Value.Type.SameStorage(cast.Type)
        => new(cast.Type.Bits, source.Zero & targetMask, source.One & targetMask),
      _ => Unknown(cast.Type.Bits),
    };
  }

  private KnownBits Phi(IrPhi phi, int depth) {
    KnownBits? result = null;
    foreach (var incoming in phi.Operands) {
      if (ReferenceEquals(incoming, phi))
        continue;
      var bits = this.Compute(incoming, depth);
      result = result is { } current ? Meet(current, bits) : bits;
    }
    return result ?? Unknown(phi.Type.Bits);
  }

  private static KnownBits SignExtend(KnownBits source, int sourceWidth, int targetWidth) {
    var sourceMask = Mask(sourceWidth);
    var targetMask = Mask(targetWidth);
    var high = targetMask & ~sourceMask;
    var signBit = 1UL << (sourceWidth - 1);
    var zero = source.Zero & sourceMask;
    var one = source.One & sourceMask;
    if ((zero & signBit) != 0)
      zero |= high;
    else if ((one & signBit) != 0)
      one |= high;
    return new(targetWidth, zero & targetMask, one & targetMask);
  }

  private static KnownBits Exact(IrConstantInt constant) {
    var mask = Mask(constant.Type.Bits);
    var bits = unchecked((ulong)constant.Value) & mask;
    return new(constant.Type.Bits, (~bits) & mask, bits);
  }

  private static KnownBits Meet(KnownBits left, KnownBits right) {
    if (left.Width <= 0 || right.Width <= 0 || left.Width != right.Width)
      return Unknown(Math.Max(left.Width, right.Width));
    var mask = Mask(left.Width);
    return new(left.Width, (left.Zero & right.Zero) & mask, (left.One & right.One) & mask);
  }

  private static KnownBits Unknown(int width) => new(width, 0, 0);

  private static ulong Mask(int width) => width switch {
    <= 0 => 0,
    >= 64 => ulong.MaxValue,
    _ => (1UL << width) - 1,
  };
}
