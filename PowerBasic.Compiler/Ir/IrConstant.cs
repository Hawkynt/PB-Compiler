namespace PowerBasic.Compiler.Ir;

/// <summary>A compile-time constant operand.</summary>
public abstract class IrConstant(IrType type) : IrValue(type);

/// <summary>
/// An integer constant. The <see cref="Value"/> is stored as a 64-bit two's-complement
/// pattern; its interpretation (signed vs unsigned) is decided by the consuming
/// instruction, exactly as in LLVM where <c>i32 -1</c> and <c>i32 4294967295</c> share a bit pattern.
/// </summary>
public sealed class IrConstantInt(IrType type, long value) : IrConstant(type) {
  public long Value { get; } = value;

  /// <summary>The value masked to its type's bit width (the canonical unsigned pattern).</summary>
  public ulong ZeroExtended => this.Type.Bits >= 64
    ? unchecked((ulong)this.Value)
    : (ulong)this.Value & ((1UL << this.Type.Bits) - 1);

  public bool IsZero => (this.Value & (this.Type.Bits >= 64 ? -1L : (1L << this.Type.Bits) - 1)) == 0;
}

/// <summary>
/// A floating-point constant. Binary32 and binary64 retain the existing host value representation;
/// x87 binary80 stores its architectural 80-bit representation exactly in <see cref="Float80"/>.
///
/// <para>
/// An <c>f32</c> constant is rounded to single precision on construction: PB types an unsuffixed
/// decimal literal SINGLE, so <c>d# = 3.14159</c> must widen the single value
/// (3.1415901184082) rather than the full-precision one.
/// </para>
/// <para>
/// The <see cref="Value"/> compatibility property is intentionally available only when the value is
/// exactly representable as binary64. Code that can see arbitrary F80 constants must use
/// <see cref="TryGetDoubleExact"/> or <see cref="Float80"/> instead; silently rounding an extended
/// constant to <see cref="double"/> would turn an optimization into a miscompile.
/// </para>
/// </summary>
public sealed class IrConstantFloat : IrConstant {
  private readonly double _value;
  private readonly IrFloat80 _float80;

  /// <summary>Constructs a binary32/binary64 value, or exactly widens a binary64 value to F80.</summary>
  public IrConstantFloat(IrType type, double value) : base(type) {
    this._value = type.Bits == 32 ? (float)value : value;
    if (type is { Kind: IrTypeKind.Float, Format: IrFloatFormat.Ieee, Bits: 80 })
      this._float80 = IrFloat80.FromDouble(value);
  }

  /// <summary>Constructs an F80 constant from its exact architectural bit fields.</summary>
  public IrConstantFloat(IrFloat80 value) : base(IrType.F80) => this._float80 = value;

  /// <summary>Constructs an F80 constant from its exact sign/exponent word and significand.</summary>
  public static IrConstantFloat FromFloat80Bits(ushort signExponent, ulong significand)
    => new(new IrFloat80(signExponent, significand));

  /// <summary>
  /// The value as binary64, only when that is exact. Arbitrary F80 constants deliberately throw here
  /// rather than returning a rounded approximation; use <see cref="TryGetDoubleExact"/> in code that
  /// may encounter them.
  /// </summary>
  public double Value => this.TryGetDoubleExact(out var value)
    ? value
    : throw new InvalidOperationException($"{this.Type} constant {this.Float80.ToLlvmHexString()} is not exactly representable as binary64");

  /// <summary>The exact x87 representation. Valid only for an IEEE F80 constant.</summary>
  public IrFloat80 Float80 => this.Type is { Kind: IrTypeKind.Float, Format: IrFloatFormat.Ieee, Bits: 80 }
    ? this._float80
    : throw new InvalidOperationException($"{this.Type} is not an x87 extended floating type");

  /// <summary>Returns the binary64 value iff no rounding, overflow, underflow, or payload loss occurs.</summary>
  public bool TryGetDoubleExact(out double value) {
    if (this.Type is { Kind: IrTypeKind.Float, Format: IrFloatFormat.Ieee, Bits: 80 })
      return this._float80.TryToDoubleExact(out value);
    value = this._value;
    return true;
  }

  /// <summary>True when this constant is exactly the supplied binary64 value in its declared format.</summary>
  public bool IsExactly(double value) {
    if (this.Type is { Kind: IrTypeKind.Float, Format: IrFloatFormat.Ieee, Bits: 80 })
      return this._float80 == IrFloat80.FromDouble(value);
    var declared = this.Type.Bits == 32 ? (double)(float)value : value;
    return this._value.Equals(declared);
  }

  /// <summary>True when this value is a NaN without narrowing an F80 payload.</summary>
  public bool IsNaN => this.Type.Bits == 80 && this.Type.IsIeeeFloat
    ? this._float80.IsNaN
    : double.IsNaN(this._value);

  /// <summary>True only for a numerically meaningful finite value.</summary>
  public bool IsFinite => this.Type.Bits == 80 && this.Type.IsIeeeFloat
    ? this._float80.IsFinite
    : double.IsFinite(this._value);

  /// <summary>True for either signed floating zero.</summary>
  public bool IsZero => this.Type.Bits == 80 && this.Type.IsIeeeFloat
    ? this._float80.IsZero
    : this._value == 0.0;

  /// <summary>Raw-bit equality, including signed zero and NaN payloads.</summary>
  public bool SameBits(IrConstantFloat other) {
    if (other is null || !this.Type.Equals(other.Type))
      return false;
    return this.Type.Bits switch {
      32 => BitConverter.SingleToInt32Bits((float)this._value) == BitConverter.SingleToInt32Bits((float)other._value),
      64 => BitConverter.DoubleToInt64Bits(this._value) == BitConverter.DoubleToInt64Bits(other._value),
      80 when this.Type.IsIeeeFloat => this._float80 == other._float80,
      _ => BitConverter.DoubleToInt64Bits(this._value) == BitConverter.DoubleToInt64Bits(other._value),
    };
  }

  /// <summary>A deterministic exact key for value numbering and diagnostics.</summary>
  public string BitPatternKey() => this.Type.Bits switch {
    32 => unchecked((uint)BitConverter.SingleToInt32Bits((float)this._value)).ToString("X8", System.Globalization.CultureInfo.InvariantCulture),
    64 => unchecked((ulong)BitConverter.DoubleToInt64Bits(this._value)).ToString("X16", System.Globalization.CultureInfo.InvariantCulture),
    80 when this.Type.IsIeeeFloat => this._float80.ToBitString(),
    _ => unchecked((ulong)BitConverter.DoubleToInt64Bits(this._value)).ToString("X16", System.Globalization.CultureInfo.InvariantCulture),
  };

  /// <summary>Clones without routing F80 through binary64.</summary>
  public IrConstantFloat Clone()
    => this.Type.Bits == 80 && this.Type.IsIeeeFloat ? new(this._float80) : new(this.Type, this._value);
}

/// <summary>
/// The <c>null</c> pointer constant. It carries an address space so that seeding an unwritten pointer
/// slot does not quietly narrow a far one to the program's own memory - the bits are the same either
/// way, but the TYPE is what every later read of that slot inherits.
/// </summary>
public sealed class IrNullPtr(IrType? type = null) : IrConstant(type ?? IrType.Ptr);

/// <summary>
/// The address of a basic block - LLVM's <c>blockaddress</c>. PB needs one for exactly one reason:
/// <c>ON ERROR GOTO</c> arms a handler by writing its code address into a runtime cell, and a fault
/// anywhere afterwards - including inside a runtime routine - resumes there. That control-flow edge
/// exists at run time but has no representation as a CFG edge, because its source is "any point in
/// the armed region".
///
/// A function holding one of these is therefore <see cref="IrFunction.HasErrorHandler"/>, and the
/// optimizer is required to leave it alone: a pass reasoning from the CFG alone would conclude the
/// handler is unreachable, or that a variable's value at handler entry is the one that reaches its
/// only visible predecessor. Both conclusions are wrong, and both are silent. The direct emitter
/// takes the identical discipline - <c>_trackResume</c> switches its optimizations off.
/// </summary>
public sealed class IrBlockAddress(IrBasicBlock block) : IrConstant(IrType.Ptr) {
  /// <summary>The block whose address this is.</summary>
  public IrBasicBlock Block { get; } = block;
}

/// <summary>
/// An undefined value of a given type. Reading it yields an arbitrary bit pattern;
/// it marks "any value is acceptable here" so later passes are free to choose one.
/// </summary>
public sealed class IrUndef(IrType type) : IrConstant(type);
