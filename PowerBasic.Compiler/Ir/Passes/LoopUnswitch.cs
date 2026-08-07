namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0114 — loop unswitching. A conditional inside a loop whose condition is loop-INVARIANT is tested
/// on every iteration for an answer that never changes; the test moves out and the loop is cloned once
/// per outcome.
///
/// <para>
/// The saving is not the compare. It is that each clone can then be specialized: the condition is a
/// known constant inside it, so <see cref="SimplifyCfg"/> folds the branch and <see cref="Dce"/>
/// deletes the arm that cannot run. A loop whose body was <c>IF mode THEN ... ELSE ...</c> becomes two
/// loops that each do one thing - which is why this pass runs before them rather than after.
/// </para>
/// <para>
/// The shape accepted is narrow on purpose. The condition must be defined OUTSIDE the loop, because a
/// value computed inside it is invariant only if nothing on the back edge changes it, and proving that
/// is a different pass. Both cloned loops must be complete copies, so the loop must be a region this
/// can enumerate: a header, a straight body chain ending at the latch, and one exit. Anything else
/// declines - an unswitcher that is clever about which loops it recognises is eventually wrong about
/// one, and here being wrong means running the other half of a branch.
/// </para>
/// </summary>
public static class LoopUnswitch {

  /// <summary>The most instructions a loop may have and still be worth duplicating.</summary>
  private const int _MAX_INSTRUCTIONS = 96;

  /// <summary>Unswitches what it can in <paramref name="fn"/>; returns how many loops were split.</summary>
  public static int Run(IrFunction fn) {
    if (fn.HasErrorHandler || fn.HasInlineAsm)
      return 0;                                  // control can arrive where the CFG does not say

    // One loop per call: cloning invalidates the block list, and a second pass over a rewritten
    // function is cheaper to reason about than an iterator that survives its own edits. The pass
    // manager runs to a fixpoint, so nested cases are reached on the next sweep.
    foreach (var header in fn.Blocks.ToList())
      if (Match(fn, header) is { } loop && Unswitch(fn, loop))
        return 1;
    return 0;
  }

  private sealed record Loop(
    IrBasicBlock Header, List<IrBasicBlock> Body, IrBasicBlock Latch,
    IrBasicBlock Preheader, IrBasicBlock Exit, IrCondBr Branch, IrValue Condition);

  /// <summary>Whether a value is defined outside the loop, so every iteration sees the same one.</summary>
  private static bool Invariant(IrValue value, IEnumerable<IrBasicBlock> inside)
    => value is IrConstant || value.Users.Count == 0
       || (value is IrInstruction instruction && instruction.Parent is { } where && !inside.Contains(where))
       || value is IrArgument or IrGlobalValue;

  private static Loop? Match(IrFunction fn, IrBasicBlock header) {
    if (header.Terminator is not IrCondBr)
      return null;

    var predecessors = fn.Blocks.Where(b => b.Terminator is { } t && t.Successors.Contains(header)).ToList();
    if (predecessors.Count != 2)
      return null;

    // The body is every block reachable from the true edge without leaving through the exit. It has
    // to be collected by TRAVERSAL rather than by walking one path: an IF inside the loop has two
    // arms, and following only the true one leaves the ELSE block outside the region - which then
    // gets cloned as a shared block both copies branch into, i.e. not cloned at all.
    var entry = ((IrCondBr)header.Terminator!).IfTrue;
    var exit = ((IrCondBr)header.Terminator!).IfFalse;
    var body = new List<IrBasicBlock>();
    var queue = new Queue<IrBasicBlock>([entry]);
    IrBasicBlock? latch = null;
    IrCondBr? inner = null;

    while (queue.Count > 0) {
      var at = queue.Dequeue();
      if (ReferenceEquals(at, header) || ReferenceEquals(at, exit) || body.Contains(at))
        continue;
      body.Add(at);
      switch (at.Terminator) {
        case IrBr onward when ReferenceEquals(onward.Target, header):
          if (latch is not null && !ReferenceEquals(latch, at))
            return null;                         // two back edges: not a region this can clone
          latch = at;
          break;
        case IrBr onward:
          queue.Enqueue(onward.Target);
          break;
        // exactly one conditional inside the body; more than one is a shape this declines rather
        // than guesses at, and an unswitcher that guesses runs the other half of a branch
        case IrCondBr branch when inner is null:
          inner = branch;
          queue.Enqueue(branch.IfTrue);
          queue.Enqueue(branch.IfFalse);
          break;
        default:
          return null;
      }
    }

    if (inner is null || latch is null)
      return null;
    var preheader = predecessors.SingleOrDefault(b => !ReferenceEquals(b, latch));
    if (preheader is null || ReferenceEquals(exit, header) || body.Contains(exit))
      return null;

    var region = new List<IrBasicBlock>(body) { header };
    if (!body.Contains(inner.IfFalse) || !Invariant(inner.Condition, region))
      return null;
    if (region.Sum(b => b.Instructions.Count) > _MAX_INSTRUCTIONS)
      return null;

    // nothing outside may jump into the middle of the region
    foreach (var block in fn.Blocks)
      if (!region.Contains(block) && block.Terminator is { } outside)
        foreach (var successor in outside.Successors)
          if (body.Contains(successor))
            return null;

    return new(header, body, latch, preheader, exit, inner, inner.Condition);
  }

  private static bool Unswitch(IrFunction fn, Loop loop) {
    var region = new List<IrBasicBlock> { loop.Header };
    region.AddRange(loop.Body);

    // two complete copies, each with the condition pre-bound to its outcome. Seeding the clone map
    // with the condition is what specializes the copy: every use inside it becomes the constant, and
    // the passes that follow fold the branch and delete the arm that cannot run.
    var whenTrue = IrCloner.Clone(fn, region, Seed(loop.Condition, 1), "uns.t.", out var trueValues);
    var whenFalse = IrCloner.Clone(fn, region, Seed(loop.Condition, 0), "uns.f.", out var falseValues);

    // the preheader now chooses which loop to enter, once
    var chooser = loop.Preheader;
    if (chooser.Terminator is { } existing)
      chooser.Remove(existing);
    chooser.Append(new IrCondBr(loop.Condition, whenTrue[loop.Header], whenFalse[loop.Header]));

    // Every value the loop computes and something after it reads now has TWO definitions, one per
    // clone, so each needs a phi in the exit to choose between them.
    //
    // This is the LCSSA step, and it is not optional here: the IR does not keep loop-closed SSA, so a
    // value defined in the loop is used directly after it rather than through a phi at the boundary.
    // Remapping the exit's existing phis was not enough - `PRINT t` after the loop reads the header's
    // counter itself, and once the header is removed that operand dominates nothing, which is exactly
    // what the verifier reported.
    var escaping = region
      .SelectMany(b => b.Instructions)
      .Where(v => v.Users.Any(u => u.Parent is { } where && !region.Contains(where)))
      .ToList();

    foreach (var value in escaping) {
      var joined = loop.Exit.AppendPhi(new IrPhi(value.Type) { Name = value.Name });
      joined.AddIncoming(trueValues.GetValueOrDefault(value, value), whenTrue[loop.Header]);
      joined.AddIncoming(falseValues.GetValueOrDefault(value, value), whenFalse[loop.Header]);

      foreach (var user in value.Users.ToList())
        if (user.Parent is { } where && !region.Contains(where) && !ReferenceEquals(user, joined))
          user.ReplaceOperand(value, joined);
    }

    foreach (var block in region)
      fn.RemoveBlock(block);
    return true;
  }

  private static Dictionary<IrValue, IrValue> Seed(IrValue condition, long value) {
    var seed = new Dictionary<IrValue, IrValue>(ReferenceEqualityComparer.Instance) {
      [condition] = new IrConstantInt(condition.Type, value),
    };
    return seed;
  }
}
