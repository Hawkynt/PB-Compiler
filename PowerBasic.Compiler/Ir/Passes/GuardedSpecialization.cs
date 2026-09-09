namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>The CFG and SSA objects created by <see cref="GuardedSpecialization.TryVersion"/>.</summary>
public sealed record GuardedSpecializationResult(
  IrBasicBlock GuardBlock,
  IrBasicBlock FastEntry,
  IrBasicBlock FallbackEntry,
  IrBasicBlock Exit,
  IReadOnlyDictionary<IrBasicBlock, IrBasicBlock> BlockMap,
  IReadOnlyDictionary<IrValue, IrValue> ValueMap);

/// <summary>
/// O0304 — guarded specialization. Duplicates a single-entry/single-exit region behind runtime
/// predicates, keeping the original region as the fully general fallback and exposing the clone for a
/// caller to optimize under facts that are true only on the guarded path.
///
/// <para>
/// This is deliberately a MECHANISM, not a guesser. Alias analysis, range analysis, profiling or a
/// target-specific pass decides which assumptions are profitable and supplies the predicates that
/// prove them. O0304 owns the part every such pass otherwise gets subtly different: one combined guard,
/// one clone, an untouched fallback, a code-size budget, and the SSA repair at both region boundaries.
/// </para>
/// <para>
/// The accepted shape is intentionally conservative: one unconditional preheader, no side entries,
/// one exit target, and no address-taken blocks. A shape that needs edge splitting, deoptimization or
/// exception-aware control flow is declined instead of being approximately versioned.
/// </para>
/// </summary>
public static class GuardedSpecialization {

  private const int _DEFAULT_MAX_CLONED_INSTRUCTIONS = 96;

  /// <summary>
  /// Versions <paramref name="region"/> when its shape and default code-size budget permit it.
  /// Every <paramref name="guards"/> value is assumed true in the fast clone.
  /// </summary>
  public static bool TryVersion(
      IrFunction fn,
      IReadOnlyList<IrBasicBlock> region,
      IReadOnlyList<IrValue> guards,
      out GuardedSpecializationResult? result)
    => TryVersion(
      fn,
      region,
      guards,
      new Dictionary<IrValue, IrValue>(ReferenceEqualityComparer.Instance),
      _DEFAULT_MAX_CLONED_INSTRUCTIONS,
      out result);

  /// <summary>
  /// Versions <paramref name="region"/> under <paramref name="guards"/> and seeds the fast clone with
  /// <paramref name="assumptions"/>, using the default code-size budget.
  /// </summary>
  public static bool TryVersion(
      IrFunction fn,
      IReadOnlyList<IrBasicBlock> region,
      IReadOnlyList<IrValue> guards,
      IReadOnlyDictionary<IrValue, IrValue> assumptions,
      out GuardedSpecializationResult? result)
    => TryVersion(fn, region, guards, assumptions, _DEFAULT_MAX_CLONED_INSTRUCTIONS, out result);

  /// <summary>
  /// Versions <paramref name="region"/> under <paramref name="guards"/> and seeds the fast clone with
  /// <paramref name="assumptions"/>. The caller is responsible for making every seeded replacement a
  /// logical consequence of the guards; the fallback remains correct regardless of those assumptions.
  /// </summary>
  public static bool TryVersion(
      IrFunction fn,
      IReadOnlyList<IrBasicBlock> region,
      IReadOnlyList<IrValue> guards,
      IReadOnlyDictionary<IrValue, IrValue> assumptions,
      int maxClonedInstructions,
      out GuardedSpecializationResult? result) {
    ArgumentNullException.ThrowIfNull(fn);
    ArgumentNullException.ThrowIfNull(region);
    ArgumentNullException.ThrowIfNull(guards);
    ArgumentNullException.ThrowIfNull(assumptions);
    ArgumentOutOfRangeException.ThrowIfNegative(maxClonedInstructions);
    result = null;

    var regionSet = ValidateArguments(fn, region, guards, assumptions);
    if (fn.Entry is null || fn.HasErrorHandler || fn.HasInlineAsm)
      return false;
    if (region.SelectMany(block => block.Instructions).Any(instruction => instruction is IrInlineAsm))
      return false;
    if (region.Sum(block => block.Instructions.Count) > maxClonedInstructions)
      return false;
    if (fn.AddressTakenBlocks().Overlaps(regionSet))
      return false;
    if (!TryMatchRegion(region, regionSet, out var shape))
      return false;

    var dominators = IrDominators.Build(fn)!;
    if (!FactsAvailableAtGuard(shape, guards, assumptions, dominators))
      return false;
    if (!TryCollectLiveOuts(shape, dominators, out var liveOuts))
      return false;

    result = Apply(fn, region, guards, assumptions, shape, liveOuts);
    return true;
  }

  private sealed record RegionShape(
    IrBasicBlock Entry,
    IrBasicBlock Preheader,
    IrBasicBlock Exit,
    IReadOnlyList<IrBasicBlock> ExitPredecessors,
    HashSet<IrBasicBlock> Blocks);

  private sealed record LiveOut(IrInstruction Value, IReadOnlyList<IrInstruction> Users);

  private static HashSet<IrBasicBlock> ValidateArguments(
      IrFunction fn,
      IReadOnlyList<IrBasicBlock> region,
      IReadOnlyList<IrValue> guards,
      IReadOnlyDictionary<IrValue, IrValue> assumptions) {
    if (region.Count == 0)
      throw new ArgumentException("the specialization region must contain at least one block", nameof(region));
    if (region.Any(block => block is null))
      throw new ArgumentException("the specialization region cannot contain a null block", nameof(region));
    if (guards.Count == 0)
      throw new ArgumentException("guarded specialization requires at least one predicate", nameof(guards));
    if (guards.Any(guard => guard is null))
      throw new ArgumentException("the specialization guards cannot contain a null value", nameof(guards));

    var blocks = new HashSet<IrBasicBlock>(region, ReferenceEqualityComparer.Instance);
    if (blocks.Count != region.Count)
      throw new ArgumentException("the specialization region contains a block more than once", nameof(region));
    if (region.Any(block => !ReferenceEquals(block.Parent, fn)))
      throw new ArgumentException("every specialization block must belong to the supplied function", nameof(region));
    if (guards.Any(guard => !guard.Type.IsBool))
      throw new ArgumentException("every specialization guard must have type i1", nameof(guards));
    foreach (var (value, replacement) in assumptions) {
      if (value is null || replacement is null)
        throw new ArgumentException("specialization assumptions cannot contain null values", nameof(assumptions));
      if (!value.Type.SameStorage(replacement.Type))
        throw new ArgumentException(
          "every specialization assumption must preserve the value's storage type",
          nameof(assumptions));
    }
    return blocks;
  }

  private static bool TryMatchRegion(
      IReadOnlyList<IrBasicBlock> region,
      HashSet<IrBasicBlock> blocks,
      out RegionShape shape) {
    shape = null!;
    var entry = region[0];
    var outsideEntryPredecessors = entry.Predecessors
      .Where(predecessor => !blocks.Contains(predecessor))
      .ToList();
    if (outsideEntryPredecessors is not [var preheader])
      return false;
    if (preheader.Terminator is not IrBr branch || !ReferenceEquals(branch.Target, entry))
      return false;
    if (entry.Phis.Any(phi => phi.IncomingFrom(preheader) is null))
      return false;

    foreach (var block in region) {
      if (block.Terminator is not { } terminator || !terminator.Successors.Any())
        return false;
      if (!ReferenceEquals(block, entry)
          && block.Predecessors.Any(predecessor => !blocks.Contains(predecessor)))
        return false;
    }
    if (!IsConnected(entry, blocks))
      return false;

    var exits = region.SelectMany(block => block.Successors)
      .Where(successor => !blocks.Contains(successor))
      .Distinct<IrBasicBlock>(ReferenceEqualityComparer.Instance)
      .ToList();
    if (exits is not [var exit] || ReferenceEquals(exit, preheader))
      return false;

    var exitPredecessors = region
      .Where(block => block.Successors.Any(successor => ReferenceEquals(successor, exit)))
      .ToList();
    if (exitPredecessors.Count == 0)
      return false;
    foreach (var phi in exit.Phis)
      if (exitPredecessors.Any(predecessor => phi.IncomingFrom(predecessor) is null))
        return false;

    shape = new(entry, preheader, exit, exitPredecessors, blocks);
    return true;
  }

  private static bool IsConnected(IrBasicBlock entry, HashSet<IrBasicBlock> region) {
    var seen = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance) { entry };
    var pending = new Queue<IrBasicBlock>([entry]);
    while (pending.Count > 0)
      foreach (var successor in pending.Dequeue().Successors)
        if (region.Contains(successor) && seen.Add(successor))
          pending.Enqueue(successor);
    return seen.Count == region.Count;
  }

  private static bool FactsAvailableAtGuard(
      RegionShape shape,
      IReadOnlyList<IrValue> guards,
      IReadOnlyDictionary<IrValue, IrValue> assumptions,
      IrDominators dominators) {
    foreach (var guard in guards)
      if (!IsAvailableAtGuard(guard, shape, dominators))
        return false;
    foreach (var (value, replacement) in assumptions)
      if (!IsAvailableAtGuard(value, shape, dominators)
          || !IsAvailableAtGuard(replacement, shape, dominators))
        return false;
    return true;
  }

  private static bool IsAvailableAtGuard(IrValue value, RegionShape shape, IrDominators dominators) {
    if (value is not IrInstruction instruction)
      return true;
    if (instruction.Parent is not { } block || shape.Blocks.Contains(block))
      return false;
    return ReferenceEquals(block, shape.Preheader) || dominators.Dominates(block, shape.Preheader);
  }

  private static bool TryCollectLiveOuts(
      RegionShape shape,
      IrDominators dominators,
      out IReadOnlyList<LiveOut> liveOuts) {
    var result = new List<LiveOut>();
    var hasExternalExitPredecessor = shape.Exit.Predecessors
      .Any(predecessor => !shape.Blocks.Contains(predecessor));

    foreach (var value in shape.Blocks
        .SelectMany(block => block.Instructions)
        .Where(instruction => !instruction.Type.IsVoid)) {
      var users = new List<IrInstruction>();
      foreach (var user in value.Users) {
        if (user.Parent is not { } useBlock)
          return Fail(out liveOuts);
        if (shape.Blocks.Contains(useBlock))
          continue;
        if (user is IrPhi && ReferenceEquals(useBlock, shape.Exit))
          continue; // existing exit phis are extended separately, preserving their edge-specific values
        if (user is IrPhi
            || (!ReferenceEquals(useBlock, shape.Exit) && !dominators.Dominates(shape.Exit, useBlock)))
          return Fail(out liveOuts);
        users.Add(user);
      }

      if (users.Count == 0)
        continue;
      if (hasExternalExitPredecessor || value.Parent is not { } definition)
        return Fail(out liveOuts);
      if (shape.ExitPredecessors.Any(predecessor =>
          !ReferenceEquals(definition, predecessor) && !dominators.Dominates(definition, predecessor)))
        return Fail(out liveOuts);
      result.Add(new(value, users));
    }

    liveOuts = result;
    return true;
  }

  private static bool Fail(out IReadOnlyList<LiveOut> liveOuts) {
    liveOuts = [];
    return false;
  }

  private static GuardedSpecializationResult Apply(
      IrFunction fn,
      IReadOnlyList<IrBasicBlock> region,
      IReadOnlyList<IrValue> guards,
      IReadOnlyDictionary<IrValue, IrValue> assumptions,
      RegionShape shape,
      IReadOnlyList<LiveOut> liveOuts) {
    var prefix = UniquePrefix(fn, region);
    var seed = new Dictionary<IrValue, IrValue>(ReferenceEqualityComparer.Instance);
    foreach (var (value, replacement) in assumptions)
      seed[value] = replacement;
    foreach (var guard in guards)
      seed[guard] = IrBuilder.ConstBool(true);

    var blockMap = IrCloner.Clone(fn, region, seed, prefix, out var valueMap);
    var fastEntry = blockMap[shape.Entry];
    var guardBlock = fn.CreateBlock(prefix + "guard");
    foreach (var phi in shape.Entry.Phis)
      phi.RenameIncomingBlock(shape.Preheader, guardBlock);
    foreach (var phi in fastEntry.Phis)
      phi.RenameIncomingBlock(shape.Preheader, guardBlock);

    ((IrBr)shape.Preheader.Terminator!).Target = guardBlock;
    var combined = CombineGuards(guardBlock, guards);
    guardBlock.Append(new IrCondBr(combined, fastEntry, shape.Entry));

    RepairExitPhis(shape, blockMap, valueMap);
    RepairDirectLiveOuts(shape, liveOuts, blockMap, valueMap);
    return new(guardBlock, fastEntry, shape.Entry, shape.Exit, blockMap, valueMap);
  }

  private static IrValue CombineGuards(IrBasicBlock guardBlock, IReadOnlyList<IrValue> guards) {
    var combined = guards[0];
    for (var i = 1; i < guards.Count; ++i)
      combined = guardBlock.Append(new IrBinary(IrBinaryOp.And, combined, guards[i]));
    return combined;
  }

  private static void RepairExitPhis(
      RegionShape shape,
      IReadOnlyDictionary<IrBasicBlock, IrBasicBlock> blockMap,
      IReadOnlyDictionary<IrValue, IrValue> valueMap) {
    foreach (var phi in shape.Exit.Phis.ToList()) {
      var incoming = shape.ExitPredecessors
        .Select(predecessor => (Predecessor: predecessor, Value: phi.IncomingFrom(predecessor)!))
        .ToList();
      foreach (var (predecessor, value) in incoming)
        phi.AddIncoming(valueMap.GetValueOrDefault(value, value), blockMap[predecessor]);
    }
  }

  private static void RepairDirectLiveOuts(
      RegionShape shape,
      IReadOnlyList<LiveOut> liveOuts,
      IReadOnlyDictionary<IrBasicBlock, IrBasicBlock> blockMap,
      IReadOnlyDictionary<IrValue, IrValue> valueMap) {
    foreach (var liveOut in liveOuts) {
      var joined = shape.Exit.AppendPhi(new IrPhi(liveOut.Value.Type) { Name = liveOut.Value.Name });
      foreach (var predecessor in shape.ExitPredecessors) {
        joined.AddIncoming(liveOut.Value, predecessor);
        joined.AddIncoming(valueMap[liveOut.Value], blockMap[predecessor]);
      }
      foreach (var user in liveOut.Users)
        user.ReplaceOperand(liveOut.Value, joined);
    }
  }

  private static string UniquePrefix(IrFunction fn, IReadOnlyList<IrBasicBlock> region) {
    var labels = fn.Blocks.Select(block => block.Label).ToHashSet(StringComparer.Ordinal);
    for (var id = 0; ; ++id) {
      var prefix = $"spec{id}.";
      if (!labels.Contains(prefix + "guard") && region.All(block => !labels.Contains(prefix + block.Label)))
        return prefix;
    }
  }
}
