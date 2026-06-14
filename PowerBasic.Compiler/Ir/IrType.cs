namespace PowerBasic.Compiler.Ir;

/// <summary>
/// The kind of an <see cref="IrType"/>. The IR type system is deliberately
/// target-independent (an LLVM-style first-class type lattice) so the same IR can
/// be lowered to 16-bit DOS, 32/64-bit native, or a textual LLVM module.
/// </summary>
public enum IrTypeKind {
  /// <summary>No value (the result type of stores, void calls and ret void).</summary>
  Void,
  /// <summary>An integer of a fixed bit width (<c>i1</c>, <c>i8</c>, <c>i16</c>, <c>i32</c>, <c>i64</c>).</summary>
  Int,
  /// <summary>An IEEE / x87 floating-point value (<c>f32</c>, <c>f64</c>, <c>f80</c>).</summary>
  Float,
  /// <summary>An opaque pointer (<c>ptr</c>); the pointee type travels on the memory instruction, as in modern LLVM.</summary>
  Ptr,
}

/// <summary>
/// A value type in the IR. Types are immutable and value-equatable, so a single
/// canonical instance per shape can be shared and compared by reference or value.
/// </summary>
/// <param name="Kind">The category of the type.</param>
/// <param name="Bits">
/// The bit width for <see cref="IrTypeKind.Int"/> and <see cref="IrTypeKind.Float"/>;
/// zero for <see cref="IrTypeKind.Void"/> and <see cref="IrTypeKind.Ptr"/> (a pointer's
/// storage width is a target property, not a type property).
/// </param>
public sealed record IrType(IrTypeKind Kind, int Bits) {

  public static readonly IrType Void = new(IrTypeKind.Void, 0);
  public static readonly IrType I1 = new(IrTypeKind.Int, 1);
  public static readonly IrType I8 = new(IrTypeKind.Int, 8);
  public static readonly IrType I16 = new(IrTypeKind.Int, 16);
  public static readonly IrType I32 = new(IrTypeKind.Int, 32);
  public static readonly IrType I64 = new(IrTypeKind.Int, 64);
  public static readonly IrType F32 = new(IrTypeKind.Float, 32);
  public static readonly IrType F64 = new(IrTypeKind.Float, 64);
  public static readonly IrType F80 = new(IrTypeKind.Float, 80);
  public static readonly IrType Ptr = new(IrTypeKind.Ptr, 0);

  /// <summary>Returns the canonical integer type of the given bit width.</summary>
  public static IrType Integer(int bits) => bits switch {
    1 => I1,
    8 => I8,
    16 => I16,
    32 => I32,
    64 => I64,
    _ => new IrType(IrTypeKind.Int, bits),
  };

  /// <summary>Returns the canonical floating-point type of the given bit width.</summary>
  public static IrType Floating(int bits) => bits switch {
    32 => F32,
    64 => F64,
    80 => F80,
    _ => new IrType(IrTypeKind.Float, bits),
  };

  public bool IsVoid => this.Kind == IrTypeKind.Void;
  public bool IsInteger => this.Kind == IrTypeKind.Int;
  public bool IsFloat => this.Kind == IrTypeKind.Float;
  public bool IsPointer => this.Kind == IrTypeKind.Ptr;

  /// <summary>True for the single-bit boolean type produced by comparisons.</summary>
  public bool IsBool => this.Kind == IrTypeKind.Int && this.Bits == 1;

  public override string ToString() => this.Kind switch {
    IrTypeKind.Void => "void",
    IrTypeKind.Int => "i" + this.Bits,
    IrTypeKind.Float => "f" + this.Bits,
    IrTypeKind.Ptr => "ptr",
    _ => "?",
  };
}
