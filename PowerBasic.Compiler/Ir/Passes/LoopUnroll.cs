namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Full unrolling of a counted loop whose trip count is known at compile time - the first of the
/// direct emitter's optimizations to be ported to the IR (docs/optimizations/O0007-loop-unrolling.md
/// is the machine-level original).
///
/// It is deliberately narrow. The shape it accepts is the one the lowering produces for
/// <c>FOR i = a TO b [STEP c]</c> over constant bounds and nothing else: a header holding only phis,
/// one compare and the conditional branch, and a single body block that is also the latch. Anything
/// else declines, because an unroller that is clever about which loops it recognises is an unroller
/// that is eventually wrong about one.
///
/// <para>
/// The transform deletes the loop rather than peeling it: each iteration's copy of the body is cloned
/// with every header phi mapped to its value at that iteration, so the counter becomes a constant
/// inside each copy and the arithmetic built from it folds. What flows out of the loop is each phi's
/// value after the last iteration, which is what uses after the loop are rewritten to.
/// </para>
/// <para>
/// It is checked the way every IR pass here is: by rendering the IR back to BASIC before and after
/// and running both programs (<c>IrPassObservableEquivalenceTests</c>). Unrolling changes the code by
/// definition, so no statement about the code itself would mean anything - only the output can.
/// </para>
/// </summary>
public static class LoopUnroll {

  /// <summary>The most iterations to unroll; beyond this the code growth stops paying.</summary>
  private const int _MAX_TRIPS = 16;

  /// <summary>The most instructions an unrolled loop may become, counting every copy.</summary>
  private const int _MAX_INSTRUCTIONS = 192;

  /// <summary>Unrolls what it can in <paramref name="fn"/>; returns how many loops it took.</summary>
  public static int Run(IrFunction fn) {
    if (fn.HasErrorHandler)
      return 0;                                  // a fault can enter this function anywhere - see IrFunction
    var unrolled = 0;
    foreach (var header in fn.Blocks.ToList())
      if (Match(fn, header) is { } loop && TryUnroll(fn, loop))
        ++unrolled;
    return unrolled;
  }

  /// <summary>A recognized counted loop: the blocks that make it and the counter's constant progression.</summary>
  private sealed record Loop(
    IrBasicBlock Header, IReadOnlyList<IrBasicBlock> Body, IrBasicBlock Latch,
    IrBasicBlock Preheader, IrBasicBlock Exit, int Trips);

  private static Loop? Match(IrFunction fn, IrBasicBlock header) {
    if (header.Terminator is not IrCondBr branch || branch.Condition is not IrCmp test)
      return null;

    // the header holds phis, the compare and the branch - nothing else, so there is no work in it to
    // be duplicated or lost
    var phis = header.Instructions.OfType<IrPhi>().ToList();
    if (header.Instructions.Count != phis.Count + 2
        || !ReferenceEquals(header.Instructions[^1], branch)
        || !ReferenceEquals(header.Instructions[^2], test))
      return null;

    var predecessors = fn.Blocks.Where(b => b.Terminator is { } t && t.Successors.Contains(header)).ToList();
    if (predecessors.Count != 2)
      return null;

    // The body is the straight-line chain from the true edge until something branches back to the
    // header - and that block is the LATCH. Picking the latch out of the header's predecessors
    // instead does not work: the PREHEADER also ends in an unconditional branch to the header, and
    // is indistinguishable from a latch by its terminator alone. Walking forward decides it.
    var exit = branch.IfFalse;
    var bodyBlocks = new List<IrBasicBlock>();
    IrBasicBlock? latch = null;
    for (var at = branch.IfTrue; latch is null; ) {
      if (ReferenceEquals(at, header) || bodyBlocks.Contains(at))
        return null;          // the chain leaves the loop or goes round twice
      bodyBlocks.Add(at);
      if (at.Terminator is not IrBr onward)
        return null;          // control flow of its own: this pass copies, it does not reason
      if (ReferenceEquals(onward.Target, header))
        latch = at;
      else
        at = onward.Target;
    }

    var preheader = predecessors.SingleOrDefault(b => !ReferenceEquals(b, latch));
    if (preheader is null || predecessors.Count != 2)
      return null;
    if (ReferenceEquals(exit, header) || bodyBlocks.Contains(exit))
      return null;
    // only the header may be entered from outside the chain
    foreach (var block in fn.Blocks)
      if (!bodyBlocks.Contains(block) && !ReferenceEquals(block, header) && block.Terminator is { } outside)
        foreach (var successor in outside.Successors)
          if (bodyBlocks.Contains(successor))
            return null;

    // the counter is the phi the test looks at, against a constant limit
    if (test.Lhs is not IrPhi counter || !phis.Contains(counter) || test.Rhs is not IrConstantInt limit)
      return null;
    if (counter.IncomingFrom(preheader) is not IrConstantInt init)
      return null;
    if (counter.IncomingFrom(latch) is not IrBinary { Op: IrBinaryOp.Add } next
        || next.Rhs is not IrConstantInt step || !ReferenceEquals(next.Lhs, counter) || step.Value == 0)
      return null;

    // nothing inside may be read outside except through the phis, whose value on exit this transform
    // knows; a body value read after the loop would need a phi this pass does not build
    foreach (var block in bodyBlocks)
      foreach (var instruction in block.Instructions)
        foreach (var user in instruction.Users)
          if (user.Parent is { } where && !bodyBlocks.Contains(where) && !ReferenceEquals(where, header))
            return null;

    var trips = TripCount(init.Value, step.Value, limit.Value, test.Pred);
    var size = bodyBlocks.Sum(b => b.Instructions.Count);
    if (trips is not { } count || count == 0 || (count + 1) * size > _MAX_INSTRUCTIONS)
      return null;

    return new(header, bodyBlocks, latch, preheader, exit, count);
  }

  /// <summary>
  /// How many times the loop runs, by simulating the counter - or null when that is not a small,
  /// terminating, wrap-free number. Overflow is what makes the guard necessary rather than tidy: a
  /// counter that wraps runs a completely different number of times.
  /// </summary>
  private static int? TripCount(long init, long step, long limit, IrCmpPred pred) {
    var counter = init;
    for (var trips = 0; trips <= _MAX_TRIPS; ++trips) {
      if (!Holds(pred, counter, limit))
        return trips;
      counter += step;
      if (counter is > short.MaxValue or < short.MinValue)
        return null;                             // it would wrap; the real trip count is not this
    }
    return null;                                 // more iterations than are worth unrolling
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

  /// <summary>Points a block's terminator at <paramref name="target"/>, replacing whatever it was.</summary>
  private static void Retarget(IrBasicBlock block, IrBasicBlock target) {
    if (block.Terminator is { } existing)
      block.Remove(existing);
    block.Append(new IrBr(target));
  }

  private static bool TryUnroll(IrFunction fn, Loop loop) {
    var phis = loop.Header.Instructions.OfType<IrPhi>().ToList();

    // each phi's value at the start of the iteration about to be cloned
    var current = new Dictionary<IrPhi, IrValue>(ReferenceEqualityComparer.Instance);
    foreach (var phi in phis) {
      if (phi.IncomingFrom(loop.Preheader) is not { } entry)
        return false;
      current[phi] = entry;
    }

    IrBasicBlock? first = null, previousLatch = null;
    for (var trip = 0; trip < loop.Trips; ++trip) {
      var seed = new Dictionary<IrValue, IrValue>(ReferenceEqualityComparer.Instance);
      foreach (var phi in phis)
        seed[phi] = current[phi];
      var clones = IrCloner.Clone(fn, loop.Body, seed, $"unroll{trip}.");

      // the copies run one after another: the previous iteration's latch falls into this one's entry
      first ??= clones[loop.Body[0]];
      if (previousLatch is not null)
        Retarget(previousLatch, clones[loop.Body[0]]);
      previousLatch = clones[loop.Latch];

      // what each phi carries into the NEXT iteration is what this copy computed for it, which is
      // the clone of the value the latch edge named
      var carried = new Dictionary<IrPhi, IrValue>(ReferenceEqualityComparer.Instance);
      foreach (var phi in phis) {
        var latched = phi.IncomingFrom(loop.Latch)!;
        carried[phi] = seed.GetValueOrDefault(latched, latched);
      }
      foreach (var phi in phis)
        current[phi] = carried[phi];
    }
    if (first is null || previousLatch is null)
      return false;
    Retarget(previousLatch, loop.Exit);

    // uses after the loop see the value each phi ended on
    foreach (var phi in phis)
      phi.ReplaceAllUsesWith(current[phi]);

    Retarget(loop.Preheader, first);
    foreach (var phi in loop.Exit.Instructions.OfType<IrPhi>())
      phi.RenameIncomingBlock(loop.Header, previousLatch);

    foreach (var block in loop.Body)
      fn.RemoveBlock(block);
    fn.RemoveBlock(loop.Header);
    return true;
  }
}
