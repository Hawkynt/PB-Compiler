namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>
/// Common query facade over independently cached abstract domains. This object owns no lattice of its
/// own: range, known bits and nullness retain separate solvers/invalidation, while consumers get one
/// program-point-oriented vocabulary.
/// </summary>
public sealed class IrValueFacts {
  private readonly IrRangeAnalysis? _ranges;
  private readonly IrKnownBitsAnalysis _knownBits;
  private readonly IrNullnessAnalysis _nullness;

  internal IrValueFacts(
      IrRangeAnalysis? ranges,
      IrKnownBitsAnalysis knownBits,
      IrNullnessAnalysis nullness) {
    this._ranges = ranges;
    this._knownBits = knownBits;
    this._nullness = nullness;
  }

  /// <summary>Integer interval at a program point, including dominating branch refinement.</summary>
  public ValueRange RangeAt(IrValue value, IrBasicBlock block) {
    ArgumentNullException.ThrowIfNull(value);
    ArgumentNullException.ThrowIfNull(block);
    return this._ranges?.RangeAt(value, block) ?? ValueRange.OfType(value.Type);
  }

  /// <summary>Known-zero/known-one bits. These are SSA-value facts and therefore point-independent.</summary>
  public IrKnownBitsAnalysis.KnownBits KnownBits(IrValue value) => this._knownBits.For(value);

  /// <summary>Pointer nullness at a program point.</summary>
  public IrNullness NullnessAt(IrValue value, IrBasicBlock block) => this._nullness.At(value, block);

  /// <summary>
  /// Decides a comparison when one of the shared domains proves its result at <paramref name="block"/>.
  /// Unknown means exactly that: consumers must leave the comparison intact.
  /// </summary>
  public bool? Decide(IrCmp comparison, IrBasicBlock block) {
    ArgumentNullException.ThrowIfNull(comparison);
    ArgumentNullException.ThrowIfNull(block);

    if (comparison.Lhs.Type.IsInteger && comparison.Rhs.Type.IsInteger)
      return this._ranges?.Decide(comparison, block);

    if (!IrNullnessAnalysis.TryNullTest(comparison, out var value, out var trueMeansNull))
      return null;
    return this.NullnessAt(value, block) switch {
      IrNullness.Null => trueMeansNull,
      IrNullness.NonNull => !trueMeansNull,
      _ => null,
    };
  }
}
