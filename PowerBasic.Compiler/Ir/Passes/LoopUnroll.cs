namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Loop unrolling in SSA IR.
///
/// <para>
/// O0007 fully unrolls the narrow counted-loop shape emitted for <c>FOR i = a TO b [STEP c]</c>
/// when its trip count is a small compile-time constant. O0063 handles the complementary unit-stride
/// case whose trip count is only known at run time: it keeps the original loop as a prologue bounded
/// by <c>trips MOD 4</c> and follows it with a loop whose body is four copies, so the loop condition
/// is tested once per four iterations.
/// </para>
/// <para>
/// Both transforms are deliberately narrow. They accept a header holding only phis, one compare and
/// the conditional branch, plus a straight-line body/latch chain. Anything else declines, because an
/// unroller that is clever about which loops it recognises is an unroller that is eventually wrong
/// about one.
/// </para>
/// <para>
/// Full unrolling deletes the loop: each iteration's copy of the body is cloned with every header phi
/// mapped to its value at that iteration. O0066 additionally seeds the induction phi with the exact
/// literal for that copy instead of carrying the previous copy's increment node, so arithmetic and
/// addressing derived from the counter are immediately exposed to constant folding. What flows out of
/// the loop is each phi's value after the last iteration, which is what uses after the loop are
/// rewritten to. Runtime unrolling instead keeps both cycles single-entry, which is what lets the
/// loop passes after it - LICM above all - still recognise them; a merge block publishes the state
/// the code after the loop reads, whether the loop ran or not.
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
      if (MatchRuntime(fn, header) is { } runtime && TryRuntimeUnroll(fn, runtime))
        ++unrolled;
    }
    return unrolled;
  }

  /// <summary>A recognized constant-trip counted loop.</summary>
  private sealed record Loop(
    IrBasicBlock Header, IReadOnlyList<IrBasicBlock> Body, IrBasicBlock Latch,
    IrBasicBlock Preheader, IrBasicBlock Exit, IrPhi Counter,
    IrConstantInt CounterInit, IrConstantInt CounterStep, int Trips);

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

    return new(header, bodyBlocks, latch, preheader, exit, counter, init, step, count);
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
    // The prologue keeps the header but replaces its bound, so the original comparison stops being
    // the loop's exit condition. Anything else reading it - a body compare GVN merged into it on an
    // earlier sweep, say - would read a value that no longer means what it did.
    if (test.Users.Count != 1 || !ReferenceEquals(test.Users[0], branch))
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
    // changing policy just because full unrolling declined its code-size budget. The test has to see
    // through the width casts the lowering wraps literals in - a LONG bound arrives as
    // `sext i16 100 to i32`, which is every bit as much a compile-time constant as the literal.
    if (IsCompileTimeConstant(init) && IsCompileTimeConstant(limit))
      return null;

    foreach (var phi in phis)
      if (phi.IncomingFrom(preheader) is null || phi.IncomingFrom(latch) is null)
        return null;

    foreach (var block in bodyBlocks)
      foreach (var instruction in block.Instructions)
        foreach (var user in instruction.Users)
          if (user.Parent is { } where && !bodyBlocks.Contains(where) && !ReferenceEquals(where, header))
            return null;

    // the loop keeps its own body as the prologue and gains _RUNTIME_FACTOR copies on top
    var size = bodyBlocks.Sum(b => b.Instructions.Count);
    if ((_RUNTIME_FACTOR + 1) * size > _MAX_INSTRUCTIONS)
      return null;

    return new(header, bodyBlocks, latch, preheader, exit, phis, counter, test, init, limit, ascending);
  }

  /// <summary>Whether the value is a literal, including one the lowering wrapped in width casts.</summary>
  private static bool IsCompileTimeConstant(IrValue value) => value switch {
    IrConstant => true,
    IrCast cast => IsCompileTimeConstant(cast.Value),
    _ => false,
  };

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

    // O0066: the induction value is not merely derivable for every copy, it is already KNOWN here.
    // Keep it as a literal instead of threading the previous copy's cloned `counter + step` through
    // the next seed. That makes every counter-derived expression local constant-folding material and
    // also makes the value after the fully-unrolled loop a literal.
    var counterValue = loop.CounterInit.Value;

    IrBasicBlock? first = null, previousLatch = null;
    for (var trip = 0; trip < loop.Trips; ++trip) {
      current[loop.Counter] = new IrConstantInt(loop.Counter.Type, counterValue);

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

      counterValue += loop.CounterStep.Value;
      current[loop.Counter] = new IrConstantInt(loop.Counter.Type, counterValue);
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
  /// O0063: unroll a variable-trip loop by four.
  ///
  /// <para>
  /// The shape is the conventional one for a count that is only known at run time. A zero-trip guard
  /// runs the original entry test; the ORIGINAL loop is then kept as a prologue bounded by
  /// <c>trips MOD 4</c>, and a second loop follows whose body is four copies of the original, so its
  /// test runs once per four iterations. Nothing here is shared between the two, which is the point:
  /// both loops keep one header and one entry, and that is the only shape the dominator-based loop
  /// machinery downstream - LICM, GVN, the counted-loop recognisers - is able to reason about.
  /// Duff's own computed entry would instead give one cycle four entry points; the IR stays valid,
  /// but every loop pass after this one declines it and loop-invariant work ends up recomputed in
  /// each copy.
  /// </para>
  /// </summary>
  private static bool TryRuntimeUnroll(IrFunction fn, RuntimeLoop loop) {
    if (loop.Header.Terminator is not IrCondBr headerBranch)
      return false;

    var entry = new Dictionary<IrPhi, IrValue>(ReferenceEqualityComparer.Instance);
    var latched = new Dictionary<IrPhi, IrValue>(ReferenceEqualityComparer.Instance);
    foreach (var phi in loop.Phis) {
      if (phi.IncomingFrom(loop.Preheader) is not { } first || phi.IncomingFrom(loop.Latch) is not { } next)
        return false;
      entry[phi] = first;
      latched[phi] = next;
    }

    // Record the uses that are genuinely outside the loop BEFORE the new blocks exist. They have to
    // read the merge block: the initial state on the zero-trip edge, the state after the last group
    // otherwise. The old header phi would dominate neither path once the guard is in front of it.
    var inside = new HashSet<IrBasicBlock>(loop.Body, ReferenceEqualityComparer.Instance) { loop.Header };
    var outsideUses = new Dictionary<IrPhi, List<(IrInstruction User, int Operand)>>(ReferenceEqualityComparer.Instance);
    foreach (var phi in loop.Phis) {
      var uses = new List<(IrInstruction, int)>();
      foreach (var block in fn.Blocks)
        if (!inside.Contains(block))
          foreach (var instruction in block.Instructions)
            for (var i = 0; i < instruction.Operands.Count; ++i)
              if (ReferenceEquals(instruction.GetOperand(i), phi))
                uses.Add((instruction, i));
      outsideUses[phi] = uses;
    }

    var pred = loop.Test.Pred;
    var isSourceCondition = loop.Test.IsSourceCondition;
    var stem = UniqueStem(fn);
    var guard = fn.CreateBlock($"{stem}.guard");
    var setup = fn.CreateBlock($"{stem}.setup");
    var main = fn.CreateBlock($"{stem}.main");
    var merge = fn.CreateBlock($"{stem}.exit");

    // the guard is the original entry test, so a loop that never ran still never runs
    var guardTest = guard.Append(new IrCmp(pred, loop.Init, loop.Limit) { IsSourceCondition = isSourceCondition });
    guard.Append(new IrCondBr(guardTest, setup, merge));

    // For a unit stride the exact finite trip count is distance + 1, and computing it in the
    // counter's own width is intentional rather than sloppy: wrap is modulo 2^bits and 2^bits is
    // divisible by four, so the low two bits are the mathematical trip count modulo four even at the
    // signed edge. The prologue therefore stops at init + step * (trips MOD 4), which an equality
    // test reaches exactly - a relational one could be fooled by the same wrap.
    var distance = setup.Append(new IrBinary(IrBinaryOp.Sub,
      loop.Ascending ? loop.Limit : loop.Init,
      loop.Ascending ? loop.Init : loop.Limit));
    var trips = setup.Append(new IrBinary(IrBinaryOp.Add, distance, new IrConstantInt(loop.Counter.Type, 1)));
    var remainder = setup.Append(new IrBinary(IrBinaryOp.And, trips, new IrConstantInt(loop.Counter.Type, 3)));
    var prologueEnd = setup.Append(new IrBinary(
      loop.Ascending ? IrBinaryOp.Add : IrBinaryOp.Sub, loop.Init, remainder));
    setup.Append(new IrBr(loop.Header));

    // the original loop, kept whole, is the prologue - only its bound and its exit change
    Retarget(loop.Preheader, guard);
    foreach (var phi in loop.Phis)
      phi.RenameIncomingBlock(loop.Preheader, setup);
    headerBranch.EraseFromParent();
    loop.Test.EraseFromParent();                   // the match proved the branch was its only user
    var prologueTest = loop.Header.Append(new IrCmp(IrCmpPred.Ne, loop.Counter, prologueEnd));
    loop.Header.Append(new IrCondBr(prologueTest, loop.Body[0], main));

    // the main loop: what is left is a multiple of four, so the original test admits a whole group
    var mainPhis = new Dictionary<IrPhi, IrPhi>(ReferenceEqualityComparer.Instance);
    foreach (var phi in loop.Phis) {
      var carried = main.AppendPhi(new IrPhi(phi.Type));
      carried.AddIncoming(phi, loop.Header);
      mainPhis[phi] = carried;
    }
    var mainTest = main.Append(new IrCmp(pred, mainPhis[loop.Counter], loop.Limit) {
      IsSourceCondition = isSourceCondition,
    });

    var current = new Dictionary<IrPhi, IrValue>(ReferenceEqualityComparer.Instance);
    foreach (var phi in loop.Phis)
      current[phi] = mainPhis[phi];

    IrBasicBlock? firstCopy = null, previousLatch = null;
    for (var copy = 0; copy < _RUNTIME_FACTOR; ++copy) {
      var seed = new Dictionary<IrValue, IrValue>(ReferenceEqualityComparer.Instance);
      foreach (var phi in loop.Phis)
        seed[phi] = current[phi];

      var clones = IrCloner.Clone(fn, loop.Body, seed, $"{stem}.c{copy}.", out var values);
      firstCopy ??= clones[loop.Body[0]];
      if (previousLatch is not null)
        Retarget(previousLatch, clones[loop.Body[0]]);
      previousLatch = clones[loop.Latch];

      // what this copy hands the next one is its clone of the value the latch edge named
      foreach (var phi in loop.Phis)
        current[phi] = values.GetValueOrDefault(latched[phi], latched[phi]);
    }
    if (firstCopy is null || previousLatch is null)
      return false;
    Retarget(previousLatch, main);
    foreach (var phi in loop.Phis)
      mainPhis[phi].AddIncoming(current[phi], previousLatch);
    main.Append(new IrCondBr(mainTest, firstCopy, merge));

    // the exit now sees one block, whichever way the loop finished
    var exitState = new Dictionary<IrPhi, IrPhi>(ReferenceEqualityComparer.Instance);
    foreach (var phi in loop.Phis) {
      var merged = merge.AppendPhi(new IrPhi(phi.Type));
      merged.AddIncoming(entry[phi], guard);
      merged.AddIncoming(mainPhis[phi], main);
      exitState[phi] = merged;
    }
    merge.Append(new IrBr(loop.Exit));
    foreach (var phi in loop.Phis)
      foreach (var (user, operand) in outsideUses[phi])
        user.SetOperand(operand, exitState[phi]);
    foreach (var phi in loop.Exit.Phis.ToList())
      phi.RenameIncomingBlock(loop.Header, merge);

    return true;
  }

  /// <summary>
  /// A label stem no block in <paramref name="fn"/> already uses, so a second runtime unroll in the
  /// same function does not mint a duplicate label. Block labels reach the assembler, and the machine
  /// passes index blocks by label, so a collision is not cosmetic.
  /// </summary>
  private static string UniqueStem(IrFunction fn) {
    for (var suffix = 1; ; ++suffix) {
      var stem = suffix == 1 ? "unroll4" : $"unroll4.{suffix}";
      if (fn.Blocks.All(block => !block.Label.StartsWith(stem + ".", StringComparison.Ordinal)))
        return stem;
    }
  }
}
