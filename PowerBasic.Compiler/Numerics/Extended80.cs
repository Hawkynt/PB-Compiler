using System.Numerics;

namespace PowerBasic.Compiler.Numerics;

/// <summary>
/// An x87 double-extended float, in software: one sign bit, fifteen exponent bits and a
/// sixty-four-bit significand whose leading bit is stored rather than implied.
///
/// <para>
/// This exists because the compiler has to be able to compute what the program will compute. PB
/// evaluates float expressions at the x87's own width, so a constant folded in a 64-bit
/// <see cref="double"/> is not the same number the emitted code arrives at - and the difference is
/// observable, because the declared type picks the FORMATTER rather than a rounding step. Today
/// <c>Ir.IrConstFold</c> handles that by refusing to fold anything inexact; with this type it can
/// fold everything and be right. The same need arrives from the other direction for any back end
/// whose target has no x87, which must reproduce this width in software or produce different
/// answers.
/// </para>
/// <para>
/// <b>Only the exactly-specified operations are here</b> - add, subtract, multiply, divide, square
/// root, comparison and conversion. Those are pinned to the last bit by IEEE 754 and the x87
/// implements them faithfully, so a software version can be held to the same standard. The
/// transcendentals are deliberately absent: <c>FSIN</c> and its neighbours are NOT correctly
/// rounded, they carry a documented error of one to two units in the last place, and they reduce
/// their argument against a 66-bit approximation of pi. Reproducing them means reproducing those
/// choices bug for bug, and a correctly-rounded sine here would disagree with the hardware - which
/// is the one thing this type exists to prevent. Until they are matched deliberately, a constant
/// expression containing one does not fold.
/// </para>
/// <para>
/// The arithmetic runs on <see cref="BigInteger"/>. It is a compile-time facility, evaluated a few
/// thousand times per build rather than a few million times per second, so it is written to be read
/// and checked rather than to be fast: every operation computes an exact result first and rounds it
/// once, which is the definition the standard gives and therefore the thing least likely to be
/// subtly wrong.
/// </para>
/// </summary>
public readonly struct Extended80 : IEquatable<Extended80> {

  /// <summary>Exponent bias: a stored exponent of 16383 means 2^0.</summary>
  private const int _BIAS = 16383;

  /// <summary>The significand's width, counting the explicit integer bit.</summary>
  private const int _PRECISION = 64;

  /// <summary>The stored exponent that means infinity or NaN.</summary>
  private const int _SPECIAL = 0x7FFF;

  /// <summary>
  /// The smallest exponent any value can have, in units of <c>significand * 2^e</c>. A stored
  /// exponent of 1 with the significand read as an integer scales by <c>1 - _BIAS - 63</c>, and a
  /// denormal shares that scale with a stored exponent of zero.
  /// </summary>
  private const int _MIN_SCALE = 1 - _BIAS - (_PRECISION - 1);

  private readonly ulong _significand;
  private readonly ushort _signExponent;

  private Extended80(ushort signExponent, ulong significand) {
    this._signExponent = signExponent;
    this._significand = significand;
  }

  #region shape

  /// <summary>True when the sign bit is set - of a NaN and of negative zero as much as of a number.</summary>
  public bool IsNegative => (this._signExponent & 0x8000) != 0;

  /// <summary>The stored (biased) exponent.</summary>
  private int StoredExponent => this._signExponent & 0x7FFF;

  public bool IsZero => this.StoredExponent == 0 && this._significand == 0;

  public bool IsNaN => this.StoredExponent == _SPECIAL && (this._significand & 0x7FFF_FFFF_FFFF_FFFF) != 0;

  public bool IsInfinity => this.StoredExponent == _SPECIAL && (this._significand & 0x7FFF_FFFF_FFFF_FFFF) == 0;

  public bool IsFinite => this.StoredExponent != _SPECIAL;

  public static Extended80 Zero => new(0, 0);
  public static Extended80 NegativeZero => new(0x8000, 0);
  public static Extended80 PositiveInfinity => new(_SPECIAL, 0x8000_0000_0000_0000);
  public static Extended80 NegativeInfinity => new(0x8000 | _SPECIAL, 0x8000_0000_0000_0000);

  /// <summary>The default quiet NaN, which is what the x87 produces for an invalid operation.</summary>
  public static Extended80 NaN => new(0x8000 | _SPECIAL, 0xC000_0000_0000_0000);

  public static Extended80 One => FromInt64(1);

  #endregion

  #region decomposition

  /// <summary>
  /// The value as <c>(-1)^sign * significand * 2^scale</c>, exactly. Only meaningful for a finite
  /// value; zero decomposes to a zero significand.
  /// </summary>
  private (bool Sign, BigInteger Significand, int Scale) Decompose() {
    var stored = this.StoredExponent;
    // a denormal shares the smallest normal's scale and simply has a smaller significand, which is
    // why the stored exponent jumps from 0 to 1 without the scale changing
    var scale = (stored == 0 ? 1 : stored) - _BIAS - (_PRECISION - 1);
    return (this.IsNegative, this._significand, scale);
  }

  /// <summary>
  /// Assembles a finite value from an exact <c>(-1)^sign * significand * 2^scale</c>, rounding once.
  /// <paramref name="sticky"/> says that nonzero bits were discarded below the significand already -
  /// a division's remainder, say - which affects a tie but nothing else.
  /// </summary>
  private static Extended80 Pack(bool sign, BigInteger significand, int scale, bool sticky, FloatRounding mode) {
    if (significand.IsZero && !sticky)
      return sign ? NegativeZero : Zero;

    var (rounded, resultScale, overflowed) =
      Round(significand, scale, sticky, _PRECISION, _MIN_SCALE, _BIAS + _PRECISION - 1, sign, mode);
    if (overflowed)
      return Overflowed(sign, mode);

    var stored = rounded.IsZero
      ? 0
      : rounded.GetBitLength() < _PRECISION
        ? 0                                        // a denormal: the scale is the minimum, stored as zero
        : resultScale + _BIAS + _PRECISION - 1;
    return new((ushort)((sign ? 0x8000 : 0) | stored), (ulong)rounded);
  }

  /// <summary>
  /// What a magnitude too large to represent becomes. Rounding toward zero cannot produce infinity
  /// from a finite computation, and neither can rounding away from the value's own sign, so those
  /// cases saturate at the largest finite value instead.
  /// </summary>
  private static Extended80 Overflowed(bool sign, FloatRounding mode) {
    var toInfinity = mode switch {
      FloatRounding.ToNearestEven => true,
      FloatRounding.Truncate => false,
      FloatRounding.Down => sign,
      FloatRounding.Up => !sign,
      _ => true,
    };
    return toInfinity
      ? sign ? NegativeInfinity : PositiveInfinity
      : new((ushort)((sign ? 0x8000 : 0) | (_SPECIAL - 1)), ulong.MaxValue);
  }

  #endregion

  #region the rounding kernel

  /// <summary>
  /// Rounds the exact value <c>significand * 2^scale</c> to <paramref name="precision"/> significant
  /// bits, not letting the scale fall below <paramref name="minScale"/> (which is what makes
  /// denormals come out as denormals rather than as impossible exponents).
  ///
  /// <para>
  /// One rounding, at the end, from the exact value - not a sequence of roundings that happens to
  /// end in the right place. That is what the standard specifies and it is also the only version
  /// that is obviously right, which matters more here than speed does.
  /// </para>
  /// </summary>
  private static (BigInteger Significand, int Scale, bool Overflowed) Round(
      BigInteger significand, int scale, bool sticky,
      int precision, int minScale, int maxUnbiasedExponent,
      bool sign, FloatRounding mode) {

    // how far right the significand must move to fit the precision, and never so far left that the
    // scale would drop below the format's minimum
    var shift = (int)significand.GetBitLength() - precision;
    if (scale + shift < minScale)
      shift = minScale - scale;

    if (shift > 0) {
      var dropped = significand & ((BigInteger.One << shift) - 1);
      significand >>= shift;
      var half = BigInteger.One << (shift - 1);
      var roundBit = dropped >= half;
      sticky |= roundBit ? dropped != half : !dropped.IsZero;
      scale += shift;
      if (ShouldIncrement(roundBit, sticky, !significand.IsEven, sign, mode))
        ++significand;
    } else {
      if (shift < 0) {
        significand <<= -shift;
        scale += shift;
      }
      if (sticky && ShouldIncrement(roundBit: false, sticky: true, isOdd: !significand.IsEven, sign, mode))
        ++significand;
    }

    // rounding up can carry into an extra bit: 0xFFFF... + 1 is a power of two one place wider
    if (significand.GetBitLength() > precision) {
      significand >>= 1;
      ++scale;
    }

    var unbiased = scale + precision - 1;
    return (significand, scale, unbiased > maxUnbiasedExponent);
  }

  private static bool ShouldIncrement(bool roundBit, bool sticky, bool isOdd, bool sign, FloatRounding mode) => mode switch {
    FloatRounding.ToNearestEven => roundBit && (sticky || isOdd),
    FloatRounding.Truncate => false,
    FloatRounding.Down => sign && (roundBit || sticky),
    FloatRounding.Up => !sign && (roundBit || sticky),
    _ => false,
  };

  #endregion

  #region arithmetic

  public static Extended80 Add(Extended80 l, Extended80 r, FloatRounding mode = FloatRounding.ToNearestEven)
    => AddOrSubtract(l, r, subtract: false, mode);

  public static Extended80 Subtract(Extended80 l, Extended80 r, FloatRounding mode = FloatRounding.ToNearestEven)
    => AddOrSubtract(l, r, subtract: true, mode);

  private static Extended80 AddOrSubtract(Extended80 l, Extended80 r, bool subtract, FloatRounding mode) {
    if (l.IsNaN || r.IsNaN)
      return NaN;
    var rightNegative = r.IsNegative ^ subtract;
    if (l.IsInfinity || r.IsInfinity) {
      if (l.IsInfinity && r.IsInfinity)
        return l.IsNegative == rightNegative ? l : NaN;   // inf - inf is invalid
      return l.IsInfinity ? l : rightNegative ? NegativeInfinity : PositiveInfinity;
    }

    var (leftSign, leftSignificand, leftScale) = l.Decompose();
    var (_, rightSignificand, rightScale) = r.Decompose();

    // line the two up on the finer of the two scales; the exact sum needs no rounding until the end
    var scale = Math.Min(leftScale, rightScale);
    var left = leftSignificand << (leftScale - scale);
    var right = rightSignificand << (rightScale - scale);

    var sum = (leftSign ? -left : left) + (rightNegative ? -right : right);
    if (sum.IsZero)
      // a sum that cancels exactly is +0, except when rounding downward, where it is -0
      return mode == FloatRounding.Down ? NegativeZero : Zero;

    return Pack(sum.Sign < 0, BigInteger.Abs(sum), scale, sticky: false, mode);
  }

  public static Extended80 Multiply(Extended80 l, Extended80 r, FloatRounding mode = FloatRounding.ToNearestEven) {
    if (l.IsNaN || r.IsNaN)
      return NaN;
    var sign = l.IsNegative ^ r.IsNegative;
    if (l.IsInfinity || r.IsInfinity)
      return l.IsZero || r.IsZero ? NaN : sign ? NegativeInfinity : PositiveInfinity;
    if (l.IsZero || r.IsZero)
      return sign ? NegativeZero : Zero;

    var (_, leftSignificand, leftScale) = l.Decompose();
    var (_, rightSignificand, rightScale) = r.Decompose();
    return Pack(sign, leftSignificand * rightSignificand, leftScale + rightScale, sticky: false, mode);
  }

  public static Extended80 Divide(Extended80 l, Extended80 r, FloatRounding mode = FloatRounding.ToNearestEven) {
    if (l.IsNaN || r.IsNaN)
      return NaN;
    var sign = l.IsNegative ^ r.IsNegative;
    if (l.IsInfinity)
      return r.IsInfinity ? NaN : sign ? NegativeInfinity : PositiveInfinity;
    if (r.IsInfinity)
      return sign ? NegativeZero : Zero;
    if (r.IsZero)
      return l.IsZero ? NaN : sign ? NegativeInfinity : PositiveInfinity;
    if (l.IsZero)
      return sign ? NegativeZero : Zero;

    var (_, dividend, dividendScale) = l.Decompose();
    var (_, divisor, divisorScale) = r.Decompose();

    // enough quotient bits that the rounding decision is never made on a guess: the significands are
    // at most 64 bits each, so shifting the dividend by precision + 2 leaves the quotient with at
    // least precision + 1 bits, and the remainder carries everything below them
    const int _EXTRA = _PRECISION + 2;
    var quotient = BigInteger.DivRem(dividend << _EXTRA, divisor, out var remainder);
    return Pack(sign, quotient, dividendScale - divisorScale - _EXTRA, !remainder.IsZero, mode);
  }

  public static Extended80 SquareRoot(Extended80 value, FloatRounding mode = FloatRounding.ToNearestEven) {
    if (value.IsNaN)
      return NaN;
    if (value.IsZero)
      return value;                                // sqrt(-0) is -0
    if (value.IsNegative)
      return NaN;
    if (value.IsInfinity)
      return PositiveInfinity;

    var (_, significand, scale) = value.Decompose();

    // halving the scale needs it even, and the root of a 64-bit significand is only 32 bits, so the
    // significand is shifted up by an even amount large enough to leave more than the precision
    const int _EXTRA = 2 * (_PRECISION + 2);
    var shift = _EXTRA + (int)(((uint)scale & 1) ^ ((uint)_EXTRA & 1));
    if (((scale - shift) & 1) != 0)
      ++shift;
    var root = IntegerSquareRoot(significand << shift, out var exact);
    return Pack(sign: false, root, (scale - shift) / 2, !exact, mode);
  }

  /// <summary>The integer square root of <paramref name="value"/>, by Newton's method on integers.</summary>
  private static BigInteger IntegerSquareRoot(BigInteger value, out bool exact) {
    if (value.IsZero) {
      exact = true;
      return BigInteger.Zero;
    }

    // a starting point with the right magnitude; Newton then converges in a few dozen steps
    var estimate = BigInteger.One << (int)((value.GetBitLength() + 1) / 2);
    for (;;) {
      var next = (estimate + value / estimate) >> 1;
      if (next >= estimate)
        break;
      estimate = next;
    }
    exact = estimate * estimate == value;
    return estimate;
  }

  public static Extended80 Negate(Extended80 value) => new((ushort)(value._signExponent ^ 0x8000), value._significand);

  public static Extended80 Abs(Extended80 value) => new((ushort)(value._signExponent & 0x7FFF), value._significand);

  #endregion

  #region comparison

  /// <summary>
  /// Orders two values, or returns null when they are unordered - which is what a NaN operand makes
  /// every comparison, and the reason this cannot be an <see cref="IComparable{T}"/>.
  /// </summary>
  public static int? Compare(Extended80 l, Extended80 r) {
    if (l.IsNaN || r.IsNaN)
      return null;
    if (l.IsZero && r.IsZero)
      return 0;                                    // -0 equals +0

    var left = l.ToRational();
    var right = r.ToRational();
    return left.CompareTo(right);
  }

  /// <summary>
  /// The value as an exact scaled integer, comparable against another such: <c>significand</c> shifted
  /// so that both operands share the finer scale. Infinities sort outside every finite value.
  /// </summary>
  private BigInteger ToRational() {
    if (this.IsInfinity)
      return this.IsNegative ? BigInteger.MinusOne << 40000 : BigInteger.One << 40000;
    var (sign, significand, scale) = this.Decompose();
    // every finite value shifted onto the common minimum scale is an exact integer, and the shift is
    // bounded by the exponent range, so this stays a few kilobytes at worst
    var scaled = significand << (scale - _MIN_SCALE);
    return sign ? -scaled : scaled;
  }

  public bool Equals(Extended80 other) => Compare(this, other) == 0;

  public override bool Equals(object? obj) => obj is Extended80 other && this.Equals(other);

  /// <summary>
  /// Hashes the numeric value, so the two zeros hash alike and every NaN hashes alike - matching what
  /// <see cref="Equals(Extended80)"/> calls equal rather than what the bits say.
  /// </summary>
  public override int GetHashCode()
    => this.IsNaN ? 0 : this.IsZero ? 1 : HashCode.Combine(this._signExponent, this._significand);

  #endregion

  #region conversion

  /// <summary>Converts exactly - every <see cref="double"/> is an extended value.</summary>
  public static Extended80 FromDouble(double value) {
    if (double.IsNaN(value))
      return NaN;
    if (double.IsPositiveInfinity(value))
      return PositiveInfinity;
    if (double.IsNegativeInfinity(value))
      return NegativeInfinity;

    var bits = BitConverter.DoubleToUInt64Bits(value);
    var sign = (bits & 0x8000_0000_0000_0000) != 0;
    var exponent = (int)(bits >> 52) & 0x7FF;
    var fraction = bits & 0x000F_FFFF_FFFF_FFFF;
    if (exponent == 0 && fraction == 0)
      return sign ? NegativeZero : Zero;

    // a normal double carries an implied leading one; a subnormal does not, and Pack normalizes it
    var significand = exponent == 0 ? fraction : fraction | 0x0010_0000_0000_0000;
    var scale = (exponent == 0 ? 1 : exponent) - 1023 - 52;
    return Pack(sign, significand, scale, sticky: false, FloatRounding.ToNearestEven);
  }

  public static Extended80 FromSingle(float value) => FromDouble(value);

  public static Extended80 FromInt64(long value) {
    if (value == 0)
      return Zero;
    var negative = value < 0;
    var magnitude = negative ? BigInteger.Negate(value) : value;   // safe for long.MinValue
    return Pack(negative, magnitude, 0, sticky: false, FloatRounding.ToNearestEven);
  }

  /// <summary>Rounds to a <see cref="double"/>, which is lossy by construction - hence the mode.</summary>
  public double ToDouble(FloatRounding mode = FloatRounding.ToNearestEven) {
    if (this.IsNaN)
      return double.NaN;
    if (this.IsInfinity)
      return this.IsNegative ? double.NegativeInfinity : double.PositiveInfinity;
    if (this.IsZero)
      return this.IsNegative ? -0.0 : 0.0;

    var (sign, significand, scale) = this.Decompose();
    var (rounded, resultScale, overflowed) = Round(significand, scale, sticky: false, 53, -1074, 1023, sign, mode);
    if (overflowed)
      return sign ? double.NegativeInfinity : double.PositiveInfinity;
    if (rounded.IsZero)
      return sign ? -0.0 : 0.0;

    var storedExponent = rounded.GetBitLength() < 53 ? 0 : resultScale + 1023 + 52;
    var fraction = (ulong)rounded & 0x000F_FFFF_FFFF_FFFF;
    var bits = (sign ? 0x8000_0000_0000_0000UL : 0) | ((ulong)storedExponent << 52) | fraction;
    return BitConverter.UInt64BitsToDouble(bits);
  }

  public float ToSingle(FloatRounding mode = FloatRounding.ToNearestEven) {
    if (this.IsNaN)
      return float.NaN;
    if (this.IsInfinity)
      return this.IsNegative ? float.NegativeInfinity : float.PositiveInfinity;
    if (this.IsZero)
      return this.IsNegative ? -0.0f : 0.0f;

    var (sign, significand, scale) = this.Decompose();
    var (rounded, resultScale, overflowed) = Round(significand, scale, sticky: false, 24, -149, 127, sign, mode);
    if (overflowed)
      return sign ? float.NegativeInfinity : float.PositiveInfinity;
    if (rounded.IsZero)
      return sign ? -0.0f : 0.0f;

    var storedExponent = rounded.GetBitLength() < 24 ? 0 : resultScale + 127 + 23;
    var fraction = (uint)rounded & 0x007F_FFFF;
    var bits = (sign ? 0x8000_0000U : 0) | ((uint)storedExponent << 23) | fraction;
    return BitConverter.UInt32BitsToSingle(bits);
  }

  /// <summary>
  /// Converts to an integer the way <c>FIST</c> does, or null when the value does not fit - which is
  /// the x87's invalid-operation case and PB's error 6, and so must be distinguishable from a result.
  /// </summary>
  public long? ToInt64(FloatRounding mode = FloatRounding.ToNearestEven) {
    if (!this.IsFinite)
      return null;
    if (this.IsZero)
      return 0;

    var (sign, significand, scale) = this.Decompose();
    // rounding to an integer is rounding at scale zero; asking for enough precision to hold every
    // bit above the point means the only thing rounded away is the fraction
    var integerBits = (int)significand.GetBitLength() + scale;
    if (integerBits > 64)
      return null;
    var (rounded, resultScale, _) = Round(significand, scale, sticky: false,
      Math.Max(integerBits, 1), 0, int.MaxValue, sign, mode);
    var magnitude = rounded << resultScale;
    if (magnitude > (sign ? BigInteger.One << 63 : (BigInteger.One << 63) - 1))
      return null;
    return sign ? (long)-magnitude : (long)magnitude;
  }

  #endregion

  #region storage

  /// <summary>The ten bytes the x87 stores, in memory order: significand first, then sign and exponent.</summary>
  public byte[] ToBytes() {
    var bytes = new byte[10];
    BitConverter.TryWriteBytes(bytes.AsSpan(0, 8), this._significand);
    BitConverter.TryWriteBytes(bytes.AsSpan(8, 2), this._signExponent);
    return bytes;
  }

  public static Extended80 FromBytes(ReadOnlySpan<byte> bytes)
    => bytes.Length < 10
      ? throw new ArgumentException("an extended real is ten bytes", nameof(bytes))
      : new(BitConverter.ToUInt16(bytes[8..10]), BitConverter.ToUInt64(bytes[..8]));

  public override string ToString()
    => this.IsNaN ? "NaN"
     : this.IsInfinity ? this.IsNegative ? "-Inf" : "Inf"
     : this.ToDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture);

  #endregion
}
