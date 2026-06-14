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
        break;
      case IrBinaryOp.Sub:
        if (IsZero(r)) return l;
        if (ReferenceEquals(l, r)) return Zero(t);
        break;
      case IrBinaryOp.Mul:
        if (IsOne(r)) return l;
        if (IsOne(l)) return r;
        if (IsZero(r) || IsZero(l)) return Zero(t);
        break;
      case IrBinaryOp.And:
        if (IsZero(r) || IsZero(l)) return Zero(t);
        if (IsAllOnes(r)) return l;
        if (IsAllOnes(l)) return r;
        if (ReferenceEquals(l, r)) return l;
        break;
      case IrBinaryOp.Or:
        if (IsZero(r)) return l;
        if (IsZero(l)) return r;
        if (IsAllOnes(r) || IsAllOnes(l)) return AllOnes(t);
        if (ReferenceEquals(l, r)) return l;
        break;
      case IrBinaryOp.Xor:
        if (IsZero(r)) return l;
        if (IsZero(l)) return r;
        if (ReferenceEquals(l, r)) return Zero(t);
        break;
      case IrBinaryOp.Shl or IrBinaryOp.LShr or IrBinaryOp.AShr:
        if (IsZero(r)) return l;
        break;
      case IrBinaryOp.SDiv or IrBinaryOp.UDiv:
        if (IsOne(r)) return l;
        break;
      case IrBinaryOp.SRem or IrBinaryOp.URem:
        if (IsOne(r)) return Zero(t);
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

  private static ulong FullMask(int bits) => bits >= 64 ? ~0UL : (1UL << bits) - 1;
  private static bool IsZero(IrValue v) => v is IrConstantInt c && c.IsZero;
  private static bool IsOne(IrValue v) => v is IrConstantInt c && c.ZeroExtended == 1;
  private static bool IsAllOnes(IrValue v) => v is IrConstantInt c && c.ZeroExtended == FullMask(c.Type.Bits);
  private static IrConstantInt Zero(IrType t) => new(t, 0);
  private static IrConstantInt AllOnes(IrType t) => new(t, IrConstFold.Wrap(-1, t));
  private static IrConstantInt True() => new(IrType.I1, 1);
  private static IrConstantInt False() => new(IrType.I1, 0);
}
