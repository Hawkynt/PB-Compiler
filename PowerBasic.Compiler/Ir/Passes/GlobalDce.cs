namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Module-level global dead-code elimination (LLVM's globaldce): removes functions and global
/// variables that nothing references. A function is dead when it has no users (no <c>call</c>, no
/// taken address) and is not the program entry <c>@main</c> - clearing a dead function's body drops
/// its callees' and globals' uses, so removal cascades to a fixpoint (a function that becomes
/// unreferenced once its only caller is deleted, e.g. after inlining, is then removed too). A global
/// variable is dead when it has no users. This shrinks the emitted module - dead code and dead data
/// that survived per-function DCE because they were only kept alive by other dead code.
/// </summary>
public static class GlobalDce {

  /// <summary>Removes unreferenced functions and globals from the module; returns how many were removed.</summary>
  public static int Run(IrModule module) {
    var removed = 0;

    // functions, to a fixpoint: deleting a dead function frees its callees, which may then be dead too
    for (var changed = true; changed;) {
      changed = false;
      foreach (var function in module.Functions.ToList())
        if (function.HasNoUsers && !IsEntry(function)) {
          function.ClearBody();               // drop the body's operand uses so callees/globals lose this user
          module.RemoveFunction(function);
          ++removed;
          changed = true;
        }
    }

    // globals: a single sweep after the functions are gone (a global's only users were instructions)
    foreach (var global in module.Globals.ToList())
      if (global.HasNoUsers) {
        module.RemoveGlobal(global);
        ++removed;
      }

    return removed;
  }

  private static bool IsEntry(IrFunction function) => function.Name.Equals("main", System.StringComparison.OrdinalIgnoreCase);
}
