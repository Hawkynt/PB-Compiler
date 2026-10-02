using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Folds what an operand's known bits settle (O0222, O0223, the bit half of O0218): a comparison
/// with a constant that a known bit contradicts, an AND whose mask selects only known-zero bits
/// (<c>(n * 4) AND 3</c> is 0) or every bit that could be one (<c>(n AND 7) AND 15</c> is
/// <c>n AND 7</c>), and a signed remainder by a power of two of a value whose sign bit is known clear,
/// which is the AND it equals there. The facts come from <see cref="IrKnownBitsAnalysis"/>; the range
/// half of the same questions is <see cref="RangeCheckElim"/>'s.
/// </summary>
public static class KnownBitsSimplify {

  public static int Run(IrFunction function) {
    ArgumentNullException.ThrowIfNull(function);
    return IrFunctionPassPipeline.RunStandalone(function, "knownbits", Run);
  }

  public static IrPassResult Run(IrFunction function, IrAnalysisManager analyses) {
    ArgumentNullException.ThrowIfNull(function);
    ArgumentNullException.ThrowIfNull(analyses);
    if (!ReferenceEquals(function, analyses.Function))
      throw new ArgumentException("Analysis manager belongs to a different function.", nameof(analyses));

    var known = analyses.Get(IrAnalyses.KnownBits);
    var changes = 0;
    foreach (var instruction in function.AllInstructions.ToList()) {
      if (instruction.Parent is null || instruction.HasNoUsers)
        continue;
      IrValue? replacement = instruction switch {
        IrCmp compare => DecideCompare(compare, known),
        IrBinary { Op: IrBinaryOp.And } and => SimplifyAnd(and, known),
        IrBinary { Op: IrBinaryOp.SRem } rem => SignClearRemainder(rem, known),
        _ => null,
      };
      if (replacement is null)
        continue;
      if (replacement is IrInstruction { Parent: null } fresh)
        instruction.Parent.InsertBefore(fresh, instruction);
      instruction.ReplaceAllUsesWith(replacement);
      instruction.EraseFromParent();
      ++changes;
    }
    return changes == 0
      ? IrPassResult.Unchanged
      : IrPassResult.ChangedPreservingSets(changes, IrAnalysisSets.Cfg);
  }

  /// <summary><c>x = C</c> is false, and <c>x &lt;&gt; C</c> true, when some bit of <c>x</c> is known to differ from <c>C</c>'s.</summary>
  private static IrValue? DecideCompare(IrCmp compare, IrKnownBitsAnalysis known) {
    if (compare.Pred is not (IrCmpPred.Eq or IrCmpPred.Ne) || !compare.Lhs.Type.IsInteger)
      return null;
    var (value, constant) = compare.Rhs is IrConstantInt c ? (compare.Lhs, c)
      : compare.Lhs is IrConstantInt l ? (compare.Rhs, l) : (null, null);
    if (value is null || constant is null || value is IrConstantInt)
      return null;
    var bits = known.For(value);
    var pattern = constant.ZeroExtended;
    var contradicted = (bits.Zero & pattern) != 0 || (bits.One & ~pattern & Mask(bits.Width)) != 0;
    return contradicted ? IrBuilder.ConstBool(compare.Pred == IrCmpPred.Ne) : null;
  }

  /// <summary><c>x AND M</c>: 0 when M selects only known-zero bits of x, x when M keeps every bit x could have set.</summary>
  private static IrValue? SimplifyAnd(IrBinary and, IrKnownBitsAnalysis known) {
    var (value, mask) = and.Rhs is IrConstantInt m ? (and.Lhs, m)
      : and.Lhs is IrConstantInt n ? (and.Rhs, n) : (null, null);
    if (value is null || mask is null || value is IrConstantInt)
      return null;
    var bits = known.For(value);
    var width = Mask(bits.Width);
    var selected = mask.ZeroExtended & width;
    if (bits.Width == 0)
      return null;
    if ((selected & ~bits.Zero) == 0)
      return new IrConstantInt(and.Type, 0);
    var possiblyOne = width & ~bits.Zero;
    return (possiblyOne & ~selected) == 0 ? value : null;
  }

  /// <summary>
  /// <c>x MOD 2^k</c> with x's sign bit known clear is <c>x AND (2^k - 1)</c>: a signed remainder of a
  /// non-negative dividend is the unsigned one.
  /// </summary>
  private static IrValue? SignClearRemainder(IrBinary rem, IrKnownBitsAnalysis known) {
    if (rem.Rhs is not IrConstantInt { Value: > 0 and var divisor } || (divisor & (divisor - 1)) != 0)
      return null;
    var bits = known.For(rem.Lhs);
    if (bits.Width == 0 || (bits.Zero & (1UL << (bits.Width - 1))) == 0)
      return null;
    return new IrBinary(IrBinaryOp.And, rem.Lhs, new IrConstantInt(rem.Type, divisor - 1));
  }

  private static ulong Mask(int width) => width switch { <= 0 => 0, >= 64 => ulong.MaxValue, _ => (1UL << width) - 1 };
}
