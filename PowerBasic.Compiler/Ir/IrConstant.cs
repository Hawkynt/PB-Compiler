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
/// A floating-point constant. An <c>f32</c> constant is rounded to single precision on
/// construction: PB types an unsuffixed decimal literal SINGLE, so <c>d# = 3.14159</c> must widen
/// the single value (3.1415901184082) rather than the full-precision one - keeping the wider
/// payload would make the IR disagree with the direct emitter, and would not even be a legal
/// LLVM <c>float</c> literal.
/// </summary>
public sealed class IrConstantFloat(IrType type, double value) : IrConstant(type) {
  public double Value { get; } = type.Bits == 32 ? (float)value : value;
}

/// <summary>The <c>null</c> pointer constant.</summary>
public sealed class IrNullPtr() : IrConstant(IrType.Ptr);

/// <summary>
/// An undefined value of a given type. Reading it yields an arbitrary bit pattern;
/// it marks "any value is acceptable here" so later passes are free to choose one.
/// </summary>
public sealed class IrUndef(IrType type) : IrConstant(type);
