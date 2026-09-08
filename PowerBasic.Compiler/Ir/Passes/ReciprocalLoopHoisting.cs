namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// The guarded loop slice of O0338. A shared runtime reciprocal is profitable only if it is formed
/// once, but an ordinary LICM hoist would evaluate <c>1/d</c> even when a zero-trip loop never executes
/// its first division. This pass clones the loop-entry comparison into the preheader and evaluates the
/// reciprocal only on the entering edge.
/// </summary>
internal static class ReciprocalLoopHoisting {

  private sealed record Loop(
    IrBasicBlock Header,
    HashSet<IrBasicBlock> Body,
    IrBasicBlock Preheader,
    IrBasicBlock Exit,
    IrCmp Test,
    bool BodyOnTrue);

  /// <summary>Hoists guarded reciprocal values and returns how many reciprocal divisions moved.</summary>
  public static int Run(IrFunction fn) {
    var moved = 0;
    while (IrDominators.Build(fn) is { } dominators) {
      var plan = FindPlan(fn, dominators);
      if (plan is null)
        break;
      moved += Apply(fn, plan.Value.Loop, plan.Value.Reciprocals);
    }
    return moved;
  }

  private static (Loop Loop, List<IrBinary> Reciprocals)? FindPlan(IrFunction fn, IrDominators dominators) {
    foreach (var loop in DetectLoops(fn, dominators)) {
      if (!IsGuardable(loop, dominators))
        continue;

      var reciprocals = loop.Body
        .SelectMany(block => block.Instructions)
        .OfType<IrBinary>()
        .Where(binary => IsSharedReciprocal(binary, loop, dominators))
        .ToList();
      if (reciprocals.Count > 0)
        return (loop, reciprocals);
    }
    return null;
  }

  private static bool IsGuardable(Loop loop, IrDominators dominators) {
    if (loop.Preheader.Terminator is not IrBr branch || !ReferenceEquals(branch.Target, loop.Header))
      return false;
    if (loop.Exit.Phis.Any() || loop.Exit.Predecessors.Count() != 1)
      return false;
    if (loop.Body.SelectMany(block => block.Instructions).Any(instruction => instruction is IrCall))
      return false;

    var phis = loop.Header.Phis.ToList();
    if (loop.Header.Instructions.Count != phis.Count + 2
        || !ReferenceEquals(loop.Header.Instructions[^2], loop.Test)
        || loop.Test.Users.Any(user => !ReferenceEquals(user, loop.Header.Terminator)))
      return false;

    // Bypassing the header on the zero-trip edge is safe only when no loop-carried value is read after
    // the loop. Broader SSA repair is possible, but declining here keeps this transform transactional.
    foreach (var phi in phis)
      if (phi.Users.Any(user => user.Parent is not { } parent || !loop.Body.Contains(parent)))
        return false;

    return TryMapEntryValue(loop.Test.Lhs, loop, dominators, out _)
      && TryMapEntryValue(loop.Test.Rhs, loop, dominators, out _);
  }

  private static bool IsSharedReciprocal(IrBinary binary, Loop loop, IrDominators dominators) {
    if (binary is not {
          Op: IrBinaryOp.FDiv,
          Type: { Kind: IrTypeKind.Float, Format: IrFloatFormat.Ieee, Bits: 32 or 64 or 80 },
          Lhs: IrConstantFloat { Value: 1.0 },
        }
        || (binary.FastMathFlags & IrFastMathFlags.AllowReciprocal) == 0
        || binary.Parent is not { } parent
        || !loop.Body.Contains(parent))
      return false;

    // The entry guard protects the zero-trip case, but it must not turn an inner conditional use into
    // an unconditional evaluation on every entered iteration. Keep this slice deliberately canonical:
    // the reciprocal has to be the first real operation in the body block selected directly by the
    // header. Then every non-zero iteration would have evaluated it immediately anyway; the transform
    // only changes "once per iteration" into "once before the first iteration".
    if (loop.Header.Terminator is not IrCondBr entryBranch)
      return false;
    var firstBody = loop.BodyOnTrue ? entryBranch.IfTrue : entryBranch.IfFalse;
    if (!ReferenceEquals(parent, firstBody)
        || !ReferenceEquals(parent.Instructions.FirstOrDefault(instruction => instruction is not IrPhi), binary))
      return false;

    if (binary.Rhs is IrInstruction divisorInstruction) {
      if (divisorInstruction.Parent is not { } divisorBlock
          || loop.Body.Contains(divisorBlock)
          || !dominators.Dominates(divisorBlock, loop.Preheader))
        return false;
    }

    var users = binary.Users.ToList();
    return users.Count >= 2
      && users.All(user => user is IrBinary { Op: IrBinaryOp.FMul }
        && user.Parent is { } userBlock && loop.Body.Contains(userBlock));
  }

  private static int Apply(IrFunction fn, Loop loop, IReadOnlyList<IrBinary> reciprocals) {
    if (!TryMapEntryValue(loop.Test.Lhs, loop, IrDominators.Build(fn)!, out var guardLhs)
        || !TryMapEntryValue(loop.Test.Rhs, loop, IrDominators.Build(fn)!, out var guardRhs)
        || loop.Preheader.Terminator is not { } oldTerminator)
      return 0;

    var guard = loop.Preheader.InsertBefore(new IrCmp(loop.Test.Pred, guardLhs, guardRhs) {
      IsSourceCondition = loop.Test.IsSourceCondition,
      FastMathFlags = loop.Test.FastMathFlags,
    }, oldTerminator);

    var init = fn.CreateBlock(UniqueLabel(fn, "recip.init"));
    foreach (var reciprocal in reciprocals) {
      reciprocal.Parent!.Remove(reciprocal);
      init.Append(reciprocal);
    }
    init.Append(new IrBr(loop.Header));

    foreach (var phi in loop.Header.Phis)
      phi.RenameIncomingBlock(loop.Preheader, init);

    oldTerminator.EraseFromParent();
    loop.Preheader.Append(loop.BodyOnTrue
      ? new IrCondBr(guard, init, loop.Exit)
      : new IrCondBr(guard, loop.Exit, init));
    return reciprocals.Count;
  }

  private static bool TryMapEntryValue(IrValue value, Loop loop, IrDominators dominators, out IrValue mapped) {
    if (value is IrPhi phi && ReferenceEquals(phi.Parent, loop.Header)) {
      mapped = phi.IncomingFrom(loop.Preheader)!;
      return mapped is not null && IsAvailable(mapped, loop, dominators);
    }

    mapped = value;
    return IsAvailable(mapped, loop, dominators);
  }

  private static bool IsAvailable(IrValue value, Loop loop, IrDominators dominators) {
    if (value is not IrInstruction instruction)
      return true;
    return instruction.Parent is { } block
      && !loop.Body.Contains(block)
      && dominators.Dominates(block, loop.Preheader);
  }

  private static List<Loop> DetectLoops(IrFunction fn, IrDominators dominators) {
    var bodies = new Dictionary<IrBasicBlock, HashSet<IrBasicBlock>>(ReferenceEqualityComparer.Instance);
    foreach (var latch in fn.Blocks.Where(dominators.IsReachable))
      foreach (var successor in latch.Successors)
        if (dominators.Dominates(successor, latch)) {
          if (!bodies.TryGetValue(successor, out var body))
            bodies[successor] = body = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance) { successor };
          var pending = new Stack<IrBasicBlock>();
          pending.Push(latch);
          while (pending.Count > 0) {
            var block = pending.Pop();
            if (!body.Add(block))
              continue;
            foreach (var predecessor in block.Predecessors)
              pending.Push(predecessor);
          }
        }

    var result = new List<Loop>();
    foreach (var (header, body) in bodies) {
      if (header.Terminator is not IrCondBr branch || branch.Condition is not IrCmp test)
        continue;
      var trueInside = body.Contains(branch.IfTrue);
      var falseInside = body.Contains(branch.IfFalse);
      if (trueInside == falseInside)
        continue;

      var preheaders = header.Predecessors.Where(predecessor => !body.Contains(predecessor)).ToList();
      if (preheaders.Count != 1)
        continue;
      var exit = trueInside ? branch.IfFalse : branch.IfTrue;
      if (body.Any(block => block.Successors.Any(successor =>
            !body.Contains(successor) && !(ReferenceEquals(block, header) && ReferenceEquals(successor, exit)))))
        continue;

      result.Add(new Loop(header, body, preheaders[0], exit, test, trueInside));
    }
    return result;
  }

  private static string UniqueLabel(IrFunction fn, string stem) {
    if (fn.Blocks.All(block => !string.Equals(block.Label, stem, StringComparison.Ordinal)))
      return stem;
    for (var suffix = 1; ; ++suffix) {
      var candidate = $"{stem}.{suffix}";
      if (fn.Blocks.All(block => !string.Equals(block.Label, candidate, StringComparison.Ordinal)))
        return candidate;
    }
  }
}
