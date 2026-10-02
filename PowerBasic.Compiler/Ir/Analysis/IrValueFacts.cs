namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>
/// Common query facade over independently cached abstract domains. This object owns no lattice of its
/// own: range, known bits, nullness and alignment retain separate solvers/invalidation, while consumers get one
/// program-point-oriented vocabulary.
/// </summary>
public sealed class IrValueFacts {
  private readonly IrRangeAnalysis? _ranges;
  private readonly IrKnownBitsAnalysis _knownBits;
  private readonly IrNullnessAnalysis _nullness;
  private readonly IrAlignmentAnalysis _alignment;

  internal IrValueFacts(
      IrRangeAnalysis? ranges,
      IrKnownBitsAnalysis knownBits,
      IrNullnessAnalysis nullness,
      IrAlignmentAnalysis alignment) {
    this._ranges = ranges;
    this._knownBits = knownBits;
    this._nullness = nullness;
    this._alignment = alignment;
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

  /// <summary>Minimum power-of-two pointer alignment known at a program point; one means unknown.</summary>
  public ulong AlignmentAt(IrValue value, IrBasicBlock block) => this._alignment.MinimumAt(value, block);

  /// <summary>
  /// Decides an alignment comparison under an additional branch assumption that has not yet become
  /// a dominating CFG fact. This is the shared path-fact entry used by versioning/speculation.
  /// </summary>
  public bool? DecideAlignmentUnder(
      IrCmp comparison,
      IrCmp assumption,
      bool assumptionOutcome,
      IrBasicBlock block)
    => this._alignment.DecideUnder(comparison, assumption, assumptionOutcome, block);

  /// <summary>
  /// Decides a comparison when one of the shared domains proves its result at <paramref name="block"/>.
  /// Unknown means exactly that: consumers must leave the comparison intact.
  /// </summary>
  public bool? Decide(IrCmp comparison, IrBasicBlock block) {
    ArgumentNullException.ThrowIfNull(comparison);
    ArgumentNullException.ThrowIfNull(block);

    if (comparison.Lhs.Type.IsInteger && comparison.Rhs.Type.IsInteger
        && this._ranges?.Decide(comparison, block) is { } integerDecision)
      return integerDecision;

    if (this._alignment.Decide(comparison, block) is { } alignmentDecision)
      return alignmentDecision;

    if (!IrNullnessAnalysis.TryNullTest(comparison, out var value, out var trueMeansNull))
      return null;
    return this.NullnessAt(value, block) switch {
      IrNullness.Null => trueMeansNull,
      IrNullness.NonNull => !trueMeansNull,
      _ => null,
    };
  }
}
