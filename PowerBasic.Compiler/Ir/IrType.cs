namespace PowerBasic.Compiler.Ir;

/// <summary>
/// The kind of an <see cref="IrType"/>. The IR type system is deliberately
/// target-independent (an LLVM-style first-class type lattice) so the same IR can
/// be lowered to 16-bit DOS, 32/64-bit native, or a textual LLVM module.
/// </summary>
public enum IrTypeKind {
  /// <summary>No value (the result type of stores, void calls and ret void).</summary>
  Void,
  /// <summary>An integer of a fixed bit width (<c>i1</c>, <c>i8</c>, <c>i16</c>, <c>i32</c>, <c>i64</c>) and a signedness.</summary>
  Int,
  /// <summary>A floating-point value - IEEE/x87 (<c>f32</c>, <c>f64</c>, <c>f80</c>) or Microsoft Binary Format (<c>mbf32</c>, <c>mbf64</c>).</summary>
  Float,
  /// <summary>An opaque pointer (<c>ptr</c>); the pointee type travels on the memory instruction, as in modern LLVM.</summary>
  Ptr,
}

/// <summary>
/// The in-memory encoding of a floating-point value. LLVM has only one (IEEE), but the BASIC family
/// does not: BASICA, GW-BASIC and the BASCOM-heritage QuickBASIC releases store SINGLE and DOUBLE in
/// <b>Microsoft Binary Format</b> - a different exponent bias, an explicit-sign layout and no
/// infinities or NaNs. MBF is a <i>storage</i> format: the x87 cannot compute on it, so a value is
/// converted to IEEE on load and back on store (<see cref="IrCastOp.MbfToFP"/> /
/// <see cref="IrCastOp.FPToMbf"/>), exactly as the direct emitter does.
/// </summary>
public enum IrFloatFormat {
  /// <summary>IEEE 754 binary32/binary64, or the x87 80-bit extended format.</summary>
  Ieee,
  /// <summary>Microsoft Binary Format (4- or 8-byte); storage only - never an arithmetic operand.</summary>
  Mbf,
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
/// <param name="Signed">
/// How an <see cref="IrTypeKind.Int"/> is interpreted. PowerBASIC has both families at every width
/// (<c>BYTE</c>/<c>SBYTE</c>, <c>WORD</c>/<c>INTEGER</c>, <c>DWORD</c>/<c>LONG</c>,
/// <c>QWORD</c>/<c>QUAD</c>), and a back end reading only the IR has to know which: it decides
/// widening (<c>CBW</c> versus <c>XOR AH,AH</c>), the divide instruction (<c>IDIV</c> versus
/// <c>DIV</c>), the compare condition and how the value prints. Signedness is an <i>interpretation</i>
/// of the same storage, so it does not affect <see cref="SameStorage"/>.
/// </param>
/// <param name="Format">The encoding of an <see cref="IrTypeKind.Float"/>; see <see cref="IrFloatFormat"/>.</param>
public sealed record IrType(IrTypeKind Kind, int Bits, bool Signed = true, IrFloatFormat Format = IrFloatFormat.Ieee) {

  public static readonly IrType Void = new(IrTypeKind.Void, 0);
  public static readonly IrType I1 = new(IrTypeKind.Int, 1);
  public static readonly IrType I8 = new(IrTypeKind.Int, 8);
  public static readonly IrType I16 = new(IrTypeKind.Int, 16);
  public static readonly IrType I32 = new(IrTypeKind.Int, 32);
  public static readonly IrType I64 = new(IrTypeKind.Int, 64);

  /// <summary>The unsigned integer types - PB's <c>BYTE</c>, <c>WORD</c>, <c>DWORD</c> and <c>QWORD</c>.</summary>
  public static readonly IrType U8 = new(IrTypeKind.Int, 8, Signed: false);
  public static readonly IrType U16 = new(IrTypeKind.Int, 16, Signed: false);
  public static readonly IrType U32 = new(IrTypeKind.Int, 32, Signed: false);
  public static readonly IrType U64 = new(IrTypeKind.Int, 64, Signed: false);

  public static readonly IrType F32 = new(IrTypeKind.Float, 32);
  public static readonly IrType F64 = new(IrTypeKind.Float, 64);
  public static readonly IrType F80 = new(IrTypeKind.Float, 80);

  /// <summary>Microsoft Binary Format storage - the SINGLE/DOUBLE of BASICA, GW-BASIC and BASCOM-heritage QuickBASIC.</summary>
  public static readonly IrType Mbf32 = new(IrTypeKind.Float, 32, Format: IrFloatFormat.Mbf);
  public static readonly IrType Mbf64 = new(IrTypeKind.Float, 64, Format: IrFloatFormat.Mbf);

  public static readonly IrType Ptr = new(IrTypeKind.Ptr, 0);

  /// <summary>Returns the canonical integer type of the given bit width and signedness.</summary>
  public static IrType Integer(int bits, bool signed = true) => (bits, signed) switch {
    (1, _) => I1,
    (8, true) => I8,
    (8, false) => U8,
    (16, true) => I16,
    (16, false) => U16,
    (32, true) => I32,
    (32, false) => U32,
    (64, true) => I64,
    (64, false) => U64,
    _ => new IrType(IrTypeKind.Int, bits, signed),
  };

  /// <summary>Returns the canonical floating-point type of the given bit width and encoding.</summary>
  public static IrType Floating(int bits, IrFloatFormat format = IrFloatFormat.Ieee) => (bits, format) switch {
    (32, IrFloatFormat.Ieee) => F32,
    (64, IrFloatFormat.Ieee) => F64,
    (80, IrFloatFormat.Ieee) => F80,
    (32, IrFloatFormat.Mbf) => Mbf32,
    (64, IrFloatFormat.Mbf) => Mbf64,
    _ => new IrType(IrTypeKind.Float, bits, Format: format),
  };

  public bool IsVoid => this.Kind == IrTypeKind.Void;
  public bool IsInteger => this.Kind == IrTypeKind.Int;
  public bool IsFloat => this.Kind == IrTypeKind.Float;
  public bool IsPointer => this.Kind == IrTypeKind.Ptr;

  /// <summary>True for an unsigned integer type (<c>u8</c>/<c>u16</c>/<c>u32</c>/<c>u64</c>).</summary>
  public bool IsUnsigned => this.Kind == IrTypeKind.Int && !this.Signed && this.Bits > 1;

  /// <summary>True for Microsoft Binary Format storage, which is never a valid arithmetic operand.</summary>
  public bool IsMbf => this.Kind == IrTypeKind.Float && this.Format == IrFloatFormat.Mbf;

  /// <summary>True for an IEEE/x87 float - the only float an arithmetic instruction may take.</summary>
  public bool IsIeeeFloat => this.Kind == IrTypeKind.Float && this.Format == IrFloatFormat.Ieee;

  /// <summary>True for the single-bit boolean type produced by comparisons.</summary>
  public bool IsBool => this.Kind == IrTypeKind.Int && this.Bits == 1;

  /// <summary>The same type read the other way round; identity for everything but an integer.</summary>
  public IrType WithSign(bool signed) => this.IsInteger ? Integer(this.Bits, signed) : this;

  /// <summary>
  /// True when two types occupy the same storage and may be moved between without a conversion -
  /// same kind, same width, same float encoding. Signedness is deliberately excluded: it changes how
  /// the bits are <i>read</i> (which the instruction says: <c>sdiv</c> vs <c>udiv</c>, <c>slt</c> vs
  /// <c>ult</c>, <c>sext</c> vs <c>zext</c>), not what they are, so <c>u16</c> and <c>i16</c> mix
  /// freely in a phi, a store or a binary operand pair. An IEEE float and an MBF float of the same
  /// width are <b>not</b> storage-compatible - the encodings differ.
  /// </summary>
  public bool SameStorage(IrType other) =>
    other is not null && this.Kind == other.Kind && this.Bits == other.Bits && this.Format == other.Format;

  public override string ToString() => this.Kind switch {
    IrTypeKind.Void => "void",
    IrTypeKind.Int => (this.Signed || this.Bits == 1 ? "i" : "u") + this.Bits,
    IrTypeKind.Float => (this.Format == IrFloatFormat.Mbf ? "mbf" : "f") + this.Bits,
    IrTypeKind.Ptr => "ptr",
    _ => "?",
  };
}
