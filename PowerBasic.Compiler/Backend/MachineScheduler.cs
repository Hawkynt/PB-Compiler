using PowerBasic.Compiler.CodeGen;

namespace PowerBasic.Compiler.Backend;

/// <summary>
/// Stage 6 of the x86-16 back end (docs/X86-BACKEND.md): instruction scheduling on the machine IR.
/// This is the payoff of the whole backend - run immediately before register allocation, the
/// dependency-driven list scheduler can interleave independent chains and cluster memory/ALU work,
/// the reordering the AX-centric byte-level scheduler could never reach. It reuses the shared
/// <see cref="InlineAsmScheduler.ScheduleByDependency"/> core,
/// reading each instruction's explicit def/use descriptor (registers - virtual or physical - plus
/// flags and memory) rather than re-deriving it from bytes. The block terminator stays pinned last.
/// </summary>
public static class MachineScheduler {

  /// <summary>
  /// How many values the register file can hold at once - <c>AX CX DX BX SI DI</c>, the pool
  /// <see cref="LinearScanAllocator"/> allocates from (BP/SP are the frame). Above this a block needs
  /// the spiller, and the spiller cannot always oblige, so it is the line the scheduler will not push
  /// a block across.
  /// </summary>
  private const int _registerFile = 6;

  /// <summary>Runs optimizer-gated target combines, then reorders non-terminators by their dependencies.</summary>
  public static void Schedule(MFunction function) {
    if (MachineOptimizationState.IsMarked(function)) {
      // O0348/O0349 live here rather than in selection: only after all IR instructions have become
      // one machine stream can a private TBYTE spill/reload pair be recognized. Run before any
      // reordering so the x87 stack proof describes the selector's source-order expression tree.
      X87StackOptimizer.Run(function);
      MachineCombiner.Run(function);
      SuperoptimizedPeepholes.Run(function);
    }
    foreach (var block in function.Blocks)
      ScheduleBlock(block);
  }

  private static void ScheduleBlock(MBlock block) {
    var instrs = block.Instructions;
    var n = instrs.Count;
    while (n > 0 && instrs[n - 1].IsTerminator)
      --n;                                       // keep trailing terminators where they are
    if (n < 3)
      return;

    var keys = new (HashSet<int> Reads, HashSet<int> Writes)[n];
    for (var i = 0; i < n; ++i)
      keys[i] = RegisterKeys(instrs[i]);

    var order = InlineAsmScheduler.ScheduleByDependency(n,
      (a, b) => Conflicts(instrs[a], keys[a], instrs[b], keys[b]),
      i => instrs[i].Effect.ReadsMemory || instrs[i].Effect.WritesMemory);
    if (order == null)
      return;
    if (CostsRegisters(keys, order))
      return;

    var scheduled = new List<MInstr>(instrs.Count);
    foreach (var idx in order)
      scheduled.Add(instrs[idx]);
    for (var i = n; i < instrs.Count; ++i)
      scheduled.Add(instrs[i]);
    instrs.Clear();
    instrs.AddRange(scheduled);
  }

  /// <summary>
  /// Whether the proposed order would keep more values alive at once than the register file holds,
  /// having kept fewer in program order - in which case the block is left as the selector wrote it.
  ///
  /// <para>
  /// A list scheduler maximizes independence, and independence IS register pressure: every chain it
  /// interleaves is one more value waiting for its consumer. The selector emits a 32-bit accumulation
  /// over an array as ten serial steps - load an element, sign-extend it, add the pair, move on - which
  /// needs four registers at a time whatever the array's length. All ten loads are ready at the top of
  /// the block, though, and nothing but this stops them being issued there: ten live values on a
  /// six-register machine, and the accumulation then declines at allocation instead of compiling.
  /// </para>
  ///
  /// <para>
  /// Spilling cannot rescue that shape. A loaded element's definition already carries a memory operand,
  /// so it cannot become one itself, and with the whole loop unrolled there is no CALL between the load
  /// and its use for live-range splitting to split around. The pressure has to not be created.
  /// </para>
  ///
  /// <para>
  /// The gate is the register file rather than "no increase at all": below six, reordering is free and
  /// the scheduler keeps every schedule it used to pick, so this only ever refuses one that could not
  /// have been allocated as written. Physical registers are excluded because they are already pinned -
  /// an ABI setup window is a scheduling barrier via its clobbers.
  /// </para>
  /// </summary>
  private static bool CostsRegisters((HashSet<int> Reads, HashSet<int> Writes)[] keys, int[] order) {
    var scheduled = PeakPressure(keys, order);
    if (scheduled <= _registerFile)
      return false;
    var written = new int[order.Length];
    for (var i = 0; i < written.Length; ++i)
      written[i] = i;
    return scheduled > PeakPressure(keys, written);
  }

  /// <summary>
  /// The largest number of virtual values live at one point of a linear order, each value counted from
  /// where the order first mentions it to where it last does. Values live across the whole block
  /// contribute the same to every order of it, so the two figures are comparable even though neither is
  /// the function's true pressure.
  /// </summary>
  private static int PeakPressure((HashSet<int> Reads, HashSet<int> Writes)[] keys, int[] order) {
    var first = new Dictionary<int, int>();
    var last = new Dictionary<int, int>();
    for (var at = 0; at < order.Length; ++at) {
      var (reads, writes) = keys[order[at]];
      foreach (var value in reads.Concat(writes)) {
        if (value < 0)
          continue;                              // a physical register: pinned, not allocated
        if (!first.ContainsKey(value))
          first[value] = at;
        last[value] = at;
      }
    }

    var delta = new int[order.Length + 1];
    foreach (var (value, from) in first) {
      ++delta[from];
      --delta[last[value] + 1];
    }
    int live = 0, peak = 0;
    for (var at = 0; at < order.Length; ++at) {
      live += delta[at];
      if (live > peak)
        peak = live;
    }
    return peak;
  }

  // a < b in program order: does b depend on a (so their order must be preserved)?
  private static bool Conflicts(MInstr a, (HashSet<int> Reads, HashSet<int> Writes) ka,
                                MInstr b, (HashSet<int> Reads, HashSet<int> Writes) kb) {
    // A CALL is a barrier. Nothing is gained by moving work across one on this target - there is no
    // renaming to hide a latency behind - and something is lost: hoisting a value's definition above a
    // call stretches its live range across the whole caller-saved file, which is precisely what the
    // allocator cannot satisfy. Scheduling ran before allocation and was making the pressure it then
    // failed on.
    if (a.Opcode == MOpcode.Call || b.Opcode == MOpcode.Call)
      return true;
    // Explicit physical clobbers also delimit pinned-register sequences. Allocation has not happened
    // yet, so an otherwise independent virtual instruction moved into such a sequence could later be
    // assigned the pinned register and overwrite a prepared runtime argument or implicit result.
    if (a.Clobbers.Count > 0 || b.Clobbers.Count > 0)
      return true;
    // the x87 stack is a resource no effect descriptor names, so two instructions that use it are
    // ordered against each other whatever their operands say - see MOpcodes.UsesX87
    if (MOpcodes.UsesX87(a.Opcode) && MOpcodes.UsesX87(b.Opcode))
      return true;
    // register RAW / WAR / WAW
    if (ka.Writes.Overlaps(kb.Reads) || ka.Writes.Overlaps(kb.Writes) || ka.Reads.Overlaps(kb.Writes))
      return true;
    // flags
    if ((a.Effect.WritesFlags && (b.Effect.ReadsFlags || b.Effect.WritesFlags)) || (a.Effect.ReadsFlags && b.Effect.WritesFlags))
      return true;
    // memory - conservative: any pair where at least one writes is ordered (no aliasing analysis here)
    var aMem = a.Effect.ReadsMemory || a.Effect.WritesMemory;
    var bMem = b.Effect.ReadsMemory || b.Effect.WritesMemory;
    return (a.Effect.WritesMemory && bMem) || (aMem && b.Effect.WritesMemory);
  }

  // the registers an instruction reads/writes as scheduler keys (virtual id, or a distinct key per physical register)
  private static (HashSet<int> Reads, HashSet<int> Writes) RegisterKeys(MInstr instr) {
    var reads = new HashSet<int>();
    var writes = new HashSet<int>();
    foreach (var i in instr.Effect.ReadRegs)
      if (instr.Operands[i] is MOperand.Register r)
        reads.Add(Key(r.Reg));
    foreach (var i in instr.Effect.WrittenRegs)
      if (instr.Operands[i] is MOperand.Register w)
        writes.Add(Key(w.Reg));
    // a clobber is a write the operands do not name - a CALL destroying the caller-saved set, an IDIV
    // overwriting DX:AX. Without it the scheduler sees no dependency at all between the clobberer and
    // the MOV that reads the result out of AX, and is free to hoist that MOV above it.
    foreach (var clobbered in instr.Clobbers)
      writes.Add(Key(MReg.Physical_(clobbered)));
    foreach (var operand in instr.Operands)
      if (operand is MOperand.Memory mem) {
        if (mem.Base is { } b)
          reads.Add(Key(b));
        if (mem.Index is { } x)
          reads.Add(Key(x));
        if (mem.Segment is { } s)
          reads.Add(Key(s));
      }
    return (reads, writes);
  }

  private static int Key(MReg reg) => reg.IsVirtual ? reg.VirtualId : -((int)reg.Physical + 1);
}
