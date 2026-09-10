using System.Globalization;
using System.Numerics;

namespace PowerBasic.Compiler.Ir;

/// <summary>
/// The raw 80-bit x87 double-extended representation: a sign/exponent word plus the explicit
/// 64-bit significand. Unlike <see cref="double"/>, this can preserve the extra 11 significand bits,
/// the wider exponent range, signed zero, NaN payloads, and even non-canonical x87 encodings.
/// </summary>
/// <remarks>
/// The two fields are the architectural bit fields, not the 10-byte little-endian memory order.
/// <see cref="SignExponent"/> contains bit 79 (sign) and bits 78..64 (biased exponent), while
/// <see cref="Significand"/> contains bits 63..0 including the x87 explicit integer bit at bit 63.
/// </remarks>
public readonly record struct IrFloat80(ushort SignExponent, ulong Significand) {
  private const ulong _INTEGER_BIT = 1UL << 63;
  private const ulong _PAYLOAD_MASK = _INTEGER_BIT - 1;
  private const int _EXPONENT_BIAS = 16383;

  /// <summary>The raw sign bit.</summary>
  public bool Sign => (this.SignExponent & 0x8000) != 0;

  /// <summary>The raw 15-bit biased exponent.</summary>
  public ushort Exponent => (ushort)(this.SignExponent & 0x7fff);

  /// <summary>Whether the explicit integer bit is set.</summary>
  public bool IntegerBit => (this.Significand & _INTEGER_BIT) != 0;

  /// <summary>True for either signed zero encoding.</summary>
  public bool IsZero => this.Exponent == 0 && this.Significand == 0;

  /// <summary>True for the canonical x87 infinity encoding.</summary>
  public bool IsInfinity => this.Exponent == 0x7fff && this.Significand == _INTEGER_BIT;

  /// <summary>True for a canonical x87 NaN encoding.</summary>
  public bool IsNaN => this.Exponent == 0x7fff && this.IntegerBit && (this.Significand & _PAYLOAD_MASK) != 0;

  /// <summary>
  /// True when the encoding is one whose numerical interpretation is unambiguous to target-neutral
  /// optimization: zero/subnormal, a normal with the explicit integer bit set, infinity, or NaN.
  /// Unsupported x87 pseudo/unnormal encodings are still preserved bit-for-bit, but numerical passes
  /// must not manufacture an interpretation for them.
  /// </summary>
  public bool IsCanonical => this.Exponent switch {
    0 => !this.IntegerBit,                           // zero or true subnormal
    0x7fff => this.IntegerBit,                      // infinity or NaN
    _ => this.IntegerBit,                           // normal
  };

  /// <summary>True for a canonical finite value (including signed zero and subnormals).</summary>
  public bool IsFinite => this.IsCanonical && this.Exponent != 0x7fff;

  /// <summary>
  /// Widens one binary64 value exactly into the x87 representation. This is the compatibility path
  /// used by the existing <c>IrConstantFloat(IrType.F80, double)</c> constructor; no information is
  /// lost because every binary64 value is exactly representable in x87 extended precision.
  /// </summary>
  public static IrFloat80 FromDouble(double value) {
    var raw = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
    var sign = (ushort)((raw >> 48) & 0x8000);
    var exponent = (int)((raw >> 52) & 0x7ff);
    var fraction = raw & 0x000f_ffff_ffff_ffffUL;

    if (exponent == 0x7ff) {
      var significand = fraction == 0
        ? _INTEGER_BIT
        : _INTEGER_BIT | (fraction << 11);
      return new((ushort)(sign | 0x7fff), significand);
    }

    if (exponent == 0) {
      if (fraction == 0)
        return new(sign, 0);

      var top = 63 - BitOperations.LeadingZeroCount(fraction); // 0..51
      var unbiased = top - 1074;
      var extendedExponent = unbiased + _EXPONENT_BIAS;
      var significand = fraction << (63 - top);
      return new((ushort)(sign | extendedExponent), significand);
    }

    return new(
      (ushort)(sign | (exponent - 1023 + _EXPONENT_BIAS)),
      _INTEGER_BIT | (fraction << 11));
  }

  /// <summary>
  /// Converts to binary64 only when that conversion is exact. Returning false rather than rounding is
  /// the guard used by legacy host-double optimizations: an arbitrary f80 constant must make such a
  /// transform decline, never quietly become a nearby binary64 number.
  /// </summary>
  public bool TryToDoubleExact(out double value) {
    var sign = this.Sign ? 0x8000_0000_0000_0000UL : 0UL;
    var exponent = this.Exponent;
    var significand = this.Significand;

    if (this.IsZero) {
      value = BitConverter.Int64BitsToDouble(unchecked((long)sign));
      return true;
    }

    if (this.IsInfinity) {
      value = BitConverter.Int64BitsToDouble(unchecked((long)(sign | 0x7ff0_0000_0000_0000UL)));
      return true;
    }

    if (this.IsNaN) {
      // A binary64 round-trip preserves exactly the x87 NaNs that came from binary64: the explicit
      // integer bit has no binary64 counterpart, and the remaining payload has eleven zero low bits.
      if ((significand & 0x7ffUL) != 0) {
        value = default;
        return false;
      }
      var payload = (significand & _PAYLOAD_MASK) >> 11;
      if (payload == 0) {
        value = default;
        return false;
      }
      value = BitConverter.Int64BitsToDouble(unchecked((long)(sign | 0x7ff0_0000_0000_0000UL | payload)));
      return true;
    }

    if (!this.IsCanonical || exponent == 0) {
      // Every nonzero true x87 subnormal is far below binary64's minimum subnormal. Unsupported
      // pseudo/unnormal encodings are intentionally not assigned a target-neutral numeric meaning.
      value = default;
      return false;
    }

    var unbiased = exponent - _EXPONENT_BIAS;
    if (unbiased is > 1023 or < -1074) {
      value = default;
      return false;
    }

    ulong raw;
    if (unbiased >= -1022) {
      if ((significand & 0x7ffUL) != 0) {
        value = default;
        return false;
      }
      var fraction = (significand >> 11) & 0x000f_ffff_ffff_ffffUL;
      raw = sign | ((ulong)(unbiased + 1023) << 52) | fraction;
    } else {
      var shift = -unbiased - 1011; // 12..63: x87 significand -> binary64 subnormal fraction
      var mask = shift == 64 ? ulong.MaxValue : (1UL << shift) - 1;
      if ((significand & mask) != 0) {
        value = default;
        return false;
      }
      var fraction = significand >> shift;
      if (fraction == 0 || fraction >= (1UL << 52)) {
        value = default;
        return false;
      }
      raw = sign | fraction;
    }

    value = BitConverter.Int64BitsToDouble(unchecked((long)raw));
    return true;
  }

  /// <summary>LLVM's legacy exact x86_fp80 bit-string spelling.</summary>
  public string ToLlvmHexString()
    => $"0xK{this.SignExponent.ToString("X4", CultureInfo.InvariantCulture)}{this.Significand.ToString("X16", CultureInfo.InvariantCulture)}";

  /// <summary>A compact deterministic bit key used by GVN/debug printing.</summary>
  public string ToBitString()
    => $"{this.SignExponent.ToString("X4", CultureInfo.InvariantCulture)}{this.Significand.ToString("X16", CultureInfo.InvariantCulture)}";
}
