using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>Result of one function transform: mutation count plus the analyses that remain valid.</summary>
public readonly record struct IrPassResult {

  public IrPassResult(int changes, IrPreservedAnalyses preservedAnalyses) {
    if (changes < 0)
      throw new ArgumentOutOfRangeException(nameof(changes));
    ArgumentNullException.ThrowIfNull(preservedAnalyses);
    this.Changes = changes;
    this.PreservedAnalyses = preservedAnalyses;
  }

  /// <summary>Number of IR mutations reported by the pass.</summary>
  public int Changes { get; }

  /// <summary>Analyses that remain valid after those mutations.</summary>
  public IrPreservedAnalyses PreservedAnalyses { get; }

  /// <summary>An unchanged pass preserves every cached analysis.</summary>
  public static IrPassResult Unchanged { get; } = new(0, IrPreservedAnalyses.All);

  /// <summary>A changing legacy transform conservatively invalidates every cached analysis.</summary>
  public static IrPassResult Changed(int changes) {
    if (changes <= 0)
      throw new ArgumentOutOfRangeException(nameof(changes));
    return new IrPassResult(changes, IrPreservedAnalyses.None);
  }

  /// <summary>A changing transform preserving exactly the supplied analyses.</summary>
  public static IrPassResult ChangedPreserving(int changes, params IrAnalysisKey[] analyses) {
    if (changes <= 0)
      throw new ArgumentOutOfRangeException(nameof(changes));
    return new IrPassResult(changes, IrPreservedAnalyses.Preserve(analyses));
  }
}
