namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Recovers the DISPATCH a chain of comparisons is: a run of blocks that each test one integer value
/// against constants and branch becomes a single <see cref="IrSwitch"/>, which is the only form a back
/// end can turn into a table, a hash or a mask.
///
/// <para>
/// It exists because the lowering never emits a switch for <c>SELECT CASE</c>. Every arm becomes its
/// own block with its own <c>icmp</c>/<c>or</c> tree, which is a faithful rendering of what the source
/// says and a complete loss of what it MEANS - by the time selection sees it there is no statement
/// left, only twelve compares in six blocks. The direct emitter never had this problem because it
/// reads the AST, where the arms are still one construct; the IR path has to put the construct back
/// together, and that is all this pass does. It chooses no dispatch shape: which of a jump table, a
/// byte-index table, a perfect hash, a decision tree or a membership mask is right depends on the
/// target's addressing modes and on the optimization objective, so it is decided in
/// <c>Backend/InstructionSelector.cs</c> where both are known.
/// </para>
///
/// <para>
/// <b>What it recognizes.</b> A branch condition is read as the SET of subject values that make it
/// true, over closed intervals: <c>x = k</c> is <c>{k}</c>, <c>x &lt;&gt; k</c> its complement,
/// <c>x &gt;= lo</c> and <c>x &lt;= hi</c> are half-lines, and <c>OR</c>/<c>AND</c> are union and
/// intersection. That single reading covers all three spellings the corpus uses - a value list
/// (<c>CASE 1, 8, 15</c>), a range (<c>CASE 0 TO 9</c>, whose two signed compares intersect to one
/// interval) and the <c>IF k = 1 OR k = 8</c> / <c>IF k &lt;&gt; 2 AND k &lt;&gt; 5</c> pair, which are
/// De Morgan complements of each other and need no case of their own. PB's own truth spelling is read
/// through: a comparison materializes as <c>sext i1</c> to <c>-1</c>/<c>0</c> and the arms combine with
/// integer <c>OR</c>, so the pass accepts <c>x != 0</c> over a tree of sign-extended comparisons as the
/// same boolean it would have accepted at <c>i1</c>.
/// </para>
///
/// <para>
/// <b>Why the set is sound.</b> Only SIGNED predicates against compile-time constants are read, and
/// every constant is normalized to the signed value of its low <c>N</c> bits first, so one domain
/// orders every bound. <c>AND</c> may only be intersected because both operands are known to be
/// all-ones-or-zero: the recursion's base cases are a comparison and a sign-extension of one, and
/// <c>OR</c>/<c>AND</c> of two such values is another, so the invariant holds by induction. Without it
/// <c>a AND b</c> being nonzero would not mean both are (1 AND 2 is 0).
/// </para>
///
/// <para>
/// <b>What it refuses.</b> A chain is only absorbed through a block that is reached from the chain
/// ALONE, has no phis, whose every instruction is pure and used only there, and whose address nothing
/// has taken - anything else is a block with a life of its own, and folding it into the dispatch would
/// delete code that something still reaches. Fewer than three distinct values is left alone on
/// purpose: two compares are already the cheapest dispatch there is, and rewriting them would churn
/// every <c>IF a = 1 OR a = 2</c> in the corpus for nothing. The enumerated side is capped at 256
/// values, which is also the widest span the back end's dense table covers, so a range over a whole
/// 16-bit type is left as the two compares it already is. Floating-point and string subjects never
/// arrive here: their comparisons are not integer predicates against constants.
/// </para>
///
/// <para>
/// Case order is source order and the first case naming a value wins, which is what
/// <see cref="IrSwitch.TargetFor"/> already promises and what <c>SELECT CASE</c> means - so an arm that
/// repeats an earlier arm's value stays unreachable rather than becoming reachable.
/// </para>
/// </summary>
public static class SwitchFormation {

  /// <summary>
  /// The widest set the pass will enumerate. It is the direct emitter's own dense-table span limit:
  /// past it no table is built, so turning the compares into 300 cases would only make the back end
  /// write the chain back out again - longer, and through more blocks.
  /// </summary>
  private const int _MAX_VALUES = 256;

  /// <summary>
  /// Below this a switch buys nothing. Two equality compares ARE the dispatch a two-case switch
  /// selects into, and three is the smallest set the membership mask - the cheapest shape that is not
  /// a compare per value - will take.
  /// </summary>
  private const int _MIN_VALUES = 3;

  /// <summary>Forms switches in <paramref name="fn"/>; the number formed.</summary>
  public static int Run(IrFunction fn) {
    ArgumentNullException.ThrowIfNull(fn);
    if (fn.Entry is null)
      return 0;

    var formed = 0;
    var addressed = fn.AddressTakenBlocks();
    foreach (var block in fn.Blocks.ToList())
      if (block.Parent is not null && TryFormAt(fn, block, addressed))
        ++formed;
    return formed;
  }

  /// <summary>Forms one switch out of the dispatch chain starting at <paramref name="head"/>, if there is one.</summary>
  private static bool TryFormAt(IrFunction fn, IrBasicBlock head, HashSet<IrBasicBlock> addressed) {
    if (head.Terminator is not IrCondBr)
      return false;

    IrValue? subject = null;
    var cases = new List<(long Value, IrBasicBlock Target)>();
    var claimed = new HashSet<long>();
    var absorbed = new List<IrBasicBlock>();
    IrBasicBlock? fallthrough = null;

    for (var cursor = head; ;) {
      if (cursor.Terminator is not IrCondBr branch || Evaluate(branch.Condition, ref subject) is not { } set) {
        fallthrough = cursor;                        // the chain ends here: everything left goes to this block
        break;
      }

      // the true side when it is the small side, the false side when the test is an EXCLUSION - either
      // way the enumerated values are the ones a table can name and the other side is the default
      var (values, target, rest) = set.Count() <= _MAX_VALUES
        ? (set, branch.IfTrue, branch.IfFalse)
        : (set.Complement(), branch.IfFalse, branch.IfTrue);
      if (values.Count() > _MAX_VALUES || cases.Count + values.Count() > _MAX_VALUES) {
        fallthrough = cursor;
        break;
      }

      // only NOW is this block's test part of the switch, and only now may the block go. Recording it
      // when the walk stepped into it instead deleted the block the switch had just made its default:
      // a `CASE IS > 1000` arm evaluates fine and is then rejected for being 31767 values wide, so the
      // walk ends ON it - and it had already been counted as consumed.
      if (!ReferenceEquals(cursor, head))
        absorbed.Add(cursor);
      foreach (var value in values.Values())
        if (claimed.Add(value))
          cases.Add((value, target));

      // an exclusion has answered for every value there is, so there is no chain left to walk
      if (!ReferenceEquals(target, branch.IfTrue) || !Absorbable(rest, cursor, addressed)) {
        fallthrough = rest;
        break;
      }
      cursor = rest;
    }

    if (fallthrough is null || claimed.Count < _MIN_VALUES || subject is null)
      return false;
    // a phi in a target would have to be told that its incoming edge now comes from the dispatch block
    // instead of the absorbed one; refusing is cheaper than a rename that has to stay right when two
    // absorbed blocks reach the same target
    if (absorbed.Count > 0
        && cases.Select(c => c.Target).Append(fallthrough).Distinct().Any(t => t.Phis.Any()))
      return false;

    var dispatch = new IrSwitch(subject, fallthrough);
    foreach (var (value, target) in cases)
      dispatch.AddCase(value, target);
    head.Terminator!.EraseFromParent();
    head.Append(dispatch);

    foreach (var block in absorbed) {
      foreach (var instruction in block.Instructions.Reverse().ToList())
        instruction.EraseFromParent();
      fn.RemoveBlock(block);
    }
    return true;
  }

  /// <summary>
  /// Whether <paramref name="block"/> is nothing but the next test of the chain <paramref name="from"/>
  /// belongs to - reached from there alone, phi-free, address-free, and computing only pure values that
  /// nothing outside it reads.
  /// </summary>
  private static bool Absorbable(IrBasicBlock block, IrBasicBlock from, HashSet<IrBasicBlock> addressed) {
    if (ReferenceEquals(block, from) || addressed.Contains(block) || block.Phis.Any())
      return false;
    if (block.Terminator is not IrCondBr)
      return false;

    var predecessors = block.Predecessors.ToList();
    if (predecessors.Count != 1 || !ReferenceEquals(predecessors[0], from))
      return false;

    foreach (var instruction in block.Instructions) {
      if (instruction.IsTerminator)
        continue;
      if (instruction is not (IrCmp or IrCast or IrBinary))
        return false;                                // anything else may read or write something
      foreach (var user in instruction.Users)
        if (!ReferenceEquals(user.Parent, block))
          return false;                              // the value outlives the block, so the block must too
    }
    return true;
  }

  /// <summary>
  /// The set of <paramref name="subject"/> values for which <paramref name="condition"/> is NONZERO, or
  /// null when the condition is not a test of one integer value against constants. The subject is
  /// discovered on the first leaf and every later leaf must name the same value.
  /// </summary>
  private static ValueSet? Evaluate(IrValue condition, ref IrValue? subject) {
    switch (condition) {
      // PB's own truth value: `x <> 0` over a tree of sign-extended comparisons is that tree's answer,
      // and `x = 0` is its negation. Read before the ordinary compare below, or the tree itself would
      // be taken for the subject.
      case IrCmp { Pred: IrCmpPred.Ne or IrCmpPred.Eq } wrapper
        when AgainstZero(wrapper) is { } inner && Evaluate(inner, ref subject) is { } answer:
        return wrapper.Pred == IrCmpPred.Ne ? answer : answer.Complement();
      case IrCmp compare:
        return Leaf(compare, ref subject);
      case IrCast { Op: IrCastOp.SExt or IrCastOp.ZExt } widened:
        return Evaluate(widened.Value, ref subject);
      case IrBinary { Op: IrBinaryOp.Or } either:
        return Combine(either, ref subject, union: true);
      case IrBinary { Op: IrBinaryOp.And } both:
        return Combine(both, ref subject, union: false);
      default:
        return null;
    }
  }

  private static ValueSet? Combine(IrBinary node, ref IrValue? subject, bool union) {
    if (Evaluate(node.Lhs, ref subject) is not { } left || Evaluate(node.Rhs, ref subject) is not { } right)
      return null;
    return union ? left.Union(right) : left.Intersect(right);
  }

  /// <summary>The other side of a comparison against the integer zero, or null.</summary>
  private static IrValue? AgainstZero(IrCmp compare) {
    if (compare.Rhs is IrConstantInt { Value: 0 })
      return compare.Lhs;
    return compare.Lhs is IrConstantInt { Value: 0 } ? compare.Rhs : null;
  }

  /// <summary>One <c>subject RELATION constant</c> leaf as a value set, unifying the subject.</summary>
  private static ValueSet? Leaf(IrCmp compare, ref IrValue? subject) {
    var (value, constant, pred) = compare.Rhs is IrConstantInt right
      ? (compare.Lhs, right, compare.Pred)
      : compare.Lhs is IrConstantInt left ? (compare.Rhs, left, Mirrored(compare.Pred)) : (null, null, compare.Pred);
    if (value is null || constant is null || value is IrConstantInt)
      return null;
    if (!value.Type.IsInteger || value.Type.Bits is not (8 or 16 or 32))
      return null;
    if (subject is null)
      subject = value;
    else if (!ReferenceEquals(subject, value))
      return null;                                   // two different variables are not one dispatch

    var bits = value.Type.Bits;
    var (min, max) = Domain(bits);
    // equality is a question about BITS, so an unsigned spelling of a negative pattern (65535 for -1)
    // names the same case; an ordering bound outside the signed domain is not one this reading covers
    var bound = Normalize(constant.Value, bits);
    if (pred is not (IrCmpPred.Eq or IrCmpPred.Ne) && bound != constant.Value)
      return null;

    return pred switch {
      IrCmpPred.Eq => ValueSet.Of(bits, (bound, bound)),
      IrCmpPred.Ne => ValueSet.Of(bits, (bound, bound)).Complement(),
      IrCmpPred.Slt => bound > min ? ValueSet.Of(bits, (min, bound - 1)) : ValueSet.Empty(bits),
      IrCmpPred.Sle => bound >= min ? ValueSet.Of(bits, (min, bound)) : ValueSet.Empty(bits),
      IrCmpPred.Sgt => bound < max ? ValueSet.Of(bits, (bound + 1, max)) : ValueSet.Empty(bits),
      IrCmpPred.Sge => bound <= max ? ValueSet.Of(bits, (bound, max)) : ValueSet.Empty(bits),
      _ => null,                                     // unsigned and float predicates: not this reading
    };
  }

  /// <summary>The same question with the sides swapped - the mirror, not the negation.</summary>
  private static IrCmpPred Mirrored(IrCmpPred pred) => pred switch {
    IrCmpPred.Slt => IrCmpPred.Sgt,
    IrCmpPred.Sgt => IrCmpPred.Slt,
    IrCmpPred.Sle => IrCmpPred.Sge,
    IrCmpPred.Sge => IrCmpPred.Sle,
    _ => pred,
  };

  private static (long Min, long Max) Domain(int bits) => (-(1L << (bits - 1)), (1L << (bits - 1)) - 1);

  /// <summary>The signed value of the low <paramref name="bits"/> bits - the one domain every bound is ordered in.</summary>
  private static long Normalize(long value, int bits) {
    var mask = (1L << bits) - 1;
    var sign = 1L << (bits - 1);
    return ((value & mask) ^ sign) - sign;
  }

  /// <summary>
  /// A set of subject values as sorted, disjoint, non-adjacent closed intervals over the subject's
  /// signed domain. Intervals rather than a value list because a <c>CASE lo TO hi</c> arm is one, and
  /// because the complement of a small exclusion set is otherwise 65533 elements to carry around.
  /// </summary>
  private sealed class ValueSet {

    private readonly int _bits;
    private readonly List<(long Lo, long Hi)> _intervals;

    private ValueSet(int bits, List<(long Lo, long Hi)> intervals) {
      this._bits = bits;
      this._intervals = intervals;
    }

    public static ValueSet Empty(int bits) => new(bits, []);

    public static ValueSet Of(int bits, params (long Lo, long Hi)[] intervals)
      => new(bits, Normalized(intervals));

    /// <summary>How many values the set holds, saturated - a whole-domain set must not overflow a count.</summary>
    public long Count() {
      var total = 0L;
      foreach (var (lo, hi) in this._intervals) {
        total += hi - lo + 1;
        if (total > _MAX_VALUES)
          return total;
      }
      return total;
    }

    public IEnumerable<long> Values() {
      foreach (var (lo, hi) in this._intervals)
        for (var value = lo; ; ++value) {
          yield return value;
          if (value == hi)
            break;                                   // ...and not `value <= hi`, which never ends at long.MaxValue
        }
    }

    public ValueSet Union(ValueSet other) => new(this._bits, Normalized([.. this._intervals, .. other._intervals]));

    public ValueSet Intersect(ValueSet other) {
      var result = new List<(long Lo, long Hi)>();
      foreach (var (lo, hi) in this._intervals)
        foreach (var (otherLo, otherHi) in other._intervals) {
          var low = Math.Max(lo, otherLo);
          var high = Math.Min(hi, otherHi);
          if (low <= high)
            result.Add((low, high));
        }
      return new(this._bits, Normalized([.. result]));
    }

    public ValueSet Complement() {
      var (min, max) = Domain(this._bits);
      var result = new List<(long Lo, long Hi)>();
      var next = min;
      foreach (var (lo, hi) in this._intervals) {
        if (lo > next)
          result.Add((next, lo - 1));
        next = hi + 1;                               // hi < max is guaranteed below, so this cannot overflow
        if (next > max)
          return new(this._bits, result);
      }
      result.Add((next, max));
      return new(this._bits, result);
    }

    /// <summary>Sorts, merges overlapping and adjacent intervals, and drops empty ones.</summary>
    private static List<(long Lo, long Hi)> Normalized((long Lo, long Hi)[] intervals) {
      var sorted = intervals.Where(i => i.Lo <= i.Hi).OrderBy(i => i.Lo).ThenBy(i => i.Hi).ToList();
      var merged = new List<(long Lo, long Hi)>();
      foreach (var (lo, hi) in sorted)
        if (merged.Count > 0 && lo <= merged[^1].Hi + 1)
          merged[^1] = (merged[^1].Lo, Math.Max(merged[^1].Hi, hi));
        else
          merged.Add((lo, hi));
      return merged;
    }
  }
}
