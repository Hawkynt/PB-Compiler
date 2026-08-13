namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>
/// A closed integer interval <c>[Lo, Hi]</c> over the value a program computes - the range half of
/// the direct emitter's O16 lattice (<c>CodeGen/IntervalRange.cs</c>), restated for the IR.
///
/// <para>
/// Values are held in the <b>mathematical</b> space the type denotes, not as bit patterns: an
/// unsigned <c>i16</c> holding 40000 has the range <c>[40000, 40000]</c>, not <c>[-25536, -25536]</c>.
/// That is what makes <see cref="OfType"/> the honest starting point for anything unknown, and it is
/// why the unsigned compare predicates below insist on a non-negative range before deciding anything:
/// the unsigned reading of a signed interval that straddles zero is not an interval at all.
/// </para>
///
/// <para>
/// Every operation <b>over</b>-approximates. Anything that cannot be bounded exactly answers
/// <see cref="Top"/>, so a consumer that acts only when the whole interval qualifies stays sound -
/// which is the entire safety argument for eliding a runtime trap.
/// </para>
/// </summary>
public readonly record struct ValueRange(long Lo, long Hi) {

  /// <summary>Unknown - the full 64-bit range.</summary>
  public static readonly ValueRange Top = new(long.MinValue, long.MaxValue);

  /// <summary>Unreachable - the empty set, the identity of <see cref="Join"/>.</summary>
  public static readonly ValueRange Bottom = new(long.MaxValue, long.MinValue);

  public bool IsTop => this.Lo == long.MinValue && this.Hi == long.MaxValue;
  public bool IsEmpty => this.Lo > this.Hi;
  public bool Contains(long v) => v >= this.Lo && v <= this.Hi;

  public static ValueRange Of(long c) => new(c, c);

  /// <summary>
  /// The whole set of values a variable of this type can hold. 64-bit integers answer
  /// <see cref="Top"/> in both signednesses: <c>QWORD</c>'s upper end does not fit a <c>long</c>, and
  /// pretending otherwise would be the one place this lattice could report a range tighter than the
  /// truth.
  /// </summary>
  public static ValueRange OfType(IrType type) {
    if (!type.IsInteger || type.Bits >= 64)
      return Top;
    if (type.Bits == 1)
      return new(0, 1);
    var span = 1L << type.Bits;
    return type.Signed ? new(-(span / 2), span / 2 - 1) : new(0, span - 1);
  }

  /// <summary>The convex hull - the lattice join. Bottom is absorbed, so an unvisited edge costs nothing.</summary>
  public ValueRange Join(ValueRange o) {
    if (this.IsEmpty) return o;
    if (o.IsEmpty) return this;
    return new(Math.Min(this.Lo, o.Lo), Math.Max(this.Hi, o.Hi));
  }

  /// <summary>The intersection - how a branch condition and a type bound are folded into what is known.</summary>
  public ValueRange Meet(ValueRange o) => new(Math.Max(this.Lo, o.Lo), Math.Min(this.Hi, o.Hi));

  private static ValueRange Hull(params long[] xs) => new(xs.Min(), xs.Max());

  public ValueRange Add(ValueRange o) {
    if (this.IsEmpty || o.IsEmpty) return Bottom;
    if (this.IsTop || o.IsTop) return Top;
    try { return new(checked(this.Lo + o.Lo), checked(this.Hi + o.Hi)); }
    catch (OverflowException) { return Top; }
  }

  public ValueRange Subtract(ValueRange o) {
    if (this.IsEmpty || o.IsEmpty) return Bottom;
    if (this.IsTop || o.IsTop) return Top;
    try { return new(checked(this.Lo - o.Hi), checked(this.Hi - o.Lo)); }
    catch (OverflowException) { return Top; }
  }

  public ValueRange Negate() {
    if (this.IsEmpty) return Bottom;
    if (this.IsTop) return Top;
    try { return new(checked(-this.Hi), checked(-this.Lo)); }
    catch (OverflowException) { return Top; }
  }

  public ValueRange Multiply(ValueRange o) {
    if (this.IsEmpty || o.IsEmpty) return Bottom;
    if (this.IsTop || o.IsTop) return Top;
    try {
      return Hull(checked(this.Lo * o.Lo), checked(this.Lo * o.Hi),
                  checked(this.Hi * o.Lo), checked(this.Hi * o.Hi));
    } catch (OverflowException) { return Top; }
  }

  /// <summary>
  /// Truncated division, which is monotonic in the dividend for a fixed non-zero divisor. Top when
  /// the divisor interval straddles zero - not only because the quotient is unbounded there but
  /// because the division itself would trap, and a range that answered anyway would be the fact that
  /// removed the guard.
  /// </summary>
  public ValueRange Divide(ValueRange o) {
    if (this.IsEmpty || o.IsEmpty) return Bottom;
    if (this.IsTop || o.IsTop || o.Contains(0)) return Top;
    try {
      return Hull(checked(this.Lo / o.Lo), checked(this.Lo / o.Hi),
                  checked(this.Hi / o.Lo), checked(this.Hi / o.Hi));
    } catch (OverflowException) { return Top; }
  }

  /// <summary>
  /// Truncated remainder: <c>|result| &lt; |divisor|</c> and the result takes the dividend's sign, so
  /// it lies in <c>[-(|k|-1), |k|-1]</c>, tightened to <c>[0, |k|-1]</c> for a provably non-negative
  /// dividend. Only a constant non-zero divisor is modelled.
  /// </summary>
  public ValueRange Remainder(ValueRange o) {
    if (this.IsEmpty || o.IsEmpty) return Bottom;
    if (o.Lo != o.Hi || o.Lo == 0 || o.Lo == long.MinValue) return Top;
    var bound = Math.Abs(o.Lo) - 1;
    return this.Lo >= 0 ? new(0, bound) : new(-bound, bound);
  }

  /// <summary>
  /// Bitwise AND. <b>One</b> non-negative operand is enough to bound the result, and that asymmetry
  /// is the whole value of the rule: the result's bits are a subset of that operand's, so it cannot
  /// exceed that operand's maximum and its sign bit is clear - however unknown the other side is.
  ///
  /// <para>
  /// It is why <c>a(x AND 7)</c> needs no bounds check whatever <c>x</c> holds, and - less obviously,
  /// and worth a great deal more - it is what decides the signed overflow trap. The lowering asks that
  /// one as <c>(~(l^r) &amp; (sum^l)) &lt; 0</c>, a fact about three correlated values that no
  /// interval can reconstruct from the tree; but when <c>l</c> and <c>r</c> are known non-negative and
  /// small, <c>sum^l</c> is bounded and non-negative on its own, this rule carries that through the
  /// <c>AND</c>, and the comparison against zero falls out. An earlier attempt matched the sign rule
  /// syntactically instead and broke on the first thing <c>instcombine</c> did to it.
  /// </para>
  /// </summary>
  public ValueRange And(ValueRange o) {
    if (this.IsEmpty || o.IsEmpty) return Bottom;
    if (this.Lo >= 0 && o.Lo >= 0) return new(0, Math.Min(this.Hi, o.Hi));
    if (o.Lo >= 0) return new(0, o.Hi);
    if (this.Lo >= 0) return new(0, this.Hi);
    return Top;
  }

  /// <summary>
  /// OR and XOR over two non-negative operands: the result cannot exceed the next power of two above
  /// the larger endpoint, because no bit above that is set in either side.
  /// </summary>
  public ValueRange MergeBits(ValueRange o) {
    if (this.IsEmpty || o.IsEmpty) return Bottom;
    if (this.Lo < 0 || o.Lo < 0) return Top;
    var saturated = Math.Max(this.Hi, o.Hi);
    saturated |= saturated >> 1; saturated |= saturated >> 2; saturated |= saturated >> 4;
    saturated |= saturated >> 8; saturated |= saturated >> 16; saturated |= saturated >> 32;
    return new(0, saturated);
  }

  /// <summary>A left shift by a known count is a multiply by a power of two.</summary>
  public ValueRange ShiftLeft(long count)
    => count is < 0 or > 62 ? Top : this.Multiply(Of(1L << (int)count));

  /// <summary>An arithmetic right shift by a known count, monotonic in the shifted value.</summary>
  public ValueRange ShiftRightArithmetic(long count) {
    if (this.IsEmpty) return Bottom;
    if (this.IsTop || count is < 0 or > 62) return Top;
    return new(this.Lo >> (int)count, this.Hi >> (int)count);
  }

  /// <summary>A logical right shift, which only says anything when the value is provably non-negative.</summary>
  public ValueRange ShiftRightLogical(long count) {
    if (this.IsEmpty) return Bottom;
    if (this.Lo < 0 || count is < 0 or > 62) return Top;
    return new(this.Lo >> (int)count, this.Hi >> (int)count);
  }

  /// <summary>
  /// What survives a store into <paramref name="type"/>: the range when it fits, and
  /// <see cref="OfType"/> when it does not, because a value that overflows the type wraps to
  /// something this lattice cannot name.
  /// </summary>
  public ValueRange Fit(IrType type) {
    if (this.IsEmpty) return Bottom;
    var whole = OfType(type);
    return this.Lo >= whole.Lo && this.Hi <= whole.Hi ? this : whole;
  }

  /// <summary>
  /// Interval widening: an endpoint that grew jumps to the type's own bound rather than to infinity.
  /// Stopping at the type is what keeps a loop counter useful - <c>[1, 32767]</c> still proves an
  /// <c>INTEGER</c> subscript non-negative, where <c>[1, +inf]</c> would go on to claim the sum of
  /// two of them fits, which it does not.
  /// </summary>
  public ValueRange Widen(ValueRange candidate, IrType type) {
    if (this.IsEmpty) return candidate;
    if (candidate.IsEmpty) return this;
    var whole = OfType(type);
    return new(candidate.Lo < this.Lo ? whole.Lo : this.Lo,
               candidate.Hi > this.Hi ? whole.Hi : this.Hi);
  }

  public override string ToString() => this.IsEmpty ? "[]" : this.IsTop ? "[top]" : $"[{this.Lo}, {this.Hi}]";
}
