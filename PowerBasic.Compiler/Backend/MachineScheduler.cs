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

  /// <summary>Reorders the non-terminator instructions of every block by their data dependencies.</summary>
  public static void Schedule(MFunction function) {
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

    var scheduled = new List<MInstr>(instrs.Count);
    foreach (var idx in order)
      scheduled.Add(instrs[idx]);
    for (var i = n; i < instrs.Count; ++i)
      scheduled.Add(instrs[i]);
    instrs.Clear();
    instrs.AddRange(scheduled);
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
