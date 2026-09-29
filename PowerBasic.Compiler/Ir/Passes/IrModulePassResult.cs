using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>Result of one module transform: mutation count plus cached analyses that remain valid.</summary>
public readonly record struct IrModulePassResult {
  public IrModulePassResult(int changes, IrModulePreservedAnalyses preservedAnalyses) {
    if (changes < 0)
      throw new ArgumentOutOfRangeException(nameof(changes));
    ArgumentNullException.ThrowIfNull(preservedAnalyses);
    this.Changes = changes;
    this.PreservedAnalyses = preservedAnalyses;
  }

  public int Changes { get; }
  public IrModulePreservedAnalyses PreservedAnalyses { get; }

  public static IrModulePassResult Unchanged { get; } = new(0, IrModulePreservedAnalyses.All);

  public static IrModulePassResult Changed(int changes) {
    if (changes <= 0)
      throw new ArgumentOutOfRangeException(nameof(changes));
    return new(changes, IrModulePreservedAnalyses.None);
  }

  public static IrModulePassResult ChangedPreserving(int changes, params IrModuleAnalysisKey[] analyses) {
    if (changes <= 0)
      throw new ArgumentOutOfRangeException(nameof(changes));
    return new(changes, IrModulePreservedAnalyses.Preserve(analyses));
  }
}
