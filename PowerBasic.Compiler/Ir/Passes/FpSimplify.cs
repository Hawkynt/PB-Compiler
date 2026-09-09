using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0346/O0347 — strict floating classification simplification and proven mixed-precision narrowing.
/// Branch-refined integer ranges strengthen classification only at integer-to-float conversion sites;
/// algebraically collapsed FP intervals are deliberately not treated as strict finite/non-zero proofs,
/// because an intermediate FP operation may overflow or underflow before the algebraic result is formed.
/// </summary>
public static class FpSimplify {

  private readonly record struct Facts(bool NonNaN, bool Finite, bool NonNegative, bool Positive, bool NonZero);

  public static int Run(IrFunction function) => Run(function, IrFastMathFlags.None);

  internal static int Run(IrFunction function, IrFastMathFlags assumptions) {
    if (function.HasErrorHandler || function.HasInlineAsm)
      return 0;
    var ranges = IrRangeAnalysis.Build(function);
    var changes = SimplifyClassifications(function, assumptions, ranges);
    changes += NarrowDemandedPrecision(function, assumptions);
    return changes;
  }

  private static int SimplifyClassifications(IrFunction function, IrFastMathFlags assumptions,
      IrRangeAnalysis? ranges) {
    var changes = 0;
    var memo = new Dictionary<IrValue, Facts>(ReferenceEqualityComparer.Instance);
    foreach (var cmp in function.AllInstructions.OfType<IrCmp>().ToList()) {
      if (cmp.Parent is null || cmp.Pred is < IrCmpPred.Foeq or > IrCmpPred.Foge)
        continue;
      if (TryDecide(cmp, assumptions, ranges, memo) is not { } answer)
        continue;
      cmp.ReplaceAllUsesWith(new IrConstantInt(IrType.I1, answer ? 1 : 0));
      cmp.EraseFromParent();
      ++changes;
    }
    return changes;
  }

  private static bool? TryDecide(IrCmp cmp, IrFastMathFlags assumptions, IrRangeAnalysis? ranges,
      Dictionary<IrValue, Facts> memo) {
    if (ReferenceEquals(cmp.Lhs, cmp.Rhs)) {
      var facts = FactsOf(cmp.Lhs, assumptions, memo, []);
      return cmp.Pred switch {
        IrCmpPred.Fone or IrCmpPred.Folt or IrCmpPred.Fogt => false,
        IrCmpPred.Foeq or IrCmpPred.Fole or IrCmpPred.Foge when facts.NonNaN
          || (assumptions & IrFastMathFlags.NoNaNs) != 0 => true,
        _ => null,
      };
    }

    IrValue value;
    IrCmpPred predicate;
    if (IsZero(cmp.Rhs)) {
      value = cmp.Lhs;
      predicate = cmp.Pred;
    } else if (IsZero(cmp.Lhs)) {
      value = cmp.Rhs;
      predicate = Flip(cmp.Pred);
    } else
      return null;

    var factsOfValue = FactsOf(value, assumptions, memo, []);
    if (ranges is not null && cmp.Parent is { } block
        && value is IrCast { Op: IrCastOp.SIToFP or IrCastOp.UIToFP } cast) {
      var range = ranges.RangeAt(cast.Value, block);
      if (!range.IsTop && !range.IsEmpty) {
        factsOfValue = factsOfValue with {
          NonNaN = true,
          Finite = true,
          NonNegative = range.Lo >= 0,
          Positive = range.Lo > 0,
          NonZero = range.Lo > 0 || range.Hi < 0,
        };
      }
    }
    if ((assumptions & IrFastMathFlags.NoNaNs) != 0)
      factsOfValue = factsOfValue with { NonNaN = true };
    if ((assumptions & IrFastMathFlags.NoInfs) != 0 && factsOfValue.NonNaN)
      factsOfValue = factsOfValue with { Finite = true };
    if (!factsOfValue.NonNaN)
      return null;

    if (factsOfValue.Positive || (factsOfValue.NonNegative && factsOfValue.NonZero))
      return predicate switch {
        IrCmpPred.Fogt or IrCmpPred.Foge or IrCmpPred.Fone => true,
        IrCmpPred.Folt or IrCmpPred.Fole or IrCmpPred.Foeq => false,
        _ => null,
      };
    if (factsOfValue.NonNegative)
      return predicate switch {
        IrCmpPred.Foge => true,
        IrCmpPred.Folt => false,
        _ => null,
      };
    return null;
  }

  private static IrCmpPred Flip(IrCmpPred predicate) => predicate switch {
    IrCmpPred.Folt => IrCmpPred.Fogt,
    IrCmpPred.Fole => IrCmpPred.Foge,
    IrCmpPred.Fogt => IrCmpPred.Folt,
    IrCmpPred.Foge => IrCmpPred.Fole,
    _ => predicate,
  };

  private static bool IsZero(IrValue value) => value is IrConstantFloat { Value: 0.0 };

  private static Facts FactsOf(IrValue value, IrFastMathFlags assumptions,
      Dictionary<IrValue, Facts> memo, HashSet<IrValue> visiting) {
    if (memo.TryGetValue(value, out var cached))
      return cached;
    if (!visiting.Add(value))
      return default;

    var facts = value switch {
      IrConstantFloat constant => ConstantFacts(constant.Value),
      IrCast { Op: IrCastOp.SIToFP or IrCastOp.UIToFP } cast => IntegerCastFacts(cast),
      IrCast { Op: IrCastOp.FPExt } cast => FactsOf(cast.Value, assumptions, memo, visiting),
      IrCast { Op: IrCastOp.FPTrunc } cast => TruncatedFacts(FactsOf(cast.Value, assumptions, memo, visiting)),
      IrBinary { Op: IrBinaryOp.FMul } binary when ReferenceEquals(binary.Lhs, binary.Rhs)
        => SquareFacts(FactsOf(binary.Lhs, assumptions, memo, visiting)),
      IrCall call when IrFpMath.TryGet(call, out var kind) && kind == IrFpMathFunction.Sqrt
                       && call.Args.FirstOrDefault() is { } argument
        => SqrtFacts(FactsOf(argument, assumptions, memo, visiting)),
      _ => default,
    };

    visiting.Remove(value);
    if (value.Type.IsIeeeFloat) {
      if ((assumptions & IrFastMathFlags.NoNaNs) != 0)
        facts = facts with { NonNaN = true };
      if ((assumptions & IrFastMathFlags.NoInfs) != 0 && facts.NonNaN)
        facts = facts with { Finite = true };
    }
    memo[value] = facts;
    return facts;
  }

  private static Facts ConstantFacts(double value) {
    var nonNaN = !double.IsNaN(value);
    var finite = double.IsFinite(value);
    return new(nonNaN, finite, nonNaN && value >= 0.0, nonNaN && value > 0.0, nonNaN && value != 0.0);
  }

  private static Facts IntegerCastFacts(IrCast cast) {
    if (cast.Value is IrConstantInt constant) {
      if (cast.Op == IrCastOp.UIToFP) {
        var value = constant.ZeroExtended;
        return new(true, true, true, value != 0, value != 0);
      }
      return new(true, true, constant.Value >= 0, constant.Value > 0, constant.Value != 0);
    }
    return cast.Op == IrCastOp.UIToFP
      ? new(true, true, true, false, false)
      : new(true, true, false, false, false);
  }

  private static Facts TruncatedFacts(Facts source)
    => new(source.NonNaN, false, source.NonNegative, false, false);

  private static Facts SquareFacts(Facts source)
    => new(source.NonNaN, false, source.NonNaN, false, false);

  private static Facts SqrtFacts(Facts source) {
    var defined = source.NonNaN && source.NonNegative;
    return new(defined, defined && source.Finite, defined, defined && source.Positive, defined && source.Positive);
  }

  private static int NarrowDemandedPrecision(IrFunction function, IrFastMathFlags assumptions) {
    var changes = 0;
    foreach (var trunc in function.AllInstructions.OfType<IrCast>().ToList()) {
      if (trunc.Parent is null || trunc is not { Op: IrCastOp.FPTrunc, Type.Bits: 32 }
          || trunc.Value is not IrBinary { Op: IrBinaryOp.FMul, Type.Bits: 64, Users.Count: 1 } wide
          || !ReferenceEquals(wide.Parent, trunc.Parent)
          || wide.Lhs is not IrCast { Op: IrCastOp.FPExt, Type.Bits: 64 } left || left.Value.Type.Bits != 32
          || wide.Rhs is not IrCast { Op: IrCastOp.FPExt, Type.Bits: 64 } right || right.Value.Type.Bits != 32)
        continue;

      var memo = new Dictionary<IrValue, Facts>(ReferenceEqualityComparer.Instance);
      var leftFacts = FactsOf(left.Value, assumptions, memo, []);
      var rightFacts = FactsOf(right.Value, assumptions, memo, []);
      if (!leftFacts.Finite || !leftFacts.NonNaN || !rightFacts.Finite || !rightFacts.NonNaN)
        continue;

      var block = trunc.Parent;
      var narrow = block.InsertBefore(new IrBinary(IrBinaryOp.FMul, left.Value, right.Value) {
        FastMathFlags = wide.FastMathFlags,
      }, trunc);
      trunc.ReplaceAllUsesWith(narrow);
      trunc.EraseFromParent();
      if (wide.HasNoUsers) wide.EraseFromParent();
      if (left.HasNoUsers) left.EraseFromParent();
      if (!ReferenceEquals(left, right) && right.HasNoUsers) right.EraseFromParent();
      ++changes;
    }
    return changes;
  }
}
