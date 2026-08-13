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

  /// <summary>
  /// The intervals plus the set of values live AT each instruction index - the same marks the
  /// intervals are the hull of, kept instead of only their minimum and maximum.
  ///
  /// <para>
  /// The hull is what linear scan needs to decide overlap, and it is deliberately conservative: two
  /// values whose hulls meet get distinct registers whether or not they are ever live together. But
  /// the allocator asks a second, different question - "is a register a CALL destroys occupied by
  /// this value at that point" - and answering that from the hull is conservative in a way that costs
  /// real code. A loop-carried value whose loop EXIT block is laid out between the head and the latch
  /// (which is how <c>IrLowering</c> orders a FOR) has a hull covering the exit's <c>PRINT</c> call,
  /// though it is dead there: it was read into <c>AX</c> one instruction earlier and the loop it came
  /// from is over. The hull says the counter cannot live in a register; the marks say it can.
  /// </para>
  ///
  /// <para>
  /// Reading the marks is sound for that question and only that one. A value dead at <c>i</c> has no
  /// path from <c>i</c> to a use of it without an intervening definition, so nothing that instruction
  /// destroys is ever read back - while the hull still governs which values may share a register.
  /// </para>
  /// </summary>
  /// <param name="LiveAfter">
  /// the values live immediately AFTER each instruction - what a definition has to be checked against,
  /// since a value that dies at an instruction is not harmed by what that instruction writes.
  /// </param>
  public sealed record Liveness(IReadOnlyList<LiveInterval> Intervals, IReadOnlyList<HashSet<int>> LiveAt,
    IReadOnlyList<HashSet<int>> LiveAfter);

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
        // a far operand's segment register is read the same way the base is - the emitter moves it
        // into ES in front of the access, so the value has to still be there
        if (mem.Segment is { IsVirtual: true } s)
          reads.Add(s.VirtualId);
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
  public static IReadOnlyList<LiveInterval> Compute(MFunction function) => Analyze(function).Intervals;

  /// <summary>The same walk, keeping the per-instruction marks the intervals are the hull of.</summary>
  public static Liveness Analyze(MFunction function) {
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
        // including the edges an inline-asm jump makes: `!JNZ AddLoop` closes a loop the IR never drew
        // an edge for, and a value live round it would otherwise die at its last LINEAR use
        foreach (var s in blocks[b].SuccessorsWithAsmJumps())
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
    var liveAt = new HashSet<int>[reads.Length];
    var liveAfter = new HashSet<int>[reads.Length];
    for (var i = 0; i < liveAt.Length; ++i) {
      liveAt[i] = [];
      liveAfter[i] = [];
    }
    void Mark(int v, int at) {
      if (!lo.TryGetValue(v, out var l) || at < l)
        lo[v] = at;
      if (!hi.TryGetValue(v, out var h) || at > h)
        hi[v] = at;
      if (at < liveAt.Length)     // an EMPTY block marks at its start index, which is one past the end
        liveAt[at].Add(v);
    }
    for (var b = 0; b < n; ++b) {
      var live = new HashSet<int>(liveOut[b]);
      for (var i = end[b] - 1; i >= start[b]; --i) {
        liveAfter[i].UnionWith(live);
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
    return new(intervals, liveAt, liveAfter);
  }

  /// <summary>
  /// The values that are live all the way round a loop - a counter or an accumulator, the two things
  /// worth keeping in a register for the whole of one.
  ///
  /// <para>
  /// A back edge is an edge to a block at or before its source in the layout, and a value is
  /// loop-carried when its live range covers that whole span: it is already live where the loop is
  /// re-entered and still live where control jumps back. Nothing here is a proof about the CFG's
  /// natural loops - the span is the LAID-OUT range, so an unrelated block inside it widens the test
  /// - and nothing needs to be, because the only consumer is a register PREFERENCE. Naming one value
  /// too many costs a different register; naming one too few costs the residency.
  /// </para>
  /// </summary>
  public static HashSet<int> LoopCarried(MFunction function) {
    var blocks = function.Blocks;
    var index = new Dictionary<string, int>(blocks.Count);
    for (var b = 0; b < blocks.Count; ++b)
      index[blocks[b].Label] = b;

    var start = new int[blocks.Count];
    var end = new int[blocks.Count];
    for (int b = 0, gi = 0; b < blocks.Count; ++b) {
      start[b] = gi;
      gi += blocks[b].Instructions.Count;
      end[b] = gi;
    }

    var spans = new List<(int Head, int Tail)>();
    for (var b = 0; b < blocks.Count; ++b)
      foreach (var successor in blocks[b].Successors)
        if (index.TryGetValue(successor, out var s) && s <= b && end[b] > start[s])
          spans.Add((start[s], end[b] - 1));
    var carried = new HashSet<int>();
    if (spans.Count == 0)
      return carried;

    foreach (var interval in Compute(function))
      foreach (var (head, tail) in spans)
        if (interval.Start <= head && interval.End >= tail) {
          carried.Add(interval.VirtualId);
          break;
        }
    return carried;
  }

  private static int CountInstructions(MFunction function) {
    var total = 0;
    foreach (var block in function.Blocks)
      total += block.Instructions.Count;
    return total;
  }
}
