using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0346/O0347 — strict floating classification simplification and proven mixed-precision narrowing.
/// Branch-refined integer ranges strengthen classification at integer-to-float conversion sites, while
/// <see cref="FpDomainAnalysis"/> carries those ranges through supported binary32/binary64 expressions.
/// Algebraically collapsed FP intervals are deliberately not treated as strict finite/non-zero proofs,
/// because an intermediate FP operation may overflow or underflow before the algebraic result is formed.
/// </summary>
public static class FpSimplify {

  private readonly record struct Facts(
    bool NonNaN,
    bool Finite,
    bool NonNegative,
    bool NonPositive,
    bool Positive,
    bool Negative,
    bool NonZero);

  public static int Run(IrFunction function) => Run(function, IrFastMathFlags.None);

  internal static int Run(IrFunction function, IrFastMathFlags assumptions) {
    if (function.HasErrorHandler || function.HasInlineAsm)
      return 0;
    var ranges = IrRangeAnalysis.Build(function);
    var domains = FpDomainAnalysis.Build(function);
    var changes = SimplifyClassifications(function, assumptions, ranges, domains);
    changes += NarrowDemandedPrecision(function, assumptions);
    return changes;
  }

  private static int SimplifyClassifications(IrFunction function, IrFastMathFlags assumptions,
      IrRangeAnalysis? ranges, FpDomainAnalysis? domains) {
    var changes = 0;
    var memo = new Dictionary<IrValue, Facts>(ReferenceEqualityComparer.Instance);
    foreach (var cmp in function.AllInstructions.OfType<IrCmp>().ToList()) {
      if (cmp.Parent is null || cmp.Pred is < IrCmpPred.Foeq or > IrCmpPred.Foge)
        continue;
      if (TryDecide(cmp, assumptions, ranges, domains, memo) is not { } answer)
        continue;
      cmp.ReplaceAllUsesWith(new IrConstantInt(IrType.I1, answer ? 1 : 0));
      cmp.EraseFromParent();
      ++changes;
    }
    return changes;
  }

  private static bool? TryDecide(IrCmp cmp, IrFastMathFlags assumptions, IrRangeAnalysis? ranges,
      FpDomainAnalysis? domains, Dictionary<IrValue, Facts> memo) {
    if (ReferenceEquals(cmp.Lhs, cmp.Rhs)) {
      var facts = FactsOf(cmp.Lhs, assumptions, memo, []);
      return cmp.Pred switch {
        IrCmpPred.Fone or IrCmpPred.Folt or IrCmpPred.Fogt => false,
        IrCmpPred.Foeq or IrCmpPred.Fole or IrCmpPred.Foge when facts.NonNaN
          || (assumptions & IrFastMathFlags.NoNaNs) != 0 => true,
        _ => null,
      };
    }

    if (cmp.Parent is { } comparisonBlock && domains is not null
        && TryDecideDomains(cmp.Pred, domains.DomainAt(cmp.Lhs, comparisonBlock),
          domains.DomainAt(cmp.Rhs, comparisonBlock)) is { } domainAnswer)
      return domainAnswer;

    IrValue value;
    IrCmpPred predicate;
    double constant;
    if (cmp.Rhs is IrConstantFloat rightConstant) {
      if (!rightConstant.TryGetDoubleExact(out constant))
        return null;
      value = cmp.Lhs;
      predicate = cmp.Pred;
    } else if (cmp.Lhs is IrConstantFloat leftConstant) {
      if (!leftConstant.TryGetDoubleExact(out constant))
        return null;
      value = cmp.Rhs;
      predicate = Flip(cmp.Pred);
    } else
      return null;

    // Every predicate represented by IrCmpPred is ordered, so a NaN operand makes it false.
    if (double.IsNaN(constant))
      return false;

    var factsOfValue = FactsOf(value, assumptions, memo, []);
    if (ranges is not null && cmp.Parent is { } block
        && value is IrCast { Op: IrCastOp.SIToFP or IrCastOp.UIToFP } cast) {
      var range = ranges.RangeAt(cast.Value, block);
      if (!range.IsTop && !range.IsEmpty) {
        factsOfValue = factsOfValue with {
          NonNaN = true,
          Finite = true,
          NonNegative = factsOfValue.NonNegative || range.Lo >= 0,
          NonPositive = factsOfValue.NonPositive || range.Hi <= 0,
          Positive = factsOfValue.Positive || range.Lo > 0,
          Negative = factsOfValue.Negative || range.Hi < 0,
          NonZero = factsOfValue.NonZero || range.Lo > 0 || range.Hi < 0,
        };
      }
    }
    if ((assumptions & IrFastMathFlags.NoNaNs) != 0)
      factsOfValue = factsOfValue with { NonNaN = true };
    if ((assumptions & IrFastMathFlags.NoInfs) != 0 && factsOfValue.NonNaN)
      factsOfValue = factsOfValue with { Finite = true };
    if (!factsOfValue.NonNaN)
      return null;

    if (double.IsPositiveInfinity(constant))
      return factsOfValue.Finite ? predicate switch {
        IrCmpPred.Fone or IrCmpPred.Folt or IrCmpPred.Fole => true,
        IrCmpPred.Foeq or IrCmpPred.Fogt or IrCmpPred.Foge => false,
        _ => null,
      } : null;
    if (double.IsNegativeInfinity(constant))
      return factsOfValue.Finite ? predicate switch {
        IrCmpPred.Fone or IrCmpPred.Fogt or IrCmpPred.Foge => true,
        IrCmpPred.Foeq or IrCmpPred.Folt or IrCmpPred.Fole => false,
        _ => null,
      } : null;
    if (constant != 0.0)
      return null;

    if (factsOfValue.Positive || (factsOfValue.NonNegative && factsOfValue.NonZero))
      return predicate switch {
        IrCmpPred.Fogt or IrCmpPred.Foge or IrCmpPred.Fone => true,
        IrCmpPred.Folt or IrCmpPred.Fole or IrCmpPred.Foeq => false,
        _ => null,
      };
    if (factsOfValue.Negative || (factsOfValue.NonPositive && factsOfValue.NonZero))
      return predicate switch {
        IrCmpPred.Folt or IrCmpPred.Fole or IrCmpPred.Fone => true,
        IrCmpPred.Fogt or IrCmpPred.Foge or IrCmpPred.Foeq => false,
        _ => null,
      };
    if (factsOfValue.NonNegative)
      return predicate switch {
        IrCmpPred.Foge => true,
        IrCmpPred.Folt => false,
        _ => null,
      };
    if (factsOfValue.NonPositive)
      return predicate switch {
        IrCmpPred.Fole => true,
        IrCmpPred.Fogt => false,
        _ => null,
      };
    return null;
  }

  private static bool? TryDecideDomains(IrCmpPred predicate, FpDomainAnalysis.Domain left,
      FpDomainAnalysis.Domain right) {
    if (!left.IsKnown || !right.IsKnown)
      return null;

    var disjoint = left.Hi < right.Lo || right.Hi < left.Lo;
    var sameSingleton = left.Lo == left.Hi && right.Lo == right.Hi && left.Lo == right.Lo;
    return predicate switch {
      IrCmpPred.Foeq => disjoint ? false : sameSingleton ? true : null,
      IrCmpPred.Fone => disjoint ? true : sameSingleton ? false : null,
      IrCmpPred.Folt => Order(left, right, strict: true),
      IrCmpPred.Fole => Order(left, right, strict: false),
      IrCmpPred.Fogt => Order(right, left, strict: true),
      IrCmpPred.Foge => Order(right, left, strict: false),
      _ => null,
    };
  }

  private static bool? Order(FpDomainAnalysis.Domain left, FpDomainAnalysis.Domain right, bool strict) {
    if (strict ? left.Hi < right.Lo : left.Hi <= right.Lo)
      return true;
    if (strict ? left.Lo >= right.Hi : left.Lo > right.Hi)
      return false;
    return null;
  }

  private static IrCmpPred Flip(IrCmpPred predicate) => predicate switch {
    IrCmpPred.Folt => IrCmpPred.Fogt,
    IrCmpPred.Fole => IrCmpPred.Foge,
    IrCmpPred.Fogt => IrCmpPred.Folt,
    IrCmpPred.Foge => IrCmpPred.Fole,
    _ => predicate,
  };

  private static Facts FactsOf(IrValue value, IrFastMathFlags assumptions,
      Dictionary<IrValue, Facts> memo, HashSet<IrValue> visiting) {
    if (memo.TryGetValue(value, out var cached))
      return cached;
    if (!visiting.Add(value))
      return default;

    var facts = value switch {
      IrConstantFloat constant => ConstantFacts(constant),
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

  private static Facts ConstantFacts(IrConstantFloat constant) {
    if (constant.TryGetDoubleExact(out var value))
      return ConstantFacts(value);
    if (constant.Type is not { Bits: 80, Format: IrFloatFormat.Ieee } || !constant.Float80.IsCanonical)
      return default;

    var bits = constant.Float80;
    if (bits.IsNaN)
      return default;
    var zero = bits.IsZero;
    var negative = bits.Sign && !zero;
    return new(
      NonNaN: true,
      Finite: bits.IsFinite,
      NonNegative: !negative,
      NonPositive: bits.Sign || zero,
      Positive: !bits.Sign && !zero && bits.IsFinite,
      Negative: negative && bits.IsFinite,
      NonZero: !zero);
  }

  private static Facts ConstantFacts(double value) {
    var nonNaN = !double.IsNaN(value);
    var finite = double.IsFinite(value);
    return new(
      nonNaN,
      finite,
      nonNaN && value >= 0.0,
      nonNaN && value <= 0.0,
      nonNaN && value > 0.0,
      nonNaN && value < 0.0,
      nonNaN && value != 0.0);
  }

  private static Facts IntegerCastFacts(IrCast cast) {
    if (cast.Value is IrConstantInt constant) {
      if (cast.Op == IrCastOp.UIToFP) {
        var unsignedValue = constant.ZeroExtended;
        return new(true, true, true, unsignedValue == 0, unsignedValue != 0, false, unsignedValue != 0);
      }
      var signedValue = SignedValue(constant);
      return new(true, true, signedValue >= 0, signedValue <= 0,
        signedValue > 0, signedValue < 0, signedValue != 0);
    }
    return cast.Op == IrCastOp.UIToFP
      ? new(true, true, true, false, false, false, false)
      : new(true, true, false, false, false, false, false);
  }

  private static long SignedValue(IrConstantInt constant) {
    if (constant.Type.Bits >= 64)
      return constant.Value;
    var shift = 64 - constant.Type.Bits;
    return unchecked((long)(constant.ZeroExtended << shift)) >> shift;
  }

  private static Facts TruncatedFacts(Facts source)
    => new(source.NonNaN, false, source.NonNegative, source.NonPositive, false, false, false);

  private static Facts SquareFacts(Facts source) {
    var zero = source.NonNaN && source.NonNegative && source.NonPositive;
    return new(source.NonNaN, false, source.NonNaN, zero, false, false, false);
  }

  private static Facts SqrtFacts(Facts source) {
    var defined = source.NonNaN && source.NonNegative;
    var zero = defined && source.NonPositive;
    return new(defined, defined && source.Finite, defined, zero,
      defined && source.Positive, false, defined && source.Positive);
  }

  /// <summary>
  /// Narrows one binary32 operation that was performed in binary64 and immediately rounded back to
  /// binary32. For round-to-nearest IEEE arithmetic, binary64 has enough precision that the two-step
  /// result is the correctly rounded binary32 result for add/subtract/multiply/divide when both inputs
  /// are binary32 values. We still require strict finite/non-NaN facts so changing the operation width
  /// cannot alter NaN payload/signalling behavior; division additionally requires a non-zero divisor.
  /// </summary>
  private static int NarrowDemandedPrecision(IrFunction function, IrFastMathFlags assumptions) {
    var changes = 0;
    foreach (var trunc in function.AllInstructions.OfType<IrCast>().ToList()) {
      if (trunc.Parent is null || trunc is not { Op: IrCastOp.FPTrunc, Type: var narrowType }
          || narrowType != IrType.F32
          || trunc.Value is not IrBinary { Op: IrBinaryOp.FAdd or IrBinaryOp.FSub or IrBinaryOp.FMul or IrBinaryOp.FDiv,
            Type: var wideType, Users.Count: 1 } wide
          || wideType != IrType.F64
          || !ReferenceEquals(wide.Parent, trunc.Parent)
          || wide.Lhs is not IrCast { Op: IrCastOp.FPExt, Type: var leftWideType } left
          || leftWideType != IrType.F64 || left.Value.Type != IrType.F32
          || wide.Rhs is not IrCast { Op: IrCastOp.FPExt, Type: var rightWideType } right
          || rightWideType != IrType.F64 || right.Value.Type != IrType.F32)
        continue;

      var memo = new Dictionary<IrValue, Facts>(ReferenceEqualityComparer.Instance);
      var leftFacts = FactsOf(left.Value, assumptions, memo, []);
      var rightFacts = FactsOf(right.Value, assumptions, memo, []);
      if (!leftFacts.Finite || !leftFacts.NonNaN || !rightFacts.Finite || !rightFacts.NonNaN
          || wide.Op == IrBinaryOp.FDiv && !rightFacts.NonZero)
        continue;

      var narrow = trunc.Parent.InsertBefore(new IrBinary(wide.Op, left.Value, right.Value) {
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
