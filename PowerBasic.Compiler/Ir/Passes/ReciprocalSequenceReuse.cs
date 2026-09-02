namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0338 — replaces repeated floating divisions by the same exact power-of-two constant with
/// multiplications by its exact reciprocal. General reciprocal reuse needs an explicit fast-math
/// contract because <c>x / d</c> and <c>x * (1/d)</c> otherwise need not round identically.
/// </summary>
public static class ReciprocalSequenceReuse {

  /// <summary>Rewrites repeated exact reciprocal groups; returns the number of divisions replaced.</summary>
  public static int Run(IrFunction fn) {
    ArgumentNullException.ThrowIfNull(fn);
    if (fn.HasErrorHandler || fn.HasInlineAsm)
      return 0;

    var groups = fn.AllInstructions
      .OfType<IrBinary>()
      .Where(binary => binary.Op == IrBinaryOp.FDiv && binary.Rhs is IrConstantFloat divisor
                       && TryReciprocal(divisor, out _))
      .GroupBy(binary => Key((IrConstantFloat)binary.Rhs))
      .Where(group => group.Count() > 1)
      .ToList();

    var replaced = 0;
    foreach (var group in groups) {
      var divisor = (IrConstantFloat)group.First().Rhs;
      if (!TryReciprocal(divisor, out var reciprocal))
        continue;
      foreach (var division in group.ToList()) {
        var block = division.Parent;
        if (block is null)
          continue;
        var multiply = block.InsertBefore(new IrBinary(IrBinaryOp.FMul, division.Lhs,
          new IrConstantFloat(division.Type, reciprocal)), division);
        division.ReplaceAllUsesWith(multiply);
        division.EraseFromParent();
        ++replaced;
      }
    }
    return replaced;
  }

  private static (int Bits, long Pattern) Key(IrConstantFloat value) => value.Type.Bits switch {
    32 => (32, BitConverter.SingleToInt32Bits((float)value.Value)),
    64 => (64, BitConverter.DoubleToInt64Bits(value.Value)),
    _ => (value.Type.Bits, 0),
  };

  private static bool TryReciprocal(IrConstantFloat divisor, out double reciprocal) {
    reciprocal = 0;
    if (divisor.Type is not { Kind: IrTypeKind.Float, Format: IrFloatFormat.Ieee, Bits: 32 or 64 }
        || divisor.Value == 0 || !double.IsFinite(divisor.Value) || !IsPowerOfTwo(divisor))
      return false;

    reciprocal = 1.0 / divisor.Value;
    if (divisor.Type.Bits == 32)
      reciprocal = (float)reciprocal;
    return double.IsFinite(reciprocal) && reciprocal != 0;
  }

  private static bool IsPowerOfTwo(IrConstantFloat value) {
    if (value.Type.Bits == 32) {
      var pattern = (uint)BitConverter.SingleToInt32Bits(MathF.Abs((float)value.Value)) & 0x7fff_ffffU;
      var exponent = pattern & 0x7f80_0000U;
      var fraction = pattern & 0x007f_ffffU;
      return exponent == 0 ? fraction != 0 && (fraction & (fraction - 1)) == 0 : fraction == 0;
    }

    var bits = (ulong)BitConverter.DoubleToInt64Bits(Math.Abs(value.Value)) & 0x7fff_ffff_ffff_ffffUL;
    var doubleExponent = bits & 0x7ff0_0000_0000_0000UL;
    var doubleFraction = bits & 0x000f_ffff_ffff_ffffUL;
    return doubleExponent == 0
      ? doubleFraction != 0 && (doubleFraction & (doubleFraction - 1)) == 0
      : doubleFraction == 0;
  }
}
