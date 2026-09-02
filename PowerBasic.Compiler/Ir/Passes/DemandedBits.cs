namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Eliminates work that only affects bits discarded by a later truncation. This is deliberately a
/// target-neutral SSA transform: it describes which result bits are observable, not how any target
/// spells the operation. The selector therefore receives a smaller graph and remains free to choose
/// the best instruction sequence for its own CPU and cost model.
/// </summary>
public static class DemandedBits {

  /// <summary>Runs demanded-bit simplification over one function; returns the number of rewrites.</summary>
  public static int Run(IrFunction function) {
    var changes = 0;
    foreach (var cast in function.AllInstructions.OfType<IrCast>().ToList()) {
      if (cast.Parent is null || cast.Op != IrCastOp.Trunc || !cast.Type.IsInteger)
        continue;

      if (TryCollapseExtensionRoundTrip(cast)) {
        ++changes;
        continue;
      }

      if (TryDropDiscardedBitOperation(cast))
        ++changes;
    }
    return changes;
  }

  /// <summary>
  /// <c>trunc (zext/sext x) to sizeof(x)</c> is exactly <c>x</c>. Signedness is interpretation rather
  /// than storage in this IR, so <see cref="IrType.SameStorage"/> is the right equality relation.
  /// </summary>
  private static bool TryCollapseExtensionRoundTrip(IrCast trunc) {
    if (trunc.Value is not IrCast { Op: IrCastOp.ZExt or IrCastOp.SExt } extension
        || !extension.Value.Type.SameStorage(trunc.Type))
      return false;

    trunc.ReplaceAllUsesWith(extension.Value);
    trunc.EraseFromParent();
    return true;
  }

  /// <summary>
  /// A truncation to N bits only observes the low N bits. AND with ones there, or OR/XOR with zeroes
  /// there, cannot affect the truncated value and is therefore pure abstraction overhead.
  /// </summary>
  private static bool TryDropDiscardedBitOperation(IrCast trunc) {
    if (trunc.Value is not IrBinary binary || !binary.Type.IsInteger || binary.Type.Bits <= trunc.Type.Bits)
      return false;
    if (binary.Op is not (IrBinaryOp.And or IrBinaryOp.Or or IrBinaryOp.Xor))
      return false;
    if (!TryConstantOperand(binary, out var value, out var other))
      return false;

    var demanded = LowMask(trunc.Type.Bits);
    var constant = unchecked((ulong)value.Value);
    var irrelevant = binary.Op switch {
      IrBinaryOp.And => (constant & demanded) == demanded,
      IrBinaryOp.Or or IrBinaryOp.Xor => (constant & demanded) == 0,
      _ => false,
    };
    if (!irrelevant)
      return false;

    trunc.SetOperand(0, other);
    return true;
  }

  private static bool TryConstantOperand(IrBinary binary, out IrConstantInt constant, out IrValue other) {
    if (binary.Lhs is IrConstantInt left) {
      constant = left;
      other = binary.Rhs;
      return true;
    }
    if (binary.Rhs is IrConstantInt right) {
      constant = right;
      other = binary.Lhs;
      return true;
    }

    constant = null!;
    other = null!;
    return false;
  }

  private static ulong LowMask(int bits) => bits >= 64 ? ulong.MaxValue : (1UL << bits) - 1;
}
