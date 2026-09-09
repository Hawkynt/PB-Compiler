namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0310 — explicit-CFG side exits for speculative loop versions.
///
/// <para>
/// The generic loop remains the semantic authority. A fast clone runs under one per-iteration
/// assumption; while the assumption holds, uses dominated by that guard see its constant truth value.
/// When it fails, control does NOT jump back to the generic header. Instead, only the remainder of the
/// current generic iteration is cloned and seeded with the fast clone's SSA values. That resume suffix
/// performs work that has not happened yet, reaches the generic latch, and transfers the reconstructed
/// loop-carried state into the original header for all following iterations.
/// </para>
/// <para>
/// This is deliberately a construction primitive rather than a pass that invents assumptions. O0304,
/// O0308 and O0309 know which fact is profitable to speculate; O0310 owns the difficult part they all
/// share: exact resume position, SSA state reconstruction and a code-growth fence.
/// </para>
/// </summary>
public static class SideExitDeoptimization {

  /// <summary>The most instructions the fast version plus resume suffix may clone.</summary>
  private const int _MAX_CLONED_INSTRUCTIONS = 192;

  /// <summary>
  /// The concrete version produced for a consumer. The maps let the producer find the fast copy of an
  /// operation it wants to specialize further without rediscovering cloned values by name.
  /// </summary>
  public sealed record Version(
    IrBasicBlock FastHeader,
    IrBasicBlock DeoptimizationEntry,
    IReadOnlyDictionary<IrBasicBlock, IrBasicBlock> FastBlocks,
    IReadOnlyDictionary<IrValue, IrValue> FastValues,
    IReadOnlyDictionary<IrBasicBlock, IrBasicBlock> ResumeBlocks,
    IReadOnlyDictionary<IrValue, IrValue> ResumeValues);

  /// <summary>
  /// Versions one canonical loop around <paramref name="guard"/>. The selected
  /// <paramref name="assumedValue"/> stays in the fast copy; the opposite edge side-exits through an
  /// exact generic suffix and then remains in the generic loop.
  ///
  /// Returns false without mutation when the loop is not a single-entry/single-latch region this
  /// helper can reconstruct exactly.
  /// </summary>
  public static bool TryVersionLoop(IrFunction fn, IrBasicBlock header, IrCondBr guard,
      bool assumedValue, out Version? version) {
    ArgumentNullException.ThrowIfNull(fn);
    ArgumentNullException.ThrowIfNull(header);
    ArgumentNullException.ThrowIfNull(guard);
    version = null;

    if (fn.Entry is null || fn.HasErrorHandler || fn.HasInlineAsm
        || !ReferenceEquals(header.Parent, fn)
        || guard.Parent is not { } guardBlock
        || !ReferenceEquals(guardBlock.Parent, fn)
        || !ReferenceEquals(guardBlock.Terminator, guard)
        || guard.Condition is IrConstant)
      return false;

    var loop = Match(fn, header);
    if (loop is null || !loop.Region.Contains(guardBlock) || ReferenceEquals(guardBlock, header)
        || ReferenceEquals(guardBlock, loop.Latch))
      return false;

    var assumed = assumedValue ? guard.IfTrue : guard.IfFalse;
    var fallback = assumedValue ? guard.IfFalse : guard.IfTrue;
    if (ReferenceEquals(assumed, fallback)
        || !loop.Region.Contains(assumed) || !loop.Region.Contains(fallback)
        || ReferenceEquals(assumed, header) || ReferenceEquals(fallback, header))
      return false;

    var dominators = IrDominators.Build(fn)!;
    if (!dominators.Dominates(guardBlock, loop.Latch)
        || !dominators.Dominates(guardBlock, assumed)
        || !dominators.Dominates(guardBlock, fallback))
      return false;

    var resumeRegion = CollectResumeRegion(fallback, loop);
    if (resumeRegion is null || !resumeRegion.Contains(loop.Latch))
      return false;

    if (loop.Region.Any(fn.AddressTakenBlocks().Contains)
        || loop.Region.SelectMany(b => b.Instructions).Any(i => !Cloneable(i)))
      return false;

    var regionBlocks = Ordered(fn, header, loop.Region);
    var resumeBlocks = Ordered(fn, fallback, resumeRegion);
    var cloneCost = regionBlocks.Sum(b => b.Instructions.Count) + resumeBlocks.Sum(b => b.Instructions.Count);
    if (cloneCost > _MAX_CLONED_INSTRUCTIONS)
      return false;

    var headerState = new List<(IrPhi Phi, IrValue Latched)>();
    foreach (var phi in header.Phis) {
      if (phi.IncomingBlocks.Count != 2
          || phi.IncomingFrom(loop.Preheader) is null
          || phi.IncomingFrom(loop.Latch) is not { } latched)
        return false;
      headerState.Add((phi, latched));
    }

    var exitPhis = new List<(IrPhi Phi, IrValue Incoming)>();
    foreach (var phi in loop.Exit.Phis) {
      if (phi.IncomingBlocks.Count != 1 || phi.IncomingFrom(header) is not { } incoming)
        return false;
      exitPhis.Add((phi, incoming));
    }
    var exitPhiSet = exitPhis.Select(x => x.Phi).ToHashSet(ReferenceEqualityComparer.Instance);
    var escaping = loop.Region
      .SelectMany(b => b.Instructions)
      .Where(i => !i.Type.IsVoid
        && i.Users.Any(u => u.Parent is { } where && !loop.Region.Contains(where)
          && (u is not IrPhi phi || !exitPhiSet.Contains(phi))))
      .ToList();

    // Complete fast copy. Its header keeps the original preheader as the entry incoming and gets the
    // cloned latch as its back-edge incoming, which is exactly the versioned-loop state.
    var fastBlocks = IrCloner.Clone(fn, regionBlocks,
      new Dictionary<IrValue, IrValue>(ReferenceEqualityComparer.Instance),
      "deopt.fast.", out var fastValues);
    var fastHeader = fastBlocks[header];
    var fastGuardBlock = fastBlocks[guardBlock];
    var fastGuard = (IrCondBr)fastGuardBlock.Terminator!;

    // Clone only the work that remains AFTER a failed guard. Seeding with the fast value map is the
    // state map: every value produced before the exit resumes from its already-computed fast value,
    // while values defined in the suffix are overwritten by their newly cloned generic definitions.
    var resumeSeed = new Dictionary<IrValue, IrValue>(ReferenceEqualityComparer.Instance);
    foreach (var (generic, fast) in fastValues)
      resumeSeed[generic] = fast;
    resumeSeed[guard.Condition] = new IrConstantInt(IrType.I1, assumedValue ? 0 : 1);

    var resumeBlockMap = IrCloner.Clone(fn, resumeBlocks, resumeSeed, "deopt.resume.", out var resumeValues);
    var deoptimizationEntry = resumeBlockMap[fallback];
    var resumeLatch = resumeBlockMap[loop.Latch];

    // IrCloner deliberately preserves predecessors that live outside the cloned set. For a resume
    // suffix those are paths the failed guard did NOT take. Remove them from cloned phis, except for
    // the fallback edge itself, which now arrives from the FAST guard rather than the generic one.
    RepairResumePhis(resumeBlocks, resumeRegion, resumeBlockMap, fallback, guardBlock, fastGuardBlock);

    // The fast guard is the one runtime check that remains. Its successful edge stays entirely in the
    // fast copy; failure starts after the guard in the resume suffix, so nothing before the guard is
    // executed twice.
    if (assumedValue) {
      fastGuard.IfTrue = fastBlocks[guard.IfTrue];
      fastGuard.IfFalse = deoptimizationEntry;
    } else {
      fastGuard.IfTrue = deoptimizationEntry;
      fastGuard.IfFalse = fastBlocks[guard.IfFalse];
    }
    foreach (var phi in fastBlocks[fallback].Phis)
      phi.RemoveIncoming(fastGuardBlock);

    // Inside the remainder of THIS successful iteration the guard fact is now materialized in the IR.
    // Stop at the back edge: the next iteration must test its freshly computed condition again.
    var postGuard = CollectFastIterationSuffix(fastBlocks[assumed], fastHeader, fastBlocks.Values);
    var known = new IrConstantInt(IrType.I1, assumedValue ? 1 : 0);
    foreach (var user in fastGuard.Condition.Users.ToList())
      if (!ReferenceEquals(user, fastGuard)
          && user.Parent is { } where
          && postGuard.Contains(where))
        user.ReplaceOperand(fastGuard.Condition, known);

    // Once deoptimization has completed the failed iteration, all following iterations run the
    // original generic loop. Replace its no-longer-present preheader incoming with the exact state
    // produced by the cloned generic latch.
    foreach (var (phi, latched) in headerState) {
      phi.RemoveIncoming(loop.Preheader);
      phi.AddIncoming(Map(resumeValues, latched), resumeLatch);
    }

    // The loop may also finish without deoptimizing. Existing exit phis and direct escaping SSA values
    // therefore need one incoming for the generic header and one for the fast header.
    foreach (var (phi, incoming) in exitPhis)
      phi.AddIncoming(Map(fastValues, incoming), fastHeader);

    foreach (var value in escaping) {
      var joined = loop.Exit.AppendPhi(new IrPhi(value.Type) { Name = value.Name });
      joined.AddIncoming(value, header);
      joined.AddIncoming(Map(fastValues, value), fastHeader);

      foreach (var user in value.Users.ToList()) {
        if (ReferenceEquals(user, joined) || user.Parent is not { } where || loop.Region.Contains(where))
          continue;
        if (user is IrPhi phi && exitPhiSet.Contains(phi))
          continue;
        user.ReplaceOperand(value, joined);
      }
    }

    ((IrBr)loop.Preheader.Terminator!).Target = fastHeader;

    // Sending the fast guard's failing edge into the resume suffix orphans the fast copy of the
    // fallback arm, and with it every fast block only that arm reached. They are unreachable but still
    // present: still reading values whose definitions no longer dominate them, and still named by phis
    // in the fast blocks that survived. Neither is verifiable SSA, so the copies go with the edge.
    Prune(fn, fastBlocks, fastValues, out var liveBlocks, out var liveValues);

    version = new(fastHeader, deoptimizationEntry, liveBlocks, liveValues, resumeBlockMap, resumeValues);
    return true;
  }

  /// <summary>
  /// Removes the cloned blocks that no longer have a path from the entry, drops the phi incomings that
  /// name them, and answers the block/value maps restricted to what remains.
  /// </summary>
  private static void Prune(IrFunction fn,
      IReadOnlyDictionary<IrBasicBlock, IrBasicBlock> blocks, IReadOnlyDictionary<IrValue, IrValue> values,
      out IReadOnlyDictionary<IrBasicBlock, IrBasicBlock> liveBlocks,
      out IReadOnlyDictionary<IrValue, IrValue> liveValues) {
    var reachable = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance) { fn.Entry! };
    var pending = new Stack<IrBasicBlock>([fn.Entry!]);
    while (pending.Count > 0)
      foreach (var successor in pending.Pop().Successors)
        if (reachable.Add(successor))
          pending.Push(successor);

    var orphaned = blocks.Values.Where(b => !reachable.Contains(b)).ToList();
    liveBlocks = blocks;
    liveValues = values;
    if (orphaned.Count == 0)
      return;

    var dead = orphaned.ToHashSet(ReferenceEqualityComparer.Instance);
    foreach (var block in fn.Blocks)
      if (!dead.Contains(block))
        foreach (var phi in block.Phis.ToList())
          foreach (var predecessor in phi.IncomingBlocks.ToList())
            if (dead.Contains(predecessor))
              phi.RemoveIncoming(predecessor);

    foreach (var block in orphaned)
      fn.RemoveBlock(block);

    liveBlocks = blocks.Where(pair => !dead.Contains(pair.Value))
      .ToDictionary(pair => pair.Key, pair => pair.Value, ReferenceEqualityComparer.Instance
        as IEqualityComparer<IrBasicBlock>);
    liveValues = values.Where(pair => pair.Value is not IrInstruction clone || clone.Parent is not null)
      .ToDictionary(pair => pair.Key, pair => pair.Value, ReferenceEqualityComparer.Instance
        as IEqualityComparer<IrValue>);
  }

  private sealed record Loop(
    IrBasicBlock Header,
    IrBasicBlock Preheader,
    IrBasicBlock Latch,
    IrBasicBlock Exit,
    HashSet<IrBasicBlock> Region);

  private static Loop? Match(IrFunction fn, IrBasicBlock header) {
    if (header.Terminator is not IrCondBr loopBranch)
      return null;

    var exit = loopBranch.IfFalse;
    if (ReferenceEquals(loopBranch.IfTrue, exit))
      return null;

    var region = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance) { header };
    var queue = new Queue<IrBasicBlock>([loopBranch.IfTrue]);
    IrBasicBlock? latch = null;

    while (queue.Count > 0) {
      var block = queue.Dequeue();
      if (ReferenceEquals(block, header) || !region.Add(block))
        continue;
      if (!ReferenceEquals(block.Parent, fn))
        return null;

      if (block.Terminator is not (IrBr or IrCondBr))
        return null;

      foreach (var successor in block.Successors) {
        if (ReferenceEquals(successor, exit))
          return null; // only the header may leave the loop
        if (ReferenceEquals(successor, header)) {
          if (latch is not null && !ReferenceEquals(latch, block))
            return null;
          latch = block;
        } else
          queue.Enqueue(successor);
      }
    }

    if (latch is null || latch.Terminator is not IrBr back || !ReferenceEquals(back.Target, header))
      return null;
    if (HasInternalCycle(header, region))
      return null;
    if (!AllReach(region, latch))
      return null;
    foreach (var block in region)
      if (!ReferenceEquals(block, header) && block.Predecessors.Any(p => !region.Contains(p)))
        return null;

    var predecessors = header.Predecessors.ToList();
    if (predecessors.Count != 2)
      return null;
    var preheader = predecessors.SingleOrDefault(b => !ReferenceEquals(b, latch));
    if (preheader?.Terminator is not IrBr entry || !ReferenceEquals(entry.Target, header))
      return null;

    var exitPredecessors = exit.Predecessors.ToList();
    if (exitPredecessors.Count != 1 || !ReferenceEquals(exitPredecessors[0], header))
      return null;

    return new(header, preheader, latch, exit, region);
  }

  private static HashSet<IrBasicBlock>? CollectResumeRegion(IrBasicBlock entry, Loop loop) {
    var result = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance);
    var queue = new Queue<IrBasicBlock>([entry]);

    while (queue.Count > 0) {
      var block = queue.Dequeue();
      if (ReferenceEquals(block, loop.Header) || !loop.Region.Contains(block))
        return null;
      if (!result.Add(block))
        continue;

      foreach (var successor in block.Successors) {
        if (ReferenceEquals(successor, loop.Header))
          continue;
        if (!loop.Region.Contains(successor))
          return null;
        queue.Enqueue(successor);
      }
    }

    return result;
  }

  private static void RepairResumePhis(
      IReadOnlyList<IrBasicBlock> sourceBlocks,
      HashSet<IrBasicBlock> resumeRegion,
      IReadOnlyDictionary<IrBasicBlock, IrBasicBlock> resumeBlocks,
      IrBasicBlock entry,
      IrBasicBlock genericGuard,
      IrBasicBlock fastGuard) {
    foreach (var sourceBlock in sourceBlocks) {
      var sourcePhis = sourceBlock.Phis.ToList();
      var clonedPhis = resumeBlocks[sourceBlock].Phis.ToList();

      for (var p = 0; p < sourcePhis.Count; ++p) {
        var sourcePhi = sourcePhis[p];
        var clone = clonedPhis[p];
        foreach (var predecessor in sourcePhi.IncomingBlocks.ToList()) {
          if (resumeRegion.Contains(predecessor))
            continue;
          if (ReferenceEquals(sourceBlock, entry) && ReferenceEquals(predecessor, genericGuard))
            clone.RenameIncomingBlock(predecessor, fastGuard);
          else
            clone.RemoveIncoming(predecessor);
        }
      }
    }
  }

  private static HashSet<IrBasicBlock> CollectFastIterationSuffix(
      IrBasicBlock entry, IrBasicBlock fastHeader, IEnumerable<IrBasicBlock> fastBlocks) {
    var allowed = fastBlocks.ToHashSet(ReferenceEqualityComparer.Instance);
    var result = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance);
    var queue = new Queue<IrBasicBlock>([entry]);

    while (queue.Count > 0) {
      var block = queue.Dequeue();
      if (ReferenceEquals(block, fastHeader) || !allowed.Contains(block) || !result.Add(block))
        continue;
      foreach (var successor in block.Successors)
        if (!ReferenceEquals(successor, fastHeader) && allowed.Contains(successor))
          queue.Enqueue(successor);
    }

    return result;
  }

  private static bool HasInternalCycle(IrBasicBlock entry, HashSet<IrBasicBlock> region) {
    var visiting = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance);
    var visited = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance);

    return Visit(entry);

    bool Visit(IrBasicBlock block) {
      if (!visited.Add(block))
        return false;
      visiting.Add(block);

      foreach (var successor in block.Successors) {
        if (ReferenceEquals(successor, entry) || !region.Contains(successor))
          continue;
        if (visiting.Contains(successor) || !visited.Contains(successor) && Visit(successor))
          return true;
      }

      visiting.Remove(block);
      return false;
    }
  }

  private static bool AllReach(HashSet<IrBasicBlock> region, IrBasicBlock target) {
    var reaching = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance) { target };
    var queue = new Queue<IrBasicBlock>([target]);

    while (queue.Count > 0) {
      var block = queue.Dequeue();
      foreach (var predecessor in block.Predecessors)
        if (region.Contains(predecessor) && reaching.Add(predecessor))
          queue.Enqueue(predecessor);
    }

    return region.All(reaching.Contains);
  }

  private static List<IrBasicBlock> Ordered(IrFunction fn, IrBasicBlock first, HashSet<IrBasicBlock> blocks) {
    var result = new List<IrBasicBlock> { first };
    result.AddRange(fn.Blocks.Where(b => blocks.Contains(b) && !ReferenceEquals(b, first)));
    return result;
  }

  private static IrValue Map(IReadOnlyDictionary<IrValue, IrValue> map, IrValue value)
    => map.TryGetValue(value, out var mapped) ? mapped : value;

  private static bool Cloneable(IrInstruction instruction) => instruction is
    IrPhi or IrBinary or IrCmp or IrCast or IrAlloca or IrLoad or IrStore or IrGep or IrFarPtr
    or IrSelect or IrCall or IrRet or IrBr or IrCondBr or IrSwitch or IrIndirectBr or IrUnreachable;
}
