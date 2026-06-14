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
  public static int Run(IrFunction fn) {
    var replaced = 0;
    var worklist = new Queue<IrInstruction>(fn.AllInstructions);
    while (worklist.Count > 0) {
      var inst = worklist.Dequeue();
      if (inst.Parent is null)
        continue;                                    // already removed
      var simpler = Simplify(inst);
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
    return inst switch {
      IrBinary b => SimplifyBinary(b),
      IrCmp c => SimplifyCmp(c),
      IrCast { Op: IrCastOp.BitCast } cast when cast.Value.Type.Equals(cast.Type) => cast.Value,
      _ => null,
    };
  }

  private static IrValue? SimplifyBinary(IrBinary b) {
    var (l, r, t) = (b.Lhs, b.Rhs, b.Type);
    switch (b.Op) {
      case IrBinaryOp.Add:
        if (IsZero(r)) return l;
        if (IsZero(l)) return r;
        if (MergeConst(IrBinaryOp.Add, l, r, t) is { } addM) return addM;
        break;
      case IrBinaryOp.Sub:
        if (IsZero(r)) return l;
        if (ReferenceEquals(l, r)) return Zero(t);
        break;
      case IrBinaryOp.Mul:
        if (IsOne(r)) return l;
        if (IsOne(l)) return r;
        if (IsZero(r) || IsZero(l)) return Zero(t);
        if (MergeConst(IrBinaryOp.Mul, l, r, t) is { } mulM) return mulM;
        if (Pow2Shift(r) is { } sr) return new IrBinary(IrBinaryOp.Shl, l, new IrConstantInt(t, sr));   // x * 2^k -> x << k
        if (Pow2Shift(l) is { } sl) return new IrBinary(IrBinaryOp.Shl, r, new IrConstantInt(t, sl));
        break;
      case IrBinaryOp.And:
        if (IsZero(r) || IsZero(l)) return Zero(t);
        if (IsAllOnes(r)) return l;
        if (IsAllOnes(l)) return r;
        if (ReferenceEquals(l, r)) return l;
        if (MergeConst(IrBinaryOp.And, l, r, t) is { } andM) return andM;
        break;
      case IrBinaryOp.Or:
        if (IsZero(r)) return l;
        if (IsZero(l)) return r;
        if (IsAllOnes(r) || IsAllOnes(l)) return AllOnes(t);
        if (ReferenceEquals(l, r)) return l;
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

  private static IrValue? SimplifyCmp(IrCmp c) {
    if (!ReferenceEquals(c.Lhs, c.Rhs))
      return null;
    // x <cmp> x: integer comparisons of identical SSA values are decidable
    return c.Pred switch {
      IrCmpPred.Eq or IrCmpPred.Sle or IrCmpPred.Sge or IrCmpPred.Ule or IrCmpPred.Uge => True(),
      IrCmpPred.Ne or IrCmpPred.Slt or IrCmpPred.Sgt or IrCmpPred.Ult or IrCmpPred.Ugt => False(),
      _ => null,                                      // float x==x is false for NaN; do not fold
    };
  }

  private static bool HasSideEffects(IrInstruction inst) =>
    inst is IrStore or IrCall || inst.IsTerminator;

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
