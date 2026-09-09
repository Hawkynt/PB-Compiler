namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// The stable identity O0276 carries from the IR into post-link layout metadata: procedure name plus
/// the block's structural index before any physical layout is chosen.
/// </summary>
public readonly record struct IrPostLinkBlockId(string Function, int BlockIndex) {
  public override string ToString() => $"{this.Function}#{this.BlockIndex}";
}

/// <summary>A sampled execution count for one CFG edge in a post-link profile.</summary>
public readonly record struct IrPostLinkEdgeSample(IrPostLinkBlockId From, IrPostLinkBlockId To, ulong Count);

/// <summary>
/// One independently placeable block descriptor. The ID and successor IDs are deliberately separate
/// from physical order: the later object/linker stages may move this fragment without rediscovering
/// the CFG from machine bytes.
/// </summary>
public sealed record IrPostLinkFragment(
  IrPostLinkBlockId Id,
  IrBasicBlock Block,
  IReadOnlyList<IrPostLinkBlockId> Successors);

/// <summary>A profile-guided fragment order proposed for the eventual post-link rewriter.</summary>
public sealed record IrPostLinkLayoutPlan(
  IReadOnlyList<IrPostLinkFragment> Fragments,
  ulong ProfiledEdgeWeight,
  ulong OriginalFallthroughWeight,
  ulong PlannedFallthroughWeight) {

  /// <summary>True when the proposed physical order improves the measured fall-through objective.</summary>
  public bool ChangesLayout => this.PlannedFallthroughWeight > this.OriginalFallthroughWeight;

  /// <summary>The fraction of sampled CFG-edge weight represented by adjacent blocks in the plan.</summary>
  public double WeightedFallthroughRatio
    => this.ProfiledEdgeWeight == 0 ? 0 : (double)this.PlannedFallthroughWeight / this.ProfiledEdgeWeight;
}

/// <summary>
/// O0276 — builds the target-neutral layout contract consumed by a post-link fragment rewriter.
///
/// <para>
/// The executable rewriter cannot live in the SSA middle end: final addresses, relocations and branch
/// relaxation do not exist here. What the middle end can preserve is the information a rewriter would
/// otherwise have to recover by disassembly — stable block identities, the CFG and a profile-guided
/// physical order. This planner therefore leaves the <see cref="IrFunction"/> untouched and returns a
/// separate fragment order for O0360/linker metadata to consume.
/// </para>
/// <para>
/// Profile edges are validated against the current CFG before they influence layout. That deliberately
/// rejects a stale or mismatched profile rather than attaching samples to the wrong code. The planner
/// greedily chains the hottest legal fall-through edges and accepts the candidate only when it strictly
/// increases sampled fall-through weight, so this stage cannot regress its own objective.
/// </para>
/// </summary>
public static class PostLinkOptimization {

  /// <summary>
  /// Builds a post-link fragment order for <paramref name="fn"/>. Returns <see langword="null"/> when
  /// the function has no body or contains control flow opaque to the IR.
  /// </summary>
  public static IrPostLinkLayoutPlan? Plan(IrFunction fn, IEnumerable<IrPostLinkEdgeSample> samples) {
    ArgumentNullException.ThrowIfNull(fn);
    ArgumentNullException.ThrowIfNull(samples);
    if (fn.IsDeclaration || fn.HasErrorHandler || fn.HasInlineAsm)
      return null;

    var blocks = fn.Blocks;
    if (blocks.Count == 0)
      return null;

    var indexOf = new Dictionary<IrBasicBlock, int>(ReferenceEqualityComparer.Instance);
    for (var i = 0; i < blocks.Count; ++i)
      indexOf.Add(blocks[i], i);

    var weights = new Dictionary<(int From, int To), ulong>();
    foreach (var sample in samples) {
      ValidateId(fn, blocks.Count, sample.From, nameof(samples));
      ValidateId(fn, blocks.Count, sample.To, nameof(samples));

      var from = blocks[sample.From.BlockIndex];
      var to = blocks[sample.To.BlockIndex];
      if (!from.Successors.Any(successor => ReferenceEquals(successor, to)))
        throw new ArgumentException(
          $"profile edge {sample.From} -> {sample.To} is not present in the current CFG",
          nameof(samples));

      var edge = (sample.From.BlockIndex, sample.To.BlockIndex);
      weights[edge] = SaturatingAdd(weights.GetValueOrDefault(edge), sample.Count);
    }

    var totalWeight = weights.Values.Aggregate(0UL, SaturatingAdd);
    var originalOrder = Enumerable.Range(0, blocks.Count).ToArray();
    var originalFallthrough = FallthroughWeight(originalOrder, weights);
    if (weights.Count == 0)
      return BuildPlan(fn, blocks, indexOf, originalOrder, 0, 0, 0);

    var chains = Enumerable.Range(0, blocks.Count).Select(index => new Chain(index)).ToArray();
    var chainOf = chains.ToArray();
    var heat = new ulong[blocks.Count];
    foreach (var (edge, weight) in weights) {
      heat[edge.From] = SaturatingAdd(heat[edge.From], weight);
      heat[edge.To] = SaturatingAdd(heat[edge.To], weight);
    }

    foreach (var (edge, _) in weights
      .OrderByDescending(pair => pair.Value)
      .ThenBy(pair => pair.Key.From)
      .ThenBy(pair => pair.Key.To)) {
      var fromChain = chainOf[edge.From];
      var toChain = chainOf[edge.To];
      if (ReferenceEquals(fromChain, toChain))
        continue;
      if (fromChain.Blocks[^1] != edge.From || toChain.Blocks[0] != edge.To)
        continue;
      // Block zero is the function entry and must remain the first physical block. A back-edge into
      // its chain may be hot, but making it a fall-through would put some other block before entry.
      if (toChain.Blocks.Contains(0))
        continue;

      fromChain.Blocks.AddRange(toChain.Blocks);
      foreach (var block in toChain.Blocks)
        chainOf[block] = fromChain;
      toChain.Active = false;
    }

    var active = chains.Where(chain => chain.Active).ToList();
    var entryChain = chainOf[0];
    var candidate = new List<int>(blocks.Count);
    candidate.AddRange(entryChain.Blocks);
    foreach (var chain in active
      .Where(chain => !ReferenceEquals(chain, entryChain))
      .OrderByDescending(chain => ChainHeat(chain, heat))
      .ThenBy(chain => chain.Blocks[0]))
      candidate.AddRange(chain.Blocks);

    var candidateFallthrough = FallthroughWeight(candidate, weights);
    IReadOnlyList<int> selected = candidateFallthrough > originalFallthrough ? candidate : originalOrder;
    var selectedFallthrough = Math.Max(candidateFallthrough, originalFallthrough);
    return BuildPlan(fn, blocks, indexOf, selected, totalWeight, originalFallthrough, selectedFallthrough);
  }

  private static IrPostLinkLayoutPlan BuildPlan(IrFunction fn, IReadOnlyList<IrBasicBlock> blocks,
      IReadOnlyDictionary<IrBasicBlock, int> indexOf, IEnumerable<int> order, ulong totalWeight,
      ulong originalFallthrough, ulong plannedFallthrough) {
    var fragments = order.Select(index => {
      var block = blocks[index];
      var successors = block.Successors
        .Select(successor => new IrPostLinkBlockId(fn.Name, indexOf[successor]))
        .ToArray();
      return new IrPostLinkFragment(new(fn.Name, index), block, successors);
    }).ToArray();
    return new(fragments, totalWeight, originalFallthrough, plannedFallthrough);
  }

  private static void ValidateId(IrFunction fn, int blockCount, IrPostLinkBlockId id, string parameterName) {
    if (!string.Equals(id.Function, fn.Name, StringComparison.Ordinal) || (uint)id.BlockIndex >= (uint)blockCount)
      throw new ArgumentException($"profile block {id} does not identify a block in function {fn.Name}", parameterName);
  }

  private static ulong FallthroughWeight(IEnumerable<int> order, IReadOnlyDictionary<(int From, int To), ulong> weights) {
    using var iterator = order.GetEnumerator();
    if (!iterator.MoveNext())
      return 0;

    var previous = iterator.Current;
    var result = 0UL;
    while (iterator.MoveNext()) {
      var current = iterator.Current;
      result = SaturatingAdd(result, weights.GetValueOrDefault((previous, current)));
      previous = current;
    }
    return result;
  }

  private static ulong ChainHeat(Chain chain, IReadOnlyList<ulong> heat)
    => chain.Blocks.Aggregate(0UL, (sum, block) => SaturatingAdd(sum, heat[block]));

  private static ulong SaturatingAdd(ulong left, ulong right)
    => ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

  private sealed class Chain(int block) {
    public List<int> Blocks { get; } = [block];
    public bool Active { get; set; } = true;
  }
}
