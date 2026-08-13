namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>
/// What interval an integer SSA value is provably confined to - the IR's answer to the direct
/// emitter's O16 interval lattice (<c>CodeGen/IntervalRange.cs</c>).
///
/// <para>
/// The proofs it supplies are the ones <c>docs/BACKENDS.md</c> names as target-independent and
/// therefore owed to every back end: a subscript that cannot leave its dimension, a sum that cannot
/// overflow its type, a divisor that cannot be zero, a 32-bit value that fits sixteen bits. The
/// direct emitter derives them by walking the bound statement list; this derives them from SSA, which
/// is both simpler (a value has one definition) and stronger (the counter of a loop the front end
/// never called a FOR is still a phi).
/// </para>
///
/// <para><b>Two halves, and they answer different questions.</b></para>
/// <list type="number">
///   <item><b>A global fixpoint over the def-use graph.</b> Every instruction starts at
///   <see cref="ValueRange.Bottom"/> (optimistic), constants and type bounds seed the leaves, and the
///   blocks are swept in reverse postorder until nothing moves. A phi joins its incoming edges, so a
///   loop counter converges upward one iteration at a time - and is <b>widened</b> to its type's bound
///   after <see cref="_WIDEN_AFTER"/> sweeps, which is what makes the fixpoint terminate on a counter
///   whose limit lives in the loop's own test rather than in its definition.</item>
///   <item><b>A per-block refinement from dominating branches.</b> The fixpoint alone cannot see that
///   <c>i</c> is at most 10 inside a loop body guarded by <c>i &lt;= 10</c>, because that fact belongs
///   to an EDGE, not to the definition. <see cref="RangeAt"/> therefore re-evaluates an expression at
///   the block that uses it, intersecting each leaf with whatever the dominating conditional edges
///   prove about it. This is <c>CorrelatedValueProp</c> generalized from "equals a constant" to every
///   ordering predicate, and it is where nearly all the bounds-check elision actually comes from.</item>
/// </list>
///
/// <para>
/// <b>Everything over-approximates.</b> An unknown leaf is its type's whole range, an unmodelled
/// operation is <see cref="ValueRange.Top"/>, and a value read out of memory is never assumed to be
/// anything but its type. A consumer may act only when the whole interval qualifies - eliding a trap
/// that could fire is a silent miscompile, and no answer here is worth that.
/// </para>
/// </summary>
public sealed class IrRangeAnalysis {

  /// <summary>How many sweeps a value may grow before its moving endpoint jumps to the type's bound.</summary>
  private const int _WIDEN_AFTER = 3;

  /// <summary>How many sweeps the ascent may take before it stops regardless (widening bounds this,
  /// but a cap keeps a pathological CFG from being paid for).</summary>
  private const int _MAX_SWEEPS = 24;

  /// <summary>
  /// How many descending sweeps recover what widening gave away. Every one of them stays above the
  /// least fixpoint, so stopping early costs precision and never soundness - which is why this is a
  /// budget rather than a convergence requirement.
  /// </summary>
  private const int _NARROW_SWEEPS = 8;

  /// <summary>
  /// How deep <see cref="RangeAt"/> re-evaluates an expression before falling back on the global
  /// answer. Every step is a real IR operation, so this is far past anything a BASIC subscript or a
  /// checked add reaches, and it keeps a shared sub-expression from being re-proved exponentially.
  /// </summary>
  private const int _REFINE_DEPTH = 10;

  private readonly IrDominators _dom;
  private readonly Dictionary<IrValue, ValueRange> _global = new(ReferenceEqualityComparer.Instance);
  private readonly Dictionary<IrBasicBlock, List<(IrValue Value, IrCmpPred Pred, IrValue Against)>> _facts =
    new(ReferenceEqualityComparer.Instance);
  private bool _solved;

  private IrRangeAnalysis(IrDominators dom) {
    this._dom = dom;
    this.Solve();
    this._solved = true;
  }

  /// <summary>Builds the analysis for a function with a body; null for a declaration.</summary>
  public static IrRangeAnalysis? Build(IrFunction fn) {
    ArgumentNullException.ThrowIfNull(fn);
    return IrDominators.Build(fn) is { } dom ? new IrRangeAnalysis(dom) : null;
  }

  /// <summary>The dominator tree the refinements were derived from, so a consumer need not rebuild it.</summary>
  public IrDominators Dominators => this._dom;

  /// <summary>
  /// What <paramref name="value"/> is confined to anywhere in the function - no branch facts, so it is
  /// valid at every use. Use <see cref="RangeAt"/> when the question is about a particular block.
  /// </summary>
  public ValueRange RangeOf(IrValue value) {
    ArgumentNullException.ThrowIfNull(value);
    return this.Global(value);
  }

  /// <summary>
  /// What <paramref name="value"/> is confined to at <paramref name="block"/>, taking into account
  /// every conditional edge that dominates it. The stronger of the two answers and the one a
  /// check-elision decision wants.
  /// </summary>
  public ValueRange RangeAt(IrValue value, IrBasicBlock block) {
    ArgumentNullException.ThrowIfNull(value);
    ArgumentNullException.ThrowIfNull(block);
    return this.Refined(value, block, _REFINE_DEPTH);
  }

  /// <summary>
  /// Whether <paramref name="cmp"/> is decided for every value its operands can take at
  /// <paramref name="block"/>, and which way. Null when the ranges overlap - which is the answer for
  /// anything this must not act on.
  /// </summary>
  public bool? Decide(IrCmp cmp, IrBasicBlock block) {
    ArgumentNullException.ThrowIfNull(cmp);
    ArgumentNullException.ThrowIfNull(block);
    if (!cmp.Lhs.Type.IsInteger || !cmp.Rhs.Type.IsInteger)
      return null;                                   // a float compare has NaN, which no interval models
    return DecideFrom(cmp.Pred, this.Refined(cmp.Lhs, block, _REFINE_DEPTH), this.Refined(cmp.Rhs, block, _REFINE_DEPTH));
  }

  /// <summary>
  /// The predicate applied to two whole intervals: true when it holds for every pair, false when it
  /// holds for none, null when the intervals overlap in a way that leaves it open.
  ///
  /// <para>
  /// The unsigned predicates insist on two non-negative ranges. A signed interval that straddles zero
  /// is NOT an interval when read unsigned - <c>[-1, 1]</c> becomes <c>{0, 1, 0xFFFF}</c> - so
  /// answering from its endpoints would be exactly the unsound step this whole class exists to avoid.
  /// </para>
  /// </summary>
  private static bool? DecideFrom(IrCmpPred pred, ValueRange l, ValueRange r) {
    if (l.IsEmpty || r.IsEmpty || l.IsTop || r.IsTop)
      return null;
    switch (pred) {
      case IrCmpPred.Eq:
        if (l.Hi < r.Lo || r.Hi < l.Lo) return false;
        return l.Lo == l.Hi && r.Lo == r.Hi ? true : null;
      case IrCmpPred.Ne:
        if (l.Hi < r.Lo || r.Hi < l.Lo) return true;
        return l.Lo == l.Hi && r.Lo == r.Hi ? false : null;
      case IrCmpPred.Slt: return Order(l, r, strict: true);
      case IrCmpPred.Sle: return Order(l, r, strict: false);
      case IrCmpPred.Sgt: return Order(r, l, strict: true);
      case IrCmpPred.Sge: return Order(r, l, strict: false);
      case IrCmpPred.Ult: return l.Lo >= 0 && r.Lo >= 0 ? Order(l, r, strict: true) : null;
      case IrCmpPred.Ule: return l.Lo >= 0 && r.Lo >= 0 ? Order(l, r, strict: false) : null;
      case IrCmpPred.Ugt: return l.Lo >= 0 && r.Lo >= 0 ? Order(r, l, strict: true) : null;
      case IrCmpPred.Uge: return l.Lo >= 0 && r.Lo >= 0 ? Order(r, l, strict: false) : null;
      default: return null;                          // a float predicate
    }
  }

  /// <summary>True when every value of <paramref name="l"/> precedes every value of <paramref name="r"/>.</summary>
  private static bool? Order(ValueRange l, ValueRange r, bool strict) {
    if (strict ? l.Hi < r.Lo : l.Hi <= r.Lo) return true;
    if (strict ? l.Lo >= r.Hi : l.Lo > r.Hi) return false;
    return null;
  }

  #region the global fixpoint

  /// <summary>
  /// The optimistic fixpoint, in the textbook two phases.
  ///
  /// <para>
  /// <b>Ascending, with widening.</b> Every instruction starts at <see cref="ValueRange.Bottom"/> and
  /// grows. A cycle would grow forever, so after <see cref="_WIDEN_AFTER"/> sweeps a phi endpoint that
  /// is still moving jumps straight to its type's bound. Starting optimistically is only SOUND once
  /// the sweep has stopped moving - an interrupted optimistic solve leaves values claiming to be
  /// narrower than they are - so a solve that does not settle inside <see cref="_MAX_SWEEPS"/> throws
  /// its answers away and the analysis falls back on type bounds.
  /// </para>
  ///
  /// <para>
  /// <b>Descending, without it.</b> Widening is what makes the ascent terminate and it is also what
  /// makes the answer useless on its own, because it never takes anything back: <c>FOR i% = 1 TO 10</c>
  /// widens its counter to the whole of <c>INTEGER</c> the moment <c>i + 1</c> is evaluated without
  /// the loop's own test, and a counter that might be negative proves nothing about the subscript it
  /// indexes. A converged ascent is a post-fixpoint, so re-applying the transfer can only descend
  /// towards the least fixpoint and never below it - which is what recovers <c>[1, 11]</c>, and with
  /// it every bounds check in a counted loop.
  /// </para>
  /// </summary>
  private void Solve() {
    var order = this._dom.ReversePostorder;
    var ascended = false;
    for (var sweep = 0; sweep < _MAX_SWEEPS && !ascended; ++sweep) {
      var changed = false;
      foreach (var block in order)
        foreach (var instruction in block.Instructions) {
          if (!instruction.Type.IsInteger)
            continue;
          var before = this.Global(instruction);
          var after = this.Evaluate(instruction).Fit(instruction.Type);
          // Widening is applied to the phis only: they are the sole place a cycle can grow without
          // bound, and widening a straight-line value would throw away a range for nothing.
          if (sweep >= _WIDEN_AFTER && instruction is IrPhi)
            after = before.Widen(after, instruction.Type);
          if (after.Equals(before))
            continue;
          this._global[instruction] = after;
          changed = true;
        }
      ascended = !changed;
    }
    if (!ascended) {
      this._global.Clear();
      return;
    }

    for (var sweep = 0; sweep < _NARROW_SWEEPS; ++sweep) {
      var changed = false;
      foreach (var block in order)
        foreach (var instruction in block.Instructions) {
          if (!instruction.Type.IsInteger)
            continue;
          var after = this.Evaluate(instruction).Fit(instruction.Type);
          if (after.Equals(this._global.GetValueOrDefault(instruction, ValueRange.Top)))
            continue;
          this._global[instruction] = after;
          changed = true;
        }
      if (!changed)
        return;
    }
  }

  /// <summary>
  /// The range recorded for a value: an instruction's fixpoint estimate, a constant's own value, and
  /// its type's whole range for everything else (an argument, a global, a load, a value in an
  /// unreachable block).
  /// </summary>
  private ValueRange Global(IrValue value) {
    if (value is IrConstantInt c)
      return ValueRange.Of(c.Value).Fit(c.Type);
    if (this._global.TryGetValue(value, out var known))
      return known;
    // During the solve an unswept instruction is optimistically empty; once it is over, a value with
    // no answer has none, and the type is all that can honestly be claimed.
    return !this._solved && value is IrInstruction { Parent: not null }
      ? ValueRange.Bottom
      : ValueRange.OfType(value.Type);
  }

  /// <summary>One transfer step: what an instruction computes from what its operands are known to be.</summary>
  private ValueRange Evaluate(IrInstruction instruction) => instruction switch {
    IrPhi phi => this.EvaluatePhi(phi),
    IrCmp => new ValueRange(0, 1),
    IrCast cast => this.EvaluateCast(cast),
    IrBinary bin => this.EvaluateBinary(bin, this.Global(bin.Lhs), this.Global(bin.Rhs)),
    // a load, a call, a select over unknowns: nothing beyond the type
    _ => ValueRange.OfType(instruction.Type),
  };

  /// <summary>
  /// A phi joins its incoming values, and each is evaluated <b>at the edge it arrives on</b> rather
  /// than globally. That is not a refinement, it is the definition - the value flowing in from a
  /// predecessor is that operand as computed there - and it is what makes a loop counter converge to
  /// anything useful.
  ///
  /// <para>
  /// Worth writing down, because the plain version looks equivalent and is not. <c>FOR i% = 1 TO 10</c>
  /// closes its latch with <c>i + 1</c>, and evaluated globally that is <c>[2, 32768]</c> - which does
  /// not fit an <c>INTEGER</c>, so it widens to the whole type, the phi's lower bound goes with it, and
  /// the subscript the loop guards can no longer be shown non-negative. Evaluated at the latch, where
  /// the loop's own test has already proved <c>i &lt;= 10</c>, the same expression is <c>[2, 11]</c>
  /// and the counter stays <c>[1, 11]</c>.
  /// </para>
  /// </summary>
  private ValueRange EvaluatePhi(IrPhi phi) {
    var joined = ValueRange.Bottom;
    for (var i = 0; i < phi.Operands.Count; ++i) {
      var incoming = phi.GetOperand(i);
      var from = i < phi.IncomingBlocks.Count ? phi.IncomingBlocks[i] : null;
      joined = joined.Join(from is null
        ? this.Global(incoming)
        : this.Refined(incoming, from, _REFINE_DEPTH));
    }
    return joined;
  }

  private ValueRange EvaluateCast(IrCast cast) => Widened(cast, this.Global(cast.Value));

  /// <summary>
  /// What a conversion leaves of its operand's range.
  ///
  /// <para>
  /// Both widenings have a case where the value does NOT survive, and both are about the operand's
  /// signedness disagreeing with the conversion's. A <c>sext</c> of an UNSIGNED source reads its top
  /// bit as a sign, so a <c>WORD</c> holding 40000 comes out as -25536 - the source range says 40000
  /// and would be a lie. A <c>zext</c> of a SIGNED source is the mirror: -1 comes out as 65535. In each
  /// case the honest answer is the range the CONVERSION can produce from that width, which is still far
  /// tighter than the destination type.
  /// </para>
  /// </summary>
  private static ValueRange Widened(IrCast cast, ValueRange inner) {
    var source = cast.Value.Type;
    return cast.Op switch {
      IrCastOp.SExt when source.Signed => inner,
      IrCastOp.SExt => SignedSpan(source),
      IrCastOp.ZExt when inner.Lo >= 0 => inner,
      IrCastOp.ZExt => UnsignedSpan(source),
      // a truncation keeps the value only when it already fitted; otherwise it wrapped
      IrCastOp.Trunc => Fits(inner, cast.Type) ? inner : ValueRange.OfType(cast.Type),
      _ => ValueRange.OfType(cast.Type),
    };
  }

  /// <summary>Every value the sign-extension of a <paramref name="source"/>-wide bit pattern can take.</summary>
  private static ValueRange SignedSpan(IrType source)
    => source.IsInteger && source.Bits is > 0 and < 64
      ? new ValueRange(-(1L << (source.Bits - 1)), (1L << (source.Bits - 1)) - 1)
      : ValueRange.Top;

  /// <summary>Every value the zero-extension of a <paramref name="source"/>-wide bit pattern can take.</summary>
  private static ValueRange UnsignedSpan(IrType source)
    => source.IsInteger && source.Bits is > 0 and < 64
      ? new ValueRange(0, (1L << source.Bits) - 1)
      : ValueRange.Top;

  private static bool Fits(ValueRange range, IrType type) {
    var whole = ValueRange.OfType(type);
    return !range.IsEmpty && range.Lo >= whole.Lo && range.Hi <= whole.Hi;
  }

  private ValueRange EvaluateBinary(IrBinary bin, ValueRange l, ValueRange r) => bin.Op switch {
    IrBinaryOp.Add => l.Add(r),
    IrBinaryOp.Sub => l.Subtract(r),
    IrBinaryOp.Mul => l.Multiply(r),
    IrBinaryOp.SDiv => l.Divide(r),
    // an unsigned divide is the signed one once both sides are known non-negative, which is the only
    // case the range space can speak about at all
    IrBinaryOp.UDiv => l.Lo >= 0 && r.Lo >= 0 ? l.Divide(r) : ValueRange.OfType(bin.Type),
    IrBinaryOp.SRem => l.Remainder(r),
    IrBinaryOp.URem => l.Lo >= 0 && r.Lo >= 0 ? l.Remainder(r) : ValueRange.OfType(bin.Type),
    IrBinaryOp.And => l.And(r),
    IrBinaryOp.Or or IrBinaryOp.Xor => l.MergeBits(r),
    IrBinaryOp.Shl when Constant(bin.Rhs) is { } sl => l.ShiftLeft(sl),
    IrBinaryOp.AShr when Constant(bin.Rhs) is { } sa => l.ShiftRightArithmetic(sa),
    IrBinaryOp.LShr when Constant(bin.Rhs) is { } sr => l.ShiftRightLogical(sr),
    _ => ValueRange.OfType(bin.Type),
  };

  private static long? Constant(IrValue value) => value is IrConstantInt c ? c.Value : null;

  #endregion

  #region per-block refinement

  /// <summary>
  /// The range of <paramref name="value"/> at <paramref name="block"/>: the global answer, tightened
  /// by any dominating branch that constrains it, and - for an expression - recomputed from operands
  /// that were tightened the same way. Recomputing rather than only intersecting is the point: the
  /// branch bounds <c>i</c>, and what the bounds check actually tests is <c>sext i</c>.
  /// </summary>
  private ValueRange Refined(IrValue value, IrBasicBlock block, int depth) {
    var known = this.Global(value).Meet(this.FactAbout(value, block));
    if (depth <= 0 || value is not IrInstruction instruction)
      return known;
    var recomputed = instruction switch {
      IrCast cast => this.RefinedCast(cast, block, depth),
      IrBinary bin => this.EvaluateBinary(bin, this.Refined(bin.Lhs, block, depth - 1), this.Refined(bin.Rhs, block, depth - 1))
                          .Fit(bin.Type),
      _ => ValueRange.Top,
    };
    return known.Meet(recomputed);
  }

  private ValueRange RefinedCast(IrCast cast, IrBasicBlock block, int depth)
    => Widened(cast, this.Refined(cast.Value, block, depth - 1)).Fit(cast.Type);

  /// <summary>
  /// Everything the dominating conditional edges say about one value at one block.
  ///
  /// <para>
  /// The constraints are stored as the comparison they came from and turned into an interval HERE,
  /// not when they were collected. That is deliberate: <see cref="FactsAt"/> is cached, the fixpoint
  /// calls into it while its own answers are still growing, and a bound computed against a
  /// half-solved operand and then cached would be a fact tighter than the truth - the one shape of
  /// mistake this class must not make.
  /// </para>
  /// </summary>
  private ValueRange FactAbout(IrValue value, IrBasicBlock block) {
    var narrowed = ValueRange.Top;
    foreach (var (constrained, pred, against) in this.FactsAt(block)) {
      if (!ReferenceEquals(constrained, value))
        continue;
      var other = this.Global(against);
      // an unsigned predicate says nothing usable about a range that may be negative: read unsigned,
      // such a range is two intervals, not one
      if (IsUnsigned(pred) && (other.Lo < 0 || this.Global(value).Lo < 0))
        continue;
      if (Bound(pred, other, this.Global(value)) is { } bound)
        narrowed = narrowed.Meet(bound);
    }
    return narrowed;
  }

  /// <summary>
  /// The constraints that hold throughout <paramref name="block"/>, collected once by walking its
  /// dominator chain.
  ///
  /// <para>
  /// A conditional edge yields a fact only when the successor it leads to is entered <b>solely</b>
  /// through that edge and dominates this block - the same test <c>CorrelatedValueProp</c> makes,
  /// for the same reason: a successor with a second predecessor can be reached without the condition
  /// having held, and a fact taken from it would be a fact about the wrong path.
  /// </para>
  /// </summary>
  private IReadOnlyList<(IrValue Value, IrCmpPred Pred, IrValue Against)> FactsAt(IrBasicBlock block) {
    if (this._facts.TryGetValue(block, out var cached))
      return cached;
    var collected = new List<(IrValue Value, IrCmpPred Pred, IrValue Against)>();
    this._facts[block] = collected;

    // Only the idom chain is walked, and that is complete rather than a shortcut: a fact taken from
    // an edge into T holds exactly where T dominates, and T dominates this block precisely when it
    // sits on this chain.
    for (var at = block; at is not null; at = Parent(at)) {
      var predecessors = at.Predecessors.ToList();
      if (predecessors.Count != 1)
        continue;                                    // reachable another way: the condition need not have held
      if (predecessors[0].Terminator is not IrCondBr { Condition: IrCmp cmp } branch)
        continue;
      var holds = ReferenceEquals(branch.IfTrue, at) ? true
                : ReferenceEquals(branch.IfFalse, at) ? false
                : (bool?)null;
      if (holds is { } outcome && !ReferenceEquals(branch.IfTrue, branch.IfFalse))
        AddConstraints(collected, cmp, outcome);
    }
    return collected;

    IrBasicBlock? Parent(IrBasicBlock b) {
      var idom = this._dom.ImmediateDominatorOf(b);
      return idom is null || ReferenceEquals(idom, b) ? null : idom;
    }
  }

  /// <summary>
  /// Records what <c>lhs pred rhs</c> (or its negation) says about each side. Both directions are
  /// recorded, because a check written <c>10 &gt;= i</c> constrains <c>i</c> exactly as
  /// <c>i &lt;= 10</c> does; each is stored with the constrained value first, so the predicate is
  /// swapped for the right-hand entry rather than at every read.
  /// </summary>
  private static void AddConstraints(List<(IrValue Value, IrCmpPred Pred, IrValue Against)> into, IrCmp cmp, bool holds) {
    var pred = holds ? cmp.Pred : Negate(cmp.Pred);
    if (pred is null || !cmp.Lhs.Type.IsInteger || !cmp.Rhs.Type.IsInteger)
      return;
    into.Add((cmp.Lhs, pred.Value, cmp.Rhs));
    into.Add((cmp.Rhs, Swap(pred.Value), cmp.Lhs));
  }

  private static bool IsUnsigned(IrCmpPred pred)
    => pred is IrCmpPred.Ult or IrCmpPred.Ule or IrCmpPred.Ugt or IrCmpPred.Uge;

  /// <summary>
  /// The interval one side is confined to when the predicate holds, given <paramref name="other"/> -
  /// the range of the side it is compared against - and <paramref name="own"/>, what it was already
  /// known to be.
  ///
  /// <para>
  /// <c>Ne</c> needs both, and it is the one that earns the second parameter: "not equal" is a hole
  /// rather than an interval, so it says nothing at all unless the excluded constant sits on an
  /// ENDPOINT of what the value could be - in which case the endpoint moves in by one. That single
  /// case is what proves a divisor non-zero after the guard branch, which is the whole reason
  /// <c>100 \ i</c> can drop its <c>TEST</c>.
  /// </para>
  /// </summary>
  private static ValueRange? Bound(IrCmpPred pred, ValueRange other, ValueRange own) {
    if (other.IsEmpty)
      return null;
    return pred switch {
      IrCmpPred.Eq => other,
      IrCmpPred.Slt or IrCmpPred.Ult => other.Hi == long.MinValue ? null : new ValueRange(long.MinValue, other.Hi - 1),
      IrCmpPred.Sle or IrCmpPred.Ule => new ValueRange(long.MinValue, other.Hi),
      IrCmpPred.Sgt or IrCmpPred.Ugt => other.Lo == long.MaxValue ? null : new ValueRange(other.Lo + 1, long.MaxValue),
      IrCmpPred.Sge or IrCmpPred.Uge => new ValueRange(other.Lo, long.MaxValue),
      IrCmpPred.Ne when other.Lo == other.Hi && !own.IsTop && !own.IsEmpty && own.Lo == other.Lo
        => new ValueRange(own.Lo + 1, own.Hi),
      IrCmpPred.Ne when other.Lo == other.Hi && !own.IsTop && !own.IsEmpty && own.Hi == other.Lo
        => new ValueRange(own.Lo, own.Hi - 1),
      _ => null,
    };
  }

  private static IrCmpPred? Negate(IrCmpPred pred) => pred switch {
    IrCmpPred.Eq => IrCmpPred.Ne,
    IrCmpPred.Ne => IrCmpPred.Eq,
    IrCmpPred.Slt => IrCmpPred.Sge,
    IrCmpPred.Sle => IrCmpPred.Sgt,
    IrCmpPred.Sgt => IrCmpPred.Sle,
    IrCmpPred.Sge => IrCmpPred.Slt,
    IrCmpPred.Ult => IrCmpPred.Uge,
    IrCmpPred.Ule => IrCmpPred.Ugt,
    IrCmpPred.Ugt => IrCmpPred.Ule,
    IrCmpPred.Uge => IrCmpPred.Ult,
    _ => null,                                       // a float predicate does not negate: NaN fails both
  };

  private static IrCmpPred Swap(IrCmpPred pred) => pred switch {
    IrCmpPred.Slt => IrCmpPred.Sgt,
    IrCmpPred.Sle => IrCmpPred.Sge,
    IrCmpPred.Sgt => IrCmpPred.Slt,
    IrCmpPred.Sge => IrCmpPred.Sle,
    IrCmpPred.Ult => IrCmpPred.Ugt,
    IrCmpPred.Ule => IrCmpPred.Uge,
    IrCmpPred.Ugt => IrCmpPred.Ult,
    IrCmpPred.Uge => IrCmpPred.Ule,
    _ => pred,                                       // Eq / Ne are symmetric
  };

  #endregion
}
