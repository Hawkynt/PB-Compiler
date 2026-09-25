namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0282 - a private register calling convention for procedures the program owns completely. A
/// procedure every caller of which is a direct call in this module can take its leading word arguments
/// in <c>AX, DX, BX, CX</c> (the Watcom convention; any further ones stay on the stack) instead of
/// pushing each and reading it back through <c>BP</c>: the caller already has most of them in
/// registers, and the callee wants them there.
///
/// <para>
/// What may change is decided on the IR, where the whole call graph is visible, and the definition
/// and every call site are respecified together - the back end reads both from here. A procedure
/// keeps its BASIC stack ABI when anything could reach it by another route: its address taken (a
/// far entry for a delegate, or any use that is not the callee of a direct call), an explicit
/// convention in its declaration, the module entry, a unit or linked object whose callers are not in
/// this module (<see cref="IrModule.OwnsProcedureAbi"/>), inline assembly anywhere (a text
/// <c>CALL</c> is a caller the IR cannot see), or an indirect call anywhere (its target set is not
/// known, so no candidate can be proven out of it). Only word parameters qualify - a sixteen-bit
/// integer or a near pointer, which is what a BYREF is - because that is exactly one register.
/// </para>
/// <para>
/// A SPEED trade: it removes pushes and frame reads, and on a callee that was a leaf it can remove
/// the frame, but the arguments are spilled to the frame anyway where the body wants them in memory.
/// </para>
/// </summary>
public static class PrivateCallingConvention {

  /// <summary>Respecifies every eligible procedure; the number changed.</summary>
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    if (!module.OwnsProcedureAbi)
      return 0;
    var bodies = module.Functions.Where(function => !function.IsDeclaration).ToList();
    if (bodies.Any(function => function.HasInlineAsm))
      return 0;
    var calls = bodies.SelectMany(function => function.AllInstructions).OfType<IrCall>().ToList();
    if (calls.Any(call => call.Callee is not IrFunction))
      return 0;

    var escaped = GlobalDce.FarEntryTargets(module);
    var changed = 0;
    foreach (var function in bodies) {
      if (!IsEligible(function) || escaped.Contains(function)
          || function.Users.Any(user => user is not IrCall direct || !ReferenceEquals(direct.Callee, function)
               || direct.Args.Any(argument => ReferenceEquals(argument, function))))
        continue;
      var sites = calls.Where(call => ReferenceEquals(call.Callee, function)).ToList();
      if (sites.Count == 0)
        continue;
      function.SpecializeConvention(IrCallConvention.Watcall);
      foreach (var site in sites)
        site.SpecializeConvention(IrCallConvention.Watcall);
      ++changed;
    }
    return changed;
  }

  private static bool IsEligible(IrFunction function)
    => function.Convention == IrCallConvention.Basic
       && !GlobalDce.IsEntry(function)
       && function.Parameters.Count > 0
       && function.Parameters.All(parameter => IsWord(parameter.Type));

  /// <summary>A value that IS one sixteen-bit register: a word integer, or a near pointer (a BYREF).</summary>
  private static bool IsWord(IrType type)
    => type is { IsInteger: true, Bits: 16 } || type is { IsPointer: true, IsFarPointer: false };
}
