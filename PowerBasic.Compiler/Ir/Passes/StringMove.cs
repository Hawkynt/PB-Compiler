namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0296 — transfers a dynamic-string handle instead of duplicating it when the source variable's
/// current value is provably dead after the assignment.
///
/// <para>
/// This pass intentionally runs before <see cref="Mem2Reg"/>. At that point a variable-to-variable
/// string assignment still has the explicit ownership shape
/// <c>load source; rt_str_dup; store target</c>, and the source slot still tells us whether its address
/// escaped. Once the source is promoted to SSA that storage proof is no longer available.
/// </para>
///
/// <para>
/// A move is legal only from a private scalar BASIC variable whose every use is a direct,
/// storage-compatible load/store. From the transfer point, every reachable path must reach the
/// source's ordinary <c>rt_str_free(load source)</c> destructor before another access to the slot.
/// The duplicate is then replaced by the original handle and the source slot is cleared to null, so
/// the already-lowered destructor remains correct and cannot double-free the transferred handle.
/// </para>
/// </summary>
public static class StringMove {

  private const string _DUP = "rt_str_dup";
  private const string _FREE = "rt_str_free";

  private readonly record struct State(IrBasicBlock Block, int StartIndex);

  /// <summary>Moves every provably dead private string value in <paramref name="fn"/>; returns the number moved.</summary>
  public static int Run(IrFunction fn) {
    ArgumentNullException.ThrowIfNull(fn);
    if (fn.Entry is null || fn.HasErrorHandler || fn.HasInlineAsm)
      return 0;

    var moved = 0;
    foreach (var duplicate in fn.AllInstructions.OfType<IrCall>().ToList())
      if (TryMove(duplicate))
        ++moved;
    return moved;
  }

  private static bool TryMove(IrCall duplicate) {
    if (duplicate.Parent is not { } block
        || duplicate.Callee is not IrFunction { Name: _DUP }
        || duplicate.ArgCount != 1
        || duplicate.GetOperand(1) is not IrLoad sourceRead
        || sourceRead.Pointer is not IrAlloca sourceSlot
        || !IsPrivateStringSlot(sourceSlot)
        || sourceRead.Users.Count != 1
        || !ReferenceEquals(sourceRead.Users[0], duplicate)
        || duplicate.Users.Count != 1
        || duplicate.Users[0] is not IrStore transfer
        || !ReferenceEquals(transfer.Value, duplicate)
        || ReferenceEquals(transfer.Pointer, sourceSlot)
        || !ReferenceEquals(sourceRead.Parent, block)
        || !ReferenceEquals(transfer.Parent, block))
      return false;

    var readAt = IndexIn(block, sourceRead);
    var duplicateAt = IndexIn(block, duplicate);
    var transferAt = IndexIn(block, transfer);
    if (readAt < 0 || duplicateAt <= readAt || transferAt <= duplicateAt)
      return false;

    var start = new State(block, transferAt + 1);
    if (!DiesOnEveryPath(start, sourceSlot, [], []))
      return false;

    transfer.SetOperand(0, sourceRead);
    duplicate.EraseFromParent();

    // Ownership has left the source variable. Keep the existing cleanup/reassignment structure intact:
    // its next destructor now observes null, while the target owns the original handle.
    var movedTransferAt = IndexIn(block, transfer);
    block.InsertAt(movedTransferAt + 1, new IrStore(new IrNullPtr(sourceSlot.Allocated), sourceSlot));
    return true;
  }

  /// <summary>
  /// Mirrors the promotability/escape boundary used by Mem2Reg: no GEP, BYREF call, differently typed
  /// view, array shape, or other address escape may exist for a source we empty after the move.
  /// </summary>
  private static bool IsPrivateStringSlot(IrAlloca slot) {
    if (!slot.IsSourceVariable || slot.Count != 1 || !slot.Allocated.IsPointer)
      return false;

    foreach (var user in slot.Users)
      switch (user) {
        case IrLoad load when ReferenceEquals(load.Pointer, slot) && load.Type.SameStorage(slot.Allocated):
          break;
        case IrStore store when ReferenceEquals(store.Pointer, slot)
          && !ReferenceEquals(store.Value, slot)
          && store.Value.Type.SameStorage(slot.Allocated):
          break;
        default:
          return false;
      }
    return true;
  }

  /// <summary>
  /// Proves that the current handle reaches its compiler-generated destructor before another source
  /// access on every path. Re-entering the same live state is a loop with no proven lifetime end and
  /// is conservatively rejected.
  /// </summary>
  private static bool DiesOnEveryPath(State state, IrAlloca sourceSlot,
      HashSet<State> visiting, HashSet<State> proven) {
    if (proven.Contains(state))
      return true;
    if (!visiting.Add(state))
      return false;

    var block = state.Block;
    for (var i = state.StartIndex; i < block.Instructions.Count; ++i) {
      switch (block.Instructions[i]) {
        case IrLoad load when ReferenceEquals(load.Pointer, sourceSlot):
          visiting.Remove(state);
          if (!IsTerminalDestructorLoad(load, sourceSlot))
            return false;
          proven.Add(state);
          return true;
        case IrStore store when ReferenceEquals(store.Pointer, sourceSlot):
          visiting.Remove(state);
          return false;                              // valid string reassignment frees the old value first
      }
    }

    var successors = block.Successors.ToList();
    if (successors.Count == 0) {
      visiting.Remove(state);
      return false;                                  // a normal lifetime may not silently disappear at an exit
    }

    foreach (var successor in successors)
      if (!DiesOnEveryPath(new State(successor, 0), sourceSlot, visiting, proven)) {
        visiting.Remove(state);
        return false;
      }

    visiting.Remove(state);
    proven.Add(state);
    return true;
  }

  /// <summary>
  /// A destructor load is terminal only when its one use is the matching free in the same block and
  /// no source access occurs between load and free. This avoids treating a merely-preloaded handle as
  /// the end of the source lifetime.
  /// </summary>
  private static bool IsTerminalDestructorLoad(IrLoad load, IrAlloca sourceSlot) {
    if (load.Users.Count != 1
        || load.Users[0] is not IrCall { Callee: IrFunction { Name: _FREE } } free
        || free.ArgCount != 1
        || !ReferenceEquals(free.GetOperand(1), load)
        || !ReferenceEquals(free.Parent, load.Parent)
        || load.Parent is not { } block)
      return false;

    var loadAt = IndexIn(block, load);
    var freeAt = IndexIn(block, free);
    if (loadAt < 0 || freeAt <= loadAt)
      return false;

    for (var i = loadAt + 1; i < freeAt; ++i)
      switch (block.Instructions[i]) {
        case IrLoad otherLoad when ReferenceEquals(otherLoad.Pointer, sourceSlot):
        case IrStore otherStore when ReferenceEquals(otherStore.Pointer, sourceSlot):
          return false;
      }
    return true;
  }

  private static int IndexIn(IrBasicBlock block, IrInstruction instruction) {
    for (var i = 0; i < block.Instructions.Count; ++i)
      if (ReferenceEquals(block.Instructions[i], instruction))
        return i;
    return -1;
  }
}
