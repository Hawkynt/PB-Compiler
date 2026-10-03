using PowerBasic.Compiler.Ir.Analysis;

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
  /// Recognizes the same historical counted-loop contract while sourcing recurrence and trip-count facts from
  /// shared loop/scalar-evolution analyses. This overload deliberately does not broaden the accepted shape.
  /// </summary>
  public static CountedLoop? Match(
      IrFunction fn, IrBasicBlock header, IrLoopAnalysis loops, IrScalarEvolution scalarEvolution) {
    ArgumentNullException.ThrowIfNull(fn);
    ArgumentNullException.ThrowIfNull(header);
    ArgumentNullException.ThrowIfNull(loops);
    ArgumentNullException.ThrowIfNull(scalarEvolution);

    if (header.Terminator is not IrCondBr { Condition: IrCmp test } branch
        || !SupportsCountedPredicate(test.Pred))
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

    var naturalLoop = loops.Loops.FirstOrDefault(loop => ReferenceEquals(loop.Header, header));
    if (naturalLoop is null
        || naturalLoop.Latches.Count != 1
        || !ReferenceEquals(naturalLoop.Latches[0], latch)
        || !ReferenceEquals(naturalLoop.UniqueEnteringBlock, preheader)
        || test.Lhs is not IrPhi counter
        || !ReferenceEquals(counter.Parent, header)
        || test.Rhs is not IrConstantInt
        || counter.IncomingFrom(preheader) is not IrConstantInt
        || scalarEvolution.RecurrenceFor(counter) is not { } recurrence
        || !ReferenceEquals(recurrence.Loop, naturalLoop)
        || !ReferenceEquals(recurrence.Update.Lhs, counter)
        || recurrence.Update.Rhs is not IrConstantInt step
        || step.Value == 0
        || scalarEvolution.ExactTripCount(naturalLoop) is not { } trips
        || trips == 0)
      return null;

    return new(header, preheader, latch, exit, region, test, counter, trips);
  }

  /// <summary>
  /// Whether the counter is the only thing that ends the loop, so <see cref="Trips"/> is the number of
  /// times the body runs rather than an upper bound on it. An <c>EXIT FOR</c>, a <c>GOTO</c> out, an
  /// <c>EXIT SUB</c> or a <c>RETURN</c> inside the body all leave without the counter's say-so.
  ///
  /// <para>
  /// A consumer that answers for the iterations - <see cref="RecurrenceClosedForm"/>, which writes
  /// the full-count total into the exit, and <see cref="DeadLoopElimination"/>, which deletes the loop
  /// outright - must ask. One that only needs the bound, or that recognizes an early exit as the
  /// point of the loop (a search), must not. <see cref="Region"/> cannot answer it: its forward walk
  /// follows an early exit out of the loop and keeps going, so the escape is never seen as one. The
  /// test therefore takes the NATURAL loop of the back edge - the blocks that reach the latch without
  /// passing through the header - and requires every edge out of it to be the header's own exit.
  /// </para>
  /// </summary>
  public bool RunsItsFullCount(IrFunction fn) {
    var predecessorsOf = new Dictionary<IrBasicBlock, List<IrBasicBlock>>(ReferenceEqualityComparer.Instance);
    foreach (var block in fn.Blocks)
      if (block.Terminator is { } terminator)
        foreach (var successor in terminator.Successors)
          (predecessorsOf.TryGetValue(successor, out var list) ? list : predecessorsOf[successor] = []).Add(block);

    var body = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance) { this.Header, this.Latch };
    var pending = new Stack<IrBasicBlock>([this.Latch]);
    while (pending.Count > 0)
      foreach (var predecessor in predecessorsOf.GetValueOrDefault(pending.Pop(), []))
        if (body.Add(predecessor))
          pending.Push(predecessor);

    foreach (var block in body) {
      if (block.Terminator is not { } terminator || !terminator.Successors.Any())
        return false;                              // a RET inside the body: the loop ends without the counter
      foreach (var successor in terminator.Successors)
        if (!body.Contains(successor) && !(ReferenceEquals(block, this.Header) && ReferenceEquals(successor, this.Exit)))
          return false;
    }
    return true;
  }

  /// <summary>
  /// The counter's first value and its step when both are constants of the counter's type and the
  /// latch advances it by an add - the arithmetic progression the induction-variable passes rewrite.
  /// </summary>
  public bool TryConstantProgression(out IrConstantInt start, out IrConstantInt step) {
    start = null!;
    step = null!;
    if (!this.Counter.Type.IsInteger
        || this.Counter.IncomingFrom(this.Preheader) is not IrConstantInt initial
        || this.Counter.IncomingFrom(this.Latch) is not IrBinary { Op: IrBinaryOp.Add } next
        || !ReferenceEquals(next.Lhs, this.Counter)
        || next.Rhs is not IrConstantInt increment
        || !Equals(initial.Type, this.Counter.Type)
        || !Equals(increment.Type, this.Counter.Type)
        || increment.IsZero)
      return false;

    start = initial;
    step = increment;
    return true;
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

  private static bool SupportsCountedPredicate(IrCmpPred predicate)
    => predicate is IrCmpPred.Slt or IrCmpPred.Sle or IrCmpPred.Sgt or IrCmpPred.Sge or IrCmpPred.Eq or IrCmpPred.Ne;

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
