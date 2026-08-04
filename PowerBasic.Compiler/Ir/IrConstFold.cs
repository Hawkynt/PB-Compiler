namespace PowerBasic.Compiler.Ir;

/// <summary>
/// Pure evaluation of an instruction whose operands are all constants. Integer ops
/// honour the result type's bit width (two's-complement wrap) and the opcode's
/// signedness; operations whose result would be undefined (division by zero,
/// INT_MIN/-1, out-of-range shifts or float-to-int) are declined (return null) so the
/// runtime semantics — including traps — are never silently altered.
/// </summary>
public static class IrConstFold {

  /// <summary>Folds an instruction to a constant, or returns null if it cannot or must not be folded.</summary>
  public static IrConstant? TryFold(IrInstruction inst) => inst switch {
    IrBinary b => FoldBinary(b),
    IrCmp c => FoldCmp(c),
    IrCast cast => FoldCast(cast),
    _ => null,
  };

  /// <summary>Sign-extends a raw value from a type's bit width into a canonical long.</summary>
  public static long Wrap(long value, IrType type) {
    if (type.Bits >= 64)
      return value;
    var mask = (1L << type.Bits) - 1;
    var masked = value & mask;
    var signBit = 1L << (type.Bits - 1);
    return (masked ^ signBit) - signBit;
  }

  private static ulong Unsigned(IrConstantInt c) => c.ZeroExtended;

  private static IrConstant? FoldBinary(IrBinary b) {
    if (b.Lhs is IrConstantFloat fl && b.Rhs is IrConstantFloat fr)
      return FoldFloat(b, fl.Value, fr.Value);
    if (b.Lhs is not IrConstantInt l || b.Rhs is not IrConstantInt r)
      return null;

    var t = b.Type;
    long s;
    switch (b.Op) {
      case IrBinaryOp.Add: s = l.Value + r.Value; break;
      case IrBinaryOp.Sub: s = l.Value - r.Value; break;
      case IrBinaryOp.Mul: s = l.Value * r.Value; break;
      case IrBinaryOp.And: s = l.Value & r.Value; break;
      case IrBinaryOp.Or: s = l.Value | r.Value; break;
      case IrBinaryOp.Xor: s = l.Value ^ r.Value; break;
      case IrBinaryOp.SDiv:
        if (r.Value == 0 || (l.Value == long.MinValue && r.Value == -1))
          return null;
        s = Wrap(l.Value, t) / Wrap(r.Value, t);
        break;
      case IrBinaryOp.SRem:
        if (r.Value == 0 || (l.Value == long.MinValue && r.Value == -1))
          return null;
        s = Wrap(l.Value, t) % Wrap(r.Value, t);
        break;
      case IrBinaryOp.UDiv:
        if (Unsigned(r) == 0)
          return null;
        s = (long)(Unsigned(l) / Unsigned(r));
        break;
      case IrBinaryOp.URem:
        if (Unsigned(r) == 0)
          return null;
        s = (long)(Unsigned(l) % Unsigned(r));
        break;
      case IrBinaryOp.Shl:
        if (r.Value < 0 || r.Value >= t.Bits)
          return null;
        s = l.Value << (int)r.Value;
        break;
      case IrBinaryOp.LShr:
        if (r.Value < 0 || r.Value >= t.Bits)
          return null;
        s = (long)(Unsigned(l) >> (int)r.Value);
        break;
      case IrBinaryOp.AShr:
        if (r.Value < 0 || r.Value >= t.Bits)
          return null;
        s = Wrap(l.Value, t) >> (int)r.Value;
        break;
      default:
        return null;
    }
    return new IrConstantInt(t, Wrap(s, t));
  }

  private static IrConstant? FoldFloat(IrBinary b, double l, double r) {
    var v = b.Op switch {
      IrBinaryOp.FAdd => l + r,
      IrBinaryOp.FSub => l - r,
      IrBinaryOp.FMul => l * r,
      IrBinaryOp.FDiv when r != 0.0 => l / r,        // leave x/0 to the runtime (PB error 11)
      _ => (double?)null,
    };
    return v is { } d ? new IrConstantFloat(b.Type, NarrowFloat(d, b.Type)) : null;
  }

  private static IrConstant? FoldCmp(IrCmp c) {
    if (c.Lhs is IrConstantInt li && c.Rhs is IrConstantInt ri) {
      long a = Wrap(li.Value, li.Type), bb = Wrap(ri.Value, ri.Type);
      ulong ua = li.ZeroExtended, ub = ri.ZeroExtended;
      var result = c.Pred switch {
        IrCmpPred.Eq => a == bb,
        IrCmpPred.Ne => a != bb,
        IrCmpPred.Slt => a < bb,
        IrCmpPred.Sle => a <= bb,
        IrCmpPred.Sgt => a > bb,
        IrCmpPred.Sge => a >= bb,
        IrCmpPred.Ult => ua < ub,
        IrCmpPred.Ule => ua <= ub,
        IrCmpPred.Ugt => ua > ub,
        IrCmpPred.Uge => ua >= ub,
        _ => (bool?)null,
      };
      return result is { } b ? new IrConstantInt(IrType.I1, b ? 1 : 0) : null;
    }
    if (c.Lhs is IrConstantFloat lf && c.Rhs is IrConstantFloat rf) {
      var result = c.Pred switch {
        IrCmpPred.Foeq => lf.Value == rf.Value,
        IrCmpPred.Fone => lf.Value != rf.Value,
        IrCmpPred.Folt => lf.Value < rf.Value,
        IrCmpPred.Fole => lf.Value <= rf.Value,
        IrCmpPred.Fogt => lf.Value > rf.Value,
        IrCmpPred.Foge => lf.Value >= rf.Value,
        _ => (bool?)null,
      };
      return result is { } b ? new IrConstantInt(IrType.I1, b ? 1 : 0) : null;
    }
    return null;
  }

  private static IrConstant? FoldCast(IrCast cast) {
    var to = cast.Type;
    switch (cast.Op) {
      case IrCastOp.Trunc when cast.Value is IrConstantInt c:
        return new IrConstantInt(to, Wrap(c.Value, to));
      case IrCastOp.ZExt when cast.Value is IrConstantInt c:
        return new IrConstantInt(to, Wrap((long)c.ZeroExtended, to));
      case IrCastOp.SExt when cast.Value is IrConstantInt c:
        return new IrConstantInt(to, Wrap(c.Value, to));
      case IrCastOp.SIToFP when cast.Value is IrConstantInt c:
        return new IrConstantFloat(to, NarrowFloat(Wrap(c.Value, cast.Value.Type), to));
      case IrCastOp.UIToFP when cast.Value is IrConstantInt c:
        return new IrConstantFloat(to, NarrowFloat(c.ZeroExtended, to));
      case IrCastOp.FPExt or IrCastOp.FPTrunc when cast.Value is IrConstantFloat c:
        return new IrConstantFloat(to, NarrowFloat(c.Value, to));
      case IrCastOp.FPToSI when cast.Value is IrConstantFloat c && InLongRange(c.Value):
        return new IrConstantInt(to, Wrap((long)c.Value, to));
      // the rounding conversion folds by the same rule the hardware applies: nearest, ties to even
      case IrCastOp.FPToSIRound when cast.Value is IrConstantFloat c && InLongRange(c.Value):
        return new IrConstantInt(to, Wrap((long)Math.Round(c.Value, MidpointRounding.ToEven), to));
      // bitcast reinterprets the bit pattern between same-width int and float
      case IrCastOp.BitCast when cast.Value is IrConstantFloat cf && to.IsInteger && to.Bits == 32:
        return new IrConstantInt(to, BitConverter.SingleToInt32Bits((float)cf.Value));
      case IrCastOp.BitCast when cast.Value is IrConstantFloat cf && to.IsInteger && to.Bits == 64:
        return new IrConstantInt(to, BitConverter.DoubleToInt64Bits(cf.Value));
      case IrCastOp.BitCast when cast.Value is IrConstantInt ci && to.IsFloat && to.Bits == 32:
        return new IrConstantFloat(to, BitConverter.Int32BitsToSingle((int)ci.Value));
      case IrCastOp.BitCast when cast.Value is IrConstantInt ci && to.IsFloat && to.Bits == 64:
        return new IrConstantFloat(to, BitConverter.Int64BitsToDouble(ci.Value));
      default:
        return null;
    }
  }

  private static double NarrowFloat(double value, IrType type) => type.Bits == 32 ? (float)value : value;
  private static bool InLongRange(double v) => v is >= -9.2e18 and <= 9.2e18 && !double.IsNaN(v);
}
