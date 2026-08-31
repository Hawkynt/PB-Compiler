namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Strength-reduces 16-bit constant arithmetic only after the candidate formula has been exhaustively
/// checked over the complete 16-bit input domain. The verifier is deliberately part of the compiler:
/// adding a clever formula without proving every input simply makes the candidate unavailable.
/// </summary>
public static class VerifiedArithmeticLowering {

  private static readonly Dictionary<short, MultiplyPlan?> _multiplyPlans = [];
  private static readonly Dictionary<short, bool> _signedDivisors = [];

  /// <summary>Rewrites verified constant multiplies and signed power-of-two divisions/remainders.</summary>
  public static int Run(IrFunction function) {
    ArgumentNullException.ThrowIfNull(function);
    var changed = 0;
    foreach (var binary in function.AllInstructions.OfType<IrBinary>().ToArray()) {
      if (binary.Parent is null || binary.Type.Bits != 16 || !binary.Type.IsInteger)
        continue;
      IrValue? replacement = binary.Op switch {
        IrBinaryOp.Mul => LowerMultiply(binary),
        IrBinaryOp.SDiv => LowerSignedDivision(binary, remainder: false),
        IrBinaryOp.SRem => LowerSignedDivision(binary, remainder: true),
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

    // The simple 0/1/-1/power-of-two cases already belong to InstCombine. This pass owns the first
    // non-trivial family: 2^k +/- 1, optionally negated, because it becomes one shift and one add/sub.
    var factor = unchecked((short)constant.ZeroExtended);
    if (!TryVerifiedMultiplyPlan(factor, out var plan))
      return null;

    var shifted = Emit(binary, IrBinaryOp.Shl, value, C(binary.Type, plan.Shift));
    IrValue result = Emit(binary, plan.Subtract ? IrBinaryOp.Sub : IrBinaryOp.Add, shifted, value);
    if (plan.Negate)
      result = Emit(binary, IrBinaryOp.Sub, C(binary.Type, 0), result);
    return result;
  }

  private static IrValue? LowerSignedDivision(IrBinary binary, bool remainder) {
    if (binary.Rhs is not IrConstantInt constant)
      return null;
    var divisor = unchecked((short)constant.ZeroExtended);
    if (!TryVerifiedSignedDivisor(divisor, out var shift, out var negative))
      return null;

    var type = binary.Type;
    var sign = Emit(binary, IrBinaryOp.AShr, binary.Lhs, C(type, type.Bits - 1));
    var bias = Emit(binary, IrBinaryOp.And, sign, C(type, (1 << shift) - 1));
    var adjusted = Emit(binary, IrBinaryOp.Add, binary.Lhs, bias);
    IrValue quotient = Emit(binary, IrBinaryOp.AShr, adjusted, C(type, shift));
    if (negative)
      quotient = Emit(binary, IrBinaryOp.Sub, C(type, 0), quotient);
    if (!remainder)
      return quotient;

    IrValue product = Emit(binary, IrBinaryOp.Shl, quotient, C(type, shift));
    if (negative)
      product = Emit(binary, IrBinaryOp.Sub, C(type, 0), product);
    return Emit(binary, IrBinaryOp.Sub, binary.Lhs, product);
  }

  private static IrBinary Emit(IrInstruction before, IrBinaryOp op, IrValue left, IrValue right)
    => before.Parent!.InsertBefore(new IrBinary(op, left, right), before);

  private static IrConstantInt C(IrType type, long value) => new(type, IrConstFold.Wrap(value, type));

  private static bool TryVerifiedMultiplyPlan(short factor, out MultiplyPlan plan) {
    if (!_multiplyPlans.TryGetValue(factor, out var cached)) {
      cached = CreateMultiplyPlan(factor);
      if (cached is { } candidate && !VerifyMultiply(factor, candidate))
        cached = null;
      _multiplyPlans[factor] = cached;
    }
    plan = cached ?? default;
    return cached is not null;
  }

  private static MultiplyPlan? CreateMultiplyPlan(short factor) {
    if (factor is 0 or 1 or -1)
      return null;
    var magnitude = Math.Abs((int)factor);
    if (IsPowerOfTwo(magnitude))
      return null;

    for (var shift = 1; shift < 15; ++shift) {
      var power = 1 << shift;
      if (magnitude == power + 1)
        return new(shift, Subtract: false, Negate: factor < 0);
      if (magnitude == power - 1)
        return new(shift, Subtract: true, Negate: factor < 0);
    }
    return null;
  }

  private static bool VerifyMultiply(short factor, MultiplyPlan plan) {
    for (var raw = 0; raw <= ushort.MaxValue; ++raw) {
      var x = (ushort)raw;
      var shifted = (ushort)(x << plan.Shift);
      var candidate = plan.Subtract ? (ushort)(shifted - x) : (ushort)(shifted + x);
      if (plan.Negate)
        candidate = (ushort)-candidate;
      var expected = (ushort)(x * unchecked((ushort)factor));
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

    if (!_signedDivisors.TryGetValue(divisor, out var verified)) {
      verified = VerifySignedDivisor(divisor, shift);
      _signedDivisors[divisor] = verified;
    }
    return verified;
  }

  private static bool VerifySignedDivisor(short divisor, int shift) {
    var mask = (1 << shift) - 1;
    for (var raw = short.MinValue; raw <= short.MaxValue; ++raw) {
      var x = (short)raw;
      var sign = (short)(x >> 15);
      var adjusted = (short)(x + (sign & mask));
      var quotient = (short)(adjusted >> shift);
      if (divisor < 0)
        quotient = (short)-quotient;
      if (quotient != x / divisor)
        return false;
      var product = (short)(quotient * divisor);
      var candidateRemainder = (short)(x - product);
      if (candidateRemainder != x % divisor)
        return false;
    }
    return true;
  }

  private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

  private readonly record struct MultiplyPlan(int Shift, bool Subtract, bool Negate);
}
