using System.Numerics;

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
  /// Reads a GW-BASIC decimal literal at MBF64 precision. Its digit accumulator rounds after each
  /// multiply and addition, as the interpreter does; rounding only the final decimal differs for
  /// some 17-digit literals. The exponent is applied after digit accumulation.
  /// </summary>
  public static bool TryParseDecimalLiteral(string text, out Mbf64 result) {
    ArgumentNullException.ThrowIfNull(text);
    result = Zero;
    var exponentAt = text.IndexOfAny(['D', 'd', 'E', 'e']);
    var mantissa = exponentAt < 0 ? text.AsSpan() : text.AsSpan(0, exponentAt);
    var exponent = 0;
    if (exponentAt >= 0 && !int.TryParse(text.AsSpan(exponentAt + 1), out exponent))
      return false;
    if (exponent is < -1000 or > 1000)
      return false;

    var digitsAfterPoint = 0;
    var hasPoint = false;
    var hasDigit = false;
    BigInteger accumulated = 0;
    foreach (var character in mantissa) {
      if (character == '.' && !hasPoint) {
        hasPoint = true;
        continue;
      }
      if (!char.IsAsciiDigit(character))
        return false;
      hasDigit = true;
      if (hasPoint)
        ++digitsAfterPoint;
      accumulated = RoundInteger(accumulated * 10, 56);
      accumulated = RoundInteger(accumulated + character - '0', 56);
    }
    if (!hasDigit)
      return false;

    var decimalExponent = exponent - digitsAfterPoint;
    if (decimalExponent is < -1000 or > 1000)
      return false;
    var numerator = decimalExponent >= 0 ? accumulated * BigInteger.Pow(10, decimalExponent) : accumulated;
    var denominator = decimalExponent < 0 ? BigInteger.Pow(10, -decimalExponent) : BigInteger.One;
    return TryFromRatio(numerator, denominator, out result);
  }

  private static BigInteger RoundInteger(BigInteger value, int precision) {
    var excess = value.GetBitLength() - precision;
    if (excess <= 0)
      return value;
    var divisor = BigInteger.One << (int)excess;
    var quotient = BigInteger.DivRem(value, divisor, out var remainder);
    if (remainder * 2 > divisor || remainder * 2 == divisor && !quotient.IsEven)
      ++quotient;
    return quotient * divisor;
  }

  private static bool TryFromRatio(BigInteger numerator, BigInteger denominator, out Mbf64 result) {
    result = Zero;
    if (numerator.IsZero)
      return true;
    var exponent = (int)(numerator.GetBitLength() - denominator.GetBitLength());
    if (exponent >= 0 ? numerator < denominator << exponent : numerator << -exponent < denominator)
      --exponent;
    var storedExponent = exponent + 129;
    if (storedExponent <= 0)
      return true;
    if (storedExponent > byte.MaxValue)
      return false;

    var shift = 55 - exponent;
    var scaledNumerator = shift >= 0 ? numerator << shift : numerator;
    var scaledDenominator = shift < 0 ? denominator << -shift : denominator;
    var significand = BigInteger.DivRem(scaledNumerator, scaledDenominator, out var remainder);
    if (remainder * 2 > scaledDenominator || remainder * 2 == scaledDenominator && !significand.IsEven)
      ++significand;
    if (significand == BigInteger.One << 56) {
      significand >>= 1;
      ++storedExponent;
    }
    if (storedExponent > byte.MaxValue)
      return false;
    result = new(((ulong)storedExponent << 56) | ((ulong)significand & _FRACTION_MASK));
    return true;
  }

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
