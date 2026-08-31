using System.Numerics;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Peephole instruction simplification: constant folding plus the standard algebraic
/// identities (x+0, x*1, x*0, x&amp;-1, x^x, x==x, ...). A simplified instruction is
/// replaced by its value everywhere (RAUW) and left for DCE to remove. Runs to a
/// fixpoint so a simplification that exposes another is taken too.
/// </summary>
public static class InstCombine {

  /// <summary>Simplifies the function in place; returns how many instructions were replaced.</summary>
  public static int Run(IrFunction fn) => Run(fn, Simplify);

  /// <summary>
  /// Prepares faithful IR for selection while retaining comparisons as emitted. Constant address
  /// arithmetic still has to collapse or large array programs produce a pathological machine graph;
  /// leaving comparisons alone prevents that legalization from deciding source-level branches.
  /// </summary>
  public static int RunForFaithfulSelection(IrFunction fn) => Run(fn, SimplifyForFaithfulSelection);

  private static int Run(IrFunction fn, Func<IrInstruction, IrValue?> simplify) {
    var replaced = 0;
    var worklist = new Queue<IrInstruction>(fn.AllInstructions);
    while (worklist.Count > 0) {
      var inst = worklist.Dequeue();
      if (inst.Parent is null)
        continue;                                    // already removed
      var simpler = simplify(inst);
      if (simpler is null || ReferenceEquals(simpler, inst))
        continue;

      if (simpler is IrInstruction created && created.Parent is null) {
        inst.Parent!.InsertBefore(created, inst);     // a strength-reduced replacement needs a home
        worklist.Enqueue(created);
      }
      foreach (var user in inst.Users)               // users may now simplify further
        worklist.Enqueue(user);
      inst.ReplaceAllUsesWith(simpler);
      if (inst.HasNoUsers && !HasSideEffects(inst))
        inst.EraseFromParent();
      ++replaced;
    }
    return replaced;
  }

  /// <summary>Returns a value the instruction is equal to (a constant or an existing value), or null.</summary>
  public static IrValue? Simplify(IrInstruction inst) {
    if (IrConstFold.TryFold(inst) is { } folded)
      return folded;
    return SimplifyCanonical(inst);
  }

  private static IrValue? SimplifyCanonical(IrInstruction inst) {
    return inst switch {
      IrBinary b => SimplifyBinary(b),
      IrCmp c => SimplifyCmp(c),
      IrGep g => SimplifyGep(g),
      IrCast cast => SimplifyCast(cast),
      IrSelect s => SimplifySelect(s),
      _ => null,
    };
  }

  private static IrValue? SimplifyForFaithfulSelection(IrInstruction inst)
    => inst is IrCmp { IsSourceCondition: true } ? null : Simplify(inst);

  /// <summary>
  /// <c>select true, a, b</c> is <c>a</c>, <c>select false, a, b</c> is <c>b</c>, and a select whose
  /// arms are the same value is that value however the condition turns out.
  ///
  /// A constant condition looks like something the front end would never produce, and on its own it
  /// is - but interprocedural constant propagation turns a parameter into a literal, and a flag
  /// tested by a select is exactly the kind of parameter that happens to. Without this rule the
  /// select survives to instruction selection with an immediate where a register is required, and the
  /// function is declined for a condition the compiler already knew the answer to.
  /// </summary>
  private static IrValue? SimplifySelect(IrSelect s) {
    if (ReferenceEquals(s.IfTrue, s.IfFalse))
      return s.IfTrue;
    return s.Condition is IrConstantInt c ? c.Value != 0 ? s.IfTrue : s.IfFalse : null;
  }

  private static IrValue? SimplifyBinary(IrBinary b) {
    var (l, r, t) = (b.Lhs, b.Rhs, b.Type);
    switch (b.Op) {
      case IrBinaryOp.Add:
        if (IsZero(r)) return l;
        if (IsZero(l)) return r;
        if (ReferenceEquals(l, r)) return new IrBinary(IrBinaryOp.Shl, l, new IrConstantInt(t, 1));  // x + x -> x << 1
        if (MergeConst(IrBinaryOp.Add, l, r, t) is { } addM) return addM;
        break;
      case IrBinaryOp.Sub:
        if (IsZero(r)) return l;
        if (ReferenceEquals(l, r)) return Zero(t);
        // canonicalize x - C into x + (-C) so add-chain constant merging applies
        if (r is IrConstantInt subC && !IsZero(r))
          return new IrBinary(IrBinaryOp.Add, l, new IrConstantInt(t, IrConstFold.Wrap(-subC.Value, t)));
        // 0 - (0 - x) -> x
        if (IsZero(l) && r is IrBinary { Op: IrBinaryOp.Sub } ds && IsZero(ds.Lhs)) return ds.Rhs;
        // (a + b) - a -> b ; (a + b) - b -> a
        if (l is IrBinary { Op: IrBinaryOp.Add } la) {
          if (ReferenceEquals(la.Lhs, r)) return la.Rhs;
          if (ReferenceEquals(la.Rhs, r)) return la.Lhs;
        }
        break;
      case IrBinaryOp.Mul:
        if (IsOne(r)) return l;
        if (IsOne(l)) return r;
        if (IsZero(r) || IsZero(l)) return Zero(t);
        if (IsAllOnes(r)) return new IrBinary(IrBinaryOp.Sub, Zero(t), l);   // x * -1 -> -x
        if (IsAllOnes(l)) return new IrBinary(IrBinaryOp.Sub, Zero(t), r);
        if (MergeConst(IrBinaryOp.Mul, l, r, t) is { } mulM) return mulM;
        if (Pow2Shift(r) is { } sr) return new IrBinary(IrBinaryOp.Shl, l, new IrConstantInt(t, sr));   // x * 2^k -> x << k
        if (Pow2Shift(l) is { } sl) return new IrBinary(IrBinaryOp.Shl, r, new IrConstantInt(t, sl));
        break;
      case IrBinaryOp.And:
        if (IsZero(r) || IsZero(l)) return Zero(t);
        if (IsAllOnes(r)) return l;
        if (IsAllOnes(l)) return r;
        if (ReferenceEquals(l, r)) return l;
        // absorption: x & (x | y) -> x
        if (Absorbs(l, r, IrBinaryOp.Or)) return l;
        if (Absorbs(r, l, IrBinaryOp.Or)) return r;
        if (MergeConst(IrBinaryOp.And, l, r, t) is { } andM) return andM;
        break;
      case IrBinaryOp.Or:
        if (IsZero(r)) return l;
        if (IsZero(l)) return r;
        if (IsAllOnes(r) || IsAllOnes(l)) return AllOnes(t);
        if (ReferenceEquals(l, r)) return l;
        // absorption: x | (x & y) -> x
        if (Absorbs(l, r, IrBinaryOp.And)) return l;
        if (Absorbs(r, l, IrBinaryOp.And)) return r;
        if (MergeConst(IrBinaryOp.Or, l, r, t) is { } orM) return orM;
        break;
      case IrBinaryOp.Xor:
        if (IsZero(r)) return l;
        if (IsZero(l)) return r;
        if (ReferenceEquals(l, r)) return Zero(t);
        // double complement: xor(xor(x, -1), -1) -> x
        if (IsAllOnes(r) && l is IrBinary { Op: IrBinaryOp.Xor } inner && IsAllOnes(inner.Rhs)) return inner.Lhs;
        if (MergeConst(IrBinaryOp.Xor, l, r, t) is { } xorM) return xorM;
        break;
      case IrBinaryOp.Shl or IrBinaryOp.LShr or IrBinaryOp.AShr:
        if (IsZero(r)) return l;
        // (x shift a) shift b -> x shift (a+b) for the same shift op, when the total stays in range
        if (r is IrConstantInt rb && l is IrBinary shiftInner && shiftInner.Op == b.Op
            && shiftInner.Rhs is IrConstantInt ra && ra.Value >= 0 && rb.Value >= 0 && ra.Value + rb.Value < t.Bits)
          return new IrBinary(b.Op, shiftInner.Lhs, new IrConstantInt(t, ra.Value + rb.Value));
        break;
      case IrBinaryOp.SDiv or IrBinaryOp.UDiv:
        if (IsOne(r)) return l;
        if (b.Op == IrBinaryOp.UDiv && Pow2Shift(r) is { } sd)
          return new IrBinary(IrBinaryOp.LShr, l, new IrConstantInt(t, sd));    // unsigned x / 2^k -> x >>> k
        break;
      case IrBinaryOp.SRem or IrBinaryOp.URem:
        if (IsOne(r)) return Zero(t);
        if (b.Op == IrBinaryOp.URem && r is IrConstantInt rc && Pow2Shift(rc) is not null)
          return new IrBinary(IrBinaryOp.And, l, new IrConstantInt(t, (long)(rc.ZeroExtended - 1)));  // unsigned x % 2^k -> x & (2^k-1)
        break;
    }
    return null;
  }

  private static IrValue? SimplifyCast(IrCast c) {
    if (c.Op == IrCastOp.BitCast && c.Value.Type.Equals(c.Type))
      return c.Value;                                              // bitcast to same type

    // chained widenings of the same kind combine: zext(zext x) -> zext x ; sext(sext x) -> sext x
    if ((c.Op == IrCastOp.ZExt || c.Op == IrCastOp.SExt) && c.Value is IrCast wider && wider.Op == c.Op)
      return new IrCast(c.Op, wider.Value, c.Type);

    // trunc of trunc combines
    if (c.Op == IrCastOp.Trunc && c.Value is IrCast { Op: IrCastOp.Trunc } innerTrunc)
      return new IrCast(IrCastOp.Trunc, innerTrunc.Value, c.Type);

    // trunc of a widening: trunc(ext(x to W) to A)
    if (c.Op == IrCastOp.Trunc && c.Value is IrCast { Op: IrCastOp.ZExt or IrCastOp.SExt } ext) {
      var orig = ext.Value.Type.Bits;
      if (c.Type.Bits == orig)
        return ext.Value;                                          // round-trip back to the original width
      if (c.Type.Bits > orig)
        return new IrCast(ext.Op, ext.Value, c.Type);              // still a (smaller) widening of the same kind
      return new IrCast(IrCastOp.Trunc, ext.Value, c.Type);        // narrower than the original: a plain trunc
    }
    return null;
  }

  private static IrValue? SimplifyGep(IrGep g) =>
    g.ByteOffset is IrConstantInt { IsZero: true } ? g.BasePtr : null;   // gep p, 0 -> p

  private static IrValue? SimplifyCmp(IrCmp c) {
    // canonicalize a constant operand to the right (swapping the predicate accordingly)
    if (c.Lhs is IrConstant && c.Rhs is not IrConstant)
      return new IrCmp(Swap(c.Pred), c.Rhs, c.Lhs);

    // i1 has only the bit patterns false/true. Equality against either constant is therefore either
    // the value itself or its logical complement; no compare instruction is needed.
    if (c.Pred is IrCmpPred.Eq or IrCmpPred.Ne
        && c.Lhs.Type.IsBool
        && c.Rhs is IrConstantInt boolConstant) {
      var rhsIsTrue = !boolConstant.IsZero;
      var returnsOperand = (c.Pred == IrCmpPred.Eq) == rhsIsTrue;
      return returnsOperand ? c.Lhs : NegateBool(c.Lhs, c.IsSourceCondition);
    }

    // (zext/sext i1 %b) != 0  ->  %b   and   (zext/sext i1 %b) == 0  ->  !%b
    // this collapses the "relational then compare-to-zero" shape every BASIC condition lowers to
    if (c.Pred is IrCmpPred.Ne or IrCmpPred.Eq) {
      var widened = AsWidenedBool(c.Lhs, c.Rhs) ?? AsWidenedBool(c.Rhs, c.Lhs);
      if (widened is { } b)
        return c.Pred == IrCmpPred.Ne ? b : NegateBool(b, c.IsSourceCondition);
    }

    if (!ReferenceEquals(c.Lhs, c.Rhs))
      return null;
    // x <cmp> x: integer comparisons of identical SSA values are decidable
    return c.Pred switch {
      IrCmpPred.Eq or IrCmpPred.Sle or IrCmpPred.Sge or IrCmpPred.Ule or IrCmpPred.Uge => True(),
      IrCmpPred.Ne or IrCmpPred.Slt or IrCmpPred.Sgt or IrCmpPred.Ult or IrCmpPred.Ugt => False(),
      _ => null,                                      // float x==x is false for NaN; do not fold
    };
  }

  /// <summary>
  /// Produces logical NOT of an i1. Integer comparisons stay comparisons with the complementary
  /// predicate so branch fusion can consume them directly. Ordered floating predicates deliberately
  /// do not use that shortcut: NaN makes the complement of an ordered predicate unordered-inclusive.
  /// </summary>
  private static IrValue NegateBool(IrValue value, bool isSourceCondition) {
    if (value is IrCmp cmp && InvertInteger(cmp.Pred) is { } inverse)
      return new IrCmp(inverse, cmp.Lhs, cmp.Rhs) { IsSourceCondition = isSourceCondition };
    return new IrBinary(IrBinaryOp.Xor, value, new IrConstantInt(IrType.I1, 1));
  }

  private static IrCmpPred? InvertInteger(IrCmpPred p) => p switch {
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
    _ => null,
  };

  /// <summary>If <paramref name="maybeCast"/> is a zext/sext of an i1 and <paramref name="maybeZero"/> is 0, returns the i1 source.</summary>
  private static IrValue? AsWidenedBool(IrValue maybeCast, IrValue maybeZero) =>
    maybeZero is IrConstantInt { IsZero: true }
      && maybeCast is IrCast { Op: IrCastOp.ZExt or IrCastOp.SExt } cast
      && cast.Value.Type.IsBool
      ? cast.Value
      : null;

  /// <summary>The predicate that holds when a comparison's operands are swapped (a &lt; b becomes b &gt; a).</summary>
  private static IrCmpPred Swap(IrCmpPred p) => p switch {
    IrCmpPred.Slt => IrCmpPred.Sgt, IrCmpPred.Sgt => IrCmpPred.Slt,
    IrCmpPred.Sle => IrCmpPred.Sge, IrCmpPred.Sge => IrCmpPred.Sle,
    IrCmpPred.Ult => IrCmpPred.Ugt, IrCmpPred.Ugt => IrCmpPred.Ult,
    IrCmpPred.Ule => IrCmpPred.Uge, IrCmpPred.Uge => IrCmpPred.Ule,
    IrCmpPred.Folt => IrCmpPred.Fogt, IrCmpPred.Fogt => IrCmpPred.Folt,
    IrCmpPred.Fole => IrCmpPred.Foge, IrCmpPred.Foge => IrCmpPred.Fole,
    _ => p,   // eq/ne (signed and float) are symmetric
  };

  private static bool HasSideEffects(IrInstruction inst) =>
    inst is IrStore or IrCall or IrInlineAsm || inst.IsTerminator;

  /// <summary>
  /// Reassociates a constant through a same-opcode chain: op(op(x, c1), c2) -> op(x, c1∘c2)
  /// for the associative+commutative integer ops (add/and/or/xor/mul). Returns the merged
  /// instruction, or null if the shape does not match.
  /// </summary>
  private static IrValue? MergeConst(IrBinaryOp op, IrValue l, IrValue r, IrType t) {
    IrConstantInt outer;
    IrBinary inner;
    if (r is IrConstantInt rc && l is IrBinary lb && lb.Op == op) { outer = rc; inner = lb; }
    else if (l is IrConstantInt lc && r is IrBinary rb && rb.Op == op) { outer = lc; inner = rb; }
    else return null;

    IrValue x;
    IrConstantInt c1;
    if (inner.Rhs is IrConstantInt ir) { x = inner.Lhs; c1 = ir; }
    else if (inner.Lhs is IrConstantInt il) { x = inner.Rhs; c1 = il; }
    else return null;

    var merged = op switch {
      IrBinaryOp.Add => c1.Value + outer.Value,
      IrBinaryOp.And => c1.Value & outer.Value,
      IrBinaryOp.Or => c1.Value | outer.Value,
      IrBinaryOp.Xor => c1.Value ^ outer.Value,
      IrBinaryOp.Mul => c1.Value * outer.Value,
      _ => 0L,
    };
    return new IrBinary(op, x, new IrConstantInt(t, IrConstFold.Wrap(merged, t)));
  }

  /// <summary>True if <paramref name="other"/> is an <paramref name="innerOp"/> instruction one of whose operands is <paramref name="x"/> (for absorption laws).</summary>
  private static bool Absorbs(IrValue x, IrValue other, IrBinaryOp innerOp) =>
    other is IrBinary inner && inner.Op == innerOp && (ReferenceEquals(inner.Lhs, x) || ReferenceEquals(inner.Rhs, x));

  /// <summary>If the value is a positive power of two, returns its shift exponent; otherwise null.</summary>
  private static long? Pow2Shift(IrValue v) {
    if (v is not IrConstantInt c)
      return null;
    var u = c.ZeroExtended;
    return u != 0 && (u & (u - 1)) == 0 ? BitOperations.TrailingZeroCount(u) : null;
  }

  private static ulong FullMask(int bits) => bits >= 64 ? ~0UL : (1UL << bits) - 1;
  private static bool IsZero(IrValue v) => v is IrConstantInt c && c.IsZero;
  private static bool IsOne(IrValue v) => v is IrConstantInt c && c.ZeroExtended == 1;
  private static bool IsAllOnes(IrValue v) => v is IrConstantInt c && c.ZeroExtended == FullMask(c.Type.Bits);
  private static IrConstantInt Zero(IrType t) => new(t, 0);
  private static IrConstantInt AllOnes(IrType t) => new(t, IrConstFold.Wrap(-1, t));
  private static IrConstantInt True() => new(IrType.I1, 1);
  private static IrConstantInt False() => new(IrType.I1, 0);
}