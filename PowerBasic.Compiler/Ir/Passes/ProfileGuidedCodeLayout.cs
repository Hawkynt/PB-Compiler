namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// An observed execution count for one current CFG edge. O0268 is responsible for resolving stable
/// profile identities to the current <see cref="IrBasicBlock"/> objects before O0274 consumes them.
/// </summary>
public readonly record struct IrProfileEdgeCount(IrBasicBlock Source, IrBasicBlock Target, ulong Count);

/// <summary>
/// O0274 — profile-guided code layout. Reorders an <see cref="IrFunction"/>'s physical basic-block
/// list so the hottest observed CFG edges are preferentially adjacent while preserving entry and SSA
/// dominance order.
/// </summary>
/// <remarks>
/// The pass changes no CFG edge and no instruction. The entry stays first, unreachable blocks keep
/// their original relative order, and an empty/all-zero profile is a no-op. The dominance constraint
/// is intentional: a layout consumer may walk blocks linearly, so every non-phi SSA definition still
/// appears before every block it can dominate and feed.
/// </remarks>
public static class ProfileGuidedCodeLayout {

  /// <summary>
  /// Applies profile-guided block placement and returns the number of blocks whose physical index
  /// changed. Duplicate edge observations are accumulated with saturating arithmetic.
  /// </summary>
  public static int Run(IrFunction function, IEnumerable<IrProfileEdgeCount> edgeCounts) {
    ArgumentNullException.ThrowIfNull(function);
    ArgumentNullException.ThrowIfNull(edgeCounts);
    // Error handlers transfer control along edges the CFG cannot represent and inline asm may jump to
    // BASIC labels by name. A weighted placement computed from that visible graph would therefore be
    // a confident answer to an incomplete question; match IrPassManager and decline the whole body.
    if (function.HasErrorHandler || function.HasInlineAsm || function.Entry is null || function.Blocks.Count < 2)
      return 0;

    var weights = CollectEdgeWeights(function, edgeCounts);
    if (weights.Count == 0)
      return 0;

    var dominators = IrDominators.Build(function)!;
    var reachable = new HashSet<IrBasicBlock>(dominators.ReversePostorder, ReferenceEqualityComparer.Instance);
    if (reachable.Count < 2 || !weights.Keys.Any(edge => reachable.Contains(edge.Source)))
      return 0;

    var original = function.Blocks.ToArray();
    var originalIndex = new Dictionary<IrBasicBlock, int>(ReferenceEqualityComparer.Instance);
    for (var i = 0; i < original.Length; ++i)
      originalIndex[original[i]] = i;

    var incoming = new Dictionary<IrBasicBlock, ulong>(ReferenceEqualityComparer.Instance);
    var outgoing = new Dictionary<IrBasicBlock, ulong>(ReferenceEqualityComparer.Instance);
    foreach (var (edge, count) in weights) {
      incoming[edge.Target] = SaturatingAdd(incoming.GetValueOrDefault(edge.Target), count);
      outgoing[edge.Source] = SaturatingAdd(outgoing.GetValueOrDefault(edge.Source), count);
    }

    ulong Heat(IrBasicBlock block)
      => Math.Max(incoming.GetValueOrDefault(block), outgoing.GetValueOrDefault(block));

    ulong EdgeWeight(IrBasicBlock source, IrBasicBlock target)
      => weights.GetValueOrDefault((source, target));

    var entry = function.Entry!;
    var order = new List<IrBasicBlock>(original.Length) { entry };
    var placed = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance) { entry };
    var current = entry;

    while (placed.Count < reachable.Count) {
      IrBasicBlock? best = null;
      ulong bestEdge = 0;
      ulong bestHeat = 0;
      var bestOriginalIndex = int.MaxValue;

      foreach (var candidate in original) {
        if (!reachable.Contains(candidate) || placed.Contains(candidate))
          continue;
        var idom = dominators.ImmediateDominatorOf(candidate);
        if (idom is null || !placed.Contains(idom))
          continue;

        var edge = EdgeWeight(current, candidate);
        var heat = Heat(candidate);
        var index = originalIndex[candidate];
        if (best is not null
            && (edge < bestEdge
                || edge == bestEdge && heat < bestHeat
                || edge == bestEdge && heat == bestHeat && index >= bestOriginalIndex))
          continue;

        best = candidate;
        bestEdge = edge;
        bestHeat = heat;
        bestOriginalIndex = index;
      }

      // Every reachable non-entry block has an immediate dominator. Since dominators form a tree
      // rooted at entry, at least one remaining node's parent must already have been placed.
      if (best is null)
        throw new InvalidOperationException("reachable CFG does not admit a dominance-preserving block order");

      order.Add(best);
      placed.Add(best);
      current = best;
    }

    // Layout only has evidence for executable CFG nodes. Preserve the exact relative order of dead
    // or deliberately address-taken islands instead of inventing heat for them.
    foreach (var block in original)
      if (!reachable.Contains(block))
        order.Add(block);

    var changes = 0;
    for (var i = 0; i < original.Length; ++i)
      if (!ReferenceEquals(original[i], order[i]))
        ++changes;
    if (changes == 0)
      return 0;

    function.ReorderBlocks(order);
    return changes;
  }

  private static Dictionary<(IrBasicBlock Source, IrBasicBlock Target), ulong> CollectEdgeWeights(
      IrFunction function, IEnumerable<IrProfileEdgeCount> edgeCounts) {
    var result = new Dictionary<(IrBasicBlock Source, IrBasicBlock Target), ulong>();
    foreach (var edge in edgeCounts) {
      if (edge.Source is null || edge.Target is null
          || !ReferenceEquals(edge.Source.Parent, function)
          || !ReferenceEquals(edge.Target.Parent, function))
        throw new ArgumentException("profile edge must reference blocks owned by the supplied function", nameof(edgeCounts));
      if (!edge.Source.Successors.Any(successor => ReferenceEquals(successor, edge.Target)))
        throw new ArgumentException("profile edge must exist in the supplied function's current CFG", nameof(edgeCounts));
      if (edge.Count == 0)
        continue;

      var key = (edge.Source, edge.Target);
      result[key] = SaturatingAdd(result.GetValueOrDefault(key), edge.Count);
    }
    return result;
  }

  private static ulong SaturatingAdd(ulong left, ulong right)
    => ulong.MaxValue - left < right ? ulong.MaxValue : left + right;
}
