namespace PowerBasic.Compiler.Backend;

/// <summary>
/// O0273's target-specific spill cost: the expected number of register reads/writes that would become
/// memory traffic if a virtual value were spilled. The count is exact with respect to the supplied
/// block execution counts and deliberately saturates instead of wrapping on pathological profiles.
/// </summary>
internal static class ProfileGuidedSpillCost {

  /// <summary>
  /// Computes dynamic access counts for every virtual register, or returns null when the profile is
  /// incomplete. Missing data must not mean zero: that would make an unknown block look colder than a
  /// block measured to execute once and bias allocation from a stale/partial profile.
  /// </summary>
  public static IReadOnlyDictionary<int, ulong>? Compute(MFunction function) {
    if (function.Blocks.Count == 0 || function.Blocks.Any(block => block.ExecutionCount is null))
      return null;

    var costs = new Dictionary<int, ulong>();
    foreach (var block in function.Blocks) {
      var frequency = block.ExecutionCount!.Value;
      foreach (var instruction in block.Instructions) {
        var (reads, writes) = LivenessAnalysis.RegistersOf(instruction);
        foreach (var value in reads)
          Add(costs, value, frequency);
        foreach (var value in writes)
          Add(costs, value, frequency);
      }
    }
    return costs;
  }

  private static void Add(Dictionary<int, ulong> costs, int value, ulong frequency) {
    var current = costs.GetValueOrDefault(value);
    costs[value] = ulong.MaxValue - current < frequency ? ulong.MaxValue : current + frequency;
  }
}
