using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Dead-store elimination for memory. O0065 first removes writes to unread byte ranges of
/// compiler-private, non-escaping frame objects across the whole function. O0048 then removes
/// an earlier store when a later store in the same block completely overwrites it before an
/// aliasing load or opaque call can observe it.
/// </summary>
public static class DeadStoreElim {

  public static int Run(IrFunction fn) {
    ArgumentNullException.ThrowIfNull(fn);
    return IrFunctionPassPipeline.RunStandalone(fn, "dse", Run);
  }

  /// <summary>
  /// Analysis-aware production entry. Removing stores changes memory/value facts but never CFG
  /// topology, so CFG-derived analyses remain valid while MemorySSA and dependent facts are dropped.
  /// </summary>
  internal static IrPassResult Run(IrFunction fn, IrAnalysisManager analyses) {
    ArgumentNullException.ThrowIfNull(fn);
    ArgumentNullException.ThrowIfNull(analyses);
    if (!ReferenceEquals(fn, analyses.Function))
      throw new ArgumentException("Analysis manager belongs to a different function.", nameof(analyses));
    var identities = analyses.Get(IrAnalyses.PointerIdentity);
    var escape = analyses.Get(IrAnalyses.PointerEscape);
    var removed = RunCore(fn, identities, escape);
    return removed == 0
      ? IrPassResult.Unchanged
      : IrPassResult.ChangedPreservingSets(removed, IrAnalysisSets.Cfg);
  }

  private static int RunCore(
      IrFunction fn,
      IrPointerIdentityAnalysis identities,
      IrPointerEscapeAnalysis escape) {
    var removed = RemoveUnreadPrivateFrameStores(fn, identities, escape);
    foreach (var block in fn.Blocks) {
      var pending = new List<IrStore>();             // written but not yet observed

      foreach (var inst in block.Instructions.ToList()) {
        switch (inst) {
          case IrStore store:
            foreach (var dead in pending.ToList())
              if (IrAliasAnalysis.CompletelyOverwrites(store, dead, identities)) {
                dead.EraseFromParent();
                pending.Remove(dead);
                ++removed;
              }
            pending.Add(store);
            break;
          case IrLoad load:
            pending.RemoveAll(store =>
              IrAliasAnalysis.MayAlias(
                store.Pointer, store.Value.Type, load.Pointer, load.Type, identities));
            break;
          case IrCall:
            pending.Clear();                         // a call may read any memory visible to it
            break;
        }
      }
    }
    return removed;
  }

  /// <summary>
  /// O0065: a compiler-private alloca whose address never escapes can only be observed through
  /// loads derived from that alloca. Therefore a store to a byte range that no such load may
  /// alias is dead on every control-flow path, even when branches, loops or unrelated calls
  /// separate it from function exit.
  /// </summary>
  private static int RemoveUnreadPrivateFrameStores(
      IrFunction fn,
      IrPointerIdentityAnalysis identities,
      IrPointerEscapeAnalysis escape) {
    var privateFrames = fn.AllInstructions
      .OfType<IrAlloca>()
      .Where(alloca => !alloca.IsSourceVariable && escape.DoesNotEscape(alloca))
      .ToHashSet(ReferenceEqualityComparer.Instance);
    if (privateFrames.Count == 0)
      return 0;

    var loadsByFrame = new Dictionary<IrAlloca, List<IrLoad>>(ReferenceEqualityComparer.Instance);
    foreach (var load in fn.AllInstructions.OfType<IrLoad>()) {
      var root = identities.TryResolve(load.Pointer)?.Root as IrAlloca;
      if (root is null || !privateFrames.Contains(root))
        continue;
      if (!loadsByFrame.TryGetValue(root, out var loads))
        loadsByFrame[root] = loads = [];
      loads.Add(load);
    }

    var removed = 0;
    foreach (var store in fn.AllInstructions.OfType<IrStore>().ToList()) {
      var root = identities.TryResolve(store.Pointer)?.Root as IrAlloca;
      if (root is null || !privateFrames.Contains(root))
        continue;
      if (loadsByFrame.TryGetValue(root, out var loads)
          && loads.Any(load =>
            IrAliasAnalysis.MayAlias(
              store.Pointer, store.Value.Type, load.Pointer, load.Type, identities)))
        continue;
      store.EraseFromParent();
      ++removed;
    }
    return removed;
  }


}
