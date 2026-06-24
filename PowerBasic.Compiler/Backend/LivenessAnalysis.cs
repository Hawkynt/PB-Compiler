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

  /// <summary>Computes one live interval per virtual register the function references.</summary>
  public static IReadOnlyList<LiveInterval> Compute(MFunction function) {
    var first = new Dictionary<int, int>();
    var last = new Dictionary<int, int>();

    var index = 0;
    foreach (var instr in function.AllInstructions) {
      var (reads, writes) = RegistersOf(instr);
      foreach (var v in reads) {
        first.TryAdd(v, index);     // a use before any def (a live-in argument) starts the interval here
        last[v] = index;
      }
      foreach (var v in writes) {
        first.TryAdd(v, index);
        last[v] = index;            // a write also extends the range (the value must survive to here)
      }

      ++index;
    }

    var intervals = new List<LiveInterval>(first.Count);
    foreach (var (v, start) in first)
      intervals.Add(new(v, start, last[v]));
    intervals.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.VirtualId.CompareTo(b.VirtualId));
    return intervals;
  }
}
