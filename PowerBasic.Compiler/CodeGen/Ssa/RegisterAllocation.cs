using PowerBasic.Compiler.Semantics;

namespace PowerBasic.Compiler.CodeGen.Ssa;

/// <summary>The callee-stable index registers an 8086 allocation may use (SI/DI are the only GP registers our internal ABI preserves across the evaluation scratch).</summary>
public enum AllocReg { Si, Di }

/// <summary>
/// A graph-coloring register allocator over the <see cref="ScalarLiveness"/>
/// interference graph (docs/PB36.md O5, "generalize beyond the FOR-loop shape").
/// It colors the eligible hot scalars onto a small bank of callee-stable registers
/// (SI/DI on 8086) so that no two interfering variables share a register and no
/// register-resident variable is live across a call. A variable that cannot be
/// colored (its interference degree saturates the bank, or it crosses a call, or
/// it is not a 16-bit integer) is left in memory ("spilled").
///
/// ANALYSIS ONLY: this computes an assignment; it does not itself emit code. A
/// later emitter increment consumes <see cref="Assignment"/> to keep the chosen
/// variables in registers across their live ranges, exactly as the existing
/// FOR/DO residency paths do for the loop accumulator - the difference is that the
/// candidate and the register are chosen globally from the interference graph
/// rather than by the per-loop "first eligible" heuristic.
/// </summary>
public sealed class RegisterAllocation {
  private RegisterAllocation(IReadOnlyDictionary<VariableSymbol, AllocReg> assignment, IReadOnlyCollection<VariableSymbol> spilled) {
    this.Assignment = assignment;
    this.Spilled = spilled;
  }

  /// <summary>The chosen register for each colored variable.</summary>
  public IReadOnlyDictionary<VariableSymbol, AllocReg> Assignment { get; }

  /// <summary>Eligible variables that could not be colored (stay in memory).</summary>
  public IReadOnlyCollection<VariableSymbol> Spilled { get; }

  /// <summary>The register holding <paramref name="v"/>, or null when it stays in memory.</summary>
  public AllocReg? RegisterOf(VariableSymbol v) => this.Assignment.TryGetValue(v, out var r) ? r : null;

  private static readonly AllocReg[] Bank = [AllocReg.Si, AllocReg.Di];

  /// <summary>
  /// Colors the eligible 16-bit-integer, call-free scalars of <paramref name="live"/>
  /// onto <paramref name="bank"/> (default SI/DI). Greedy by descending interference
  /// degree (the classic "most-constrained first" order), so the busiest variables
  /// win a register and only the loosely-connected ones spill.
  /// </summary>
  public static RegisterAllocation Compute(ScalarLiveness live, IReadOnlyList<AllocReg>? bank = null) {
    bank ??= Bank;

    var candidates = live.Variables
      .Where(v => !live.CrossesCall(v) && IsRegisterShape(v))
      // most-constrained-first: color the highest-degree variables while the bank is still free
      .OrderByDescending(v => live.Variables.Count(o => live.Interferes(v, o)))
      .ThenBy(v => v.Name, StringComparer.Ordinal) // deterministic tie-break (no reliance on hash order)
      .ToList();

    var assignment = new Dictionary<VariableSymbol, AllocReg>(ReferenceEqualityComparer.Instance);
    var spilled = new List<VariableSymbol>();
    foreach (var v in candidates) {
      var taken = new HashSet<AllocReg>();
      foreach (var (other, reg) in assignment)
        if (live.Interferes(v, other))
          taken.Add(reg);

      AllocReg? choice = null;
      foreach (var r in bank)
        if (!taken.Contains(r)) {
          choice = r;
          break;
        }
      if (choice is { } chosen)
        assignment[v] = chosen;
      else
        spilled.Add(v); // every register in the bank is held by an interfering neighbour
    }

    return new(assignment, spilled);
  }

  /// <summary>A 2-byte (16-bit) signed-or-unsigned integer scalar fits a whole index register on the 8086.</summary>
  private static bool IsRegisterShape(VariableSymbol v) =>
    v.Type is ScalarType { IsFloat: false, ByteSize: 2 };
}
