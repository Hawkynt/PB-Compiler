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

  // Data values take AX/CX/DX first so the scarce addressing registers stay free; an address value (one
  // used as a memory base/index) must come from BX/SI/DI - in 16-bit mode AX/CX/DX/BP/SP cannot index
  // memory (BP is reserved for the frame). BP/SP are never allocated.
  private static readonly Reg[] _pool = [Reg.AX, Reg.CX, Reg.DX, Reg.BX, Reg.SI, Reg.DI];
  private static readonly Reg[] _addressing = [Reg.BX, Reg.SI, Reg.DI];

  /// <summary>Assigns each virtual register a physical register, or returns null when the live set exceeds the register file (a spill is required).</summary>
  public static IReadOnlyDictionary<int, Reg>? Allocate(MFunction function) {
    var intervals = LivenessAnalysis.Compute(function);
    var addressVregs = AddressRegisters(function);   // vregs that ever form a memory address -> need BX/SI/DI
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

      // an address value needs an addressing-capable register; a data value takes any free one
      var slot = addressVregs.Contains(interval.VirtualId)
        ? free.FindIndex(r => System.Array.IndexOf(_addressing, r) >= 0)
        : (free.Count > 0 ? 0 : -1);
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

  /// <summary>The virtual registers that appear as a memory operand's base or index (so they must be addressing-capable).</summary>
  private static HashSet<int> AddressRegisters(MFunction function) {
    var address = new HashSet<int>();
    foreach (var instr in function.AllInstructions)
      foreach (var operand in instr.Operands)
        if (operand is MOperand.Memory mem) {
          if (mem.Base is { IsVirtual: true } b)
            address.Add(b.VirtualId);
          if (mem.Index is { IsVirtual: true } x)
            address.Add(x.VirtualId);
        }
    return address;
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
