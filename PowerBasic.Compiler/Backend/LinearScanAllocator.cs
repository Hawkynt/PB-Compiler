using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Backend;

/// <summary>
/// Stage 4 of the x86-16 back end (docs/X86-BACKEND.md): linear-scan register allocation. It sweeps
/// the live intervals (stage 3) in start order, handing each virtual register a free physical
/// register from <c>AX BX CX DX SI DI</c> (BP/SP are the frame, so reserved) and freeing it again when
/// the interval ends. Two intervals that overlap in time therefore get distinct registers, while
/// disjoint intervals reuse one - this is where independent values land in independent registers, the
/// reassignment the byte-level scheduler could never do. When more values are simultaneously live than
/// there are registers it returns null (a spill is needed); spilling to the frame is the next increment.
/// </summary>
public sealed class LinearScanAllocator {

  /// <summary>The allocatable physical registers, preferred in this order (BP/SP are reserved for the frame).</summary>
  private static readonly Reg[] _pool = [Reg.AX, Reg.BX, Reg.CX, Reg.DX, Reg.SI, Reg.DI];

  /// <summary>Assigns each virtual register a physical register, or returns null when the live set exceeds the register file (a spill is required).</summary>
  public static IReadOnlyDictionary<int, Reg>? Allocate(MFunction function) {
    var intervals = LivenessAnalysis.Compute(function);
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

      if (free.Count == 0)
        return null;                                 // register pressure exceeds the file - spill needed

      var reg = free[0];
      free.RemoveAt(0);
      assignment[interval.VirtualId] = reg;
      active.Add(interval);
      active.Sort((x, y) => x.End.CompareTo(y.End));
    }

    return assignment;
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
