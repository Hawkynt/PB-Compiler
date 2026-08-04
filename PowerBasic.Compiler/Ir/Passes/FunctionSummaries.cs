namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0161 — per-procedure mod/ref summaries, computed once over the call graph so every other pass can
/// ask "does calling this touch memory?" instead of assuming the worst.
///
/// <para>
/// Today a call is a wall: <see cref="Dce"/> treats every call as side-effecting, so a call whose
/// result nothing uses survives even when the callee does nothing but arithmetic. That is the right
/// default and the wrong answer for the many small BASIC FUNCTIONs that only compute.
/// </para>
/// <para>
/// The summary is deliberately coarse — two bits, reads and writes — because that is what the
/// consumers actually need and because a coarse fact computed correctly beats a precise one computed
/// optimistically. It is a fixpoint over the call graph: a function writes memory if it stores, or
/// calls something that writes. Anything it cannot see through makes it maximally impure: an external
/// declaration, an indirect call, an armed error handler, inline assembly.
/// </para>
/// <para>
/// Recursion is why the fixpoint starts from "pure" and only ever adds impurity. Starting from
/// "impure" and removing would need a proof about the cycle before entering it; starting clean and
/// propagating outward reaches the same answer and terminates, because each round can only ever set
/// bits.
/// </para>
/// </summary>
public sealed class FunctionSummaries {

  /// <summary>What calling a function may do to memory.</summary>
  public readonly record struct Summary(bool ReadsMemory, bool WritesMemory) {
    /// <summary>True when the call can be removed if its result is unused.</summary>
    public bool IsPure => !this.WritesMemory;
  }

  private readonly Dictionary<IrFunction, Summary> _summaries = new(ReferenceEqualityComparer.Instance);

  /// <summary>The summary for a function - maximally impure for anything not in the module.</summary>
  public Summary For(IrFunction function)
    => this._summaries.TryGetValue(function, out var summary) ? summary : new(true, true);

  /// <summary>Computes summaries for every function in <paramref name="module"/>.</summary>
  public static FunctionSummaries Compute(IrModule module) {
    var result = new FunctionSummaries();
    foreach (var function in module.Functions)
      result._summaries[function] = function.IsDeclaration || function.HasErrorHandler || function.HasInlineAsm
        ? new(true, true)                        // nothing here can be seen through
        : new(false, false);                     // optimistic: only ever made worse below

    for (var changed = true; changed;) {
      changed = false;
      foreach (var function in module.Functions) {
        if (function.IsDeclaration)
          continue;
        var current = result._summaries[function];
        if (current is { ReadsMemory: true, WritesMemory: true })
          continue;

        var merged = current;
        foreach (var instruction in function.AllInstructions)
          merged = Merge(merged, instruction, result);
        if (merged == current)
          continue;
        result._summaries[function] = merged;
        changed = true;
      }
    }
    return result;
  }

  private static Summary Merge(Summary current, IrInstruction instruction, FunctionSummaries known) => instruction switch {
    IrLoad => current with { ReadsMemory = true },
    IrStore => current with { WritesMemory = true },
    IrInlineAsm => new(true, true),
    // An indirect call could be anything. A direct one contributes its callee's summary, which for a
    // recursive cycle is whatever has been established so far - correct because the fixpoint only ever
    // adds, so a cycle settles at the union of everything reachable round it.
    IrCall call => call.Callee is IrFunction callee
      ? Union(current, known.For(callee))
      : new(true, true),
    _ => current,
  };

  private static Summary Union(Summary a, Summary b)
    => new(a.ReadsMemory || b.ReadsMemory, a.WritesMemory || b.WritesMemory);

  /// <summary>
  /// Removes calls whose result nothing uses and whose callee writes no memory. Returns how many went.
  ///
  /// This is the first consumer of the summaries and the reason they exist: a BASIC FUNCTION that only
  /// computes is exactly the shape the optimizer leaves behind after propagating its result away, and
  /// without a summary there is no way to tell it from one that prints.
  /// </summary>
  public static int RemoveDeadPureCalls(IrModule module) {
    var summaries = Compute(module);
    var removed = 0;
    foreach (var function in module.Functions) {
      if (function.IsDeclaration || function.HasErrorHandler || function.HasInlineAsm)
        continue;
      foreach (var instruction in function.AllInstructions.ToList())
        if (instruction is IrCall { Callee: IrFunction callee } call
            && call.HasNoUsers
            && summaries.For(callee).IsPure) {
          call.EraseFromParent();
          ++removed;
        }
    }
    return removed;
  }
}
