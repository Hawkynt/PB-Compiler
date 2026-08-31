using System.Numerics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

/// <summary>
/// Cross-domain reduction and transfer for O0016 value facts. Interval, known-bit and congruence
/// facts are individually useful, but their product is stronger when each domain is allowed to
/// tighten the others: [0,1] proves every bit above bit 0 is zero, a mod-8 residue fixes three low
/// bits, and fixed low bits in turn imply a power-of-two congruence.
/// </summary>
public static class ValueFactReduction {

  /// <summary>Reduces a product of interval, known-bit and congruence facts to a local fixpoint.</summary>
  public static ValueFacts Reduce(ValueFacts facts, int width, bool signed) {
    if (width is <= 0 or > 64)
      return facts;

    var range = facts.Range;
    // The interval payload is signed long. It can represent the low half of U64, but never a value
    // with bit 63 set. Keep the bit domain in that case and drop only the unrepresentable interval.
    if (!signed && width == 64 && !range.IsTop && range.Lo < 0)
      range = Interval.Top;
    else if (!range.IsTop) {
      range = Intersect(range, TypeRange(width, signed), out var validTypeRange);
      if (!validTypeRange)
        return ValueFacts.Unknown;
    }

    var bits = facts.Bits.Narrow(width);
    var mod = Canonical(facts.Mod);

    for (var iteration = 0; iteration < 4; ++iteration) {
      var before = new ValueFacts(range, bits, mod);

      bits = Meet(bits, BitsFromRange(range, width));
      bits = Meet(bits, BitsFromCongruence(mod, width));
      if ((bits.Ones & bits.Zeros & Mask(width)) != 0)
        return ValueFacts.Unknown;

      range = Intersect(range, RangeFromBits(bits, width, signed), out var validRange);
      if (!validRange)
        return ValueFacts.Unknown;
      range = RestrictToCongruence(range, mod, out validRange);
      if (!validRange)
        return ValueFacts.Unknown;

      mod = IntersectCongruence(mod, CongruenceFromBits(bits, width, signed));

      if (!range.IsTop && range.Lo == range.Hi) {
        bits = Meet(bits, KnownBits.Of(range.Lo, width));
        mod = Congruence.Of(range.Lo);
      }

      var after = new ValueFacts(range, bits, mod);
      if (after.Equals(before))
        return after;
    }

    return new(range, bits, mod);
  }

  /// <summary>All bits that can possibly be one. Bits known zero are excluded.</summary>
  public static ulong PossibleOneMask(KnownBits bits, int width) => ~bits.Zeros & Mask(width);

  /// <summary>True when no bit except <paramref name="bit"/> can ever be one.</summary>
  public static bool OnlyBitMayBeOne(ValueFacts facts, int bit, int width) {
    if (bit < 0 || bit >= width || width is <= 0 or > 64)
      return false;
    var allowed = 1UL << bit;
    return (PossibleOneMask(facts.Bits, width) & ~allowed) == 0;
  }

  /// <summary>
  /// Exact per-bit addition/subtraction over three-valued input bits and the carry/borrow chain.
  /// Subtraction is evaluated as a + ~b + 1, so it is exact for fixed-width two's-complement bits.
  /// </summary>
  public static KnownBits AddSub(KnownBits left, KnownBits right, int width, bool subtract) {
    if (width is <= 0 or > 64)
      return KnownBits.Unknown;

    left = left.Narrow(width);
    right = subtract ? right.Not().Narrow(width) : right.Narrow(width);
    ulong ones = 0, zeros = 0;
    var carries = subtract ? 0b10 : 0b01; // bit 0 => carry 0 possible, bit 1 => carry 1 possible

    for (var bit = 0; bit < width; ++bit) {
      var mask = 1UL << bit;
      var leftValues = BitValues(left, mask);
      var rightValues = BitValues(right, mask);
      var resultValues = 0;
      var nextCarries = 0;

      for (var a = 0; a <= 1; ++a) {
        if ((leftValues & (1 << a)) == 0) continue;
        for (var b = 0; b <= 1; ++b) {
          if ((rightValues & (1 << b)) == 0) continue;
          for (var carry = 0; carry <= 1; ++carry) {
            if ((carries & (1 << carry)) == 0) continue;
            var sum = a + b + carry;
            resultValues |= 1 << (sum & 1);
            nextCarries |= 1 << (sum >> 1);
          }
        }
      }

      if (resultValues == 0b01) zeros |= mask;
      else if (resultValues == 0b10) ones |= mask;
      carries = nextCarries;
    }

    return new(ones, zeros);
  }

  /// <summary>Known-bit rotate within a fixed width. Unknown bits remain unknown.</summary>
  public static KnownBits Rotate(KnownBits bits, int count, int width, bool left) {
    if (width is <= 0 or > 64 || count < 0)
      return KnownBits.Unknown;
    count %= width;
    bits = bits.Narrow(width);
    if (count == 0)
      return bits;
    var mask = Mask(width);
    ulong RotateOne(ulong value) => left
      ? ((value << count) | (value >> (width - count))) & mask
      : ((value >> count) | (value << (width - count))) & mask;
    return new(RotateOne(bits.Ones), RotateOne(bits.Zeros));
  }

  /// <summary>Arithmetic/logical right-shift facts, including known sign-bit fill.</summary>
  public static KnownBits ShiftRight(KnownBits bits, int count, int width, bool arithmetic) {
    if (width is <= 0 or > 64 || count < 0 || count >= width)
      return KnownBits.Unknown;
    var result = bits.ShiftRight(count, width, arithmetic).Narrow(width);
    if (!arithmetic || count == 0)
      return result;

    var sign = 1UL << (width - 1);
    var top = Mask(width) & ~(Mask(width) >> count);
    if ((bits.Ones & sign) != 0)
      return new(result.Ones | top, result.Zeros & ~top);
    if ((bits.Zeros & sign) != 0)
      return new(result.Ones & ~top, result.Zeros | top);
    return result;
  }

  /// <summary>Transfer for one fixed-width integral AST binary operator.</summary>
  public static ValueFacts Binary(BinaryOp op, ValueFacts left, ValueFacts right, int width, bool signed) {
    if (op is BinaryOp.Equal or BinaryOp.NotEqual or BinaryOp.Less or BinaryOp.Greater
        or BinaryOp.LessEqual or BinaryOp.GreaterEqual)
      return Compare(op, left, width, signed, right, width, signed, width);

    left = Reduce(left, width, signed);
    right = Reduce(right, width, signed);
    var exactCount = ExactInt(right);
    var shiftCount = exactCount is >= 0 and < 64 ? (int?)exactCount : null;

    var range = op switch {
      BinaryOp.Add => left.Range.Add(right.Range),
      BinaryOp.Subtract => left.Range.Subtract(right.Range),
      BinaryOp.Multiply => left.Range.Multiply(right.Range),
      BinaryOp.IntegerDivide => left.Range.Divide(right.Range),
      BinaryOp.Modulo => left.Range.Modulo(right.Range),
      BinaryOp.And => left.Range.And(right.Range),
      BinaryOp.ShiftLeft when shiftCount is { } count && count < width => ShiftLeftRange(left.Range, count),
      BinaryOp.ShiftRightArith when shiftCount is { } count && count < width => ShiftRightArithmeticRange(left.Range, count),
      BinaryOp.ShiftRightLogical when shiftCount is { } count && count < width => ShiftRightLogicalRange(left.Range, count, width),
      _ => Interval.Top,
    };
    range = FitOrTop(range, width, signed); // a mathematical range that can wrap is not a runtime interval yet

    var bits = op switch {
      BinaryOp.And => left.Bits.And(right.Bits),
      BinaryOp.Or => left.Bits.Or(right.Bits),
      BinaryOp.Xor => left.Bits.Xor(right.Bits),
      BinaryOp.Eqv => left.Bits.Xor(right.Bits).Not(),
      BinaryOp.Imp => left.Bits.Not().Or(right.Bits),
      BinaryOp.Add => AddSub(left.Bits, right.Bits, width, subtract: false),
      BinaryOp.Subtract => AddSub(left.Bits, right.Bits, width, subtract: true),
      BinaryOp.Multiply => MultiplyBits(left, right, width),
      BinaryOp.ShiftLeft when shiftCount is { } count && count < width => left.Bits.ShiftLeft(count),
      BinaryOp.ShiftRightArith when shiftCount is { } count && count < width => ShiftRight(left.Bits, count, width, arithmetic: true),
      BinaryOp.ShiftRightLogical when shiftCount is { } count && count < width => ShiftRight(left.Bits, count, width, arithmetic: false),
      BinaryOp.RotateLeft when ExactRotateCount(exactCount, width) is { } count => Rotate(left.Bits, count, width, left: true),
      BinaryOp.RotateRight when ExactRotateCount(exactCount, width) is { } count => Rotate(left.Bits, count, width, left: false),
      BinaryOp.IntegerDivide when exactCount is { } divisor && divisor > 0 && IsPowerOfTwo(divisor)
          && left.Range is { Lo: >= 0 } => ShiftRight(left.Bits, BitOperations.TrailingZeroCount((ulong)divisor), width, arithmetic: false),
      _ => KnownBits.Unknown,
    };

    var mod = op switch {
      BinaryOp.Add => left.Mod.Add(right.Mod),
      BinaryOp.Subtract => left.Mod.Subtract(right.Mod),
      BinaryOp.Multiply => left.Mod.Multiply(right.Mod),
      BinaryOp.ShiftLeft when shiftCount is { } count && count < 63
        => left.Mod.Multiply(Congruence.Of(1L << count)),
      BinaryOp.IntegerDivide when exactCount is { } divisor && divisor > 0 && IsPowerOfTwo(divisor)
          && left.Range is { Lo: >= 0 } => DivideCongruenceByPowerOfTwo(left.Mod, divisor),
      BinaryOp.Modulo when exactCount is { } divisor && divisor is not 0 and not long.MinValue
          && left.Range is { Lo: >= 0 } => ModuloCongruence(left.Mod, Math.Abs(divisor)),
      _ => Congruence.Unknown,
    };
    if (range.IsTop && !IsPowerOfTwo(mod.Modulus))
      mod = Congruence.Unknown; // non-power-of-two congruence is not invariant under 2^width wrap

    return Reduce(new(range, bits.Narrow(width), mod), width, signed);
  }

  /// <summary>
  /// Width-aware comparison transfer. The result is PB's 16-bit-style truth value, but each operand
  /// is reduced in its own type domain; a LONG comparison must never truncate its facts to the width
  /// of the boolean result.
  /// </summary>
  public static ValueFacts Compare(BinaryOp op,
      ValueFacts left, int leftWidth, bool leftSigned,
      ValueFacts right, int rightWidth, bool rightSigned,
      int resultWidth) {
    if (leftWidth > 0)
      left = Reduce(left, leftWidth, leftSigned);
    if (rightWidth > 0)
      right = Reduce(right, rightWidth, rightSigned);

    bool? result = op switch {
      BinaryOp.Equal when Disjoint(left.Range, right.Range) => false,
      BinaryOp.NotEqual when Disjoint(left.Range, right.Range) => true,
      BinaryOp.Equal when ExactInt(left) is { } l && ExactInt(right) is { } r => l == r,
      BinaryOp.NotEqual when ExactInt(left) is { } l && ExactInt(right) is { } r => l != r,
      BinaryOp.Equal when ExactInt(left) is { } lc && !right.Allows(lc, WidthForAllows(rightWidth)) => false,
      BinaryOp.Equal when ExactInt(right) is { } rc && !left.Allows(rc, WidthForAllows(leftWidth)) => false,
      BinaryOp.NotEqual when ExactInt(left) is { } lc && !right.Allows(lc, WidthForAllows(rightWidth)) => true,
      BinaryOp.NotEqual when ExactInt(right) is { } rc && !left.Allows(rc, WidthForAllows(leftWidth)) => true,
      BinaryOp.Less when Finite(left.Range, right.Range) && left.Range.Hi < right.Range.Lo => true,
      BinaryOp.Less when Finite(left.Range, right.Range) && left.Range.Lo >= right.Range.Hi => false,
      BinaryOp.LessEqual when Finite(left.Range, right.Range) && left.Range.Hi <= right.Range.Lo => true,
      BinaryOp.LessEqual when Finite(left.Range, right.Range) && left.Range.Lo > right.Range.Hi => false,
      BinaryOp.Greater when Finite(left.Range, right.Range) && left.Range.Lo > right.Range.Hi => true,
      BinaryOp.Greater when Finite(left.Range, right.Range) && left.Range.Hi <= right.Range.Lo => false,
      BinaryOp.GreaterEqual when Finite(left.Range, right.Range) && left.Range.Lo >= right.Range.Hi => true,
      BinaryOp.GreaterEqual when Finite(left.Range, right.Range) && left.Range.Hi < right.Range.Lo => false,
      _ => null,
    };
    var width = resultWidth is > 0 and <= 64 ? resultWidth : 16;
    return result is { } known ? ValueFacts.Of(known ? -1 : 0, width) : Truth(width);
  }

  /// <summary>Transfer for integer unary negation.</summary>
  public static ValueFacts Negate(ValueFacts value, int width, bool signed) {
    value = Reduce(value, width, signed);
    var range = FitOrTop(value.Range.Negate(), width, signed);
    var bits = AddSub(KnownBits.Of(0, width), value.Bits, width, subtract: true);
    var mod = range.IsTop && !IsPowerOfTwo(value.Mod.Modulus) ? Congruence.Unknown : value.Mod.Negate();
    return Reduce(new(range, bits, mod), width, signed);
  }

  /// <summary>Transfer for PB's fixed-width bitwise NOT.</summary>
  public static ValueFacts Not(ValueFacts value, int width, bool signed) {
    value = Reduce(value, width, signed);
    // The bit transfer is exact. Deriving the numeric range back from those bits is safer than
    // applying C#'s 64-bit ~ to a narrower unsigned value and then pretending it did not wrap.
    return Reduce(new(Interval.Top, value.Bits.Not().Narrow(width), Congruence.Unknown), width, signed);
  }

  /// <summary>Facts for an unknown comparison: PB truth is exactly 0 or -1.</summary>
  public static ValueFacts Truth(int width) => Reduce(new(new Interval(-1, 0), KnownBits.Unknown, Congruence.Unknown), width, signed: true);

  private static KnownBits MultiplyBits(ValueFacts left, ValueFacts right, int width) {
    if (ExactInt(right) is { } r && TryPowerOfTwoMagnitude(r, out var shift)) {
      var shifted = left.Bits.ShiftLeft(shift).Narrow(width);
      return r < 0 ? AddSub(KnownBits.Of(0, width), shifted, width, subtract: true) : shifted;
    }
    if (ExactInt(left) is { } l && TryPowerOfTwoMagnitude(l, out shift)) {
      var shifted = right.Bits.ShiftLeft(shift).Narrow(width);
      return l < 0 ? AddSub(KnownBits.Of(0, width), shifted, width, subtract: true) : shifted;
    }
    return left.Bits.Multiply(right.Bits, width).Narrow(width);
  }

  private static bool TryPowerOfTwoMagnitude(long value, out int shift) {
    var magnitude = value == long.MinValue ? 1UL << 63 : (ulong)Math.Abs(value);
    if (magnitude != 0 && (magnitude & (magnitude - 1)) == 0) {
      shift = BitOperations.TrailingZeroCount(magnitude);
      return true;
    }
    shift = 0;
    return false;
  }

  private static Congruence DivideCongruenceByPowerOfTwo(Congruence c, long divisor) {
    if (c.IsExact)
      return Congruence.Of(c.Residue / divisor);
    if (c.IsUnknown || c.Modulus % divisor != 0)
      return Congruence.Unknown;
    return Normalize(c.Modulus / divisor, c.Residue / divisor);
  }

  private static Congruence ModuloCongruence(Congruence c, long divisor) {
    if (divisor <= 0)
      return Congruence.Unknown;
    if (c.IsExact)
      return Congruence.Of(c.Residue % divisor);
    if (c.IsUnknown || c.Modulus % divisor != 0)
      return Congruence.Unknown;
    var residue = c.Residue % divisor;
    return Congruence.Of(residue < 0 ? residue + divisor : residue);
  }

  private static Interval ShiftLeftRange(Interval range, int count) {
    if (range.IsTop)
      return Interval.Top;
    var factor = BigInteger.One << count;
    var lo = new BigInteger(range.Lo) * factor;
    var hi = new BigInteger(range.Hi) * factor;
    return TryInterval(lo, hi);
  }

  private static Interval ShiftRightArithmeticRange(Interval range, int count) {
    if (range.IsTop)
      return Interval.Top;
    return count == 0 ? range : new(range.Lo >> count, range.Hi >> count);
  }

  private static Interval ShiftRightLogicalRange(Interval range, int count, int width) {
    if (range.IsTop)
      return Interval.Top;
    if (count == 0)
      return range;

    ulong Bits(long value) => unchecked((ulong)value) & Mask(width);
    if (range.Lo >= 0)
      return new((long)(Bits(range.Lo) >> count), (long)(Bits(range.Hi) >> count));
    if (range.Hi < 0)
      return new((long)(Bits(range.Lo) >> count), (long)(Bits(range.Hi) >> count));

    // Signed interval crosses zero: negative inputs map to the upper half after a logical shift,
    // positive inputs to the lower half. Their convex hull is the whole shifted bit domain.
    var max = Mask(width) >> count;
    return max <= long.MaxValue ? new(0, (long)max) : Interval.Top;
  }

  private static int? ExactRotateCount(long? count, int width) {
    if (count is not { } c || c < 0 || width <= 0)
      return null;
    return (int)(c % width);
  }

  private static KnownBits BitsFromRange(Interval range, int width) {
    if (range.IsTop || range.IsEmpty)
      return KnownBits.Unknown;
    if (range.Lo == range.Hi)
      return KnownBits.Of(range.Lo, width);
    if (range.Lo < 0 && range.Hi >= 0)
      return KnownBits.Unknown; // signed-order interval crosses the two's-complement wrap point

    var mask = Mask(width);
    var lo = unchecked((ulong)range.Lo) & mask;
    var hi = unchecked((ulong)range.Hi) & mask;
    var diff = lo ^ hi;
    if (diff == 0)
      return KnownBits.Of(range.Lo, width);
    var highest = 63 - BitOperations.LeadingZeroCount(diff);
    var varying = highest >= 63 ? ulong.MaxValue : (1UL << (highest + 1)) - 1;
    var common = mask & ~varying;
    return new(lo & common, ~lo & common & mask);
  }

  private static Interval RangeFromBits(KnownBits bits, int width, bool signed) {
    if (bits.IsUnknown)
      return Interval.Top;
    bits = bits.Narrow(width);
    var mask = Mask(width);
    var unknown = ~(bits.Ones | bits.Zeros) & mask;
    if (!signed) {
      if (width == 64 && ((bits.Zeros >> 63) & 1) == 0)
        return Interval.Top; // values above long.MaxValue cannot be represented by Interval
      var min = bits.Ones;
      var max = bits.Ones | unknown;
      return min <= long.MaxValue && max <= long.MaxValue ? new((long)min, (long)max) : Interval.Top;
    }

    var sign = 1UL << (width - 1);
    ulong minBits, maxBits;
    if ((bits.Zeros & sign) != 0) {
      minBits = bits.Ones;
      maxBits = bits.Ones | unknown;
    } else if ((bits.Ones & sign) != 0) {
      minBits = bits.Ones;
      maxBits = bits.Ones | unknown;
    } else {
      minBits = bits.Ones | sign;
      maxBits = (bits.Ones | (unknown & ~sign)) & ~sign;
    }
    return new(SignExtend(minBits, width), SignExtend(maxBits, width));
  }

  private static KnownBits BitsFromCongruence(Congruence c, int width) {
    if (c.IsUnknown)
      return KnownBits.Unknown;
    if (c.IsExact)
      return KnownBits.Of(c.Residue, width);
    if (!IsPowerOfTwo(c.Modulus))
      return KnownBits.Unknown;
    var count = BitOperations.TrailingZeroCount((ulong)c.Modulus);
    if (count <= 0)
      return KnownBits.Unknown;
    var lowMask = count >= 64 ? ulong.MaxValue : (1UL << count) - 1;
    var bits = unchecked((ulong)c.Residue) & lowMask;
    return new KnownBits(bits, ~bits & lowMask).Narrow(width);
  }

  private static Congruence CongruenceFromBits(KnownBits bits, int width, bool signed) {
    bits = bits.Narrow(width);
    var mask = Mask(width);
    var known = (bits.Ones | bits.Zeros) & mask;
    if (known == mask) {
      var valueBits = bits.Ones & mask;
      if (!signed && width == 64 && (valueBits & (1UL << 63)) != 0)
        return Congruence.Unknown; // Congruence's long payload cannot spell this unsigned value
      return Congruence.Of(signed ? SignExtend(valueBits, width) : (long)valueBits);
    }

    var count = 0;
    while (count < width && count < 62 && (known & (1UL << count)) != 0)
      ++count;
    if (count == 0)
      return Congruence.Unknown;
    var modulus = 1L << count;
    return Normalize(modulus, (long)(bits.Ones & ((1UL << count) - 1)));
  }

  private static Congruence IntersectCongruence(Congruence a, Congruence b) {
    if (a.IsUnknown) return b;
    if (b.IsUnknown) return a;
    if (a.IsExact) return b.Allows(a.Residue) ? a : Congruence.Unknown;
    if (b.IsExact) return a.Allows(b.Residue) ? b : Congruence.Unknown;

    var g = BigInteger.GreatestCommonDivisor(a.Modulus, b.Modulus);
    var diff = new BigInteger(b.Residue) - a.Residue;
    if (diff % g != 0)
      return Congruence.Unknown;

    var m1 = new BigInteger(a.Modulus) / g;
    var m2 = new BigInteger(b.Modulus) / g;
    var lcm = m1 * b.Modulus;
    if (lcm > long.MaxValue)
      return a.Modulus >= b.Modulus ? a : b; // either fact alone remains a sound over-approximation

    var rhs = diff / g;
    var inverse = ModInverse(m1, m2);
    var t = Mod(rhs * inverse, m2);
    var solution = Mod(new BigInteger(a.Residue) + new BigInteger(a.Modulus) * t, lcm);
    return Normalize((long)lcm, (long)solution);
  }

  private static Interval RestrictToCongruence(Interval range, Congruence c, out bool valid) {
    valid = true;
    if (range.IsTop || c.IsUnknown)
      return range;
    if (c.IsExact) {
      if (!range.Contains(c.Residue)) { valid = false; return range; }
      return Interval.Of(c.Residue);
    }

    var m = new BigInteger(c.Modulus);
    var lo = new BigInteger(range.Lo);
    var hi = new BigInteger(range.Hi);
    var residue = new BigInteger(c.Residue);
    var first = lo + Mod(residue - lo, m);
    var last = hi - Mod(hi - residue, m);
    if (first > last) { valid = false; return range; }
    return new((long)first, (long)last);
  }

  private static Interval Intersect(Interval a, Interval b, out bool valid) {
    if (a.IsTop) { valid = true; return b; }
    if (b.IsTop) { valid = true; return a; }
    var result = new Interval(Math.Max(a.Lo, b.Lo), Math.Min(a.Hi, b.Hi));
    valid = !result.IsEmpty;
    return valid ? result : a;
  }

  private static Interval FitOrTop(Interval range, int width, bool signed) {
    if (range.IsTop)
      return range;
    if (!signed && width == 64)
      return range.Lo >= 0 ? range : Interval.Top;
    var type = TypeRange(width, signed);
    return type.IsTop || (range.Lo >= type.Lo && range.Hi <= type.Hi) ? range : Interval.Top;
  }

  private static Interval TypeRange(int width, bool signed) {
    if (width >= 64)
      return Interval.Top;
    if (signed) {
      var sign = 1L << (width - 1);
      return new(-sign, sign - 1);
    }
    return new(0, (long)((1UL << width) - 1));
  }

  private static Interval TryInterval(BigInteger lo, BigInteger hi)
    => lo < long.MinValue || hi > long.MaxValue ? Interval.Top : new((long)lo, (long)hi);

  private static KnownBits Meet(KnownBits a, KnownBits b) => new(a.Ones | b.Ones, a.Zeros | b.Zeros);
  private static bool Disjoint(Interval a, Interval b) => !a.IsTop && !b.IsTop && (a.Hi < b.Lo || b.Hi < a.Lo);
  private static bool Finite(Interval a, Interval b) => !a.IsTop && !b.IsTop;
  private static long? ExactInt(ValueFacts facts) => !facts.Range.IsTop && facts.Range.Lo == facts.Range.Hi ? facts.Range.Lo : facts.Mod.IsExact ? facts.Mod.Residue : null;
  private static int BitValues(KnownBits bits, ulong mask) => (bits.Ones & mask) != 0 ? 0b10 : (bits.Zeros & mask) != 0 ? 0b01 : 0b11;
  private static bool IsPowerOfTwo(long value) => value > 0 && (value & (value - 1)) == 0;
  private static int WidthForAllows(int width) => width is > 0 and <= 64 ? width : 64;

  private static ulong Mask(int width) => width >= 64 ? ulong.MaxValue : (1UL << width) - 1;

  private static long SignExtend(ulong bits, int width) {
    if (width >= 64)
      return unchecked((long)bits);
    var mask = Mask(width);
    bits &= mask;
    var sign = 1UL << (width - 1);
    return unchecked((long)((bits ^ sign) - sign));
  }

  private static Congruence Canonical(Congruence c)
    => c.IsExact || c.IsUnknown ? c : Normalize(c.Modulus, c.Residue);

  private static Congruence Normalize(long modulus, long residue) {
    if (modulus == long.MinValue)
      return Congruence.Unknown;
    modulus = Math.Abs(modulus);
    if (modulus == 0) return Congruence.Of(residue);
    if (modulus == 1) return Congruence.Unknown;
    var r = residue % modulus;
    return new(modulus, r < 0 ? r + modulus : r);
  }

  private static BigInteger Mod(BigInteger value, BigInteger modulus) {
    if (modulus == 1)
      return BigInteger.Zero;
    var result = value % modulus;
    return result.Sign < 0 ? result + modulus : result;
  }

  private static BigInteger ModInverse(BigInteger value, BigInteger modulus) {
    if (modulus == 1)
      return BigInteger.Zero;
    var t = BigInteger.Zero;
    var newT = BigInteger.One;
    var r = modulus;
    var newR = Mod(value, modulus);
    while (newR != 0) {
      var q = r / newR;
      (t, newT) = (newT, t - q * newT);
      (r, newR) = (newR, r - q * newR);
    }
    return Mod(t, modulus);
  }
}
