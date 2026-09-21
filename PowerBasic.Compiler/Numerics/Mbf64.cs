namespace PowerBasic.Compiler.Numerics;

/// <summary>
/// The eight-byte Microsoft Binary Format used for BASICA/GW-BASIC <c>DOUBLE</c>: a biased
/// eight-bit exponent, one sign bit and 55 stored fraction bits below an implicit leading one.
/// </summary>
/// <remarks>
/// MBF64 has 56 significant bits, so converting through an IEEE <see cref="double"/> loses three
/// bits. Conversion therefore goes directly to and from <see cref="Extended80"/>, whose explicit
/// 64-bit significand represents every MBF64 value exactly.
/// </remarks>
public readonly struct Mbf64 : IEquatable<Mbf64> {

  private const ushort _EXTENDED_EXPONENT_OFFSET = 0x3F7E;
  private const ulong _FRACTION_MASK = 0x007F_FFFF_FFFF_FFFF;
  private const ulong _SIGN_MASK = 0x0080_0000_0000_0000;
  private const ulong _HIDDEN_BIT = 0x0080_0000_0000_0000;
  private const ulong _CARRY_BIT = 0x0100_0000_0000_0000;

  private readonly ulong _bits;

  private Mbf64(ulong bits) => this._bits = bits;

  /// <summary>The encoded eight bytes interpreted as a little-endian unsigned integer.</summary>
  public ulong Bits => this._bits;

  public bool IsZero => this.StoredExponent == 0;

  public bool IsNegative => !this.IsZero && (this._bits & _SIGN_MASK) != 0;

  private int StoredExponent => (int)(this._bits >> 56);

  public static Mbf64 Zero => new(0);

  public static Mbf64 One => new(0x8100_0000_0000_0000);

  /// <summary>
  /// Rounds an x87 value to MBF64 using nearest-even. Magnitudes below the MBF normal range become
  /// zero; values above it, infinities and NaNs cannot be represented and return <see langword="false"/>.
  /// </summary>
  public static bool TryFromExtended80(Extended80 value, out Mbf64 result) {
    result = Zero;
    if (!value.IsFinite)
      return false;
    if (value.IsZero)
      return true;

    var bytes = value.ToBytes();
    var significand = BitConverter.ToUInt64(bytes, 0);
    var signExponent = BitConverter.ToUInt16(bytes, 8);
    var exponent = signExponent & 0x7FFF;
    var retained = significand >> 8;
    var discarded = (byte)significand;
    if (discarded > 0x80 || discarded == 0x80 && (retained & 1) != 0)
      ++retained;
    if (retained == _CARRY_BIT) {
      retained >>= 1;
      ++exponent;
    }

    var mbfExponent = exponent - _EXTENDED_EXPONENT_OFFSET;
    if (mbfExponent <= 0)
      return true;
    if (mbfExponent > byte.MaxValue)
      return false;

    var sign = (signExponent & 0x8000) != 0 ? _SIGN_MASK : 0;
    result = new(((ulong)mbfExponent << 56) | sign | (retained & _FRACTION_MASK));
    return true;
  }

  /// <summary>
  /// Rounds an x87 value to MBF64, throwing when the value has no finite MBF representation.
  /// </summary>
  public static Mbf64 FromExtended80(Extended80 value)
    => TryFromExtended80(value, out var result)
      ? result
      : throw new OverflowException("the value has no finite MBF64 representation");

  /// <summary>Widens this value exactly to the x87 extended format.</summary>
  public Extended80 ToExtended80() {
    if (this.IsZero)
      return Extended80.Zero;

    var significand = (_HIDDEN_BIT | (this._bits & _FRACTION_MASK)) << 8;
    var signExponent = (ushort)(this.StoredExponent + _EXTENDED_EXPONENT_OFFSET);
    if (this.IsNegative)
      signExponent |= 0x8000;
    Span<byte> bytes = stackalloc byte[10];
    BitConverter.TryWriteBytes(bytes[..8], significand);
    BitConverter.TryWriteBytes(bytes[8..], signExponent);
    return Extended80.FromBytes(bytes);
  }

  /// <summary>The encoded bytes in the memory order used by BASIC.</summary>
  public byte[] ToBytes() {
    var bytes = new byte[8];
    BitConverter.TryWriteBytes(bytes, this._bits);
    return bytes;
  }

  public static Mbf64 FromBytes(ReadOnlySpan<byte> bytes)
    => bytes.Length < 8
      ? throw new ArgumentException("an MBF64 value is eight bytes", nameof(bytes))
      : new(BitConverter.ToUInt64(bytes[..8]));

  public bool Equals(Mbf64 other) => this._bits == other._bits;

  public override bool Equals(object? obj) => obj is Mbf64 other && this.Equals(other);

  public override int GetHashCode() => this._bits.GetHashCode();

  public static bool operator ==(Mbf64 left, Mbf64 right) => left.Equals(right);

  public static bool operator !=(Mbf64 left, Mbf64 right) => !left.Equals(right);
}
