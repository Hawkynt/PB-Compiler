using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Backend;

/// <summary>
/// Stage 4 of the x86-16 back end (docs/X86-BACKEND.md): linear-scan register allocation. It sweeps
/// the live intervals (stage 3) in start order, handing each virtual register a free physical
/// register from <c>AX BX CX DX SI DI</c> (BP/SP are the frame, so reserved) and freeing it again when
/// the interval ends. A target-gated dword interval aliases the corresponding 386 register
/// (<c>EAX</c> through <c>EDI</c>) in that same allocation slot. Two intervals that overlap in time
/// therefore get distinct registers, while
/// disjoint intervals reuse one - this is where independent values land in independent registers, the
/// reassignment the byte-level scheduler could never do. When a sweep cannot assign a value, the
/// allocator retries after rematerializing, directly spilling, or splitting one live range; it
/// returns null only when none of those transformations can make progress.
/// </summary>
public sealed partial class LinearScanAllocator {

  // Data values take AX/CX/DX first so the scarce addressing registers stay free; an address value (one
  // used as a memory base/index) must come from BX/SI/DI - in 16-bit mode AX/CX/DX/BP/SP cannot index
  // memory (BP is reserved for the frame). BP/SP are never allocated.
  private static readonly Reg[] _pool = [Reg.AX, Reg.CX, Reg.DX, Reg.BX, Reg.SI, Reg.DI];
  private static readonly Reg[] _bytePool = [Reg.AX, Reg.CX, Reg.DX, Reg.BX];
  /// <summary>Registers that may be a memory BASE on this target.</summary>
  private static readonly Reg[] _addressing = [Reg.BX, Reg.SI, Reg.DI];

  /// <summary>
  /// Registers that may be a memory INDEX, which is a strictly smaller set: 16-bit addressing allows
  /// <c>[BX+SI]</c> and <c>[BP+DI]</c> but has no encoding with BX in the index position. Treating the
  /// two roles as one set let a value that is only ever an index be given BX, which the assembler then
  /// refuses outright - it surfaced the moment rematerialization changed the pressure enough to make
  /// BX the first free choice.
  /// </summary>
  private static readonly Reg[] _indexing = [Reg.SI, Reg.DI];

  /// <summary>
  /// Registers that may be the BASE of an operand that also carries an index - the mirror of
  /// <see cref="_indexing"/>, and smaller still. 16-bit addressing has exactly four base+index
  /// encodings, <c>[BX+SI]</c>, <c>[BX+DI]</c>, <c>[BP+SI]</c> and <c>[BP+DI]</c>, so a base paired
  /// with an index must be BX or the frame pointer, and BP is never allocated.
  ///
  /// <para>
  /// Without this a base was drawn from <see cref="_addressing"/> like any other, and SI or DI is a
  /// legal answer there - so an indexed access whose base was allocated after BX had gone would emit
  /// <c>[SI+DI]</c> and end the compilation inside <c>MachineEmitter.EmitInstruction</c>, where
  /// nothing can decline any more. Measured over the whole corpus, all 53 indexed operands do get BX
  /// today, which is why nothing had met it: there is never more than one indexed base live at once,
  /// and BX is simply the first addressing register the pool offers. That is luck rather than a
  /// guarantee, and it is the same shape as the index-side defect recorded above - which also only
  /// appeared once rematerialization changed the pressure.
  /// </para>
  /// </summary>
  private static readonly Reg[] _indexedBase = [Reg.BX];

  /// <summary>
  /// Where a value that is live all the way round a loop is asked for first under
  /// <c>$OPTIMIZE SPEED</c> - the residency preference (docs/PB36.md O5).
  ///
  /// <para>
  /// The reason is not that <c>SI</c> and <c>DI</c> are faster; every general register on this target
  /// costs the same. It is that they are the two the FIXED-register sequences never claim: a multiply
  /// or divide owns <c>AX</c> and <c>DX</c>, a variable shift owns <c>CL</c>, a dispatch works in
  /// <c>AX</c>/<c>BX</c>/<c>CX</c>, and every runtime entry takes its arguments in named registers.
  /// A value the whole loop reads is the value most likely to be standing in one of those spots when
  /// the loop body needs it, so parking it where nothing is pinned is what keeps it in a register at
  /// all rather than in its frame cell. It is also the shape the direct emitter emits (its counter
  /// lives in <c>SI</c> and its second resident in <c>DI</c>), which is worth matching where the two
  /// paths' output is compared by eye.
  /// </para>
  ///
  /// <para>
  /// It is a PREFERENCE and never a constraint, and the distinction is the whole safety argument.
  /// <c>SI</c> and <c>DI</c> are two of the three registers that may address memory, so reserving one
  /// across a loop is exactly the move that once left the spiller with nowhere to put an address
  /// value. <see cref="TryResident"/> therefore falls back to the ordinary pool order for any value
  /// the pair cannot take, and to the whole plain policy on the untouched function when the preferred
  /// sweep does not answer at all - so the set of functions that allocate is unchanged and only the
  /// assignment differs (<c>BackendResidencyTests</c> measures that over the corpus).
  /// </para>
  /// </summary>
  private static readonly Reg[] _resident = [Reg.SI, Reg.DI];

  /// <summary>
  /// Assigns each virtual register a physical register, or returns null when the live set still
  /// exceeds the register file after spilling what it can.
  ///
  /// Spilling is <see cref="Spiller"/>: x86 is a memory-operand machine, so a spilled value simply
  /// becomes its frame cell and needs no reload code - a parameter becomes the caller's own word, any
  /// other value a fresh stack slot. This mutates <paramref name="function"/>, which is why it lives
  /// here: the allocator owns the function's register story.
  /// </summary>
  public static IReadOnlyDictionary<int, Reg>? Allocate(MFunction function) => Allocate(function, out _);

  /// <summary>The same, for a given target and objective - which is what turns the residency preference on.</summary>
  public static IReadOnlyDictionary<int, Reg>? Allocate(MFunction function, SelectionTarget target)
    => Allocate(function, target, out _);

  /// <summary>
  /// The same, reporting WHY it gave up. Selection says why it declines a function; allocation used to
  /// just answer null, which left "register pressure" as the whole diagnosis for every function that
  /// selected and did not route - a black box in the middle of the coverage census.
  /// </summary>
  public static IReadOnlyDictionary<int, Reg>? Allocate(MFunction function, out string? reason)
    => Allocate(function, SelectionTarget.Baseline, out reason);

  /// <summary>The same, for a given target and objective.</summary>
  public static IReadOnlyDictionary<int, Reg>? Allocate(MFunction function, SelectionTarget target, out string? reason)
    => Allocate(function, target, out reason, out _);

  /// <summary>
  /// The same, reporting how many SPILLER ROUNDS the allocation took - one per transformation the
  /// spiller applied before the sweep succeeded - and optionally overriding the work budget.
  ///
  /// <para>
  /// The count is the measurement behind the termination argument on <see cref="AdvanceSpiller"/>: it
  /// is what a fixture asserts a bound on, so a loop that stops converging shows up as a number rather
  /// than as a hang. <paramref name="moveBudget"/> exists for the same reason - a test that wants to
  /// prove the loop settles on its own has to be able to lift the backstop that would otherwise decline
  /// the function first.
  /// </para>
  /// </summary>
  public static IReadOnlyDictionary<int, Reg>? Allocate(MFunction function, SelectionTarget target,
      out string? reason, out int rounds, int? moveBudget = null) {
    rounds = 0;
    if (target is { Optimize: true, OptimizeSpeed: true }
        && TryResident(function, target, moveBudget, ref rounds) is { } resident) {
      reason = null;
      return resident;
    }
    return AllocatePlain(function, target, moveBudget, ref rounds, out reason);
  }

  /// <summary>
  /// The <c>$OPTIMIZE SPEED</c> attempt: coalesce the out-of-SSA copies away, then allocate preferring
  /// <c>SI</c>/<c>DI</c> for whatever is live all the way round a loop.
  ///
  /// <para>
  /// It works on a COPY and commits only on success, which is the whole reason the two policies can be
  /// tried in this order at all. Coalescing unions two live ranges, so the merged value must avoid the
  /// clobbers of both and may find no register where the two halves each had one; the preference asks
  /// for two of the three registers that can address memory. Either can cost an allocation that the
  /// plain policy makes, and neither may - so the plain policy runs on the untouched function whenever
  /// this one does not answer, and the set of functions that route is exactly what it was.
  /// </para>
  /// </summary>
  private static IReadOnlyDictionary<int, Reg>? TryResident(MFunction function, SelectionTarget target,
      int? moveBudget, ref int rounds) {
    var candidate = function.Clone();
    CopyCoalescer.Run(candidate);
    var progress = Spiller.Progress.Of(candidate);
    for (var budget = moveBudget ?? BudgetFor(candidate); budget > 0; --budget) {
      // recomputed each round: coalescing and spilling both move instructions, and an asm block's
      // hold is an instruction range
      var asmHeld = AsmHeldByIndex(candidate, out var asmConflict);
      if (asmConflict is not null)
        return null;                             // the plain policy reports it; this one just stands aside
      if (LivenessAnalysis.LoopCarried(candidate) is { Count: > 0 } carried
          && TryAllocate(candidate, asmHeld, target, carried) is { } assignment) {
        function.Adopt(candidate);
        return assignment;
      }
      if (TryAllocate(candidate, asmHeld, target) is { } plain) {
        function.Adopt(candidate);
        return plain;
      }
      if (!AdvanceSpiller(candidate, ref progress))
        return null;
      ++rounds;
    }
    return null;
  }

  /// <summary>How many moves the allocator may make in total, however large the function is.</summary>
  private const int _MOVE_CEILING = 512;

  /// <summary>
  /// How many spiller moves one function may cost before the back end gives it back.
  ///
  /// <para>
  /// Every round removes at most one value from the register file, so a converging allocation needs
  /// well under one move per virtual register the function started with. Measured over the whole corpus
  /// with this bound lifted out of the way, the worst function takes 174 rounds with the optimizer on
  /// and 174 with it off, well inside its own allowance; the constant term is for the small function
  /// whose ratio is worst rather than whose count is.
  /// </para>
  /// <para>
  /// It is a BACKSTOP and no longer the thing that makes the loop stop: <see cref="AdvanceSpiller"/>
  /// admits a move only when it lowers a measure that cannot go back up, so the loop terminates by
  /// construction and this bound is never what ends it (<c>BackendSpillTerminationTests</c> measures
  /// the corpus's worst round count with the optimizer on AND off, and it is far below the budget
  /// either way). It stays because a bound whose only cost is a decline is cheap insurance on a
  /// termination argument, and because it also caps the WORK a function may do before being declined
  /// for reasons that have nothing to do with looping.
  /// </para>
  /// </summary>
  private static int BudgetFor(MFunction function)
    => Math.Min(function.VirtualRegisterCount + 64, _MOVE_CEILING);

  private static IReadOnlyDictionary<int, Reg>? AllocatePlain(MFunction function, SelectionTarget target,
      int? moveBudget, ref int rounds, out string? reason) {
    var progress = Spiller.Progress.Of(function);
    for (var budget = moveBudget ?? BudgetFor(function); budget > 0; --budget) {
      // recomputed each round, because spilling renumbers the instructions the windows are measured in
      var asmHeld = AsmHeldByIndex(function, out var asmConflict);
      if (asmConflict is not null) {
        // a register an inline-asm statement left for a later one, destroyed in between by something
        // no allocation can move: there is nothing to choose, so the whole function goes back
        reason = asmConflict;
        return null;
      }
      if (TryAllocate(function, asmHeld, target) is { } assignment) {
        reason = null;
        return assignment;
      }
      if (!AdvanceSpiller(function, ref progress)) {
        reason = Blocker(function, asmHeld, target);
        return null;
      }
      ++rounds;
    }
    reason = "the spiller ran out of budget before the live set fitted the register file";
    return null;
  }

  /// <summary>
  /// The spiller's moves, in the order the allocator offers them. Recomputing a frame address comes
  /// BEFORE spilling: it is the only move available for a value used as a memory base, which cannot go
  /// to memory itself. Splitting is two moves rather than one so that a rejected split of a range
  /// caught across a clobber still leaves the pressure split to be tried.
  /// </summary>
  private static readonly Func<MFunction, bool>[] _spillerMoves =
    [Spiller.RematerializeOne, Spiller.SpillOne, Spiller.SplitCrossingOne, Spiller.SplitPressureOne];

  /// <summary>
  /// Applies the first spiller move that gets the function measurably closer to an allocation, and
  /// answers whether one did. This is where the loop is made to TERMINATE.
  ///
  /// <para>
  /// <b>The measure.</b> <see cref="Spiller.Progress"/> is the quadruple (untouched values, clobber
  /// crossings, unsettled uses, values present), ordered lexicographically, and a move is applied only
  /// if it lowers it. All four are counts of things in the function, so the order is well-founded and
  /// the loop cannot run longer than the initial measure - no counter required, and no move can be
  /// admitted whose effect the measure does not see.
  /// </para>
  /// <para>
  /// <b>Why each component is there.</b> Every move CONSUMES its subject: the id is replaced everywhere
  /// by fresh ones (rematerialize, reload, split) or by a frame cell (spill), and every id a move mints
  /// is recorded in <see cref="MFunction.MovedValues"/> at birth. So the FIRST move on any value lowers
  /// the untouched count and nothing can ever raise it, which is the whole argument for every move that
  /// happens once. The three moves that may legitimately repeat each need a component of their own:
  /// splitting a range that still crosses a clobber has to remove a crossing; rematerializing a value
  /// already moved settles at least one use and, inserting at the front of the use's preparation run,
  /// unsettles none; spilling one lowers the number of values present, because it mints nothing and its
  /// subject becomes memory.
  /// </para>
  /// <para>
  /// <b>What used to happen instead.</b> Two of the three moves were not self-limiting.
  /// <c>MOV [v_addr], v_const</c> has two recomputable operands and only one instruction slot next to
  /// the store, so rematerializing either DISPLACED the other and the two swapped for ever, one fresh
  /// virtual register per round with the instruction count never moving; and a split range that still
  /// crossed a clobber was offered for splitting again. Both need IR the optimizer never saw, which is
  /// why they surfaced only when <c>--no-optimize</c> stopped running it (docs/X86-BACKEND.md).
  /// </para>
  /// <para>
  /// The move runs on a COPY, because "did this help" can only be asked of the result: a move that does
  /// not lower the measure is discarded and the next kind offered, rather than being taken and paid
  /// for. That is also what keeps the argument true of moves added later - a new one that settles
  /// nothing simply never applies.
  /// </para>
  /// </summary>
  private static bool AdvanceSpiller(MFunction function, ref Spiller.Progress progress) {
    foreach (var move in _spillerMoves) {
      var candidate = function.Clone();
      if (!move(candidate))
        continue;
      var moved = Spiller.Progress.Of(candidate);
      if (!moved.IsBelow(progress))
        continue;
      function.Adopt(candidate);
      progress = moved;
      return true;
    }
    return false;
  }

  /// <summary>
  /// What stopped the last sweep: the first interval that found no register, and the reason the
  /// spiller then refused to move it to memory.
  /// </summary>
  private static string Blocker(MFunction function, IReadOnlyDictionary<int, IReadOnlyList<Reg>> asmHeld,
      SelectionTarget target) {
    var addressing = AddressRegisters(function);
    var byteRegisters = ByteRegisters(function);
    var clobbersAt = ClobbersByIndex(function);
    var pinnedAt = PinnedByIndex(function);
    var inFlightAt = InFlightByIndex(function);
    var sizes = RegisterSizes(function);
    var free = new List<Reg>(_pool);
    var active = new List<LivenessAnalysis.LiveInterval>();
    var liveness = LivenessAnalysis.Analyze(function);
    var liveAt = liveness.LiveAt;

    foreach (var interval in liveness.Intervals) {
      for (var a = active.Count - 1; a >= 0; --a)
        if (active[a].End < interval.Start)
          active.RemoveAt(a);
      var unsafeRegs = ClobberedOver(clobbersAt, liveAt, interval, interval.End);
      unsafeRegs.UnionWith(ClobberedOver(pinnedAt, liveAt, interval, interval.End - 1));
      unsafeRegs.UnionWith(ClobberedOver(inFlightAt, liveAt, interval, interval.End));
      unsafeRegs.UnionWith(ClobberedOver(asmHeld, liveAt, interval, interval.End));
      var legal = LegalFor(interval.VirtualId, sizes.GetValueOrDefault(interval.VirtualId, MRegSize.Word),
        addressing, byteRegisters, target);
      if (free.Count > active.Count && legal.Any(r => !unsafeRegs.Contains(r)))
        continue;
      var isAddress = addressing.Base.Contains(interval.VirtualId) || addressing.Index.Contains(interval.VirtualId);
      return isAddress
        ? "a value used as a memory base or index is live across a full-register clobber (it cannot spill)"
        : unsafeRegs.Count >= _pool.Length
          ? "a value is live across an instruction that clobbers every register, and cannot move to memory"
          : "no register is free where the value is live, and none of the candidates can move to memory";
    }
    return "no register assignment, and nothing left that can move to memory";
  }

  /// <summary>
  /// One linear-scan sweep, with no spilling: null when some interval finds no register.
  /// <paramref name="resident"/> is the set of values to offer <c>SI</c>/<c>DI</c> to first (empty
  /// for the plain sweep); <paramref name="asmHeld"/> the registers an inline-asm statement is
  /// holding for a later one to read.
  /// </summary>
  private static IReadOnlyDictionary<int, Reg>? TryAllocate(MFunction function,
      IReadOnlyDictionary<int, IReadOnlyList<Reg>> asmHeld, SelectionTarget target,
      HashSet<int>? resident = null) {
    var liveness = LivenessAnalysis.Analyze(function);
    var intervals = liveness.Intervals;
    var liveAt = liveness.LiveAt;
    var addressVregs = AddressRegisters(function);   // vregs that ever form a memory address -> need BX/SI/DI
    var byteRegisters = ByteRegisters(function);     // byte values need AL/CL/DL/BL, which SI/DI do not have
    var clobbersAt = ClobbersByIndex(function);       // global instruction index -> registers a CALL there destroys
    var pinnedAt = PinnedByIndex(function);           // ...and the ones an ABI-pinned physical write lands in
    var inFlightAt = InFlightByIndex(function);       // ...and the ones already carrying a value to a named reader
    var sizes = RegisterSizes(function);
    var dwordInductions = DwordInductionRegisters(function);
    var assignment = new Dictionary<int, Reg>();
    var free = new List<Reg>(_pool);                 // registers currently available, preferred order preserved
    var active = new List<LivenessAnalysis.LiveInterval>();  // live intervals holding a register, kept sorted by End

    foreach (var interval in intervals) {
      // expire every interval that ended before this one starts, returning its register to the pool
      for (var a = active.Count - 1; a >= 0; --a)
        if (active[a].End < interval.Start) {
          ReturnToPool(free, assignment[active[a].VirtualId]);
          active.RemoveAt(a);
        }

      // registers destroyed by a CALL anywhere this value is live cannot hold it, and neither can one
      // an ABI-PINNED write lands in while the value still has readers - nor one already in flight
      // between where a physical value was produced and the instruction that names it, nor one an
      // inline-asm statement is holding for a later statement to read
      var unsafeRegs = ClobberedOver(clobbersAt, liveAt, interval, interval.End);
      unsafeRegs.UnionWith(ClobberedOver(pinnedAt, liveAt, interval, interval.End - 1));
      unsafeRegs.UnionWith(ClobberedOver(inFlightAt, liveAt, interval, interval.End));
      unsafeRegs.UnionWith(ClobberedOver(asmHeld, liveAt, interval, interval.End));
      var size = sizes.GetValueOrDefault(interval.VirtualId, MRegSize.Word);
      var legal = LegalFor(interval.VirtualId, size, addressVregs, byteRegisters, target);
      bool Usable(Reg r) => System.Array.IndexOf(legal, r) >= 0 && !unsafeRegs.Contains(r);
      var slot = -1;
      if (resident is not null && resident.Contains(interval.VirtualId)) {
        var preferences = size == MRegSize.Dword && !dwordInductions.Contains(interval.VirtualId)
          ? _resident.Reverse()
          : _resident;
        foreach (var preferred in preferences)
          if (Usable(preferred) && (slot = free.IndexOf(preferred)) >= 0)
            break;
      }
      if (slot < 0)
        slot = free.FindIndex(Usable);              // the preference is spent - take the ordinary order
      if (slot < 0)
        return null;                                 // no suitable register free - spill needed

      var reg = free[slot];
      free.RemoveAt(slot);
      assignment[interval.VirtualId] = SizedRegister(reg, size);
      active.Add(interval);
      active.Sort((x, y) => x.End.CompareTo(y.End));
    }

    return assignment;
  }

  /// <summary>
  /// The set of physical registers destroyed at a point where <paramref name="interval"/>'s value is
  /// really live, over <c>[interval.Start, end]</c>.
  ///
  /// <para>
  /// The interval is the HULL of the value's live points, and an index inside it is not necessarily
  /// one of them - a block laid out between a loop's head and its latch belongs to the hull whether or
  /// not it belongs to the loop. Asking <see cref="LivenessAnalysis.Liveness.LiveAt"/> rather than the
  /// hull is what lets a FOR counter keep its register across the <c>PRINT</c> that follows the loop:
  /// the value is dead there, so the call destroying every register destroys nothing of its.
  /// </para>
  /// </summary>
  private static HashSet<Reg> ClobberedOver(IReadOnlyDictionary<int, IReadOnlyList<Reg>> clobbersAt,
      IReadOnlyList<HashSet<int>> liveAt, LivenessAnalysis.LiveInterval interval, int end) {
    var clobbered = new HashSet<Reg>();
    for (var i = interval.Start; i <= end; ++i)
      if (clobbersAt.TryGetValue(i, out var regs) && i < liveAt.Count && liveAt[i].Contains(interval.VirtualId))
        clobbered.UnionWith(regs);
    return clobbered;
  }

  /// <summary>
  /// Maps each global instruction index to the physical registers that instruction WRITES BY NAME -
  /// the ABI-pinned spots: the <c>AX</c>/<c>DX:AX</c> a result goes home in, the register a runtime
  /// entry takes its argument in, the <c>AX</c>/<c>DX</c> of a multiply or divide.
  ///
  /// <para>
  /// Liveness only tracks VIRTUAL registers, so without this a pinned write is invisible: the
  /// allocator would happily leave a live value in <c>AX</c> across a <c>MOV AX, something</c> and the
  /// value would simply be gone. It is not a hypothetical - a pair of returns, <c>MOV AX,lo</c> then
  /// <c>MOV DX,hi</c>, quietly returned the low word twice the moment the allocator handed <c>lo</c>
  /// the <c>DX</c> that the second move was about to read from. A CALL's clobber list already says
  /// this for the whole register file; this says it for the one register a pinned move names.
  /// </para>
  ///
  /// <para>
  /// Read over [start, end) rather than the CALL clobbers' [start, end]: a value whose LAST live point
  /// is the pinned move itself is the value being moved, and <c>MOV AX, AX</c> is not a loss.
  /// </para>
  /// </summary>
  private static IReadOnlyDictionary<int, IReadOnlyList<Reg>> PinnedByIndex(MFunction function) {
    var map = new Dictionary<int, IReadOnlyList<Reg>>();
    var index = 0;
    foreach (var block in function.Blocks)
      foreach (var instr in block.Instructions) {
        List<Reg>? pinned = null;
        foreach (var operand in instr.Effect.WrittenRegs)
          if (operand < instr.Operands.Count
              && instr.Operands[operand] is MOperand.Register { Reg: { IsVirtual: false } fixedReg })
            (pinned ??= []).Add(WholeRegister(fixedReg.Physical));
        if (pinned is not null)
          map[index] = pinned;
        ++index;
      }
    return map;
  }

  /// <summary>
  /// Maps each global instruction index to the physical registers that are already CARRYING a value
  /// there - one produced earlier and not yet consumed by the instruction that names it as a read.
  ///
  /// <para>
  /// <see cref="PinnedByIndex"/> is the same idea seen from the other end and covers only half of it. A
  /// pinned WRITE says "nothing of yours may survive this point"; a pinned READ says "something of mine
  /// arrived earlier and must still be here", and nothing said that. The gap is the window between a
  /// <c>CALL</c> and the <c>MOV v, AX</c> that takes its result out: the call's clobber list stops a
  /// value living ACROSS the call, and the extraction move writes only a virtual, so an instruction
  /// scheduled between the two looked free to take any register - including the <c>AX</c> the result is
  /// sitting in.
  /// </para>
  ///
  /// <para>
  /// That is how two dynamic arrays came to share storage. <c>REDIM b(...)</c> lowers to
  /// <c>CALL rt_arr_alloc</c> / <c>MOV v,AX</c>, the scheduler put an unrelated <c>MOV v2,[BP-2]</c>
  /// between them (legal - it writes a virtual), the allocator gave v2 <c>AX</c>, and b's data pointer
  /// became the frame word instead of the block. It then aliased whatever the NEXT allocation returned,
  /// so writing the second array changed the first.
  /// </para>
  ///
  /// <para>
  /// The window is [producer + 1, reader - 1]: the producing instruction and the consuming one are the
  /// two ends and neither is inside it, which keeps the extraction move itself free to be allocated the
  /// very register it reads (<c>MOV AX, AX</c> costs nothing and is the coalescing the selector wants).
  /// With no producer in the block the register came from outside it, and the window opens at the block.
  /// </para>
  ///
  /// <para>
  /// <see cref="AsmHeldByIndex"/> is the same window measured over the whole control-flow graph, for
  /// the producer and reader an INLINE-ASSEMBLY statement can be. It is a separate analysis rather than
  /// a widening of this one because an asm block's clobber list is conservative: this one may read a
  /// clobber as "the old value ends here", and there it would end a promise the text is still keeping.
  /// </para>
  /// </summary>
  private static IReadOnlyDictionary<int, IReadOnlyList<Reg>> InFlightByIndex(MFunction function) {
    var map = new Dictionary<int, List<Reg>>();
    var index = 0;
    foreach (var block in function.Blocks) {
      var producedAt = new Dictionary<Reg, int>();
      var blockStart = index;
      foreach (var instr in block.Instructions) {
        foreach (var read in PhysicalReads(instr)) {
          var from = producedAt.TryGetValue(read, out var producer) ? producer + 1 : blockStart;
          for (var at = from; at < index; ++at) {
            if (!map.TryGetValue(at, out var regs))
              map[at] = regs = [];
            if (!regs.Contains(read))
              regs.Add(read);
          }
        }
        foreach (var written in PhysicalWrites(instr))
          producedAt[written] = index;
        ++index;
      }
    }
    return map.ToDictionary(entry => entry.Key, entry => (IReadOnlyList<Reg>)entry.Value);
  }

  /// <summary>The physical registers an instruction names as a read - an operand or an address part.</summary>
  private static IEnumerable<Reg> PhysicalReads(MInstr instr) {
    foreach (var operand in instr.Effect.ReadRegs)
      if (operand < instr.Operands.Count
          && instr.Operands[operand] is MOperand.Register { Reg: { IsVirtual: false } read })
        yield return WholeRegister(read.Physical);
    foreach (var operand in instr.Operands) {
      if (operand is not MOperand.Memory memory)
        continue;
      if (memory.Base is { IsVirtual: false } baseRegister)
        yield return WholeRegister(baseRegister.Physical);
      if (memory.Index is { IsVirtual: false } indexRegister)
        yield return WholeRegister(indexRegister.Physical);
      if (memory.Segment is { IsVirtual: false } segmentRegister)
        yield return WholeRegister(segmentRegister.Physical);
    }
  }

  /// <summary>The physical registers an instruction ends the life of - a named write or a clobber.</summary>
  private static IEnumerable<Reg> PhysicalWrites(MInstr instr) {
    foreach (var operand in instr.Effect.WrittenRegs)
      if (operand < instr.Operands.Count
          && instr.Operands[operand] is MOperand.Register { Reg: { IsVirtual: false } written })
        yield return WholeRegister(written.Physical);
    foreach (var clobbered in instr.Clobbers)
      yield return WholeRegister(clobbered);
  }

  /// <summary>The word-sized allocation slot a byte or dword register aliases.</summary>
  private static Reg WholeRegister(Reg register)
    => register.IsByte() ? (Reg)(0x10 | (register.Index() & 0x03))
      : register.IsDword() ? (Reg)(0x10 | register.Index())
      : register;

  /// <summary>Maps each global instruction index (same numbering as the liveness pass) to the registers it clobbers.</summary>
  private static IReadOnlyDictionary<int, IReadOnlyList<Reg>> ClobbersByIndex(MFunction function) {
    var map = new Dictionary<int, IReadOnlyList<Reg>>();
    var index = 0;
    foreach (var block in function.Blocks)
      foreach (var instr in block.Instructions) {
        if (instr.Clobbers.Count > 0)
          map[index] = instr.Clobbers.Select(WholeRegister).Distinct().ToList();
        ++index;
      }
    return map;
  }

  /// <summary>
  /// The virtual registers that appear as a memory operand's base or index (so they must be
  /// addressing-capable), with the bases of INDEXED operands kept apart: those have a smaller legal
  /// set still, because 16-bit addressing pairs an index only with BX or BP.
  /// </summary>
  private static (HashSet<int> Base, HashSet<int> Index, HashSet<int> IndexedBase) AddressRegisters(MFunction function) {
    var bases = new HashSet<int>();
    var indices = new HashSet<int>();
    var indexedBases = new HashSet<int>();
    foreach (var instr in function.AllInstructions)
      foreach (var operand in instr.Operands)
        if (operand is MOperand.Memory mem) {
          if (mem.Base is { IsVirtual: true } b) {
            bases.Add(b.VirtualId);
            if (mem.Index is not null)
              indexedBases.Add(b.VirtualId);
          }
          if (mem.Index is { IsVirtual: true } x)
            indices.Add(x.VirtualId);
        }
    return (bases, indices, indexedBases);
  }

  private static HashSet<int> ByteRegisters(MFunction function) {
    var result = new HashSet<int>();
    foreach (var instruction in function.AllInstructions)
      foreach (var operand in instruction.Operands)
        switch (operand) {
          case MOperand.Register { Reg: { IsVirtual: true, Size: MRegSize.Byte } register }:
            result.Add(register.VirtualId);
            break;
          case MOperand.Memory memory:
            if (memory.Base is { IsVirtual: true, Size: MRegSize.Byte } baseRegister)
              result.Add(baseRegister.VirtualId);
            if (memory.Index is { IsVirtual: true, Size: MRegSize.Byte } indexRegister)
              result.Add(indexRegister.VirtualId);
            break;
        }
    return result;
  }

  private static Dictionary<int, MRegSize> RegisterSizes(MFunction function) {
    var sizes = new Dictionary<int, MRegSize>();
    void Record(MReg? register) {
      if (register is { IsVirtual: true } value)
        sizes[value.VirtualId] = value.Size;
    }
    foreach (var instruction in function.AllInstructions)
      foreach (var operand in instruction.Operands)
        switch (operand) {
          case MOperand.Register register:
            Record(register.Reg);
            break;
          case MOperand.Memory memory:
            Record(memory.Base);
            Record(memory.Index);
            Record(memory.Segment);
            break;
        }
    return sizes;
  }

  /// <summary>
  /// Native dword values advanced by a constant. These are loop induction variables rather than
  /// accumulators, so the residency convention gives them ESI first and the other carried dword EDI.
  /// </summary>
  private static HashSet<int> DwordInductionRegisters(MFunction function) => function.AllInstructions
    .Where(instruction => instruction.Opcode is MOpcode.Add or MOpcode.Sub
      && instruction.Operands is [MOperand.Register { Reg: { IsVirtual: true, Size: MRegSize.Dword } register },
        MOperand.Immediate])
    .Select(instruction => ((MOperand.Register)instruction.Operands[0]).Reg.VirtualId)
    .ToHashSet();

  /// <summary>
  /// The allocation slots a value may occupy given its width and address uses. The address tests run
  /// in increasing restriction, so a value used in two roles gets their intersection: an index
  /// (SI/DI), an indexed base (BX), or an ordinary base (BX/SI/DI). Native dwords use the corresponding
  /// 386 register alias and cannot form a 16-bit address.
  /// </summary>
  private static Reg[] LegalFor(int virtualId, MRegSize size,
      (HashSet<int> Base, HashSet<int> Index, HashSet<int> IndexedBase) addressing,
      HashSet<int> byteRegisters,
      SelectionTarget target) {
    if (size == MRegSize.Dword)
      return target.Cpu386 && !addressing.Base.Contains(virtualId) && !addressing.Index.Contains(virtualId)
        ? _pool
        : [];
    if (byteRegisters.Contains(virtualId))
      return addressing.Base.Contains(virtualId) || addressing.Index.Contains(virtualId) ? [] : _bytePool;
    if (addressing.Index.Contains(virtualId))
      return addressing.IndexedBase.Contains(virtualId) ? [] : _indexing;
    return addressing.IndexedBase.Contains(virtualId) ? _indexedBase
      : addressing.Base.Contains(virtualId) ? _addressing
      : _pool;
  }

  private static Reg SizedRegister(Reg register, MRegSize size)
    => size == MRegSize.Dword ? (Reg)(0x20 | register.Index()) : register;

  // keep the freed register in the pool's preferred order so allocation is deterministic
  private static void ReturnToPool(List<Reg> free, Reg reg) {
    reg = WholeRegister(reg);
    var slot = System.Array.IndexOf(_pool, reg);
    var at = 0;
    while (at < free.Count && System.Array.IndexOf(_pool, free[at]) < slot)
      ++at;
    free.Insert(at, reg);
  }
}
