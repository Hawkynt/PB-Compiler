namespace PowerBasic.Compiler.Tests.Dialects;

/// <summary>
/// D11 - bit-exact numeric behaviour, starting where it can be settled without a vintage binary:
/// the decimal-to-binary conversion of a float literal.
///
/// This is a smaller claim than the dimension's full contract and it is deliberately the first one.
/// Every later comparison rests on it: if <c>0.1</c> does not become the same 64 bits the original
/// compiler produced, no amount of agreement in the arithmetic that follows means anything, because
/// the two programs started from different numbers. And decimal-to-binary is exactly the place where
/// a hand-rolled parser drifts - by a single unit in the last place, on the values people write.
///
/// The expected patterns are IEEE-754, which is what both lineages use for the literal itself; where
/// a dialect STORES the result differently (BASICA and GW-BASIC keep SINGLE in Microsoft Binary
/// Format) that is a conversion applied afterwards, and is the runtime-selection dimension's claim,
/// not this one's.
/// </summary>
internal static class DialectBitExactClaims {

  /// <param name="Literal">The literal as it appears in source.</param>
  /// <param name="DoubleBits">The IEEE-754 binary64 pattern it must produce.</param>
  /// <param name="Why">What makes this value worth pinning.</param>
  internal sealed record Claim(string Literal, ulong DoubleBits, string Why);

  /// <summary>
  /// Values chosen because they are where conversions go wrong, not because they are round numbers.
  ///
  /// Every pattern below is the one a reference implementation produces, checked rather than typed
  /// from memory: the first version of this table had 1234567.89 wrong by four bytes, and the probe
  /// duly reported the compiler failing a claim the compiler was right about.
  /// </summary>
  internal static readonly Claim[] All = [
    new("0.1", 0x3FB999999999999AUL, "the classic non-terminating binary fraction; the last place is a round-UP"),
    new("0.2", 0x3FC999999999999AUL, "the same digits one exponent up, so a shared mantissa path shows"),
    new("0.3", 0x3FD3333333333333UL, "rounds DOWN where 0.1 and 0.2 round up - a sign the rounding is real"),
    new("1.5", 0x3FF8000000000000UL, "exactly representable; a mismatch here is not rounding but parsing"),
    new("2.5", 0x4004000000000000UL, "exact, and the tie case CINT/round-half arguments are written about"),
    new("1E10", 0x4202A05F20000000UL, "exponent notation, well inside range"),
    new("1E-7", 0x3E7AD7F29ABCAF48UL, "a small exponent, where scaling by a power of ten accumulates error"),
    new("1234567.89", 0x4132D687E3D70A3DUL, "many significant digits either side of the point"),
    new("3.14159265358979", 0x400921FB54442D11UL, "fifteen significant digits - the edge of binary64's exactness"),
  ];
}
