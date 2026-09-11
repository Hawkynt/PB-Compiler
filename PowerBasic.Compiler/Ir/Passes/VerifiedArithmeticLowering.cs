namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Strength-reduces 16-bit constant arithmetic only after the candidate formula has been exhaustively
/// checked over the complete 16-bit input domain. The verifier is deliberately part of the compiler:
/// adding a clever formula without proving every input simply makes the candidate unavailable.
/// </summary>
public static class VerifiedArithmeticLowering {

  private static readonly Dictionary<short, MultiplyPlan?> _multiplyPlans = [];
  private static readonly Dictionary<short, bool> _signedDivisors = [];
  private static readonly Dictionary<short, ReciprocalPlan?> _signedReciprocalPlans = [];
  private static readonly object _verificationLock = new();

  /// <summary>
  /// Rewrites verified constant multiplies and signed divisions/remainders. Reciprocal-multiply
  /// lowering is a SPEED trade (more IR/code for fewer divide cycles), so callers must opt into it.
  /// </summary>
  public static int Run(IrFunction function, bool optimizeForSpeed = false) {
    ArgumentNullException.ThrowIfNull(function);
    var changed = 0;
    foreach (var binary in function.AllInstructions.OfType<IrBinary>().ToArray()) {
      if (binary.Parent is null || binary.Type.Bits != 16 || !binary.Type.IsInteger)
        continue;
      IrValue? replacement = binary.Op switch {
        IrBinaryOp.Mul => LowerMultiply(binary),
        IrBinaryOp.SDiv => LowerSignedDivision(binary, remainder: false, optimizeForSpeed),
        IrBinaryOp.SRem => LowerSignedDivision(binary, remainder: true, optimizeForSpeed),
        _ => null,
      };
      if (replacement is null)
        continue;
      binary.ReplaceAllUsesWith(replacement);
      if (binary.HasNoUsers)
        binary.EraseFromParent();
      ++changed;
    }
    return changed;
  }

  private static IrValue? LowerMultiply(IrBinary binary) {
    IrValue value;
    IrConstantInt constant;
    if (binary.Rhs is IrConstantInt right) { value = binary.Lhs; constant = right; }
    else if (binary.Lhs is IrConstantInt left) { value = binary.Rhs; constant = left; }
    else return null;

    var factor = unchecked((short)constant.ZeroExtended);
    if (!TryVerifiedMultiplyPlan(factor, out var plan))
      return null;

    var first = EmitShift(binary, value, plan.FirstShift);
    IrValue result = first;
    if (plan.SecondShift is { } secondShift) {
      var second = EmitShift(binary, value, secondShift);
      result = Emit(binary, plan.Subtract ? IrBinaryOp.Sub : IrBinaryOp.Add, first, second);
    }
    if (plan.Negate)
      result = Emit(binary, IrBinaryOp.Sub, C(binary.Type, 0), result);
    return result;
  }

  private static IrValue? LowerSignedDivision(IrBinary binary, bool remainder, bool optimizeForSpeed) {
    if (binary.Rhs is not IrConstantInt constant)
      return null;
    var divisor = unchecked((short)constant.ZeroExtended);

    if (TryVerifiedSignedDivisor(divisor, out var shift, out var negative))
      return LowerSignedPowerOfTwo(binary, divisor, shift, negative, remainder);

    if (!optimizeForSpeed || !TryVerifiedSignedReciprocal(divisor, out var reciprocal))
      return null;
    return LowerSignedReciprocal(binary, divisor, reciprocal, remainder);
  }

  private static IrValue LowerSignedPowerOfTwo(IrBinary binary, short divisor, int shift, bool negative, bool remainder) {
    var type = binary.Type;
    var mask = (1 << shift) - 1;
    var sign = Emit(binary, IrBinaryOp.AShr, binary.Lhs, C(type, type.Bits - 1));
    var bias = Emit(binary, IrBinaryOp.And, sign, C(type, mask));
    var adjusted = Emit(binary, IrBinaryOp.Add, binary.Lhs, bias);

    if (remainder) {
      // For truncation-toward-zero division, ((x + bias) & mask) - bias is the signed remainder.
      // It is independent of the divisor's sign and avoids constructing a quotient only to multiply it back.
      var masked = Emit(binary, IrBinaryOp.And, adjusted, C(type, mask));
      return Emit(binary, IrBinaryOp.Sub, masked, bias);
    }

    IrValue quotient = Emit(binary, IrBinaryOp.AShr, adjusted, C(type, shift));
    if (negative)
      quotient = Emit(binary, IrBinaryOp.Sub, C(type, 0), quotient);
    return quotient;
  }

  /// <summary>
  /// O0056: materializes the target-neutral spelling of signed high-half multiplication rather than
  /// inventing an IR pseudo-op: <c>trunc((sext x * magic) ashr 16)</c>. C and LLVM can optimize that
  /// directly; the x86-16 selector recognizes the exact single-use shape and selects the DX half of
  /// one accumulator IMUL, so the widening never becomes a 32-bit runtime multiply there.
  /// </summary>
  private static IrValue LowerSignedReciprocal(IrBinary binary, short divisor, ReciprocalPlan plan, bool remainder) {
    var type = binary.Type;
    var wide = IrType.I32;
    var extended = Cast(binary, IrCastOp.SExt, binary.Lhs, wide);
    var product = Emit(binary, IrBinaryOp.Mul, extended, C(wide, plan.Magic));
    var highWide = Emit(binary, IrBinaryOp.AShr, product, C(wide, 16));
    IrValue high = Cast(binary, IrCastOp.Trunc, highWide, type);
    if (plan.AddDividend)
      high = Emit(binary, IrBinaryOp.Add, high, binary.Lhs);
    if (plan.Shift > 0)
      high = Emit(binary, IrBinaryOp.AShr, high, C(type, plan.Shift));
    var sign = Emit(binary, IrBinaryOp.AShr, binary.Lhs, C(type, 15));
    var quotient = Emit(binary, IrBinaryOp.Sub, high, sign);
    if (!remainder)
      return quotient;

    var quotientTimesDivisor = Emit(binary, IrBinaryOp.Mul, quotient, C(type, divisor));
    return Emit(binary, IrBinaryOp.Sub, binary.Lhs, quotientTimesDivisor);
  }

  private static IrValue EmitShift(IrInstruction before, IrValue value, int shift)
    => shift == 0 ? value : Emit(before, IrBinaryOp.Shl, value, C(before.Type, shift));

  private static IrBinary Emit(IrInstruction before, IrBinaryOp op, IrValue left, IrValue right)
    => before.Parent!.InsertBefore(new IrBinary(op, left, right), before);

  private static IrCast Cast(IrInstruction before, IrCastOp op, IrValue value, IrType type)
    => before.Parent!.InsertBefore(new IrCast(op, value, type), before);

  private static IrConstantInt C(IrType type, long value) => new(type, IrConstFold.Wrap(value, type));

  private static bool TryVerifiedMultiplyPlan(short factor, out MultiplyPlan plan) {
    MultiplyPlan? cached;
    lock (_verificationLock) {
      if (!_multiplyPlans.TryGetValue(factor, out cached)) {
        cached = CreateMultiplyPlan(factor);
        if (cached is { } candidate && !VerifyMultiply(factor, candidate))
          cached = null;
        _multiplyPlans[factor] = cached;
      }
    }
    plan = cached ?? default;
    return cached is not null;
  }

  private static MultiplyPlan? CreateMultiplyPlan(short factor) {
    if (factor is 0 or 1 or -1)
      return null;

    var magnitude = Math.Abs((int)factor);
    if (IsPowerOfTwo(magnitude)) {
      // Positive powers (and 0x8000, whose signed spelling is -32768) are already handled by InstCombine.
      // Other negative powers are not power-of-two bit patterns, so admit shift+negate here after proof.
      if (factor > 0 || factor == short.MinValue)
        return null;
      return new(System.Numerics.BitOperations.TrailingZeroCount((uint)magnitude), null, Subtract: false, Negate: true);
    }

    MultiplyPlan? best = null;
    var bestCost = int.MaxValue;
    for (var highShift = 1; highShift < 16; ++highShift) {
      for (var lowShift = 0; lowShift < highShift; ++lowShift) {
        Consider(new(highShift, lowShift, Subtract: false, Negate: false));
        Consider(new(highShift, lowShift, Subtract: true, Negate: false));
        Consider(new(lowShift, highShift, Subtract: true, Negate: false));
        Consider(new(highShift, lowShift, Subtract: false, Negate: true));
      }
    }
    return best;

    void Consider(MultiplyPlan candidate) {
      if (EvaluateFactor(candidate) != factor)
        return;
      var cost = PlanCost(candidate);
      if (cost >= bestCost)
        return;
      best = candidate;
      bestCost = cost;
    }
  }

  private static short EvaluateFactor(MultiplyPlan plan) {
    var first = 1 << plan.FirstShift;
    var value = first;
    if (plan.SecondShift is { } secondShift) {
      var second = 1 << secondShift;
      value = plan.Subtract ? first - second : first + second;
    }
    if (plan.Negate)
      value = -value;
    return unchecked((short)value);
  }

  private static int PlanCost(MultiplyPlan plan) {
    var cost = plan.FirstShift == 0 ? 0 : 1;
    if (plan.SecondShift is { } secondShift)
      cost += (secondShift == 0 ? 0 : 1) + 1;
    if (plan.Negate)
      ++cost;
    return cost;
  }

  private static bool VerifyMultiply(short factor, MultiplyPlan plan) {
    for (var raw = 0; raw <= ushort.MaxValue; ++raw) {
      var x = (ushort)raw;
      var first = unchecked((ushort)(x << plan.FirstShift));
      var candidate = first;
      if (plan.SecondShift is { } secondShift) {
        var second = unchecked((ushort)(x << secondShift));
        candidate = plan.Subtract
          ? unchecked((ushort)(first - second))
          : unchecked((ushort)(first + second));
      }
      if (plan.Negate)
        candidate = unchecked((ushort)-candidate);
      var expected = unchecked((ushort)(x * unchecked((ushort)factor)));
      if (candidate != expected)
        return false;
    }
    return true;
  }

  private static bool TryVerifiedSignedDivisor(short divisor, out int shift, out bool negative) {
    shift = 0;
    negative = divisor < 0;
    if (divisor is 0 or 1 or -1)
      return false; // -32768 / -1 must retain its overflow trap; +/-1 are already canonical.
    var magnitude = Math.Abs((int)divisor);
    if (!IsPowerOfTwo(magnitude))
      return false;
    shift = System.Numerics.BitOperations.TrailingZeroCount((uint)magnitude);

    bool verified;
    lock (_verificationLock) {
      if (!_signedDivisors.TryGetValue(divisor, out verified)) {
        verified = VerifySignedDivisor(divisor, shift);
        _signedDivisors[divisor] = verified;
      }
    }
    return verified;
  }

  private static bool VerifySignedDivisor(short divisor, int shift) {
    var mask = (1 << shift) - 1;
    for (var raw = (int)short.MinValue; raw <= short.MaxValue; ++raw) {
      var x = (short)raw;
      var sign = (short)(x >> 15);
      var bias = sign & mask;
      var adjusted = unchecked((short)(x + bias));
      var quotient = (short)(adjusted >> shift);
      if (divisor < 0)
        quotient = unchecked((short)-quotient);
      if (quotient != x / divisor)
        return false;

      var candidateRemainder = unchecked((short)((adjusted & mask) - bias));
      if (candidateRemainder != x % divisor)
        return false;
    }
    return true;
  }

  private static bool TryVerifiedSignedReciprocal(short divisor, out ReciprocalPlan plan) {
    ReciprocalPlan? cached;
    lock (_verificationLock) {
      if (!_signedReciprocalPlans.TryGetValue(divisor, out cached)) {
        cached = CreateSignedReciprocalPlan(divisor);
        if (cached is { } candidate && !VerifySignedReciprocal(divisor, candidate))
          cached = null;
        _signedReciprocalPlans[divisor] = cached;
      }
    }
    plan = cached ?? default;
    return cached is not null;
  }

  /// <summary>
  /// Granlund-Montgomery / Hacker's Delight signed magic-number derivation for the positive Int16
  /// divisor slice O0056 currently promises. The output is specification data, not copied
  /// implementation structure; every result is independently verified below before becoming usable.
  /// </summary>
  private static ReciprocalPlan? CreateSignedReciprocalPlan(short divisor) {
    if (divisor < 2 || IsPowerOfTwo(divisor))
      return null;

    const int width = 16;
    const long two15 = 1L << (width - 1);
    var ad = (long)divisor;
    var anc = two15 - 1 - two15 % ad;
    var p = width - 1;
    var q1 = two15 / anc;
    var r1 = two15 - q1 * anc;
    var q2 = two15 / ad;
    var r2 = two15 - q2 * ad;
    long delta;
    do {
      ++p;
      q1 *= 2;
      r1 *= 2;
      if (r1 >= anc) { ++q1; r1 -= anc; }
      q2 *= 2;
      r2 *= 2;
      if (r2 >= ad) { ++q2; r2 -= ad; }
      delta = ad - r2;
    } while (q1 < delta || (q1 == delta && r1 == 0));

    var shift = p - width;
    if (shift is < 0 or > 15)
      return null;
    var magic = unchecked((short)(q2 + 1));
    return new(magic, shift, AddDividend: magic < 0);
  }

  private static bool VerifySignedReciprocal(short divisor, ReciprocalPlan plan) {
    for (var raw = (int)short.MinValue; raw <= short.MaxValue; ++raw) {
      var x = (short)raw;
      var high = unchecked((short)(((int)x * plan.Magic) >> 16));
      if (plan.AddDividend)
        high = unchecked((short)(high + x));
      var shifted = (short)(high >> plan.Shift);
      var quotient = unchecked((short)(shifted - (x >> 15)));
      if (quotient != x / divisor)
        return false;
      var remainder = unchecked((short)(x - unchecked((short)(quotient * divisor))));
      if (remainder != x % divisor)
        return false;
    }
    return true;
  }

  private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

  private readonly record struct MultiplyPlan(int FirstShift, int? SecondShift, bool Subtract, bool Negate);
  private readonly record struct ReciprocalPlan(short Magic, int Shift, bool AddDividend);
}
