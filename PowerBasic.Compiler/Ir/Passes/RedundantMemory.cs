using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Intra-block load/store forwarding — the memory analogue of what mem2reg does for
/// promotable scalars, for the addresses that stay in memory (array elements, BYREF
/// targets). Within a block it forwards a load from the value most recently stored to
/// the same address, and reuses an earlier load of an unchanged address. Run after GVN
/// so congruent address computations are already one SSA value; intervening writes are
/// disambiguated by the shared width-aware <see cref="IrAliasAnalysis"/>. Any may-aliasing
/// store or any call conservatively invalidates the affected cache entries.
/// </summary>
public static class RedundantMemory {

  public static int Run(IrFunction fn) {
    ArgumentNullException.ThrowIfNull(fn);
    return IrFunctionPassPipeline.RunStandalone(fn, "memopt", Run);
  }

  /// <summary>
  /// Analysis-aware production entry. Forwarding/removing loads changes value and memory facts but
  /// not CFG topology, so only CFG-derived analyses survive a successful rewrite.
  /// </summary>
  internal static IrPassResult Run(IrFunction fn, IrAnalysisManager analyses) {
    ArgumentNullException.ThrowIfNull(fn);
    ArgumentNullException.ThrowIfNull(analyses);
    if (!ReferenceEquals(fn, analyses.Function))
      throw new ArgumentException("Analysis manager belongs to a different function.", nameof(analyses));
    var removed = RunCore(fn, analyses.Get(IrAnalyses.PointerIdentity));
    return removed == 0
      ? IrPassResult.Unchanged
      : IrPassResult.ChangedPreservingSets(removed, IrAnalysisSets.Cfg);
  }

  private static int RunCore(IrFunction fn, IrPointerIdentityAnalysis identities) {
    var removed = 0;
    foreach (var block in fn.Blocks) {
      var stored = new Dictionary<IrValue, IrValue>(ReferenceEqualityComparer.Instance);   // *ptr currently holds
      var loaded = new Dictionary<IrValue, IrValue>(ReferenceEqualityComparer.Instance);   // a value already read from *ptr

      foreach (var inst in block.Instructions.ToList()) {
        switch (inst) {
          case IrLoad load: {
            var p = load.Pointer;
            if (stored.TryGetValue(p, out var sv) && sv.Type.Equals(load.Type)) {
              load.ReplaceAllUsesWith(sv);
              load.EraseFromParent();
              ++removed;
            } else if (loaded.TryGetValue(p, out var lv) && lv.Type.Equals(load.Type)) {
              load.ReplaceAllUsesWith(lv);
              load.EraseFromParent();
              ++removed;
            } else {
              loaded[p] = load;
            }
            break;
          }
          case IrStore store: {
            var p = store.Pointer;
            Invalidate(stored, p, store.Value.Type, identities);
            Invalidate(loaded, p, store.Value.Type, identities);
            stored[p] = store.Value;
            break;
          }
          case IrCall:
            stored.Clear();
            loaded.Clear();
            break;
        }
      }
    }
    return removed;
  }

  private static void Invalidate(
      Dictionary<IrValue, IrValue> cache,
      IrValue writtenPointer,
      IrType writtenType,
      IrPointerIdentityAnalysis identities) {
    foreach (var key in cache.Keys.ToList())
      if (IrAliasAnalysis.MayAlias(
            key, cache[key].Type, writtenPointer, writtenType, identities))
        cache.Remove(key);
  }
}
