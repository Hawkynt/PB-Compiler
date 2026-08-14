namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Deletes a counted loop that computes nothing anyone reads.
///
/// <para>
/// It is the other half of <see cref="RecurrenceClosedForm"/>. Closing an accumulator answers what
/// the loop produced without running it, but the loop is still there afterwards, turning its counter
/// four hundred times to arrive at a number already written into the exit block. Once nothing outside
/// reads anything the loop defines, and the body writes to nothing and calls no one, the iterations
/// are unobservable and the whole region can go.
/// </para>
/// <para>
/// Three things must hold together, and dropping any one of them makes this unsound rather than
/// merely less effective:
/// </para>
/// <list type="bullet">
///   <item>the trip count must be a known finite number, because deleting a loop that never ends
///   replaces a program that hangs with one that does not, and that is a change in behaviour even
///   though nobody wanted the hang;</item>
///   <item>the body must contain no store, call or inline assembly - those are the effects the
///   printed output is made of;</item>
///   <item>nothing defined inside, the counter and the phis included, may be read outside. The
///   counter's exit value is <c>limit + step</c> and could be computed, but computing it is
///   <see cref="RecurrenceClosedForm"/>'s job, and this pass declining until that has happened is
///   what keeps the two from having to agree about arithmetic as well as about shape.</item>
/// </list>
/// <para>
/// A fourth belongs with them and is about the rewire rather than the deletion: the preheader keeps
/// every edge that did not go into the header. It is not always an unconditional branch - a loop
/// <see cref="LoopUnswitch"/> has cloned is entered through a <c>condbr</c> choosing between the two
/// copies - and rewiring by replacing that terminator deletes the OTHER copy along with this one.
/// </para>
/// <para>
/// <b>Gated on <c>$OPTIMIZE SPEED</c>.</b> A DOS-era delay loop is precisely this shape written on
/// purpose: <c>FOR i = 1 TO 30000 / NEXT</c> has no effect the IR can see and every effect the author
/// wanted. Deleting it preserves every printed byte and destroys the program. PB spells the intent
/// <c>SLEEP</c> and <c>DELAY</c>, so under SPEED the busy-wait is taken to be an accident; under SIZE
/// it is left alone.
/// </para>
/// </summary>
public static class DeadLoopElimination {

  /// <summary>Deletes what it can in <paramref name="fn"/>; returns how many loops went.</summary>
  public static int Run(IrFunction fn) {
    if (fn.HasErrorHandler || fn.HasInlineAsm)
      return 0;                                    // control can arrive where the CFG does not say

    var deleted = 0;
    foreach (var header in fn.Blocks.ToList())
      if (header.Parent is not null && TryDelete(fn, header))
        ++deleted;
    return deleted;
  }

  private static bool TryDelete(IrFunction fn, IrBasicBlock header) {
    if (CountedLoop.Match(fn, header) is not { } loop)
      return false;
    var (_, preheader, _, exit, region, _, _, _) = loop;

    // The preheader is rewired to reach the exit directly, and only the edges that went into the
    // header may move: it can perfectly well end in a CONDITIONAL branch, which is exactly what
    // LoopUnswitch leaves behind, and a rewire that replaced the terminator outright would drop the
    // other clone on the floor. It cost a silent miscompile to learn - the same one LoopUnroll cost -
    // and here it was worse than a wrong answer: an $ERROR OVERFLOW loop unswitches into a trapping
    // copy and an empty one, this pass deleted the empty copy AND the branch that chose between them,
    // and the program that had to raise Error 6 ran to completion.
    var entryBranch = preheader.Terminator;
    if (entryBranch is not (IrBr or IrCondBr))
      return false;                                // a switch preheader: not a shape this rewires
    // and the exit must not already be reachable from the preheader, in one step or none: after the
    // rewire that block would enter the exit twice, and a phi there has room for one value per
    // predecessor - which of the two it should carry is a question this pass cannot answer
    if (ReferenceEquals(preheader, exit) || entryBranch.Successors.Any(s => ReferenceEquals(s, exit)))
      return false;

    // nothing may jump into the middle of the region: the loop is deleted as a unit, and a block
    // outside it that branches to a block inside would be left pointing at nothing
    foreach (var block in fn.Blocks)
      if (!region.Contains(block) && block.Terminator is { } terminator)
        foreach (var successor in terminator.Successors)
          if (region.Contains(successor) && !ReferenceEquals(successor, header))
            return false;

    foreach (var block in region)
      foreach (var instruction in block.Instructions) {
        if (HasEffect(instruction))
          return false;
        foreach (var user in instruction.Users)
          if (user.Parent is { } where && !region.Contains(where))
            return false;                          // someone outside still reads this
      }

    // the exit used to be reached from the header's false edge; now it is reached from the preheader
    Retarget(preheader, header, exit);
    foreach (var phi in exit.Instructions.OfType<IrPhi>())
      phi.RenameIncomingBlock(header, preheader);
    foreach (var block in region)
      fn.RemoveBlock(block);
    return true;
  }

  /// <summary>
  /// Whether an instruction does something the program could notice. A terminator is not an effect -
  /// the branching is what is being deleted - but everything <see cref="Dce"/> refuses to remove is.
  /// </summary>
  private static bool HasEffect(IrInstruction instruction)
    => instruction is IrStore or IrCall or IrInlineAsm;

  /// <summary>
  /// Sends every edge <paramref name="block"/> has into <paramref name="header"/> to
  /// <paramref name="target"/> instead, and leaves the edges that went anywhere else exactly where
  /// they were - the branch is EDITED, never replaced.
  /// </summary>
  private static void Retarget(IrBasicBlock block, IrBasicBlock header, IrBasicBlock target) {
    switch (block.Terminator) {
      case IrBr br:
        br.Target = target;
        break;
      case IrCondBr conditional:
        if (ReferenceEquals(conditional.IfTrue, header))
          conditional.IfTrue = target;
        if (ReferenceEquals(conditional.IfFalse, header))
          conditional.IfFalse = target;
        break;
    }
  }
}
