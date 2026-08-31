using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Intra-block dead-store elimination for memory: a store is dead if a later store in
/// the same block completely overwrites its byte range before any load that may observe
/// it (and no intervening call, which could read memory). Runs after GVN/memopt so
/// addresses are canonical SSA values and uses the shared width-aware
/// <see cref="IrAliasAnalysis"/> for the two memory questions.
/// </summary>
public static class DeadStoreElim {

  public static int Run(IrFunction fn) {
    var removed = 0;
    foreach (var block in fn.Blocks) {
      var pending = new List<IrStore>();             // written but not yet observed

      foreach (var inst in block.Instructions.ToList()) {
        switch (inst) {
          case IrStore store:
            foreach (var dead in pending.ToList())
              if (IrAliasAnalysis.CompletelyOverwrites(store, dead)) {
                dead.EraseFromParent();
                pending.Remove(dead);
                ++removed;
              }
            pending.Add(store);
            break;
          case IrLoad load:
            pending.RemoveAll(store =>
              IrAliasAnalysis.MayAlias(store.Pointer, store.Value.Type, load.Pointer, load.Type));
            break;
          case IrCall:
            pending.Clear();                         // a call may read any memory
            break;
        }
      }
    }
    return removed;
  }
}
