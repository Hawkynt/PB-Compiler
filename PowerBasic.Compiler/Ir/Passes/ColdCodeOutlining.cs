namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0275: outlines structurally cold terminal side regions into private helper functions.
///
/// <para>
/// The pass deliberately starts with the extraction shape that has no live-outs: a single-entry
/// branch arm whose control flow ends in <see cref="IrRet"/> / <see cref="IrUnreachable"/> and never
/// rejoins the hot side. Values read from the surrounding function become helper parameters; values
/// produced inside the region must have no users outside it. This is the useful error/early-exit case
/// and, more importantly, it is the one that can be proven without inventing tuple returns or frame
/// sharing semantics.
/// </para>
/// <para>
/// In the absence of profile metadata O0275 uses conservative static coldness cues: an
/// unreachable-terminating side, a terminal exit from a loop, or the unlikely outcome of an equality
/// test against integer zero / a null pointer. The generated helper is <see cref="IrFunction.NoInline"/>
/// so a later inliner cannot immediately undo the transform. SPEED is the pipeline consumer because
/// outlining trades a little total code size for a smaller hot body.
/// </para>
/// </summary>
public static class ColdCodeOutlining {

  // A call + return replaces the outlined side in the caller. Three source instructions are therefore
  // the smallest region that actually shrinks the hot function at the IR instruction-count level.
  private const int MinRegionInstructions = 3;

  // Keep argument-move/call overhead bounded. This matches the deliberately small extraction boundary
  // used by mature hot/cold splitters and leaves larger live-in sets to a future target-aware cost model.
  private const int MaxLiveIns = 4;

  private const string GeneratedHelperMarker = "__cold_";

  /// <summary>Outlines eligible cold side regions; returns the number of helpers created.</summary>
  public static int Run(IrModule module) {
    var outlined = 0;

    // Snapshot: outlining appends helper functions to the module. A later RunOnModule call can see those
    // helpers, so explicitly leave generated bodies alone: their entire body is cold already.
    foreach (var function in module.Functions.ToList()) {
      if (function.IsDeclaration || function.HasErrorHandler || function.HasInlineAsm || IsGeneratedHelper(function))
        continue;

      var addressTaken = function.AddressTakenBlocks();
      foreach (var block in function.Blocks.ToList()) {
        if (!ReferenceEquals(block.Parent, function) || block.Terminator is not IrCondBr branch)
          continue;

        var region = ChooseRegion(function, block, branch, addressTaken);
        if (region is null || region.InstructionCount < MinRegionInstructions)
          continue;

        var liveIns = CollectLiveIns(function, region.Blocks);
        if (liveIns is null || liveIns.Count > MaxLiveIns)
          continue;

        Outline(module, function, region.Blocks, liveIns, outlined++);
      }
    }

    return outlined;
  }

  private static bool IsGeneratedHelper(IrFunction function)
    => function.NoInline && function.Name.Contains(GeneratedHelperMarker, StringComparison.Ordinal);

  private static Region? ChooseRegion(IrFunction function, IrBasicBlock branchBlock, IrCondBr branch,
      HashSet<IrBasicBlock> addressTaken) {
    if (ReferenceEquals(branch.IfTrue, branch.IfFalse))
      return null;

    var trueRegion = TryCollectRegion(function, branchBlock, branch.IfTrue, branch.IfFalse, addressTaken);
    var falseRegion = TryCollectRegion(function, branchBlock, branch.IfFalse, branch.IfTrue, addressTaken);

    // An edge whose terminal leaves are all unreachable is the strongest structural coldness signal.
    if (trueRegion?.EndsInUnreachable != falseRegion?.EndsInUnreachable)
      return trueRegion?.EndsInUnreachable == true ? trueRegion : falseRegion;

    // A side that loops back through the branch source is the continuing/hot path; the other terminal
    // side is the loop exit. Prefer this structural signal over generic value heuristics.
    if (trueRegion is not null && CanReachWithoutCrossing(branch.IfFalse, branchBlock, stop: branch.IfTrue))
      return trueRegion;
    if (falseRegion is not null && CanReachWithoutCrossing(branch.IfTrue, branchBlock, stop: branch.IfFalse))
      return falseRegion;

    // LLVM's static branch-probability analysis predicts equality with zero/null as the unlikely
    // outcome (and inequality as the likely one). Use the same public behavioral rule, independently
    // expressed for PB's IR, rather than guessing coldness from source-region size.
    var coldSuccessor = EqualityColdSuccessor(branch);
    if (ReferenceEquals(coldSuccessor, branch.IfTrue))
      return trueRegion;
    if (ReferenceEquals(coldSuccessor, branch.IfFalse))
      return falseRegion;

    return null;
  }

  private static IrBasicBlock? EqualityColdSuccessor(IrCondBr branch) {
    if (branch.Condition is not IrCmp { Pred: IrCmpPred.Eq or IrCmpPred.Ne } cmp)
      return null;

    var lhsIsZero = IsZeroLike(cmp.Lhs);
    var rhsIsZero = IsZeroLike(cmp.Rhs);
    if (lhsIsZero == rhsIsZero)
      return null; // neither operand, or both operands, are the zero/null sentinel

    return cmp.Pred == IrCmpPred.Eq ? branch.IfTrue : branch.IfFalse;
  }

  private static bool IsZeroLike(IrValue value)
    => value is IrConstantInt { Value: 0 } or IrNullPtr;

  private static Region? TryCollectRegion(IrFunction function, IrBasicBlock branchBlock,
      IrBasicBlock start, IrBasicBlock otherStart, HashSet<IrBasicBlock> addressTaken) {
    if (ReferenceEquals(start, function.Entry) || addressTaken.Contains(start))
      return null;

    var forbidden = ReachableWithoutCrossing(otherStart, branchBlock);
    if (forbidden.Contains(start))
      return null; // the two sides rejoin; that would require live-outs

    var seen = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance);
    var blocks = new List<IrBasicBlock>();
    var pending = new Queue<IrBasicBlock>();
    pending.Enqueue(start);

    while (pending.Count > 0) {
      var block = pending.Dequeue();
      if (!seen.Add(block))
        continue;
      if (ReferenceEquals(block, branchBlock) || forbidden.Contains(block)
          || !ReferenceEquals(block.Parent, function) || addressTaken.Contains(block))
        return null;
      if (block.Terminator is null || block.Terminator is IrIndirectBr)
        return null;

      blocks.Add(block);
      foreach (var successor in block.Successors)
        pending.Enqueue(successor);
    }

    var terminalBlocks = blocks.Where(block => !block.Successors.Any()).ToList();
    if (terminalBlocks.Count == 0 || terminalBlocks.Any(block => block.Terminator is not (IrRet or IrUnreachable)))
      return null; // a closed infinite region, or a malformed/non-terminal leaf, is not a cold exit

    var region = seen;
    var startPredecessors = start.Predecessors.ToList();
    if (startPredecessors.Count != 1 || !ReferenceEquals(startPredecessors[0], branchBlock) || start.Phis.Any())
      return null; // helper entry blocks cannot carry caller-edge phis or an internal backedge

    foreach (var block in blocks) {
      if (!ReferenceEquals(block, start) && block.Predecessors.Any(predecessor => !region.Contains(predecessor)))
        return null; // every non-entry block must be owned entirely by the region

      foreach (var phi in block.Phis)
        if (phi.IncomingBlocks.Any(predecessor => !region.Contains(predecessor)))
          return null;

      foreach (var instruction in block.Instructions) {
        // A value defined in the extracted function cannot continue to feed the caller.
        if (instruction.Users.Any(user => user.Parent is null || !region.Contains(user.Parent)))
          return null;

        // Block addresses are function-local. Internal ones are remapped by IrCloner; an address of a
        // caller block cannot be carried into the outlined helper.
        if (instruction.Operands.OfType<IrBlockAddress>().Any(address => !region.Contains(address.Block)))
          return null;
      }
    }

    return new Region(blocks, blocks.Sum(block => block.Instructions.Count),
      terminalBlocks.All(block => block.Terminator is IrUnreachable));
  }

  private static HashSet<IrBasicBlock> ReachableWithoutCrossing(IrBasicBlock start, IrBasicBlock barrier) {
    var seen = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance);
    var pending = new Stack<IrBasicBlock>();
    pending.Push(start);
    while (pending.Count > 0) {
      var block = pending.Pop();
      if (!seen.Add(block) || ReferenceEquals(block, barrier))
        continue;
      foreach (var successor in block.Successors)
        pending.Push(successor);
    }
    return seen;
  }

  private static bool CanReachWithoutCrossing(IrBasicBlock start, IrBasicBlock target, IrBasicBlock stop) {
    var seen = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance);
    var pending = new Stack<IrBasicBlock>();
    pending.Push(start);
    while (pending.Count > 0) {
      var block = pending.Pop();
      if (ReferenceEquals(block, target))
        return true;
      if (!seen.Add(block) || ReferenceEquals(block, stop))
        continue;
      foreach (var successor in block.Successors)
        pending.Push(successor);
    }
    return false;
  }

  private static List<IrValue>? CollectLiveIns(IrFunction function, IReadOnlyList<IrBasicBlock> blocks) {
    var region = blocks.ToHashSet(ReferenceEqualityComparer.Instance);
    var seen = new HashSet<IrValue>(ReferenceEqualityComparer.Instance);
    var result = new List<IrValue>();

    foreach (var block in blocks)
      foreach (var instruction in block.Instructions)
        foreach (var operand in instruction.Operands)
          switch (operand) {
            case IrBlockAddress address when region.Contains(address.Block):
              break;

            case IrBlockAddress:
              return null;

            case IrArgument argument when ReferenceEquals(argument.Parent, function):
              Add(argument);
              break;

            case IrArgument:
              return null;

            case IrInstruction value when value.Parent is { } definingBlock && region.Contains(definingBlock):
              break;

            case IrInstruction value when value.Parent?.Parent is { } parent && ReferenceEquals(parent, function):
              Add(value);
              break;

            case IrInstruction:
              return null;
          }

    return result;

    void Add(IrValue value) {
      if (seen.Add(value))
        result.Add(value);
    }
  }

  private static void Outline(IrModule module, IrFunction function, IReadOnlyList<IrBasicBlock> region,
      IReadOnlyList<IrValue> liveIns, int id) {
    var name = UniqueHelperName(module, function.Name, id);
    var parameters = liveIns.Select((value, index) => new IrArgument(value.Type, index, $"live{index}")).ToList();
    var helper = module.AddFunction(new IrFunction(name, function.ReturnType, parameters) { NoInline = true });

    var seed = new Dictionary<IrValue, IrValue>(ReferenceEqualityComparer.Instance);
    for (var i = 0; i < liveIns.Count; ++i)
      seed[liveIns[i]] = parameters[i];
    IrCloner.Clone(helper, region, seed, string.Empty);

    var entry = region[0];
    foreach (var block in region.Skip(1).ToList())
      function.RemoveBlock(block);
    foreach (var instruction in entry.Instructions.ToList())
      instruction.EraseFromParent();

    var call = entry.Append(new IrCall(function.ReturnType, helper, liveIns));
    entry.Append(function.ReturnType.IsVoid ? new IrRet() : new IrRet(call));
  }

  private static string UniqueHelperName(IrModule module, string functionName, int id) {
    var suffix = id;
    string candidate;
    do
      candidate = $"{functionName}{GeneratedHelperMarker}{suffix++}";
    while (module.FindFunction(candidate) is not null);
    return candidate;
  }

  private sealed record Region(IReadOnlyList<IrBasicBlock> Blocks, int InstructionCount, bool EndsInUnreachable);
}
