namespace PowerBasic.Compiler.Backend;

/// <summary>
/// Stage 3 of the x86-16 back end (docs/X86-BACKEND.md): live-interval analysis over a
/// <see cref="MFunction"/>. Each virtual register's interval runs from its first definition to its
/// last use across the linearized instruction stream; the linear-scan allocator (stage 4) assigns
/// physical registers by sweeping these intervals. Reads are the top-level read operands PLUS every
/// register nested in a memory operand (base/index form the effective address), so an address
/// register stays live across the access; writes are the top-level written operands.
/// </summary>
public sealed class LivenessAnalysis {

  /// <summary>A virtual register's live range over the linearized instruction indices (inclusive).</summary>
  public readonly record struct LiveInterval(int VirtualId, int Start, int End);

  /// <summary>Collects the virtual registers an instruction reads and writes (memory-operand base/index count as reads).</summary>
  public static (List<int> Reads, List<int> Writes) RegistersOf(MInstr instr) {
    var reads = new List<int>();
    var writes = new List<int>();

    foreach (var i in instr.Effect.ReadRegs)
      if (instr.Operands[i] is MOperand.Register { Reg.IsVirtual: true } r)
        reads.Add(r.Reg.VirtualId);
    foreach (var i in instr.Effect.WrittenRegs)
      if (instr.Operands[i] is MOperand.Register { Reg.IsVirtual: true } w)
        writes.Add(w.Reg.VirtualId);

    // every register that forms a memory operand's effective address is read, wherever the operand sits
    foreach (var operand in instr.Operands)
      if (operand is MOperand.Memory mem) {
        if (mem.Base is { IsVirtual: true } b)
          reads.Add(b.VirtualId);
        if (mem.Index is { IsVirtual: true } x)
          reads.Add(x.VirtualId);
      }

    return (reads, writes);
  }

  /// <summary>
  /// Computes one live interval per virtual register by backward dataflow over the control-flow graph,
  /// iterated to a fixpoint. A value live across a loop back-edge therefore keeps its whole-loop
  /// interval - a single straight scan would end the range at the last LINEAR use and let a later value
  /// wrongly reuse the register inside the loop. The interval is [min, max] over every instruction
  /// index where the value is live, so any two values live at the same point overlap (hence get
  /// distinct registers) - conservatively correct for the linear-scan allocator.
  /// </summary>
  public static IReadOnlyList<LiveInterval> Compute(MFunction function) {
    var blocks = function.Blocks;
    var n = blocks.Count;

    // a global instruction index per instruction; [start, end) range per block
    var start = new int[n];
    var end = new int[n];
    var reads = new List<int>[CountInstructions(function)];
    var writes = new List<int>[reads.Length];
    var labelToBlock = new Dictionary<string, int>();
    var gi = 0;
    for (var b = 0; b < n; ++b) {
      labelToBlock[blocks[b].Label] = b;
      start[b] = gi;
      foreach (var instr in blocks[b].Instructions) {
        (reads[gi], writes[gi]) = RegistersOf(instr);
        ++gi;
      }
      end[b] = gi;
    }

    // use[B] = read before any def in B; def[B] = written in B
    var use = new HashSet<int>[n];
    var def = new HashSet<int>[n];
    for (var b = 0; b < n; ++b) {
      use[b] = [];
      def[b] = [];
      for (var i = start[b]; i < end[b]; ++i) {
        foreach (var r in reads[i])
          if (!def[b].Contains(r))
            use[b].Add(r);
        foreach (var w in writes[i])
          def[b].Add(w);
      }
    }

    // liveOut[B] = U liveIn[successors]; liveIn[B] = use[B] U (liveOut[B] \ def[B]) - to a fixpoint
    var liveIn = new HashSet<int>[n];
    var liveOut = new HashSet<int>[n];
    for (var b = 0; b < n; ++b) {
      liveIn[b] = [];
      liveOut[b] = [];
    }
    for (var changed = true; changed;) {
      changed = false;
      for (var b = n - 1; b >= 0; --b) {
        var outSet = new HashSet<int>();
        foreach (var s in blocks[b].Successors)
          if (labelToBlock.TryGetValue(s, out var si))
            outSet.UnionWith(liveIn[si]);
        var inSet = new HashSet<int>(outSet);
        inSet.ExceptWith(def[b]);
        inSet.UnionWith(use[b]);
        if (!outSet.SetEquals(liveOut[b]) || !inSet.SetEquals(liveIn[b])) {
          liveOut[b] = outSet;
          liveIn[b] = inSet;
          changed = true;
        }
      }
    }

    // mark every instruction index where each value is live; the interval is the [min, max] of those
    var lo = new Dictionary<int, int>();
    var hi = new Dictionary<int, int>();
    void Mark(int v, int at) {
      if (!lo.TryGetValue(v, out var l) || at < l)
        lo[v] = at;
      if (!hi.TryGetValue(v, out var h) || at > h)
        hi[v] = at;
    }
    for (var b = 0; b < n; ++b) {
      var live = new HashSet<int>(liveOut[b]);
      for (var i = end[b] - 1; i >= start[b]; --i) {
        foreach (var v in live)
          Mark(v, i);                  // live after instruction i (spans it)
        foreach (var w in writes[i]) {
          Mark(w, i);
          live.Remove(w);
        }
        foreach (var r in reads[i]) {
          live.Add(r);
          Mark(r, i);
        }
      }
      foreach (var v in live)          // live entering the block (e.g. a loop-carried value at the top)
        Mark(v, start[b]);
    }

    var intervals = new List<LiveInterval>(lo.Count);
    foreach (var (v, l) in lo)
      intervals.Add(new(v, l, hi[v]));
    intervals.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.VirtualId.CompareTo(b.VirtualId));
    return intervals;
  }

  private static int CountInstructions(MFunction function) {
    var total = 0;
    foreach (var block in function.Blocks)
      total += block.Instructions.Count;
    return total;
  }
}
