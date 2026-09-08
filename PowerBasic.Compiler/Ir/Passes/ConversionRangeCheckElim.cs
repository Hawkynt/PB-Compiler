using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0352 — folds ordered floating range guards when the shared FP domain proves their outcome.
///
/// <para>
/// Conversion guards are emitted as ordered floating comparisons, so NaN provenance matters as much
/// as the numeric endpoints. <see cref="FpDomainAnalysis"/> already adapts integer SSA ranges through
/// integer-to-float casts and supported floating arithmetic with the declared precision at every
/// operation. This pass consumes that shared proof instead of maintaining a second, weaker float range
/// evaluator. Phis and selects retain the original O0352 join support around those proven leaf domains.
/// </para>
/// </summary>
public static class ConversionRangeCheckElim {

  private const int _MAX_JOIN_DEPTH = 12;

  /// <summary>Folds provably decided ordered float comparisons; returns how many were replaced.</summary>
  public static int Run(IrFunction fn) {
    ArgumentNullException.ThrowIfNull(fn);
    if (FpDomainAnalysis.Build(fn) is not { } domains)
      return 0;

    var decided = new List<(IrCmp Cmp, bool Outcome)>();
    foreach (var block in fn.Blocks)
      foreach (var cmp in block.Instructions.OfType<IrCmp>()) {
        if (cmp.HasNoUsers || cmp.Pred is not (IrCmpPred.Foeq or IrCmpPred.Fone or IrCmpPred.Folt
            or IrCmpPred.Fole or IrCmpPred.Fogt or IrCmpPred.Foge))
          continue;
        if (TryDomain(cmp.Lhs, block, domains, _MAX_JOIN_DEPTH, []) is not { } lhs
            || TryDomain(cmp.Rhs, block, domains, _MAX_JOIN_DEPTH, []) is not { } rhs)
          continue;
        if (Decide(cmp.Pred, lhs, rhs) is { } outcome)
          decided.Add((cmp, outcome));
      }

    foreach (var (cmp, outcome) in decided)
      cmp.ReplaceAllUsesWith(IrBuilder.ConstBool(outcome));
    return decided.Count;
  }

  private static FpDomainAnalysis.Domain? TryDomain(
      IrValue value,
      IrBasicBlock block,
      FpDomainAnalysis domains,
      int depth,
      HashSet<IrValue> active) {
    var direct = domains.DomainAt(value, block);
    if (direct.IsKnown)
      return direct;
    if (depth <= 0 || !active.Add(value))
      return null;

    try {
      switch (value) {
        case IrCast { Op: IrCastOp.FPExt } cast:
          return TryDomain(cast.Value, block, domains, depth - 1, active);

        case IrSelect select:
          var whenTrue = TryDomain(select.IfTrue, block, domains, depth - 1, active);
          var whenFalse = TryDomain(select.IfFalse, block, domains, depth - 1, active);
          return whenTrue is { } t && whenFalse is { } f ? Join(t, f) : null;

        case IrPhi phi:
          FpDomainAnalysis.Domain? joined = null;
          foreach (var incoming in phi.Operands) {
            if (TryDomain(incoming, block, domains, depth - 1, active) is not { } range)
              return null;
            joined = joined is { } current ? Join(current, range) : range;
          }
          return joined;

        default:
          return null;
      }
    } finally {
      active.Remove(value);
    }
  }

  private static FpDomainAnalysis.Domain Join(FpDomainAnalysis.Domain left, FpDomainAnalysis.Domain right)
    => new(Math.Min(left.Lo, right.Lo), Math.Max(left.Hi, right.Hi), true, true);

  private static bool? Decide(
      IrCmpPred pred,
      FpDomainAnalysis.Domain lhs,
      FpDomainAnalysis.Domain rhs) => pred switch {
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
