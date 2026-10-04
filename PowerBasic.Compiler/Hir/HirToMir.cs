using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Hir;

/// <summary>Forms explicit control flow and memory operations from high-level functions.</summary>
public static class HirToMir {

  public static IrModule? Lower(HirModule hir) => Lower(hir, null, out _);

  public static IrModule? Lower(HirModule hir, out string? declinedBecause)
    => Lower(hir, null, out declinedBecause);

  public static IrModule? Lower(HirModule hir, IReadOnlySet<DeferredSourceStmt>? unreachableDeferred,
      out string? declinedBecause, bool flatArrayDescriptors = false) {
    ArgumentNullException.ThrowIfNull(hir);
    var module = IrLowering.LowerHirToMir(hir, unreachableDeferred, out declinedBecause, flatArrayDescriptors);
    if (module is null)
      return null;

    var errors = MirVerifier.Verify(module);
    if (errors.Count == 0)
      return module;

    declinedBecause = "HIR to MIR produced invalid MIR: " + string.Join("; ", errors);
    return null;
  }
}
