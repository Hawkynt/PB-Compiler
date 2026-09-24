using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0161 — per-procedure mod/ref summaries, computed once over the call graph so every other pass can
/// ask "does calling this touch memory?" instead of assuming the worst.
///
/// <para>
/// A function-local pass cannot in general see through an internal call, so this module analysis
/// propagates callee facts through the call graph. That is what allows an unused call to a small BASIC
/// FUNCTION to disappear without pretending every call is harmless.
/// </para>
/// <para>
/// The summary is deliberately coarse: memory reads, memory definitions and whether an unused call
/// may be discarded. Those are derived from the shared operation-effect contract and propagated to
/// callers by fixpoint. Anything it cannot see through remains maximally conservative: an unmodeled
/// external declaration, an indirect call, an armed error handler or inline assembly. The checked
/// exception is the
/// checked effect contract in <see cref="IrEffects.ForExternalCall"/> - externals whose semantics are
/// known rather than guessed - which is also what lets <see cref="Gvn"/> number a call and
/// <see cref="Licm"/> hoist one.
/// </para>
/// <para>
/// Recursion is why the fixpoint starts from "pure" and only ever adds impurity. Starting from
/// "impure" and removing would need a proof about the cycle before entering it; starting clean and
/// propagating outward reaches the same answer and terminates, because each round can only ever set
/// bits.
/// </para>
/// </summary>
public sealed class FunctionSummaries {

  /// <summary>What calling a function may do to memory, plus whether an unused call may disappear.</summary>
  public readonly record struct Summary(bool ReadsMemory, bool WritesMemory, bool CanDiscard) {
    /// <summary>True when the body neither reads nor writes modeled memory.</summary>
    public bool IsPure => !this.ReadsMemory && !this.WritesMemory;
  }

  /// <summary>
  /// Whether a call to this EXTERNAL has no modeled observable effects and is deterministic, so equal calls may be
  /// merged. Compatibility wrapper for existing callers; the semantic source of truth is <see cref="IrEffects"/>.
  /// </summary>
  public static bool IsPureExternal(string name)
    => IrEffects.ForExternalCall(name).CanCse;

  /// <summary>
  /// Whether a call to this EXTERNAL may additionally execute on a path the source program did not take.
  /// Compatibility wrapper for existing callers; the semantic source of truth is <see cref="IrEffects"/>.
  /// </summary>
  public static bool IsSpeculatableExternal(string name)
    => IrEffects.ForExternalCall(name).CanSpeculate;

  private readonly Dictionary<IrFunction, Summary> _summaries = new(ReferenceEqualityComparer.Instance);

  /// <summary>The summary for a function - maximally impure for anything not in the module.</summary>
  public Summary For(IrFunction function)
    => this._summaries.TryGetValue(function, out var summary)
      ? summary
      : FromEffects(IrEffectSummary.UnknownExternal);

  /// <summary>Computes summaries for every function in <paramref name="module"/>.</summary>
  public static FunctionSummaries Compute(IrModule module) {
    var result = new FunctionSummaries();
    foreach (var function in module.Functions) {
      result._summaries[function] = function.IsDeclaration
        ? FromEffects(IrEffects.ForCall(function))
        : function.HasErrorHandler || function.HasInlineAsm
          ? FromEffects(IrEffectSummary.UnknownExternal)
          : new(false, false, true);             // optimistic: only ever made worse below
    }

    for (var changed = true; changed;) {
      changed = false;
      foreach (var function in module.Functions) {
        if (function.IsDeclaration)
          continue;
        var current = result._summaries[function];
        if (current is { ReadsMemory: true, WritesMemory: true, CanDiscard: false })
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

  private static Summary Merge(Summary current, IrInstruction instruction, FunctionSummaries known) {
    // A defined direct callee contributes the fixpoint fact for its body. Declarations and indirect
    // calls use the same operation contract as every other consumer.
    if (instruction is IrCall call && call.Callee is IrFunction callee && !callee.IsDeclaration)
      return Union(current, known.For(callee));
    return Union(current, FromEffects(IrEffects.ForInstruction(instruction)));
  }

  private static Summary FromEffects(IrEffectSummary effects)
    => new(effects.MayReadMemory, effects.DefinesMemory, effects.CanDiscard);

  private static Summary Union(Summary a, Summary b)
    => new(
      a.ReadsMemory || b.ReadsMemory,
      a.WritesMemory || b.WritesMemory,
      a.CanDiscard && b.CanDiscard);

  /// <summary>
  /// Removes calls whose result nothing uses and whose callee has no non-discardable effects. Returns how many went.
  ///
  /// This is the first consumer of the summaries and the reason they exist: a BASIC FUNCTION that only
  /// computes is exactly the shape the optimizer leaves behind after propagating its result away, and
  /// without a summary there is no way to tell it from one that prints.
  ///
  /// <para>
  /// A <c>NOINLINE</c> callee is exempt. Dropping the call is SOUND - nothing observable changes - but
  /// the modifier's contract is that the call survives, and the shape it most often guards is a
  /// procedure that exists only to be a barrier: an empty <c>SUB</c> taking a variable BYREF so the
  /// optimizer cannot know its value. Removing that call removes the barrier, and everything the
  /// programmer wanted to inspect folds away behind it.
  /// </para>
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
            && !callee.NoInline
            && summaries.For(callee).CanDiscard) {
          call.EraseFromParent();
          ++removed;
        }
    }
    return removed;
  }
}
