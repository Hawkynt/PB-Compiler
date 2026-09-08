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
    var removed = RemoveUnreadPrivateFrameStores(fn);
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
  private static int RemoveUnreadPrivateFrameStores(IrFunction fn) {
    var privateFrames = fn.AllInstructions
      .OfType<IrAlloca>()
      .Where(IsPrivateFrameObject)
      .ToHashSet(ReferenceEqualityComparer.Instance);
    if (privateFrames.Count == 0)
      return 0;

    var loadsByFrame = new Dictionary<IrAlloca, List<IrLoad>>(ReferenceEqualityComparer.Instance);
    foreach (var load in fn.AllInstructions.OfType<IrLoad>()) {
      var root = RootAlloca(load.Pointer);
      if (root is null || !privateFrames.Contains(root))
        continue;
      if (!loadsByFrame.TryGetValue(root, out var loads))
        loadsByFrame[root] = loads = [];
      loads.Add(load);
    }

    var removed = 0;
    foreach (var store in fn.AllInstructions.OfType<IrStore>().ToList()) {
      var root = RootAlloca(store.Pointer);
      if (root is null || !privateFrames.Contains(root))
        continue;
      if (loadsByFrame.TryGetValue(root, out var loads)
          && loads.Any(load =>
            IrAliasAnalysis.MayAlias(store.Pointer, store.Value.Type, load.Pointer, load.Type)))
        continue;
      store.EraseFromParent();
      ++removed;
    }
    return removed;
  }

  /// <summary>
  /// Source-variable storage is intentionally excluded: O0065 owns compiler-generated frame cells.
  /// For those cells the address must remain inside a load/store/GEP graph; a call argument, return,
  /// pointer store, cast, phi or any other use makes the address observable and declines the proof.
  /// </summary>
  private static bool IsPrivateFrameObject(IrAlloca alloca)
    => !alloca.IsSourceVariable
      && PointerDoesNotEscape(alloca, new HashSet<IrValue>(ReferenceEqualityComparer.Instance));

  private static bool PointerDoesNotEscape(IrValue pointer, HashSet<IrValue> seen) {
    if (!seen.Add(pointer))
      return true;

    foreach (var user in pointer.Users)
      switch (user) {
        case IrLoad load when ReferenceEquals(load.Pointer, pointer):
          break;
        case IrStore store when ReferenceEquals(store.Pointer, pointer)
            && !ReferenceEquals(store.Value, pointer):
          break;
        case IrGep gep when ReferenceEquals(gep.BasePtr, pointer)
            && PointerDoesNotEscape(gep, seen):
          break;
        default:
          return false;
      }
    return true;
  }

  private static IrAlloca? RootAlloca(IrValue pointer) {
    while (pointer is IrGep gep)
      pointer = gep.BasePtr;
    return pointer as IrAlloca;
  }
}
