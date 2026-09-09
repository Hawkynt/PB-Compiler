namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Loop unrolling in SSA IR.
///
/// <para>
/// O0007 fully unrolls the narrow counted-loop shape emitted for <c>FOR i = a TO b [STEP c]</c>
/// when its trip count is a small compile-time constant. O0063 handles the complementary unit-stride
/// case whose trip count is only known at run time: it computes <c>tripCount MOD 4</c>, dispatches into
/// one of four shared body copies, and thereafter tests the original loop condition once per group.
/// This is Duff's device expressed as target-neutral CFG rather than as an x86 jump table.
/// </para>
/// <para>
/// Both transforms are deliberately narrow. They accept a header holding only phis, one compare and
/// the conditional branch, plus a straight-line body/latch chain. Anything else declines, because an
/// unroller that is clever about which loops it recognises is an unroller that is eventually wrong
/// about one.
/// </para>
/// <para>
/// Full unrolling deletes the loop: each iteration's copy of the body is cloned with every header phi
/// mapped to its value at that iteration, so the counter becomes a constant inside each copy and the
/// arithmetic built from it folds. Runtime unrolling retains the original header as the once-per-four
/// group test and adds SSA merge phis at each Duff entry so loop-carried values stay correct whichever
/// remainder entry is selected.
/// </para>
/// <para>
/// It is checked the way every IR pass here is: by rendering the IR back to BASIC before and after
/// and running both programs (<c>IrPassObservableEquivalenceTests</c>). Unrolling changes the code by
/// definition, so no statement about the code itself would mean anything - only the output can.
/// </para>
/// </summary>
public static class LoopUnroll {

  /// <summary>The most iterations to fully unroll; beyond this the code growth stops paying.</summary>
  private const int _MAX_TRIPS = 16;

  /// <summary>The most instructions an unrolled loop may become, counting every copy.</summary>
  private const int _MAX_INSTRUCTIONS = 192;

  /// <summary>O0063's current runtime unroll factor.</summary>
  private const int _RUNTIME_FACTOR = 4;

  /// <summary>Unrolls what it can in <paramref name="fn"/>; returns how many loops it took.</summary>
  public static int Run(IrFunction fn) {
    if (fn.HasErrorHandler)
      return 0;                                  // a fault can enter this function anywhere - see IrFunction

    // O0272 consumes runtime trip distributions before the compile-time-only full unroller. The two
    // transforms share this early loop slot because both want the pristine SSA loop shape and both
    // expose straight-line copies for the simplification passes that follow.
    var unrolled = ProfileGuidedLoopOptimization.Run(fn);
    foreach (var header in fn.Blocks.ToList()) {
      if (Match(fn, header) is { } loop && TryUnroll(fn, loop)) {
        ++unrolled;
        continue;
      }
      if (MatchRuntime(fn, header) is { } runtime && TryDuffUnroll(fn, runtime))
        ++unrolled;
    }
    return unrolled;
  }

  /// <summary>A recognized constant-trip counted loop.</summary>
  private sealed record Loop(
    IrBasicBlock Header, IReadOnlyList<IrBasicBlock> Body, IrBasicBlock Latch,
    IrBasicBlock Preheader, IrBasicBlock Exit, int Trips);

  /// <summary>A recognized unit-stride loop whose initial trip count is not a compile-time constant.</summary>
  private sealed record RuntimeLoop(
    IrBasicBlock Header, IReadOnlyList<IrBasicBlock> Body, IrBasicBlock Latch,
    IrBasicBlock Preheader, IrBasicBlock Exit, IReadOnlyList<IrPhi> Phis,
    IrPhi Counter, IrCmp Test, IrValue Init, IrValue Limit, bool Ascending);

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
    // The preheader is REWIRED to fall into the first unrolled copy (Retarget below), which replaces
    // its terminator outright - so it has to be an unconditional branch, and a conditional one is not
    // a shape to work around but a loop to leave alone until the branch resolves. It cost a
    // miscompile to learn: LoopUnswitch clones a loop under a condition it could not fold, leaving a
    // header whose preheader ends in `condbr c, this-clone, that-clone`; unrolling it dropped the
    // condition and ran the clone specialized for the arm that was NOT taken. The next sweep folds
    // the condition and unrolls the survivor, so nothing is lost but the round trip.
    if (preheader.Terminator is not IrBr)
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
  /// Matches O0063's safe core: a variable-trip, unit-stride integer FOR loop. Unit stride is not an
  /// arbitrary first slice. With a larger modular step, PB can jump across the comparison boundary,
  /// wrap, and become true again; the mathematical quotient of <c>(limit-init)/step</c> then is not
  /// the machine loop's trip count. With +/-1 the boundary cannot be skipped, and fixed-width wrap is
  /// preserved because both the trip remainder and the counter itself are computed in their original
  /// integer width.
  /// </summary>
  private static RuntimeLoop? MatchRuntime(IrFunction fn, IrBasicBlock header) {
    if (header.Terminator is not IrCondBr branch || branch.Condition is not IrCmp test)
      return null;

    var phis = header.Instructions.OfType<IrPhi>().ToList();
    if (header.Instructions.Count != phis.Count + 2
        || !ReferenceEquals(header.Instructions[^1], branch)
        || !ReferenceEquals(header.Instructions[^2], test))
      return null;

    var predecessors = fn.Blocks.Where(b => b.Terminator is { } t && t.Successors.Contains(header)).ToList();
    if (predecessors.Count != 2)
      return null;

    var exit = branch.IfFalse;
    var bodyBlocks = new List<IrBasicBlock>();
    IrBasicBlock? latch = null;
    for (var at = branch.IfTrue; latch is null; ) {
      if (ReferenceEquals(at, header) || bodyBlocks.Contains(at))
        return null;
      bodyBlocks.Add(at);
      if (at.Phis.Any() || at.Terminator is not IrBr onward)
        return null;
      if (ReferenceEquals(onward.Target, header))
        latch = at;
      else
        at = onward.Target;
    }

    var preheader = predecessors.SingleOrDefault(b => !ReferenceEquals(b, latch));
    if (preheader is null || preheader.Terminator is not IrBr)
      return null;
    if (ReferenceEquals(exit, header) || bodyBlocks.Contains(exit))
      return null;

    foreach (var block in fn.Blocks)
      if (!bodyBlocks.Contains(block) && !ReferenceEquals(block, header) && block.Terminator is { } outside)
        foreach (var successor in outside.Successors)
          if (bodyBlocks.Contains(successor))
            return null;

    if (test.Lhs is not IrPhi counter || !phis.Contains(counter))
      return null;
    var limit = test.Rhs;
    if (limit is IrInstruction { Parent: { } limitBlock }
        && (ReferenceEquals(limitBlock, header) || bodyBlocks.Contains(limitBlock)))
      return null;                                  // the bound has to be loop invariant

    var init = counter.IncomingFrom(preheader);
    if (init is null)
      return null;
    if (counter.IncomingFrom(latch) is not IrBinary { Op: IrBinaryOp.Add } next
        || next.Rhs is not IrConstantInt step || !ReferenceEquals(next.Lhs, counter))
      return null;

    var ascending = step.Value == 1 && test.Pred is IrCmpPred.Sle or IrCmpPred.Ule;
    var descending = step.Value == -1 && test.Pred == IrCmpPred.Sge;
    if (!ascending && !descending)
      return null;

    // O0007 owns constant-trip loops. In particular this keeps a long constant loop from quietly
    // changing policy just because full unrolling declined its code-size budget.
    if (init is IrConstantInt && limit is IrConstantInt)
      return null;

    foreach (var phi in phis)
      if (phi.IncomingFrom(preheader) is null || phi.IncomingFrom(latch) is null)
        return null;

    foreach (var block in bodyBlocks)
      foreach (var instruction in block.Instructions)
        foreach (var user in instruction.Users)
          if (user.Parent is { } where && !bodyBlocks.Contains(where) && !ReferenceEquals(where, header))
            return null;

    var size = bodyBlocks.Sum(b => b.Instructions.Count);
    if (_RUNTIME_FACTOR * size > _MAX_INSTRUCTIONS)
      return null;

    return new(header, bodyBlocks, latch, preheader, exit, phis, counter, test, init, limit, ascending);
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

  /// <summary>
  /// O0063: turn one runtime-count loop into four shared copies with a modulo-4 entry switch.
  ///
  /// The original header stays in place as the group test. A new entry guard performs the original
  /// zero-trip check before the switch; each Duff lane has phis that merge the initial loop state with
  /// the state carried from the previous copy. After lane 1 the original header tests the already
  /// incremented counter: false exits, true enters lane 4 for another complete group.
  /// </summary>
  private static bool TryDuffUnroll(IrFunction fn, RuntimeLoop loop) {
    var entry = new Dictionary<IrPhi, IrValue>(ReferenceEqualityComparer.Instance);
    var latched = new Dictionary<IrPhi, IrValue>(ReferenceEqualityComparer.Instance);
    foreach (var phi in loop.Phis) {
      if (phi.IncomingFrom(loop.Preheader) is not { } first || phi.IncomingFrom(loop.Latch) is not { } next)
        return false;
      entry[phi] = first;
      latched[phi] = next;
    }

    // Record the uses that are genuinely outside the old loop BEFORE adding the new merge/state
    // blocks. They need the zero-trip value on the guard->exit edge and the final header value on the
    // ordinary exit edge; using the old header phi directly would no longer dominate both paths.
    var oldLoopBlocks = new HashSet<IrBasicBlock>(loop.Body, ReferenceEqualityComparer.Instance) { loop.Header };
    var outsideUses = new Dictionary<IrPhi, List<(IrInstruction User, int Operand)>>(ReferenceEqualityComparer.Instance);
    foreach (var phi in loop.Phis) {
      var uses = new List<(IrInstruction, int)>();
      foreach (var block in fn.Blocks)
        if (!oldLoopBlocks.Contains(block))
          foreach (var instruction in block.Instructions)
            for (var i = 0; i < instruction.Operands.Count; ++i)
              if (ReferenceEquals(instruction.GetOperand(i), phi))
                uses.Add((instruction, i));
      outsideUses[phi] = uses;
    }

    var guard = fn.CreateBlock("duff.guard");
    var dispatch = fn.CreateBlock("duff.dispatch");
    var merge = fn.CreateBlock("duff.exit");
    var states = Enumerable.Range(1, _RUNTIME_FACTOR)
      .ToDictionary(lane => lane, lane => fn.CreateBlock($"duff.{lane}"));

    var exitState = new Dictionary<IrPhi, IrPhi>(ReferenceEqualityComparer.Instance);
    foreach (var phi in loop.Phis) {
      var merged = merge.AppendPhi(new IrPhi(phi.Type));
      merged.AddIncoming(entry[phi], guard);
      merged.AddIncoming(phi, loop.Header);
      exitState[phi] = merged;
    }
    foreach (var phi in loop.Phis)
      foreach (var (user, operand) in outsideUses[phi])
        user.SetOperand(operand, exitState[phi]);

    var statePhis = new Dictionary<int, Dictionary<IrPhi, IrPhi>>();
    foreach (var lane in states.Keys) {
      var lanePhis = new Dictionary<IrPhi, IrPhi>(ReferenceEqualityComparer.Instance);
      foreach (var phi in loop.Phis)
        lanePhis[phi] = states[lane].AppendPhi(new IrPhi(phi.Type));
      statePhis[lane] = lanePhis;
    }

    var copyLatches = new Dictionary<int, IrBasicBlock>();
    var carried = new Dictionary<int, Dictionary<IrPhi, IrValue>>();
    for (var lane = _RUNTIME_FACTOR; lane >= 1; --lane) {
      var seed = new Dictionary<IrValue, IrValue>(ReferenceEqualityComparer.Instance);
      foreach (var phi in loop.Phis)
        seed[phi] = statePhis[lane][phi];

      var clones = IrCloner.Clone(fn, loop.Body, seed, $"duff{lane}.", out var values);
      Retarget(states[lane], clones[loop.Body[0]]);
      var copyLatch = clones[loop.Latch];
      copyLatches[lane] = copyLatch;

      var nextState = new Dictionary<IrPhi, IrValue>(ReferenceEqualityComparer.Instance);
      foreach (var phi in loop.Phis)
        nextState[phi] = values.GetValueOrDefault(latched[phi], latched[phi]);
      carried[lane] = nextState;

      Retarget(copyLatch, lane > 1 ? states[lane - 1] : loop.Header);
    }

    // Each shared Duff entry can be reached either directly from the remainder switch or from the
    // previous body copy. Lane 4's second predecessor is the original header on complete groups.
    foreach (var phi in loop.Phis) {
      statePhis[4][phi].AddIncoming(entry[phi], dispatch);
      statePhis[4][phi].AddIncoming(phi, loop.Header);
      for (var lane = 3; lane >= 1; --lane) {
        statePhis[lane][phi].AddIncoming(entry[phi], dispatch);
        statePhis[lane][phi].AddIncoming(carried[lane + 1][phi], copyLatches[lane + 1]);
      }
    }

    // The original header is now reached only after lane 1. Its phis therefore carry the state after
    // the last copy, while the separate guard owns the initial zero-trip test.
    foreach (var phi in loop.Phis) {
      phi.RemoveIncoming(loop.Preheader);
      phi.RemoveIncoming(loop.Latch);
      phi.AddIncoming(carried[1][phi], copyLatches[1]);
    }

    var guardTest = guard.Append(new IrCmp(loop.Test.Pred, entry[loop.Counter], loop.Limit) {
      IsSourceCondition = loop.Test.IsSourceCondition,
    });
    guard.Append(new IrCondBr(guardTest, dispatch, merge));

    // For +/-1 strides the exact finite trip count before the boundary is distance+1. Computing it
    // in the counter's own width is intentional: wrap is modulo 2^bits, and 2^bits is divisible by
    // four, so the low two bits are the mathematical trip count modulo four even at the signed edge.
    var distance = dispatch.Append(new IrBinary(IrBinaryOp.Sub,
      loop.Ascending ? loop.Limit : loop.Init,
      loop.Ascending ? loop.Init : loop.Limit));
    var count = dispatch.Append(new IrBinary(IrBinaryOp.Add, distance, new IrConstantInt(loop.Counter.Type, 1)));
    var remainder = dispatch.Append(new IrBinary(IrBinaryOp.And, count, new IrConstantInt(loop.Counter.Type, 3)));
    var selectEntry = dispatch.Append(new IrSwitch(remainder, states[4]));
    selectEntry.AddCase(1, states[1]);
    selectEntry.AddCase(2, states[2]);
    selectEntry.AddCase(3, states[3]);

    Retarget(loop.Preheader, guard);
    if (loop.Header.Terminator is not IrCondBr groupTest)
      return false;
    groupTest.IfTrue = states[4];
    groupTest.IfFalse = merge;
    merge.Append(new IrBr(loop.Exit));

    // The original exit used to be reached directly from the header; it is now reached through the
    // merge block that also carries the zero-trip path.
    foreach (var phi in loop.Exit.Phis)
      phi.RenameIncomingBlock(loop.Header, merge);

    foreach (var block in loop.Body)
      fn.RemoveBlock(block);
    return true;
  }
}
