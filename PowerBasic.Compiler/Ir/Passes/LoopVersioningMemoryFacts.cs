using System.Runtime.CompilerServices;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Guard-proven memory facts attached to instructions in a loop-versioned fast clone.
/// <para>
/// They deliberately live beside O0306 rather than weakening the base IR: the ordinary fallback has
/// no such facts, and a consumer may use these only for the instruction object on which the guard was
/// proved. This is also why the table is weak — deleting a clone deletes its assumptions with it.
/// </para>
/// </summary>
public static class LoopVersioningMemoryFacts {

  private sealed class Facts {
    public int Alignment { get; set; } = 1;
    public int NoAliasGroup { get; set; }
  }

  private static readonly ConditionalWeakTable<IrInstruction, Facts> _facts = new();

  /// <summary>The power-of-two byte alignment proved for this access; one means unknown.</summary>
  public static int AlignmentOf(IrInstruction instruction) {
    ArgumentNullException.ThrowIfNull(instruction);
    return _facts.TryGetValue(instruction, out var facts) ? facts.Alignment : 1;
  }

  /// <summary>
  /// The guard-proven disjoint range group. Two non-zero, different groups from the same versioned
  /// loop are pairwise non-aliasing on that fast path; zero means no runtime alias fact.
  /// </summary>
  public static int NoAliasGroupOf(IrInstruction instruction) {
    ArgumentNullException.ThrowIfNull(instruction);
    return _facts.TryGetValue(instruction, out var facts) ? facts.NoAliasGroup : 0;
  }

  internal static void SetAlignment(IrInstruction instruction, int alignment) {
    ArgumentNullException.ThrowIfNull(instruction);
    if (alignment < 1 || (alignment & (alignment - 1)) != 0)
      throw new ArgumentOutOfRangeException(nameof(alignment), "alignment must be a positive power of two");
    _facts.GetOrCreateValue(instruction).Alignment = alignment;
  }

  internal static void SetNoAliasGroup(IrInstruction instruction, int group) {
    ArgumentNullException.ThrowIfNull(instruction);
    if (group <= 0)
      throw new ArgumentOutOfRangeException(nameof(group));
    _facts.GetOrCreateValue(instruction).NoAliasGroup = group;
  }
}
