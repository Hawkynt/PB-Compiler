namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0134 — closed forms for loop-carried recurrences. An accumulator whose only work is adding a
/// constant each time round is <c>start + step * trips</c>, and the loop does not have to run to find
/// that out.
///
/// <para>
/// This is not unrolling with extra steps. <see cref="LoopUnroll"/> replaces a loop with copies of its
/// body and is capped at a handful of iterations, because the copies are the cost; a closed form
/// replaces the loop with ONE multiply and does not care whether the trip count is four or forty
/// thousand. The two therefore cover different loops, and this one runs after unrolling has declined.
/// </para>
/// <para>
/// It is restricted to INTEGER accumulators, and that restriction is the whole soundness argument.
/// Two's-complement addition is associative across wrapping, so accumulating <c>n</c> times and
/// multiplying by <c>n</c> reach the same value even when the intermediate steps overflow. Floating
/// point is not: each addition rounds, and a sum of forty roundings is not one multiplication. A float
/// accumulator is left alone rather than made faster and wrong.
/// </para>
/// <para>
/// The accumulator must also be UNREAD inside the loop apart from its own increment. If the body looks
/// at the running total - prints it, branches on it - then the intermediate values are observable and
/// only the final one is being replaced here.
/// </para>
/// </summary>
public static class RecurrenceClosedForm {

  /// <summary>How many iterations to simulate before giving up on finding the trip count.</summary>
  private const int _MAX_SIMULATED = 1 << 20;

  /// <summary>Rewrites what it can in <paramref name="fn"/>; returns how many recurrences closed.</summary>
  public static int Run(IrFunction fn) {
    if (fn.HasErrorHandler || fn.HasInlineAsm)
      return 0;                                  // control can arrive where the CFG does not say

    var closed = 0;
    foreach (var header in fn.Blocks.ToList())
      if (header.Parent is not null)
        closed += CloseIn(fn, header);
    return closed;
  }

  private static int CloseIn(IrFunction fn, IrBasicBlock header) {
    if (header.Terminator is not IrCondBr { Condition: IrCmp test } branch)
      return 0;

    var predecessors = fn.Blocks.Where(b => b.Terminator is { } t && t.Successors.Contains(header)).ToList();
    if (predecessors.Count != 2)
      return 0;

    // the region is everything reachable from the true edge that is not the exit; the latch is the
    // block that branches back
    var exit = branch.IfFalse;
    var region = Region(header, branch.IfTrue, exit, out var latch);
    if (region is null || latch is null)
      return 0;
    var preheader = predecessors.SingleOrDefault(b => !ReferenceEquals(b, latch));
    if (preheader is null)
      return 0;

    if (TripCount(header, test, preheader, latch) is not { } trips || trips == 0)
      return 0;

    var closed = 0;
    foreach (var phi in header.Instructions.OfType<IrPhi>().ToList()) {
      if (phi.Type.Kind != IrTypeKind.Int)
        continue;                                // a float accumulator rounds every step; see the note above
      if (phi.IncomingFrom(preheader) is not { } start)
        continue;
      if (phi.IncomingFrom(latch) is not IrBinary { Op: IrBinaryOp.Add } increment)
        continue;
      if (!ReferenceEquals(increment.Lhs, phi) || increment.Rhs is not IrConstantInt step)
        continue;

      // the accumulator's only use inside the loop is its own increment - otherwise the running
      // total is observable and the final value is not the only thing being replaced
      if (phi.Users.Any(u => u.Parent is { } where && region.Contains(where) && !ReferenceEquals(u, increment)))
        continue;
      // and the increment feeds nothing but the phi
      if (increment.Users.Count != 1)
        continue;

      var total = unchecked(step.Value * trips);
      var finalValue = start is IrConstantInt from
        ? (IrValue)new IrConstantInt(phi.Type, Truncate(phi.Type, unchecked(from.Value + total)))
        : exit.InsertAt(0, new IrBinary(IrBinaryOp.Add, start, new IrConstantInt(phi.Type, Truncate(phi.Type, total))));

      foreach (var user in phi.Users.ToList())
        if (user.Parent is { } where && !region.Contains(where) && !ReferenceEquals(user, finalValue))
          user.ReplaceOperand(phi, finalValue);
      ++closed;
    }
    return closed;
  }

  /// <summary>
  /// The blocks the loop body occupies, or null when the shape is not one this can reason about.
  /// Collected by traversal, so both arms of an inner branch are inside rather than only the one a
  /// single walk would follow.
  /// </summary>
  private static HashSet<IrBasicBlock>? Region(IrBasicBlock header, IrBasicBlock entry, IrBasicBlock exit, out IrBasicBlock? latch) {
    latch = null;
    var region = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance) { header };
    var queue = new Queue<IrBasicBlock>([entry]);
    while (queue.Count > 0) {
      var at = queue.Dequeue();
      if (ReferenceEquals(at, exit) || !region.Add(at))
        continue;
      if (at.Terminator is null)
        return null;
      foreach (var successor in at.Terminator.Successors)
        if (ReferenceEquals(successor, header)) {
          if (latch is not null && !ReferenceEquals(latch, at))
            return null;                         // more than one back edge
          latch = at;
        } else
          queue.Enqueue(successor);
    }
    return region.Contains(exit) ? null : region;
  }

  /// <summary>
  /// How many times the loop body runs, by simulating the counter the test looks at - or null when
  /// that is not a terminating, wrap-free number.
  ///
  /// Simulation rather than a formula because the predicate, the step's sign and the wrap behaviour
  /// all have to agree, and a formula that is right for three of the four is a formula that produces
  /// a plausible wrong count.
  /// </summary>
  private static long? TripCount(IrBasicBlock header, IrCmp test, IrBasicBlock preheader, IrBasicBlock latch) {
    if (test.Lhs is not IrPhi counter || !ReferenceEquals(counter.Parent, header))
      return null;
    if (test.Rhs is not IrConstantInt limit)
      return null;
    if (counter.IncomingFrom(preheader) is not IrConstantInt init)
      return null;
    if (counter.IncomingFrom(latch) is not IrBinary { Op: IrBinaryOp.Add } next
        || !ReferenceEquals(next.Lhs, counter) || next.Rhs is not IrConstantInt step || step.Value == 0)
      return null;

    var bits = counter.Type.Bits;
    var value = init.Value;
    for (long trips = 0; trips <= _MAX_SIMULATED; ++trips) {
      if (!Holds(test.Pred, value, limit.Value))
        return trips;
      var advanced = Truncate(counter.Type, unchecked(value + step.Value));
      if (advanced == value)
        return null;                             // standing still: not a counted loop
      value = advanced;
    }
    return null;
  }

  private static bool Holds(IrCmpPred pred, long l, long r) => pred switch {
    IrCmpPred.Slt => l < r,
    IrCmpPred.Sle => l <= r,
    IrCmpPred.Sgt => l > r,
    IrCmpPred.Sge => l >= r,
    IrCmpPred.Eq => l == r,
    IrCmpPred.Ne => l != r,
    _ => false,
  };

  /// <summary>Wraps a value to its type's width, the way the machine would have.</summary>
  private static long Truncate(IrType type, long value) => type.Bits switch {
    8 => (sbyte)value,
    16 => (short)value,
    32 => (int)value,
    _ => value,
  };
}
