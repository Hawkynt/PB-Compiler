using PowerBasic.Compiler.Ir.Analysis;

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
    ArgumentNullException.ThrowIfNull(function);
    return IrFunctionPassPipeline.RunStandalone(function, "demandedbits", Run);
  }

  /// <summary>Runs demanded-bit simplification using the shared known-bit query domain.</summary>
  public static IrPassResult Run(IrFunction function, IrAnalysisManager analyses) {
    ArgumentNullException.ThrowIfNull(function);
    ArgumentNullException.ThrowIfNull(analyses);
    if (!ReferenceEquals(function, analyses.Function))
      throw new ArgumentException("Analysis manager belongs to a different function.", nameof(analyses));

    var knownBits = analyses.Get(IrAnalyses.KnownBits);
    var changes = 0;
    foreach (var cast in function.AllInstructions.OfType<IrCast>().ToList()) {
      if (cast.Parent is null || cast.Op != IrCastOp.Trunc || !cast.Type.IsInteger)
        continue;

      if (TryCollapseExtensionRoundTrip(cast)) {
        ++changes;
        continue;
      }

      if (TryDropDiscardedBitOperation(cast, knownBits))
        ++changes;
    }

    return changes == 0
      ? IrPassResult.Unchanged
      : IrPassResult.ChangedPreservingSets(changes, IrAnalysisSets.Cfg);
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
  /// A truncation to N bits only observes the low N bits. AND with an operand proven one there, or
  /// OR/XOR with an operand proven zero there, cannot affect the truncated value. The neutral operand
  /// may itself be a non-constant SSA expression; <see cref="IrKnownBitsAnalysis"/> supplies the proof.
  /// </summary>
  private static bool TryDropDiscardedBitOperation(IrCast trunc, IrKnownBitsAnalysis knownBits) {
    if (trunc.Value is not IrBinary binary || !binary.Type.IsInteger || binary.Type.Bits <= trunc.Type.Bits)
      return false;
    if (binary.Op is not (IrBinaryOp.And or IrBinaryOp.Or or IrBinaryOp.Xor))
      return false;

    var demanded = LowMask(trunc.Type.Bits);
    if (!TryNeutralOperand(binary, binary.Lhs, binary.Rhs, demanded, knownBits, out var replacement)
        && !TryNeutralOperand(binary, binary.Rhs, binary.Lhs, demanded, knownBits, out replacement))
      return false;

    trunc.SetOperand(0, replacement);
    return true;
  }

  private static bool TryNeutralOperand(
      IrBinary binary,
      IrValue candidate,
      IrValue other,
      ulong demanded,
      IrKnownBitsAnalysis knownBits,
      out IrValue replacement) {
    var bits = knownBits.For(candidate);
    var neutral = binary.Op switch {
      IrBinaryOp.And => bits.AreOne(demanded),
      IrBinaryOp.Or or IrBinaryOp.Xor => bits.AreZero(demanded),
      _ => false,
    };
    replacement = other;
    return neutral;
  }

  private static ulong LowMask(int bits) => bits >= 64 ? ulong.MaxValue : (1UL << bits) - 1;
}
