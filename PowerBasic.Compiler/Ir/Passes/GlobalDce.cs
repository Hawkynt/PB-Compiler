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
  /// <param name="module">The module to sweep.</param>
  /// <param name="removeGlobals">
  /// Whether unreferenced global VARIABLES go too. The native DOS build lays its data out from the bound
  /// program, not the IR, and resolves IR globals by name - the DATA cursor, a dynamic array's
  /// descriptor cells - so deleting one there saves nothing and leaves a name it can no longer find.
  /// </param>
  public static int Run(IrModule module, bool removeGlobals = true) {
    var removed = 0;

    // functions, to a fixpoint: deleting a dead function frees its callees, which may then be dead too
    for (var changed = true; changed;) {
      changed = false;
      var farTargets = FarEntryTargets(module);
      foreach (var function in module.Functions.ToList())
        if (function.HasNoUsers && !IsEntry(function) && !farTargets.Contains(function)) {
          function.ClearBody();               // drop the body's operand uses so callees/globals lose this user
          module.RemoveFunction(function);
          ++removed;
          changed = true;
        }
    }

    // globals: a single sweep after the functions are gone (a global's only users were instructions)
    if (!removeGlobals)
      return removed;
    foreach (var global in module.Globals.ToList())
      if (global.HasNoUsers) {
        module.RemoveGlobal(global);
        ++removed;
      }

    return removed;
  }

  internal static bool IsEntry(IrFunction function) => function.Name.Equals("main", System.StringComparison.OrdinalIgnoreCase);

  /// <summary>
  /// The procedures a far entry thunk names. <see cref="IrFarEntry"/> carries its target as a property,
  /// not an operand, so the target has no recorded user - and a lambda reached only through a delegate
  /// looked unreferenced and was deleted, leaving the thunk jumping to a label nothing bound.
  /// </summary>
  internal static HashSet<IrFunction> FarEntryTargets(IrModule module)
    => module.Functions.Where(function => !function.IsDeclaration)
      .SelectMany(function => function.AllInstructions)
      .SelectMany(instruction => instruction.Operands)
      .OfType<IrFarEntry>()
      .Select(entry => entry.Target)
      .ToHashSet();
}
