using PowerBasic.Compiler.CodeGen;

namespace PowerBasic.Compiler.Backend;

/// <summary>
/// Stage 6 of the x86-16 back end (docs/X86-BACKEND.md): instruction scheduling on the machine IR.
/// This is the payoff of the whole backend - run after register allocation, when independent values
/// already live in independent registers, the dependency-driven list scheduler can finally interleave
/// their chains and cluster memory/ALU work, the reordering the AX-centric byte-level scheduler could
/// never reach. It reuses the shared <see cref="InlineAsmScheduler.ScheduleByDependency"/> core,
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
      }
    return (reads, writes);
  }

  private static int Key(MReg reg) => reg.IsVirtual ? reg.VirtualId : -((int)reg.Physical + 1);
}
