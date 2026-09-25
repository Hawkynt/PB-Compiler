using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir;

/// <summary>
/// Verifier contracts for target-neutral representation boundaries. A stage is a semantic promise,
/// not a progress counter: advancing is legal only when the module satisfies the complete contract
/// of the destination representation.
/// </summary>
public static class IrRepresentationContract {

  /// <summary>Returns every reason <paramref name="module"/> does not satisfy <paramref name="stage"/>.</summary>
  public static IReadOnlyList<string> Verify(IrModule module, IrRepresentationStage stage) {
    ArgumentNullException.ThrowIfNull(module);

    if (stage is IrRepresentationStage.MachineSsa or IrRepresentationStage.MachineIr)
      return [$"{stage} is a machine-product stage; an IrModule remains LowIr after machine lowering"];

    var errors = new List<string>();
    if (stage >= IrRepresentationStage.OptimizedSsa)
      errors.AddRange(IrVerifier.Verify(module));

    if (stage >= IrRepresentationStage.LowIr)
      VerifyLowIrSemantics(module, errors);

    return errors;
  }

  private static void VerifyLowIrSemantics(IrModule module, List<string> errors) {
    foreach (var function in module.Functions) {
      if (function.IsDeclaration)
        continue;
      foreach (var instruction in function.AllInstructions)
        try {
          _ = IrEffects.ForInstruction(instruction);
        } catch (NotSupportedException exception) {
          var block = instruction.Parent?.Label ?? "<detached>";
          errors.Add(
            $"LowIr function '{function.Name}' block '{block}' has no semantic contract for "
            + $"'{instruction.GetType().Name}': {exception.Message}");
        }
    }
  }
}
