using System.Numerics;
using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Numerics;

namespace PowerBasic.Compiler.Tests.Numerics;

/// <summary>
/// The 80-bit float, held to the standard it exists to meet.
///
/// <para>
/// A software float is easy to write and hard to trust, so almost nothing here is asserted against a
/// number typed in by hand. The encoding is checked against <see cref="Assembler.Dt(double)"/>, which
/// is an independently written converter already in the compiler; the arithmetic is checked against
/// exact rational arithmetic in <see cref="BigInteger"/>, which cannot round at all and therefore
/// cannot round the same way twice by accident; and the conversions are checked by round trip.
/// </para>
/// </summary>
[TestFixture]
public sealed class Extended80Tests {

  /// <summary>Values chosen to hit the awkward parts: exact, inexact, tiny, huge, negative, integral.</summary>
  private static readonly double[] _samples = [
    0.0, -0.0, 1.0, -1.0, 2.0, 0.5, -0.5, 3.0, 10.0, 100.0, 1e10, 1e100, 1e-100,
    0.1, 0.2, 0.3, 1.0 / 3.0, 2.0 / 3.0, Math.PI, Math.E,
    32767.0, 32768.0, -32768.0, 65535.0, 1234567.89, -1234567.89,
    double.Epsilon, -double.Epsilon, 5e-324, 2.2250738585072014e-308,
    double.MaxValue, double.MinValue, 1.5, 2.5, -1.5, -2.5, 4.9999999999999991,
  ];

  #region encoding

  /// <summary>
  /// The ten bytes must be the ten bytes the assembler already emits for the same double. That
  /// converter was written separately and is used to build every EXT constant in every image, so
  /// agreeing with it is agreeing with the compiler's existing, shipped notion of this format.
  /// </summary>
  [Test]
  public void ToBytes_GivenADouble_ThenItMatchesTheAssemblersOwnEncoder() {
    foreach (var value in _samples) {
      var asm = new Assembler();
      asm.Dt(value);
      Assert.That(Extended80.FromDouble(value).ToBytes(), Is.EqualTo(asm.ToArray()),
        $"encoding of {value:R}");
    }
  }

  [Test]
  public void FromBytes_GivenWhatToBytesWrote_ThenTheValueComesBack() {
    foreach (var value in _samples) {
      var original = Extended80.FromDouble(value);
      var restored = Extended80.FromBytes(original.ToBytes());
      Assert.That(restored.ToBytes(), Is.EqualTo(original.ToBytes()), $"{value:R}");
    }
  }

  #endregion

  #region conversion

  /// <summary>Every double is an extended value exactly, so the trip out and back must change nothing.</summary>
  [Test]
  public void ToDouble_GivenAValueThatCameFromADouble_ThenItIsTheSameDouble() {
    foreach (var value in _samples)
      Assert.That(BitConverter.DoubleToInt64Bits(Extended80.FromDouble(value).ToDouble()),
        Is.EqualTo(BitConverter.DoubleToInt64Bits(value)), $"{value:R}");
  }

  [Test]
  public void ToSingle_GivenAValueThatCameFromASingle_ThenItIsTheSameSingle() {
    foreach (var value in new[] { 0f, -0f, 1f, -1f, 0.1f, 3.4e38f, 1.4e-45f, float.Epsilon, 16777216f })
      Assert.That(Extended80.FromSingle(value).ToSingle(), Is.EqualTo(value), $"{value:R}");
  }

  [TestCase(0L)]
  [TestCase(1L)]
  [TestCase(-1L)]
  [TestCase(32767L)]
  [TestCase(-32768L)]
  [TestCase(1234567890123456789L)]
  [TestCase(long.MaxValue)]
  [TestCase(long.MinValue)]
  public void FromInt64_ThenToInt64_ThenTheIntegerSurvives(long value) {
    // long.MaxValue has 63 significant bits and fits exactly; every value here is representable
    Assert.That(Extended80.FromInt64(value).ToInt64(), Is.EqualTo(value));
  }

  /// <summary>
  /// The rounding modes, on the boundary that distinguishes them. This is the same question
  /// <c>FLDCW</c> asks at run time, which is what PB's <c>INT</c> and <c>FIX</c> are built from.
  /// </summary>
  [TestCase(2.5, FloatRounding.ToNearestEven, 2L)]
  [TestCase(3.5, FloatRounding.ToNearestEven, 4L)]
  [TestCase(-2.5, FloatRounding.ToNearestEven, -2L)]
  [TestCase(2.5, FloatRounding.Truncate, 2L)]
  [TestCase(-2.5, FloatRounding.Truncate, -2L)]
  [TestCase(2.5, FloatRounding.Down, 2L)]
  [TestCase(-2.5, FloatRounding.Down, -3L)]
  [TestCase(2.5, FloatRounding.Up, 3L)]
  [TestCase(-2.5, FloatRounding.Up, -2L)]
  [TestCase(2.1, FloatRounding.Down, 2L)]
  [TestCase(2.9, FloatRounding.Down, 2L)]
  [TestCase(-2.1, FloatRounding.Up, -2L)]
  public void ToInt64_GivenARoundingMode_ThenItRoundsThatWay(double value, FloatRounding mode, long expected)
    => Assert.That(Extended80.FromDouble(value).ToInt64(mode), Is.EqualTo(expected));

  [Test]
  public void ToInt64_GivenSomethingThatDoesNotFit_ThenItDeclines() {
    Assert.That(Extended80.FromDouble(1e30).ToInt64(), Is.Null);
    Assert.That(Extended80.PositiveInfinity.ToInt64(), Is.Null);
    Assert.That(Extended80.NaN.ToInt64(), Is.Null);
  }

  #endregion

  #region arithmetic against exact rational arithmetic

  /// <summary>
  /// The exact value of a finite extended, as a rational. Nothing here rounds, so it can say what the
  /// right answer was and let the assertion be about whether rounding found it.
  /// </summary>
  private static (BigInteger Num, BigInteger Den) Exact(Extended80 value) {
    var bytes = value.ToBytes();
    var significand = BitConverter.ToUInt64(bytes, 0);
    var signExponent = BitConverter.ToUInt16(bytes, 8);
    var negative = (signExponent & 0x8000) != 0;
    var stored = signExponent & 0x7FFF;
    var scale = (stored == 0 ? 1 : stored) - 16383 - 63;
    BigInteger num = significand;
    if (negative)
      num = -num;
    return scale >= 0 ? (num << scale, BigInteger.One) : (num, BigInteger.One << -scale);
  }

  /// <summary>
  /// Rounds an exact rational to 64 significant bits, half to even - the answer, by definition. Null
  /// when the result falls outside the format's exponent range, where "correctly rounded" is an
  /// overflow question and is asserted separately.
  ///
  /// <para>
  /// Written to be obviously right rather than efficient: divide once at a precision generously
  /// beyond what is needed, keep the remainder as the sticky bit, and round the surplus away in one
  /// step. It shares no code with the implementation it judges, which is the whole of its value.
  /// </para>
  /// </summary>
  private static Extended80? CorrectlyRounded(BigInteger num, BigInteger den) {
    if (num.IsZero)
      return Extended80.Zero;
    // the sign is the sign of the RATIO: for a division the denominator carries the divisor's sign,
    // so taking it from the numerator alone gets it wrong for exactly half the pairs
    var negative = (num.Sign < 0) ^ (den.Sign < 0);
    num = BigInteger.Abs(num);
    den = BigInteger.Abs(den);

    // enough surplus that the quotient always has far more than the 65 bits the rounding needs
    var extra = 128 + (int)Math.Max(0, den.GetBitLength() - num.GetBitLength());
    var quotient = BigInteger.DivRem(num << extra, den, out var remainder);
    var scale = -extra;
    var sticky = !remainder.IsZero;

    // drop everything below the top 64 bits, remembering the bit just below and whether any survived
    var shift = (int)quotient.GetBitLength() - 64;
    var dropped = quotient & ((BigInteger.One << shift) - 1);
    quotient >>= shift;
    scale += shift;
    var half = BigInteger.One << (shift - 1);
    var roundBit = dropped >= half;
    sticky |= roundBit ? dropped != half : !dropped.IsZero;
    if (roundBit && (sticky || !quotient.IsEven))
      ++quotient;
    if (quotient.GetBitLength() > 64) {
      quotient >>= 1;
      ++scale;
    }

    var stored = 16383 + 63 + scale;
    if (stored is <= 0 or >= 0x7FFF)
      return null;
    var bytes = new byte[10];
    BitConverter.TryWriteBytes(bytes.AsSpan(0, 8), (ulong)quotient);
    BitConverter.TryWriteBytes(bytes.AsSpan(8, 2), (ushort)((negative ? 0x8000 : 0) | stored));
    return Extended80.FromBytes(bytes);
  }

  private static readonly (double L, double R)[] _pairs = [
    (1.0, 3.0), (2.0, 3.0), (1.0, 7.0), (10.0, 4.0), (0.1, 0.2), (1e100, 3.0),
    (1.5, 2.5), (-1.0, 3.0), (2.0, -7.0), (1234567.89, 1000.0), (Math.PI, Math.E),
    (65536.0, 65536.0), (1.0, 1048576.0), (3.0, 1e-100), (7.0, 11.0),
  ];

  [Test]
  public void Multiply_GivenAnyPair_ThenItIsTheExactProductCorrectlyRounded() {
    foreach (var (l, r) in _pairs) {
      var (ln, ld) = Exact(Extended80.FromDouble(l));
      var (rn, rd) = Exact(Extended80.FromDouble(r));
      if (CorrectlyRounded(ln * rn, ld * rd) is not { } expected)
        continue;                                  // outside the format's range; the overflow tests own that case
      var actual = Extended80.Multiply(Extended80.FromDouble(l), Extended80.FromDouble(r));
      Assert.That(actual.ToBytes(), Is.EqualTo(expected.ToBytes()), $"{l:R} * {r:R}");
    }
  }

  [Test]
  public void Divide_GivenAnyPair_ThenItIsTheExactQuotientCorrectlyRounded() {
    foreach (var (l, r) in _pairs) {
      var (ln, ld) = Exact(Extended80.FromDouble(l));
      var (rn, rd) = Exact(Extended80.FromDouble(r));
      if (CorrectlyRounded(ln * rd, ld * rn) is not { } expected)
        continue;                                  // outside the format's range; the overflow tests own that case
      var actual = Extended80.Divide(Extended80.FromDouble(l), Extended80.FromDouble(r));
      Assert.That(actual.ToBytes(), Is.EqualTo(expected.ToBytes()), $"{l:R} / {r:R}");
    }
  }

  [Test]
  public void Add_GivenAnyPair_ThenItIsTheExactSumCorrectlyRounded() {
    foreach (var (l, r) in _pairs) {
      var (ln, ld) = Exact(Extended80.FromDouble(l));
      var (rn, rd) = Exact(Extended80.FromDouble(r));
      if (CorrectlyRounded(ln * rd + rn * ld, ld * rd) is not { } expected)
        continue;                                  // outside the format's range; the overflow tests own that case
      var actual = Extended80.Add(Extended80.FromDouble(l), Extended80.FromDouble(r));
      Assert.That(actual.ToBytes(), Is.EqualTo(expected.ToBytes()), $"{l:R} + {r:R}");
    }
  }

  [Test]
  public void Subtract_GivenAnyPair_ThenItIsTheExactDifferenceCorrectlyRounded() {
    foreach (var (l, r) in _pairs) {
      var (ln, ld) = Exact(Extended80.FromDouble(l));
      var (rn, rd) = Exact(Extended80.FromDouble(r));
      if (CorrectlyRounded(ln * rd - rn * ld, ld * rd) is not { } expected)
        continue;                                  // outside the format's range; the overflow tests own that case
      var actual = Extended80.Subtract(Extended80.FromDouble(l), Extended80.FromDouble(r));
      Assert.That(actual.ToBytes(), Is.EqualTo(expected.ToBytes()), $"{l:R} - {r:R}");
    }
  }

  /// <summary>
  /// The case the whole type was built for. <c>2 / 3</c> has no exact form in any binary float, and
  /// the x87 keeps sixty-four bits of it where a double keeps fifty-three - so the significand must be
  /// the repeating 1010… pattern rounded UP in its last bit, which is not what converting the double
  /// quotient would give.
  /// </summary>
  [Test]
  public void Divide_GivenTwoOverThree_ThenTheSignificandIsTheEightyBitOne() {
    var quotient = Extended80.Divide(Extended80.FromInt64(2), Extended80.FromInt64(3));
    var bytes = quotient.ToBytes();
    Assert.That(BitConverter.ToUInt64(bytes, 0), Is.EqualTo(0xAAAA_AAAA_AAAA_AAABUL), "significand");
    Assert.That(BitConverter.ToUInt16(bytes, 8), Is.EqualTo((ushort)(16383 - 1)), "exponent for 2^-1");

    // and it is a DIFFERENT number from the same division done in a double, which is the point
    Assert.That(quotient.ToBytes(), Is.Not.EqualTo(Extended80.FromDouble(2.0 / 3.0).ToBytes()));
  }

  [Test]
  public void SquareRoot_GivenAPerfectSquare_ThenItIsExact() {
    foreach (var value in new double[] { 0.0, 1.0, 4.0, 9.0, 16.0, 1024.0, 65536.0, 1e100 })
      Assert.That(Extended80.SquareRoot(Extended80.FromDouble(value)).ToDouble(), Is.EqualTo(Math.Sqrt(value)),
        $"sqrt({value:R})");
  }

  [Test]
  public void SquareRoot_GivenTwo_ThenSquaringItComesBackToWithinOneUlp() {
    var root = Extended80.SquareRoot(Extended80.FromInt64(2));
    var squared = Extended80.Multiply(root, root);
    // sqrt(2) is irrational, so the round trip cannot be exact - but it must be adjacent to 2
    Assert.That(Math.Abs(squared.ToDouble() - 2.0), Is.LessThan(1e-18));
  }

  [Test]
  public void SquareRoot_GivenANegative_ThenItIsNotANumber()
    => Assert.That(Extended80.SquareRoot(Extended80.FromInt64(-4)).IsNaN, Is.True);

  #endregion

  #region special values

  [Test]
  public void Specials_BehaveAsTheStandardSaysTheyShould() {
    var inf = Extended80.PositiveInfinity;
    var ninf = Extended80.NegativeInfinity;
    var one = Extended80.One;
    var zero = Extended80.Zero;

    Assert.Multiple(() => {
      Assert.That(Extended80.Add(inf, one).IsInfinity, Is.True, "inf + 1");
      Assert.That(Extended80.Add(inf, ninf).IsNaN, Is.True, "inf + -inf");
      Assert.That(Extended80.Subtract(inf, inf).IsNaN, Is.True, "inf - inf");
      Assert.That(Extended80.Multiply(inf, zero).IsNaN, Is.True, "inf * 0");
      Assert.That(Extended80.Divide(one, zero).IsInfinity, Is.True, "1 / 0");
      Assert.That(Extended80.Divide(one, zero).IsNegative, Is.False, "1 / +0 is +inf");
      Assert.That(Extended80.Divide(one, Extended80.NegativeZero).IsNegative, Is.True, "1 / -0 is -inf");
      Assert.That(Extended80.Divide(zero, zero).IsNaN, Is.True, "0 / 0");
      Assert.That(Extended80.Divide(one, inf).IsZero, Is.True, "1 / inf");
      Assert.That(Extended80.Add(Extended80.NaN, one).IsNaN, Is.True, "NaN + 1");
      Assert.That(Extended80.Multiply(Extended80.NaN, zero).IsNaN, Is.True, "NaN * 0");
      Assert.That(Extended80.Negate(inf).IsNegative, Is.True, "-inf");
      Assert.That(Extended80.Abs(ninf).IsNegative, Is.False, "abs(-inf)");
    });
  }

  /// <summary>Overflow past the format's range, which a double cannot even express as an input.</summary>
  [Test]
  public void Multiply_GivenAProductBeyondTheRange_ThenItOverflowsToInfinity() {
    var huge = Extended80.FromDouble(double.MaxValue);           // ~1.8e308
    var product = huge;
    for (var i = 0; i < 20; ++i)
      product = Extended80.Multiply(product, huge);              // ~1.8e308 ^ 21, far past 1.2e4932
    Assert.That(product.IsInfinity, Is.True);
    Assert.That(product.IsNegative, Is.False);
  }

  /// <summary>
  /// The range this type exists to have. A double cannot hold 1e400; the extended can, and dividing
  /// back down must land on the double that started it.
  /// </summary>
  [Test]
  public void Range_GivenAValueNoDoubleCanHold_ThenItSurvivesAndComesBack() {
    var big = Extended80.FromDouble(1e300);
    var bigger = Extended80.Multiply(big, Extended80.FromDouble(1e100));   // 1e400: beyond double
    Assert.That(bigger.IsInfinity, Is.False, "1e400 fits in 80 bits");
    Assert.That(bigger.ToDouble(), Is.EqualTo(double.PositiveInfinity), "but not in a double");
    Assert.That(Extended80.Divide(bigger, Extended80.FromDouble(1e100)).ToDouble(), Is.EqualTo(1e300));
  }

  [Test]
  public void Underflow_GivenAProductBelowTheRange_ThenItBecomesZero() {
    var tiny = Extended80.FromDouble(1e-300);
    var product = tiny;
    for (var i = 0; i < 20; ++i)
      product = Extended80.Multiply(product, tiny);
    Assert.That(product.IsZero, Is.True);
  }

  #endregion

  #region comparison

  [Test]
  public void Compare_OrdersValuesAndRefusesToOrderNaN() {
    Assert.Multiple(() => {
      Assert.That(Extended80.Compare(Extended80.FromInt64(1), Extended80.FromInt64(2)), Is.Negative);
      Assert.That(Extended80.Compare(Extended80.FromInt64(2), Extended80.FromInt64(1)), Is.Positive);
      Assert.That(Extended80.Compare(Extended80.FromInt64(2), Extended80.FromInt64(2)), Is.Zero);
      Assert.That(Extended80.Compare(Extended80.FromInt64(-5), Extended80.FromInt64(3)), Is.Negative);
      Assert.That(Extended80.Compare(Extended80.Zero, Extended80.NegativeZero), Is.Zero, "-0 equals +0");
      Assert.That(Extended80.Compare(Extended80.NaN, Extended80.One), Is.Null, "NaN is unordered");
      Assert.That(Extended80.Compare(Extended80.One, Extended80.NaN), Is.Null);
      Assert.That(Extended80.Compare(Extended80.NegativeInfinity, Extended80.FromDouble(double.MinValue)), Is.Negative);
      Assert.That(Extended80.Compare(Extended80.PositiveInfinity, Extended80.FromDouble(double.MaxValue)), Is.Positive);
    });
  }

  [Test]
  public void Compare_GivenTheSampleSet_ThenItAgreesWithDoubleComparison() {
    foreach (var l in _samples)
      foreach (var r in _samples) {
        var expected = l < r ? -1 : l > r ? 1 : 0;
        var actual = Extended80.Compare(Extended80.FromDouble(l), Extended80.FromDouble(r));
        Assert.That(Math.Sign(actual!.Value), Is.EqualTo(expected), $"{l:R} vs {r:R}");
      }
  }

  #endregion

  #region agreement with double where double is exact

  /// <summary>
  /// Where a double computes the exact answer, the extended must compute the same one. This catches
  /// a whole class of mistake the rational reference cannot - a systematic error in BOTH the
  /// implementation and the reference would survive that comparison and die here.
  /// </summary>
  [Test]
  public void Arithmetic_WhereADoubleIsAlreadyExact_ThenTheTwoAgree() {
    for (var a = -20; a <= 20; ++a)
      for (var b = -20; b <= 20; ++b) {
        var l = Extended80.FromInt64(a);
        var r = Extended80.FromInt64(b);
        Assert.That(Extended80.Add(l, r).ToDouble(), Is.EqualTo((double)(a + b)), $"{a} + {b}");
        Assert.That(Extended80.Subtract(l, r).ToDouble(), Is.EqualTo((double)(a - b)), $"{a} - {b}");
        Assert.That(Extended80.Multiply(l, r).ToDouble(), Is.EqualTo((double)(a * b)), $"{a} * {b}");
        if (b != 0 && a % b == 0)
          Assert.That(Extended80.Divide(l, r).ToDouble(), Is.EqualTo((double)(a / b)), $"{a} / {b}");
      }
  }

  #endregion
}
