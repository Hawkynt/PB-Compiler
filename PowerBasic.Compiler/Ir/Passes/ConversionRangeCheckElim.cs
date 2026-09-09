using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Removes floating-point range guards around narrowing conversions when the floating value is
/// itself derived from an integer value whose SSA range already fits the destination.
///
/// <para>
/// The integer range lattice deliberately refuses floating comparisons because an arbitrary float
/// may be NaN. This pass supplies the missing, much narrower fact domain needed by conversion guards:
/// constants and SIToFP/UIToFP values (plus phis/selects/extensions made solely from such values) are
/// known not to be NaN and have monotone numeric bounds. That is enough to decide the ordered
/// comparisons emitted by <c>OutsideIntegerRange</c> without pretending to solve general floating
/// value analysis.
/// </para>
/// </summary>
public static class ConversionRangeCheckElim {

  private const int _MAX_DEPTH = 12;

  /// <summary>Folds provably decided ordered float comparisons; returns how many were replaced.</summary>
  public static int Run(IrFunction fn) {
    ArgumentNullException.ThrowIfNull(fn);
    if (IrRangeAnalysis.Build(fn) is not { } integers)
      return 0;

    var decided = new List<(IrCmp Cmp, bool Outcome)>();
    foreach (var block in fn.Blocks)
      foreach (var cmp in block.Instructions.OfType<IrCmp>()) {
        if (cmp.HasNoUsers || cmp.Pred is not (IrCmpPred.Foeq or IrCmpPred.Fone or IrCmpPred.Folt
            or IrCmpPred.Fole or IrCmpPred.Fogt or IrCmpPred.Foge))
          continue;
        if (TryRange(cmp.Lhs, block, integers, _MAX_DEPTH, []) is not { } lhs
            || TryRange(cmp.Rhs, block, integers, _MAX_DEPTH, []) is not { } rhs)
          continue;
        if (Decide(cmp.Pred, lhs, rhs) is { } outcome)
          decided.Add((cmp, outcome));
      }

    foreach (var (cmp, outcome) in decided)
      cmp.ReplaceAllUsesWith(IrBuilder.ConstBool(outcome));
    return decided.Count;
  }

  private readonly record struct FloatRange(double Lo, double Hi) {
    public FloatRange Join(FloatRange other) => new(Math.Min(this.Lo, other.Lo), Math.Max(this.Hi, other.Hi));
  }

  private static FloatRange? TryRange(IrValue value, IrBasicBlock block, IrRangeAnalysis integers,
      int depth, HashSet<IrValue> active) {
    if (depth <= 0 || !active.Add(value))
      return null;
    try {
      switch (value) {
        case IrConstantFloat constant when !double.IsNaN(constant.Value):
          return new(constant.Value, constant.Value);

        case IrCast { Op: IrCastOp.SIToFP or IrCastOp.UIToFP } cast:
          var source = integers.RangeAt(cast.Value, block);
          if (source.IsEmpty || source.IsTop)
            return null;
          return cast.Type.Bits == 32
            ? Ordered((double)(float)source.Lo, (double)(float)source.Hi)
            : Ordered((double)source.Lo, (double)source.Hi);

        case IrCast { Op: IrCastOp.FPExt } cast:
          return TryRange(cast.Value, block, integers, depth - 1, active);

        case IrSelect select:
          var whenTrue = TryRange(select.IfTrue, block, integers, depth - 1, active);
          var whenFalse = TryRange(select.IfFalse, block, integers, depth - 1, active);
          return whenTrue is { } t && whenFalse is { } f ? t.Join(f) : null;

        case IrPhi phi:
          FloatRange? joined = null;
          foreach (var incoming in phi.Operands) {
            if (TryRange(incoming, block, integers, depth - 1, active) is not { } range)
              return null;
            joined = joined is { } current ? current.Join(range) : range;
          }
          return joined;

        default:
          return null;
      }
    } finally {
      active.Remove(value);
    }
  }

  private static FloatRange Ordered(double a, double b) => a <= b ? new(a, b) : new(b, a);

  private static bool? Decide(IrCmpPred pred, FloatRange lhs, FloatRange rhs) => pred switch {
    IrCmpPred.Foeq when lhs.Lo == lhs.Hi && rhs.Lo == rhs.Hi && lhs.Lo == rhs.Lo => true,
    IrCmpPred.Foeq when lhs.Hi < rhs.Lo || rhs.Hi < lhs.Lo => false,
    IrCmpPred.Fone when lhs.Lo == lhs.Hi && rhs.Lo == rhs.Hi && lhs.Lo == rhs.Lo => false,
    IrCmpPred.Fone when lhs.Hi < rhs.Lo || rhs.Hi < lhs.Lo => true,
    IrCmpPred.Folt when lhs.Hi < rhs.Lo => true,
    IrCmpPred.Folt when lhs.Lo >= rhs.Hi => false,
    IrCmpPred.Fole when lhs.Hi <= rhs.Lo => true,
    IrCmpPred.Fole when lhs.Lo > rhs.Hi => false,
    IrCmpPred.Fogt when lhs.Lo > rhs.Hi => true,
    IrCmpPred.Fogt when lhs.Hi <= rhs.Lo => false,
    IrCmpPred.Foge when lhs.Lo >= rhs.Hi => true,
    IrCmpPred.Foge when lhs.Hi < rhs.Lo => false,
    _ => null,
  };
}
