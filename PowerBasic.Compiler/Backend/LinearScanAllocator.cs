using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Backend;

/// <summary>
/// Stage 4 of the x86-16 back end (docs/X86-BACKEND.md): linear-scan register allocation. It sweeps
/// the live intervals (stage 3) in start order, handing each virtual register a free physical
/// register from <c>AX BX CX DX SI DI</c> (BP/SP are the frame, so reserved) and freeing it again when
/// the interval ends. Two intervals that overlap in time therefore get distinct registers, while
/// disjoint intervals reuse one - this is where independent values land in independent registers, the
/// reassignment the byte-level scheduler could never do. When a sweep cannot assign a value, the
/// allocator retries after rematerializing, directly spilling, or splitting one live range; it
/// returns null only when none of those transformations can make progress.
/// </summary>
public sealed class LinearScanAllocator {

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
  /// Assigns each virtual register a physical register, or returns null when the live set still
  /// exceeds the register file after spilling what it can.
  ///
  /// Spilling is <see cref="Spiller"/>: x86 is a memory-operand machine, so a spilled value simply
  /// becomes its frame cell and needs no reload code - a parameter becomes the caller's own word, any
  /// other value a fresh stack slot. This mutates <paramref name="function"/>, which is why it lives
  /// here: the allocator owns the function's register story.
  /// </summary>
  public static IReadOnlyDictionary<int, Reg>? Allocate(MFunction function) => Allocate(function, out _);

  /// <summary>
  /// The same, reporting WHY it gave up. Selection says why it declines a function; allocation used to
  /// just answer null, which left "register pressure" as the whole diagnosis for every function that
  /// selected and did not route - a black box in the middle of the coverage census.
  /// </summary>
  public static IReadOnlyDictionary<int, Reg>? Allocate(MFunction function, out string? reason) {
    for (;;) {
      if (TryAllocate(function) is { } assignment) {
        reason = null;
        return assignment;
      }
      // recomputing a frame address is tried BEFORE spilling: it is the only move available for a
      // value used as a memory base, which cannot go to memory itself
      if (Spiller.RematerializeOne(function))
        continue;
      if (Spiller.SpillOne(function))
        continue;
      if (Spiller.SplitOne(function))
        continue;
      reason = Blocker(function);
      return null;
    }
  }

  /// <summary>
  /// What stopped the last sweep: the first interval that found no register, and the reason the
  /// spiller then refused to move it to memory.
  /// </summary>
  private static string Blocker(MFunction function) {
    var addressing = AddressRegisters(function);
    var byteRegisters = ByteRegisters(function);
    var clobbersAt = ClobbersByIndex(function);
    var pinnedAt = PinnedByIndex(function);
    var inFlightAt = InFlightByIndex(function);
    var free = new List<Reg>(_pool);
    var active = new List<LivenessAnalysis.LiveInterval>();

    foreach (var interval in LivenessAnalysis.Compute(function)) {
      for (var a = active.Count - 1; a >= 0; --a)
        if (active[a].End < interval.Start)
          active.RemoveAt(a);
      var unsafeRegs = ClobberedOver(clobbersAt, interval.Start, interval.End);
      unsafeRegs.UnionWith(ClobberedOver(pinnedAt, interval.Start, interval.End - 1));
      unsafeRegs.UnionWith(ClobberedOver(inFlightAt, interval.Start, interval.End));
      var legal = LegalFor(interval.VirtualId, addressing, byteRegisters);
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

  /// <summary>One linear-scan sweep, with no spilling: null when some interval finds no register.</summary>
  private static IReadOnlyDictionary<int, Reg>? TryAllocate(MFunction function) {
    var intervals = LivenessAnalysis.Compute(function);
    var addressVregs = AddressRegisters(function);   // vregs that ever form a memory address -> need BX/SI/DI
    var byteRegisters = ByteRegisters(function);     // byte values need AL/CL/DL/BL, which SI/DI do not have
    var clobbersAt = ClobbersByIndex(function);       // global instruction index -> registers a CALL there destroys
    var pinnedAt = PinnedByIndex(function);           // ...and the ones an ABI-pinned physical write lands in
    var inFlightAt = InFlightByIndex(function);       // ...and the ones already carrying a value to a named reader
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
      // between where a physical value was produced and the instruction that names it
      var unsafeRegs = ClobberedOver(clobbersAt, interval.Start, interval.End);
      unsafeRegs.UnionWith(ClobberedOver(pinnedAt, interval.Start, interval.End - 1));
      unsafeRegs.UnionWith(ClobberedOver(inFlightAt, interval.Start, interval.End));
      var legal = LegalFor(interval.VirtualId, addressVregs, byteRegisters);
      var slot = free.FindIndex(r => System.Array.IndexOf(legal, r) >= 0 && !unsafeRegs.Contains(r));
      if (slot < 0)
        return null;                                 // no suitable register free - spill needed

      var reg = free[slot];
      free.RemoveAt(slot);
      assignment[interval.VirtualId] = reg;
      active.Add(interval);
      active.Sort((x, y) => x.End.CompareTo(y.End));
    }

    return assignment;
  }

  /// <summary>The set of physical registers any CALL destroys while a value spanning [start, end] is live.</summary>
  private static HashSet<Reg> ClobberedOver(IReadOnlyDictionary<int, IReadOnlyList<Reg>> clobbersAt, int start, int end) {
    var clobbered = new HashSet<Reg>();
    for (var i = start; i <= end; ++i)
      if (clobbersAt.TryGetValue(i, out var regs))
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

  /// <summary>The word register a byte half belongs to - writing <c>AL</c> destroys half of <c>AX</c>.</summary>
  private static Reg WholeRegister(Reg register)
    => register.IsByte() ? (Reg)(0x10 | (register.Index() & 0x03)) : register;

  /// <summary>Maps each global instruction index (same numbering as the liveness pass) to the registers it clobbers.</summary>
  private static IReadOnlyDictionary<int, IReadOnlyList<Reg>> ClobbersByIndex(MFunction function) {
    var map = new Dictionary<int, IReadOnlyList<Reg>>();
    var index = 0;
    foreach (var block in function.Blocks)
      foreach (var instr in block.Instructions) {
        if (instr.Clobbers.Count > 0)
          map[index] = instr.Clobbers;
        ++index;
      }
    return map;
  }

  /// <summary>The virtual registers that appear as a memory operand's base or index (so they must be addressing-capable).</summary>
  private static (HashSet<int> Base, HashSet<int> Index) AddressRegisters(MFunction function) {
    var bases = new HashSet<int>();
    var indices = new HashSet<int>();
    foreach (var instr in function.AllInstructions)
      foreach (var operand in instr.Operands)
        if (operand is MOperand.Memory mem) {
          if (mem.Base is { IsVirtual: true } b)
            bases.Add(b.VirtualId);
          if (mem.Index is { IsVirtual: true } x)
            indices.Add(x.VirtualId);
        }
    return (bases, indices);
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

  /// <summary>The registers a value may occupy given how it is used to address memory.</summary>
  private static Reg[] LegalFor(int virtualId, (HashSet<int> Base, HashSet<int> Index) addressing,
      HashSet<int> byteRegisters) {
    if (byteRegisters.Contains(virtualId))
      return addressing.Base.Contains(virtualId) || addressing.Index.Contains(virtualId) ? [] : _bytePool;
    return addressing.Index.Contains(virtualId) ? _indexing
      : addressing.Base.Contains(virtualId) ? _addressing
      : _pool;
  }

  // keep the freed register in the pool's preferred order so allocation is deterministic
  private static void ReturnToPool(List<Reg> free, Reg reg) {
    var slot = System.Array.IndexOf(_pool, reg);
    var at = 0;
    while (at < free.Count && System.Array.IndexOf(_pool, free[at]) < slot)
      ++at;
    free.Insert(at, reg);
  }
}
