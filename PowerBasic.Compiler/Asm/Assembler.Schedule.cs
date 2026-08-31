using PowerBasic.Compiler.CodeGen;

namespace PowerBasic.Compiler.Asm;

public sealed partial class Assembler {

  /// <summary>
  /// When set, the instruction methods record a def/use descriptor so <see cref="RunSchedule"/> can
  /// reorder the final instruction stream (after loop unrolling, inlining and const-folding - this runs
  /// on the emitted bytes, downstream of every codegen transform) to group memory and ALU operations and
  /// interleave independent dependency chains for better execution-port utilization. Off by default;
  /// the optimizer turns it on for the optimized standalone image (the historic dialects keep the
  /// faithful, unscheduled stream).
  /// </summary>
  public bool EnableSchedule { get; set; }

  /// <summary>
  /// A recorded instruction's data dependencies: which word registers, flags, and memory it reads
  /// and writes.
  /// </summary>
  private readonly record struct SchedInstr(
    int Start, int Length, ushort Reads, ushort Writes, bool ReadsFlags, bool WritesFlags,
    bool MemRead, bool MemWrite, object? MemBase, int MemDisp, int MemBytes) {
    public bool TouchesMemory => this.MemRead || this.MemWrite;
  }

  private List<SchedInstr>? _schedInstrs;
  private bool _scheduleRan;

  // C3: pseudo-resource bit (beyond the 8 register slots) serializing all x87 instructions
  // among themselves while letting integer work interleave around them
  private const ushort _FPUSTACK = 0x8000;

  // word-register slot 0..7 (AX..DI); a byte half maps to its word slot, a 32-bit name to the same slot
  private static int RegSlot(Reg r) => r.IsByte() ? (r.Index() & 3) : r.Index();
  private static ushort RegBit(Reg r) => (ushort)(1 << RegSlot(r));

  /// <summary>True when an instruction stream consumer needs the def/use records.</summary>
  private bool RecordingSched => this.EnableSchedule || this.EnableLoadForwarding;

  private void RecordSchedReg(int start, ushort reads, ushort writes, bool readsFlags, bool writesFlags) {
    if (this.RecordingSched)
      (this._schedInstrs ??= []).Add(new(start, this.Position - start, reads, writes, readsFlags,
        writesFlags, false, false, null, 0, 0));
  }

  private void RecordSchedMem(int start, ushort reads, ushort writes, bool readsFlags, bool writesFlags,
      bool memRead, bool memWrite, Mem mem) {
    if (!this.RecordingSched)
      return;
    // address registers are read to form the effective address
    if (mem.Base is { } b)
      reads |= RegBit(b);
    if (mem.Index is { } x)
      reads |= RegBit(x);
    // aliasing identity: a direct [label] or a [BP+disp] stack slot is a distinct cell; anything indexed is unknown
    object? memBase = mem.Index is null
      ? (mem.Label is { } l ? l : mem.Base is { } bb && bb is Reg.BP ? "BP" : (object?)null)
      : null;
    if (mem.Index is null && mem.Base is { } onlyBase && onlyBase is not Reg.BP)
      memBase = null;        // [BX]/[SI]/[DI] without a label: unknown
    // An unsized operand gets its width from the opcode. Use the largest scalar width rather than
    // inventing a narrow one that could make two overlapping accesses appear independent.
    var bytes = mem.Size == OperandSize.None ? (int)OperandSize.Tbyte : (int)mem.Size;
    (this._schedInstrs ??= []).Add(new(start, this.Position - start, reads, writes, readsFlags,
      writesFlags, memRead, memWrite, memBase, mem.Displacement, bytes));
  }

  /// <summary>
  /// Records a conditional jump: it reads the flags and clobbers nothing at all. That makes it
  /// transparent to <see cref="RunLoadForwarding"/>, which may then look across it - reaching the
  /// load in a branch's fall-through path is still reaching it from the store, because that pass
  /// separately requires no bound label in between, so nothing can enter the range from anywhere
  /// else. The scheduler never sees these: a jump carries a fixup, which excludes it from every
  /// permutation window.
  /// </summary>
  private void RecordSchedJump(int start) => this.RecordSchedReg(start, 0, 0, readsFlags: true, writesFlags: false);

  private void TrimSched(int position) => this._schedInstrs?.RemoveAll(r => r.Start >= position);

  /// <summary>
  /// Reorders contiguous, fixup-free, label-free windows of recorded instructions to group memory/ALU
  /// work, preserving every register/flags/memory dependency (the schedule is a topological order, so
  /// the program is semantically identical). Permuting whole instruction byte-blocks inside such a
  /// window needs no position fixups - the window length is unchanged and nothing inside is referenced.
  /// </summary>
  public void RunSchedule() {
    // The scheduler is the first pass that may reorder instructions, so every shrink-only rewrite
    // must have consumed and repaired its records before we inspect them. RunLoadForwarding invokes
    // the peephole too; this call covers assembler users that enable scheduling without forwarding.
    this.RunPeephole();
    if (!this.EnableSchedule || this._scheduleRan)
      return;
    this._scheduleRan = true;
    if (this._schedInstrs is not { Count: > 2 } recs)
      return;

    recs.Sort((a, b) => a.Start.CompareTo(b.Start));
    var labels = this.BoundLabelPositions();
    var fixupPositions = new HashSet<int>(this._fixups.Select(f => f.Position));

    var i = 0;
    while (i < recs.Count) {
      // grow a maximal window: byte-adjacent recorded instructions (a gap = an unrecorded instruction
      // such as a CALL/jump = a barrier), no bound label except at the window's own start, no fixup inside
      if (RangeHasFixup(fixupPositions, recs[i].Start, recs[i].Length)) {
        ++i;
        continue;
      }
      var j = i + 1;
      while (j < recs.Count
          && recs[j].Start == recs[j - 1].Start + recs[j - 1].Length
          && !labels.Contains(recs[j].Start)
          && !RangeHasFixup(fixupPositions, recs[j].Start, recs[j].Length))
        ++j;

      var count = j - i;
      if (count >= 3) {
        var window = recs.GetRange(i, count);
        var order = InlineAsmScheduler.ScheduleByDependency(count,
          (a, b) => Conflicts(window[a], window[b]), a => window[a].TouchesMemory);
        if (order != null)
          this.PermuteWindow(window, order);
      }
      i = j;
    }
  }

  private static bool RangeHasFixup(HashSet<int> fixups, int start, int length) {
    for (var p = start; p < start + length; ++p)
      if (fixups.Contains(p))
        return true;
    return false;
  }

  // moving SP changes which stack memory is safe: everything below SP belongs to whatever
  // interrupt lands next, whose pushed frame overwrites it. So no memory access may be reordered
  // across an SP update - a frame store hoisted above its own SUB SP is one timer tick from
  // being overwritten. (PUSH/POP both move SP and touch memory, so this also keeps them ordered.)
  private static readonly ushort _SPBIT = RegBit(Reg.SP);

  private static bool Conflicts(SchedInstr a, SchedInstr b) {
    if ((a.Writes & _SPBIT) != 0 && b.TouchesMemory)
      return true;
    if ((b.Writes & _SPBIT) != 0 && a.TouchesMemory)
      return true;
    if ((a.Writes & (b.Reads | b.Writes)) != 0 || (a.Reads & b.Writes) != 0)
      return true;
    if ((a.WritesFlags && (b.ReadsFlags || b.WritesFlags)) || (a.ReadsFlags && b.WritesFlags))
      return true;
    if ((a.MemWrite && (b.MemRead || b.MemWrite)) || (a.MemRead && b.MemWrite))
      return MemMayAlias(a, b);
    return false;
  }

  private static bool MemMayAlias(SchedInstr a, SchedInstr b) {
    if (a.MemBase is null || b.MemBase is null)
      return true;                                  // an indexed/unknown reference aliases everything
    if (!ReferenceEquals(a.MemBase, b.MemBase) && !a.MemBase.Equals(b.MemBase))
      return false;                                 // distinct cells (different label / stack base)
    var aStart = (long)a.MemDisp;
    var bStart = (long)b.MemDisp;
    return aStart < bStart + b.MemBytes && bStart < aStart + a.MemBytes;
  }

  /// <summary>
  /// Rewrites the bytes of a window in the scheduled order. The window has no internal labels or
  /// fixups, so positions outside it are untouched.
  /// </summary>
  private void PermuteWindow(List<SchedInstr> window, int[] order) {
    var start = window[0].Start;
    var blocks = window.Select(w => this._buffer.GetRange(w.Start, w.Length)).ToList();
    var pos = start;
    foreach (var idx in order) {
      var block = blocks[idx];
      for (var k = 0; k < block.Count; ++k)
        this._buffer[pos + k] = block[k];
      pos += block.Count;
    }
  }
}
