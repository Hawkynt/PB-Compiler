namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Establishes the first target-independent Low IR boundary. The initial implementation is
/// deliberately verifier-backed and non-destructive: legalization rules are added only when their
/// semantic contract and target-neutral representation are explicit.
/// </summary>
public static class IrLowIrLegalization {
  public static bool TryLegalize(IrModule module, out IReadOnlyList<string> errors) {
    ArgumentNullException.ThrowIfNull(module);
    var verification = IrVerifier.Verify(module);
    if (verification.Count != 0) {
      errors = verification;
      return false;
    }

    if (!module.TryAdvanceRepresentationStage(IrRepresentationStage.LowIr, out var stageError)) {
      errors = [stageError ?? "unable to advance to Low IR"];
      return false;
    }
    errors = [];
    return true;
  }
}
