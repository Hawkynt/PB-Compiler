namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0278 — global variable localization. A <c>DIM SHARED</c> that only one procedure ever touches is
/// not really global; turning it into an alloca hands it to <see cref="Mem2Reg"/>, and from there to
/// every value pass that stops at globals today.
///
/// <para>
/// The condition that makes this legal is the one worth stating, because "only one function uses it"
/// is <b>not</b> sufficient on its own. A global keeps its value between calls; a local does not. So
/// localizing is only sound when the value on entry is <b>dead</b> — when the procedure always writes
/// before it reads. This pass requires a store in the entry block with no load of the same global
/// before it, which makes that store dominate every load in the function: whatever a previous call
/// left behind cannot be observed, so there is nothing for the local to fail to remember.
/// </para>
/// <para>
/// The rest is the usual escape check. Every use must be a direct load or store — an address handed
/// to a call, stored anywhere, or indexed into means the users are not enumerable and the "only one
/// function" claim is about the ones this pass can see rather than about the program.
/// </para>
/// </summary>
public static class LocalizeGlobals {

  /// <summary>Localizes what it can in <paramref name="module"/>; returns how many globals moved.</summary>
  public static int Run(IrModule module) {
    var moved = 0;
    foreach (var global in module.Globals.ToList()) {
      if (global.Bytes is not null || global.Count != 1 || global.HasNoUsers)
        continue;                                  // a blob or an array is not one scalar
      if (SoleUser(global) is not { } fn || fn.HasErrorHandler || fn.HasInlineAsm)
        continue;
      if (fn.Entry is null || !WritesBeforeReading(global, fn.Entry))
        continue;

      Localize(module, global, fn);
      ++moved;
    }
    return moved;
  }

  /// <summary>
  /// The one function that touches this global, or null when more than one does, when a use is not a
  /// plain load or store, or when a user has been detached from the module.
  /// </summary>
  private static IrFunction? SoleUser(IrGlobalVariable global) {
    IrFunction? only = null;
    foreach (var user in global.Users) {
      if (user is not (IrLoad or IrStore) || !AddressesOnly(user, global))
        return null;
      var owner = user.Parent?.Parent;
      if (owner is null || (only is not null && !ReferenceEquals(only, owner)))
        return null;
      only = owner;
    }
    return only;
  }

  /// <summary>True when the instruction uses the global as an ADDRESS, never as a value being stored.</summary>
  private static bool AddressesOnly(IrInstruction instruction, IrValue global) => instruction switch {
    IrLoad load => ReferenceEquals(load.Pointer, global),
    IrStore store => ReferenceEquals(store.Pointer, global) && !ReferenceEquals(store.Value, global),
    _ => false,
  };

  /// <summary>
  /// True when the entry block stores to the global before any load of it — which is what makes the
  /// value a previous call left behind unobservable, and so makes a local an exact replacement.
  /// </summary>
  private static bool WritesBeforeReading(IrGlobalVariable global, IrBasicBlock entry) {
    foreach (var instruction in entry.Instructions) {
      if (instruction is IrLoad load && ReferenceEquals(load.Pointer, global))
        return false;                              // it reads what the last call left
      if (instruction is IrStore store && ReferenceEquals(store.Pointer, global))
        return true;
    }
    return false;
  }

  private static void Localize(IrModule module, IrGlobalVariable global, IrFunction fn) {
    var alloca = fn.Entry!.InsertAt(0, new IrAlloca(global.ValueType) { Name = global.Name });
    global.ReplaceAllUsesWith(alloca);
    module.RemoveGlobal(global);
  }
}
