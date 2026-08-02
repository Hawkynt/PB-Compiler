namespace PowerBasic.Compiler.CodeGen;

/// <summary>
/// O16 bit lattice: which bits of a value are provably 1 and which are provably 0. It answers the
/// questions an interval structurally cannot - <c>(n \ 2) * 2</c> spans almost the whole type, but
/// its low bit is always 0, so it can never equal 1; <c>n AND 12</c> lies in [0,12], yet 5 is
/// impossible because bit 0 must be 0.
///
/// A bit set in neither mask is unknown. A bit set in both would mean "provably 1 and provably 0",
/// i.e. unreachable code; the transfer functions never produce it, and <see cref="Unknown"/> is
/// the safe answer whenever an operation is not modelled - so every consumer sees an
/// over-approximation of the possible values, exactly like the interval domain.
///
/// Unlike an interval, these facts survive two's-complement wrapping: wrapping is arithmetic
/// modulo 2^n, which leaves the low n bits untouched. A dialect that wraps its integer arithmetic
/// in place therefore keeps every low-bit fact, and only the bits at or above the type's width
/// have to be discarded (see <see cref="Narrow"/>).
/// </summary>
public readonly record struct KnownBits(ulong Ones, ulong Zeros) {

  /// <summary>Nothing is known.</summary>
  public static readonly KnownBits Unknown = new(0, 0);

  /// <summary>Every bit of a compile-time constant is known, within the type's width.</summary>
  public static KnownBits Of(long value, int width) {
    var mask = Mask(width);
    var bits = unchecked((ulong)value) & mask;
    return new(bits, ~bits & mask);
  }

  /// <summary>The low <paramref name="width"/> bits; width 0 or 64 means the whole word.</summary>
  private static ulong Mask(int width) => width is <= 0 or >= 64 ? ulong.MaxValue : (1UL << width) - 1;

  /// <summary>True when no bit is known - the lattice's top element.</summary>
  public bool IsUnknown => this.Ones == 0 && this.Zeros == 0;

  /// <summary>Drops everything at or above <paramref name="width"/>: a narrower type does not carry those bits.</summary>
  public KnownBits Narrow(int width) {
    var mask = Mask(width);
    return new(this.Ones & mask, this.Zeros & mask);
  }

  /// <summary>
  /// True when <paramref name="candidate"/> is consistent with what is known - the query every
  /// consumer actually asks. A false answer proves the value can never be that one.
  /// </summary>
  public bool Allows(long candidate, int width) {
    var mask = Mask(width);
    var bits = unchecked((ulong)candidate) & mask;
    return (bits & this.Zeros & mask) == 0            // no bit that must be 0 is set
        && (~bits & this.Ones & mask) == 0;           // no bit that must be 1 is clear
  }

  /// <summary>The number of low bits known to be 0 - the value is a multiple of 2^this.</summary>
  public int TrailingZeros {
    get {
      var n = 0;
      while (n < 64 && (this.Zeros & (1UL << n)) != 0)
        ++n;
      return n;
    }
  }

  /// <summary>The number of low bits whose value is known either way, counted from bit 0.</summary>
  private int KnownLowBits {
    get {
      var known = this.Ones | this.Zeros;
      var n = 0;
      while (n < 64 && (known & (1UL << n)) != 0)
        ++n;
      return n;
    }
  }

  /// <summary>The value of those low bits.</summary>
  private ulong LowValue(int count) => count >= 64 ? this.Ones : this.Ones & ((1UL << count) - 1);

  #region transfer functions

  public KnownBits And(KnownBits o) => new(this.Ones & o.Ones, this.Zeros | o.Zeros);
  public KnownBits Or(KnownBits o) => new(this.Ones | o.Ones, this.Zeros & o.Zeros);

  public KnownBits Xor(KnownBits o) =>
    new((this.Ones & o.Zeros) | (this.Zeros & o.Ones), (this.Ones & o.Ones) | (this.Zeros & o.Zeros));

  public KnownBits Not() => new(this.Zeros, this.Ones);

  /// <summary>A left shift by a known count moves every fact up and fills the low bits with zeros.</summary>
  public KnownBits ShiftLeft(int count) =>
    count is < 0 or >= 64 ? Unknown : new(this.Ones << count, (this.Zeros << count) | ((1UL << count) - 1));

  /// <summary>
  /// A logical right shift moves the facts down and makes the top bits zero; an arithmetic one
  /// cannot claim those top bits without knowing the sign, so it leaves them unknown.
  /// </summary>
  public KnownBits ShiftRight(int count, int width, bool arithmetic) {
    if (count is < 0 or >= 64)
      return Unknown;
    var shifted = new KnownBits(this.Ones >> count, this.Zeros >> count);
    if (arithmetic || width is <= 0 or >= 64)
      return shifted;
    var vacated = Mask(width) & ~(Mask(width) >> count);      // the bits shifted in at the top
    return new(shifted.Ones, shifted.Zeros | vacated);
  }

  /// <summary>
  /// Addition and subtraction: the low bits of the result depend only on the low bits of the
  /// operands (a carry only ever propagates upward), so as many low bits as BOTH operands know
  /// are computable exactly. Everything above that is unknown.
  /// </summary>
  public KnownBits AddSub(KnownBits o, bool subtract) {
    var known = Math.Min(this.KnownLowBits, o.KnownLowBits);
    if (known == 0)
      return Unknown;
    var mask = known >= 64 ? ulong.MaxValue : (1UL << known) - 1;
    var value = (subtract ? this.LowValue(known) - o.LowValue(known) : this.LowValue(known) + o.LowValue(known)) & mask;
    return new(value & mask, ~value & mask);
  }

  /// <summary>
  /// Multiplication: the trailing zeros add, which is what makes <c>x * 4</c> provably a multiple
  /// of four. When one side is a fully known constant the shift is exact for its power-of-two
  /// factor, and the odd part contributes nothing beyond that.
  /// </summary>
  public KnownBits Multiply(KnownBits o, int width) {
    var zeros = this.TrailingZeros + o.TrailingZeros;
    if (zeros <= 0)
      return Unknown;
    if (zeros >= 64)
      return Of(0, width);
    return new(0, (1UL << zeros) - 1);
  }

  /// <summary>
  /// The values common to both sides of a merge: a bit is only known where both agree on it -
  /// the lattice join.
  /// </summary>
  public KnownBits Join(KnownBits o) => new(this.Ones & o.Ones, this.Zeros & o.Zeros);

  #endregion
}

/// <summary>
/// O16 congruence lattice: <c>v = Residue (mod Modulus)</c> - the domain that knows
/// <c>x * 10</c> is always a multiple of ten. Neither of its neighbours can express that: an
/// interval says nothing about divisibility, and <see cref="KnownBits"/> only ever sees moduli
/// that are powers of two (trailing zeros). Together the three answer very different questions
/// about the same value.
///
/// <see cref="Modulus"/> 0 means the value is exactly <see cref="Residue"/>; 1 means nothing is
/// known. Otherwise the residue is normalized to [0, Modulus).
/// </summary>
public readonly record struct Congruence(long Modulus, long Residue) {

  /// <summary>Nothing is known - every value is congruent to 0 modulo 1.</summary>
  public static readonly Congruence Unknown = new(1, 0);

  /// <summary>An exactly known value.</summary>
  public static Congruence Of(long value) => new(0, value);

  public bool IsUnknown => this.Modulus == 1;
  public bool IsExact => this.Modulus == 0;

  private static long Gcd(long a, long b) {
    a = Math.Abs(a);
    b = Math.Abs(b);
    while (b != 0)
      (a, b) = (b, a % b);
    return a;
  }

  /// <summary>Normalizes to a canonical (modulus, residue) pair; a modulus of 0 or 1 stays as it is.</summary>
  private static Congruence Make(long modulus, long residue) {
    modulus = Math.Abs(modulus);
    if (modulus == 0)
      return new(0, residue);
    if (modulus == 1)
      return Unknown;
    var r = residue % modulus;
    return new(modulus, r < 0 ? r + modulus : r);
  }

  /// <summary>True when <paramref name="candidate"/> is consistent with the congruence - a false answer proves the value can never be that one.</summary>
  public bool Allows(long candidate) {
    if (this.IsExact)
      return candidate == this.Residue;
    if (this.IsUnknown)
      return true;
    var r = candidate % this.Modulus;
    return (r < 0 ? r + this.Modulus : r) == this.Residue;
  }

  /// <summary>True when the value is provably a multiple of <paramref name="factor"/>.</summary>
  public bool IsMultipleOf(long factor) {
    if (factor is 0 or 1 or -1)
      return true;
    if (this.IsExact)
      return this.Residue % factor == 0;
    return !this.IsUnknown && this.Modulus % factor == 0 && this.Residue % factor == 0;
  }

  #region transfer functions

  public Congruence Add(Congruence o) => this.AddSub(o, subtract: false);
  public Congruence Subtract(Congruence o) => this.AddSub(o, subtract: true);

  private Congruence AddSub(Congruence o, bool subtract) {
    var other = subtract ? -o.Residue : o.Residue;
    if (this.IsExact && o.IsExact)
      return Of(this.Residue + other);
    // a sum is congruent modulo the gcd of the two moduli (an exact value has "modulus infinity",
    // so it contributes only its residue)
    var m = this.IsExact ? o.Modulus : o.IsExact ? this.Modulus : Gcd(this.Modulus, o.Modulus);
    return Make(m, this.Residue + other);
  }

  public Congruence Negate() => this.IsExact ? Of(-this.Residue) : Make(this.Modulus, -this.Residue);

  /// <summary>
  /// Multiplication. The everyday case is one side being a constant: <c>v * c</c> multiplies both
  /// the modulus and the residue by it, so an unknown value times ten is exactly "a multiple of
  /// ten". The general case takes the gcd of the three products, as the classical domain does.
  /// </summary>
  public Congruence Multiply(Congruence o) {
    if (this.IsExact && o.IsExact)
      return Of(this.Residue * o.Residue);
    if (o.IsExact)
      return Make(this.Modulus * o.Residue, this.Residue * o.Residue);
    if (this.IsExact)
      return Make(o.Modulus * this.Residue, o.Residue * this.Residue);
    return Make(Gcd(this.Modulus * o.Modulus, Gcd(this.Modulus * o.Residue, o.Modulus * this.Residue)),
      this.Residue * o.Residue);
  }

  /// <summary>The join: what survives a merge is the coarsest congruence both sides satisfy.</summary>
  public Congruence Join(Congruence o) {
    if (this.IsExact && o.IsExact)
      return this.Residue == o.Residue ? this : Make(Gcd(this.Residue - o.Residue, 0), this.Residue);
    var m = Gcd(this.IsExact ? 0 : this.Modulus, o.IsExact ? 0 : o.Modulus);
    return Make(Gcd(m, this.Residue - o.Residue), this.Residue);
  }

  #endregion
}
