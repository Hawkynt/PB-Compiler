using PowerBasic.Compiler.Numerics;

namespace PowerBasic.Compiler.Tests.Numerics;

/// <summary>
/// Microsoft Binary Format DOUBLE has 55 stored fraction bits plus its implicit leading bit. These
/// tests construct x87 values by bit pattern so host <see cref="double"/> cannot erase the three
/// precision bits that distinguish MBF64 from IEEE binary64.
/// </summary>
[TestFixture]
public sealed class Mbf64Tests {

  [TestCase(0x0000_0000_0000_0000UL, 0x0000, 0x0000_0000_0000_0000UL)]
  [TestCase(0x8100_0000_0000_0000UL, 0x3FFF, 0x8000_0000_0000_0000UL)]
  [TestCase(0x8080_0000_0000_0000UL, 0xBFFE, 0x8000_0000_0000_0000UL)]
  [TestCase(0x817F_FFFF_FFFF_FFFFUL, 0x3FFF, 0xFFFF_FFFF_FFFF_FF00UL)]
  public void Conversion_GivenAnExactMbfValue_ThenItsBitsRoundTrip(
      ulong bits, int signExponent, ulong significand) {
    var extended = Extended((ushort)signExponent, significand);

    var encoded = Mbf64.FromExtended80(extended);

    Assert.Multiple(() => {
      Assert.That(encoded.Bits, Is.EqualTo(bits));
      Assert.That(encoded.ToExtended80().ToBytes(), Is.EqualTo(extended.ToBytes()));
    });
  }

  /// <summary>
  /// Given values exactly halfway between two MBF64 numbers, when they are encoded, then the retained
  /// low bit decides the result: even stays and odd advances to the next even value.
  /// </summary>
  [TestCase(0x8000_0000_0000_0080UL, 0x8100_0000_0000_0000UL)]
  [TestCase(0x8000_0000_0000_0180UL, 0x8100_0000_0000_0002UL)]
  [TestCase(0x8000_0000_0000_00FFUL, 0x8100_0000_0000_0001UL)]
  public void FromExtended80_GivenAQuantizationBoundary_ThenItRoundsToNearestEven(
      ulong significand, ulong expectedBits) {
    var encoded = Mbf64.FromExtended80(Extended(0x3FFF, significand));

    Assert.That(encoded.Bits, Is.EqualTo(expectedBits));
  }

  [Test]
  public void FromExtended80_GivenRoundingCarriesTheSignificand_ThenItAdvancesTheExponent() {
    var encoded = Mbf64.FromExtended80(Extended(0x3FFF, 0xFFFF_FFFF_FFFF_FFFF));

    Assert.That(encoded.Bits, Is.EqualTo(0x8200_0000_0000_0000UL));
  }

  [Test]
  public void FromExtended80_GivenArithmeticNeedsTheFiftySixthBit_ThenItSurvives() {
    var denominator = Extended80.FromInt64(1L << 55);
    var epsilon = Extended80.Divide(Extended80.One, denominator);
    var sum = Extended80.Add(Extended80.One, epsilon);

    Assert.That(Mbf64.FromExtended80(sum).Bits, Is.EqualTo(0x8100_0000_0000_0001UL));
  }

  [Test]
  public void FromExtended80_GivenValuesOutsideTheFiniteMbfRange_ThenItUnderflowsOrDeclines() {
    var underflow = Mbf64.FromExtended80(Extended(0x3F7E, 0x8000_0000_0000_0000));

    Assert.Multiple(() => {
      Assert.That(underflow, Is.EqualTo(Mbf64.Zero));
      Assert.That(Mbf64.TryFromExtended80(Extended(0x407E, 0x8000_0000_0000_0000), out _), Is.False);
      Assert.That(Mbf64.TryFromExtended80(Extended80.PositiveInfinity, out _), Is.False);
      Assert.That(Mbf64.TryFromExtended80(Extended80.NaN, out _), Is.False);
    });
  }

  [Test]
  public void FromBytes_GivenTooFewBytes_ThenItRejectsTheInput()
    => Assert.Throws<ArgumentException>(() => Mbf64.FromBytes(new byte[7]));

  [Test]
  public void FromBytes_GivenAnEightBytePattern_ThenItPreservesEveryBit() {
    byte[] bytes = [0xEF, 0xCD, 0xAB, 0x89, 0x67, 0x45, 0xA3, 0x81];

    Assert.That(Mbf64.FromBytes(bytes).ToBytes(), Is.EqualTo(bytes));
  }

  /// <summary>Low-byte matrix captured from genuine GW-BASIC 3.23 through VARPTR/PEEK.</summary>
  [TestCase("1.00000000000000001D0", 0UL)]
  [TestCase("1.00000000000000002D0", 1UL)]
  [TestCase("1.00000000000000003D0", 1UL)]
  [TestCase("1.00000000000000005D0", 1UL)]
  [TestCase("1.0000000000000001D0", 4UL)]
  [TestCase("1.0000000000000002D0", 7UL)]
  public void ParseDecimal_GivenGwBasicOracleLiteral_ThenItsMbf64BitsMatch(string text, ulong lowByte) {
    var parsed = Mbf64.TryParseDecimalLiteral(text, out var value);
    Assert.Multiple(() => {
      Assert.That(parsed, Is.True);
      Assert.That(value.Bits, Is.EqualTo(0x8100_0000_0000_0000UL | lowByte));
    });
  }

  /// <summary>Whole MBF64 cell bytes captured from GW-BASIC 3.23, spanning exponent and tie cases.</summary>
  [TestCase("1.2345678901234567D-38", "120 181 3 221 186 110 6 3")]
  [TestCase("1.2345678901234567D38", "250 43 192 74 211 193 57 255")]
  [TestCase("9.8765432109876543D-39", "160 200 129 130 145 23 87 2")]
  [TestCase("3.1415926535897932D-20", "195 79 31 85 134 91 20 64")]
  [TestCase("2.7182818284590452D-17", "206 159 227 111 157 183 122 73")]
  [TestCase("0.99999999999999995D0", "253 255 255 255 255 255 127 128")]
  [TestCase("1.2345678901234567D30", "104 40 58 250 169 81 121 228")]
  [TestCase("1.2345678901234567D-30", "18 214 52 158 241 81 72 29")]
  [TestCase("1234567890123456.7D0", "22 88 87 145 167 90 12 179")]
  [TestCase("1.2345678901234567D-5", "204 197 51 129 73 32 79 112")]
  [TestCase("1.2345678901234567D5", "101 70 91 254 100 32 113 145")]
  [TestCase("1.2345678901234567D18", "6 76 239 163 135 16 9 189")]
  [TestCase("1.2345678901234567D-18", "212 191 204 252 167 48 54 69")]
  [TestCase("1.2345678901234567D37", "46 35 0 111 15 155 20 252")]
  [TestCase("0.00000000000000012345678901234567D0", "221 245 127 61 3 86 14 76")]
  public void ParseDecimal_GivenGwBasicExponentOracle_ThenWholeCellMatches(string text, string bytes) {
    var expected = Mbf64.FromBytes(bytes.Split(' ').Select(byte.Parse).ToArray());

    Assert.That(Mbf64.TryParseDecimalLiteral(text, out var actual), Is.True);
    Assert.That(actual, Is.EqualTo(expected));
  }

  [Test]
  public void ParseDecimal_GivenMalformedOrExtremeInput_ThenItDeclinesOrUnderflows() {
    Assert.Multiple(() => {
      Assert.That(Mbf64.TryParseDecimalLiteral("1.2.3", out _), Is.False);
      Assert.That(Mbf64.TryParseDecimalLiteral("1D1001", out _), Is.False);
      Assert.That(Mbf64.TryParseDecimalLiteral("1D50", out _), Is.False);
      Assert.That(Mbf64.TryParseDecimalLiteral("1D-50", out var tiny), Is.True);
      Assert.That(tiny, Is.EqualTo(Mbf64.Zero));
      Assert.That(() => Mbf64.TryParseDecimalLiteral(null!, out _), Throws.ArgumentNullException);
    });
  }

  private static Extended80 Extended(ushort signExponent, ulong significand) {
    Span<byte> bytes = stackalloc byte[10];
    BitConverter.TryWriteBytes(bytes[..8], significand);
    BitConverter.TryWriteBytes(bytes[8..], signExponent);
    return Extended80.FromBytes(bytes);
  }
}
