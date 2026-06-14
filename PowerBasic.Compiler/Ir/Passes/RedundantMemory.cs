namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Intra-block load/store forwarding — the memory analogue of what mem2reg does for
/// promotable scalars, for the addresses that stay in memory (array elements, BYREF
/// targets). Within a block it forwards a load from the value most recently stored to
/// the same address, and reuses an earlier load of an unchanged address. Run after GVN
/// so congruent address computations are already one SSA value; addresses are then
/// compared by reference plus a small sound alias test (distinct allocas never alias;
/// the same base at distinct constant offsets never aliases). Any may-aliasing store or
/// any call conservatively invalidates the affected cache entries.
/// </summary>
public static class RedundantMemory {

  public static int Run(IrFunction fn) {
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
            Invalidate(stored, p);
            Invalidate(loaded, p);
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

  private static void Invalidate(Dictionary<IrValue, IrValue> cache, IrValue stored) {
    foreach (var key in cache.Keys.ToList())
      if (MayAlias(key, stored))
        cache.Remove(key);
  }

  private static bool MayAlias(IrValue a, IrValue b) {
    if (ReferenceEquals(a, b))
      return true;
    var (baseA, offA) = Decompose(a);
    var (baseB, offB) = Decompose(b);
    if (ReferenceEquals(baseA, baseB))
      return !(offA.HasValue && offB.HasValue) || offA == offB;   // same base: alias unless distinct constant offsets
    return baseA is not IrAlloca || baseB is not IrAlloca;          // distinct stack slots never alias
  }

  private static (IrValue Base, long? ConstOffset) Decompose(IrValue p) =>
    p is IrGep g ? (g.BasePtr, g.ByteOffset is IrConstantInt c ? c.Value : null) : (p, 0L);
}
