namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Intra-block dead-store elimination for memory: a store is dead if a later store in
/// the same block writes the same address before any load that may observe it (and no
/// intervening call, which could read memory). Runs after GVN/memopt so addresses are
/// canonical SSA values; uses the same sound alias test - a load only keeps a pending
/// store alive if it may alias it, and the overwriting store must definitely alias
/// (same SSA pointer, i.e. same base and offset) to kill it.
/// </summary>
public static class DeadStoreElim {

  public static int Run(IrFunction fn) {
    var removed = 0;
    foreach (var block in fn.Blocks) {
      var pending = new Dictionary<IrValue, IrStore>(ReferenceEqualityComparer.Instance);  // written but not yet observed

      foreach (var inst in block.Instructions.ToList()) {
        switch (inst) {
          case IrStore store: {
            var p = store.Pointer;
            if (pending.TryGetValue(p, out var dead)) {     // same address overwritten, never read -> dead
              dead.EraseFromParent();
              ++removed;
            }
            pending[p] = store;
            break;
          }
          case IrLoad load: {
            var p = load.Pointer;
            foreach (var key in pending.Keys.ToList())       // a may-aliasing load observes the store -> keep it
              if (MayAlias(key, p))
                pending.Remove(key);
            break;
          }
          case IrCall:
            pending.Clear();                                 // a call may read any memory
            break;
        }
      }
    }
    return removed;
  }

  private static bool MayAlias(IrValue a, IrValue b) {
    if (ReferenceEquals(a, b))
      return true;
    var (baseA, offA) = Decompose(a);
    var (baseB, offB) = Decompose(b);
    if (ReferenceEquals(baseA, baseB))
      return !(offA.HasValue && offB.HasValue) || offA == offB;
    return baseA is not IrAlloca || baseB is not IrAlloca;
  }

  private static (IrValue Base, long? ConstOffset) Decompose(IrValue p) =>
    p is IrGep g ? (g.BasePtr, g.ByteOffset is IrConstantInt c ? c.Value : null) : (p, 0L);
}
