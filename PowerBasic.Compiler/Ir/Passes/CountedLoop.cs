namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// A loop that runs a known number of times: the blocks it occupies, the counter it turns, and the
/// count itself.
///
/// <para>
/// Two passes need exactly this shape and must agree about it - <see cref="RecurrenceClosedForm"/>,
/// which replaces what the loop computed, and <see cref="DeadLoopElimination"/>, which deletes the
/// loop once nothing reads what it computed. Had they each carried their own matcher, the second
/// could have deleted a loop the first had not finished with, so the agreement is not tidiness: it
/// is the reason deleting is safe.
/// </para>
/// </summary>
internal sealed record CountedLoop(
  IrBasicBlock Header,
  IrBasicBlock Preheader,
  IrBasicBlock Latch,
  IrBasicBlock Exit,
  HashSet<IrBasicBlock> Region,
  IrCmp Test,
  IrPhi Counter,
  long Trips) {

  /// <summary>How many iterations to simulate before giving up on finding the trip count.</summary>
  private const int _MAX_SIMULATED = 1 << 20;

  /// <summary>Recognizes the loop headed by <paramref name="header"/>, or null when it is not one.</summary>
  public static CountedLoop? Match(IrFunction fn, IrBasicBlock header) {
    if (header.Terminator is not IrCondBr { Condition: IrCmp test } branch)
      return null;

    var predecessors = fn.Blocks.Where(b => b.Terminator is { } t && t.Successors.Contains(header)).ToList();
    if (predecessors.Count != 2)
      return null;

    var exit = branch.IfFalse;
    var region = CollectRegion(header, branch.IfTrue, exit, out var latch);
    if (region is null || latch is null)
      return null;
    var preheader = predecessors.SingleOrDefault(b => !ReferenceEquals(b, latch));
    if (preheader is null)
      return null;

    if (TripCount(header, test, preheader, latch) is not { } trips || trips == 0)
      return null;

    return new(header, preheader, latch, exit, region, test, (IrPhi)test.Lhs, trips);
  }

  /// <summary>
  /// The blocks the loop body occupies, or null when the shape is not one this can reason about.
  /// Collected by traversal, so both arms of an inner branch are inside rather than only the one a
  /// single walk would follow.
  /// </summary>
  private static HashSet<IrBasicBlock>? CollectRegion(IrBasicBlock header, IrBasicBlock entry, IrBasicBlock exit, out IrBasicBlock? latch) {
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
            return null;                           // more than one back edge
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

    var value = init.Value;
    for (long trips = 0; trips <= _MAX_SIMULATED; ++trips) {
      if (!Holds(test.Pred, value, limit.Value))
        return trips;
      var advanced = Truncate(counter.Type, unchecked(value + step.Value));
      if (advanced == value)
        return null;                               // standing still: not a counted loop
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
  public static long Truncate(IrType type, long value) => type.Bits switch {
    8 => (sbyte)value,
    16 => (short)value,
    32 => (int)value,
    _ => value,
  };
}
