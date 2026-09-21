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

  private static Extended80 Extended(ushort signExponent, ulong significand) {
    Span<byte> bytes = stackalloc byte[10];
    BitConverter.TryWriteBytes(bytes[..8], significand);
    BitConverter.TryWriteBytes(bytes[8..], signExponent);
    return Extended80.FromBytes(bytes);
  }
}
