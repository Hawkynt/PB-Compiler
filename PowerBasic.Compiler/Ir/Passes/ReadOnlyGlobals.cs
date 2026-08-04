namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0165 — read-only global propagation. A module-level variable that nothing ever writes is a
/// constant, whatever it was declared as, and every read of it folds to that constant.
///
/// <para>
/// The constant is <b>zero</b>, because that is what PowerBASIC guarantees an uninitialized variable
/// holds — the same rule <see cref="Mem2Reg"/> relies on when a promoted slot has no reaching store.
/// A global carrying initializer bytes is left alone: those are string literals and data blobs, which
/// are reached through the runtime rather than through a load, and a byte array is not a value this
/// pass can name.
/// </para>
/// <para>
/// It sounds like it would never fire, and it fires because DOS-era BASIC uses <c>DIM SHARED</c> where
/// a modern program would use <c>CONST</c>. A flag that was configurable once, is read in three
/// places, and is never assigned reads as zero every time — and once the loads are constants, SCCP
/// folds the tests built on them and the arms they guard become dead.
/// </para>
/// <para>
/// The precondition is that every use is a plain load. A global whose address is passed to a call, or
/// stored anywhere, or indexed into, is not analysed: the callee could write through it, and then
/// "nothing ever writes it" is a statement about the stores this pass can see rather than about the
/// program. Deleting the now-unused global is <see cref="GlobalDce"/>'s job, not this one's.
/// </para>
/// </summary>
public static class ReadOnlyGlobals {

  /// <summary>Folds what it can across <paramref name="module"/>; returns how many loads it replaced.</summary>
  public static int Run(IrModule module) {
    var replaced = 0;
    foreach (var global in module.Globals) {
      if (global.Bytes is not null || !global.IsZeroInitialized || global.Count != 1)
        continue;                                  // an initialized blob or an array is not one value
      if (!ReadOnly(global, out var loads))
        continue;

      foreach (var load in loads) {
        if (ZeroOf(load.Type) is not { } zero)
          continue;                                // a pointer-typed global has no constant to be
        load.ReplaceAllUsesWith(zero);
        load.EraseFromParent();
        ++replaced;
      }
    }
    return replaced;
  }

  /// <summary>
  /// Whether every use of the global is a load, collecting them. Anything else — a store through it,
  /// a GEP, the address handed to a call — means this pass cannot see all the writes, so it declines.
  /// </summary>
  private static bool ReadOnly(IrGlobalVariable global, out List<IrLoad> loads) {
    loads = [];
    foreach (var user in global.Users) {
      // a function whose body armed an error handler is not analysed at all: a fault can enter it at
      // a point the CFG does not show, and a store on that path is a store this pass would miss
      if (user.Parent?.Parent is { HasErrorHandler: true })
        return false;
      if (user is not IrLoad load || !ReferenceEquals(load.Pointer, global))
        return false;
      loads.Add(load);
    }
    return loads.Count > 0;
  }

  /// <summary>The zero of a loadable type, or null when the type has no constant form here.</summary>
  private static IrValue? ZeroOf(IrType type) => type.Kind switch {
    IrTypeKind.Int => new IrConstantInt(type, 0),
    IrTypeKind.Float => new IrConstantFloat(type, 0),
    _ => null,
  };
}
