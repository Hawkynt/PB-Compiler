namespace PowerBasic.Compiler.Ir;

/// <summary>
/// The strongest semantic contract established for an <see cref="IrModule"/>. HIR has its own
/// operation model; MIR and later forms currently share the module container while their contracts
/// are enforced at explicit verified boundaries.
/// </summary>
public enum IrRepresentationStage {
  Mir,
  Ssa,
  OptimizedSsa,
  LowIr,
  MachineSsa,
  MachineIr,

  /// <summary>Compatibility name for modules returned directly by <see cref="PowerBasic.Compiler.Hir.HirToMir"/>.</summary>
  Lowered = Mir,
}

/// <summary>
/// Owns the representation-boundary contract for an <see cref="IrModule"/>.
/// A module may only move forward through adjacent, named domains; callers cannot
/// silently relabel optimized IR as machine IR or skip a lowering boundary.
/// </summary>
internal static class IrRepresentationTransitions {
  public static bool TryAdvance(
      IrModule module,
      IrRepresentationStage next,
      out string? error) {
    ArgumentNullException.ThrowIfNull(module);
    var current = module.RepresentationStage;
    if (next == current) {
      var existingErrors = IrRepresentationContract.Verify(module, next);
      error = existingErrors.Count == 0
        ? null
        : $"representation contract {next} is no longer satisfied: {string.Join("; ", existingErrors)}";
      return existingErrors.Count == 0;
    }

    if (next < current) {
      error = $"representation stage cannot move backwards from {current} to {next}";
      return false;
    }

    if ((int)next != (int)current + 1) {
      error = $"representation stage cannot skip the boundary from {current} to {next}";
      return false;
    }

    var errors = IrRepresentationContract.Verify(module, next);
    if (errors.Count != 0) {
      error = $"cannot establish {next}: {string.Join("; ", errors)}";
      return false;
    }

    module.TrySetRepresentationStage(next);
    error = null;
    return true;
  }
}

/// <summary>Completes SSA construction after the middle-end's mem2reg formation passes have run.</summary>
internal static class IrSsaFormationBoundary {

  public static bool TryComplete(IrModule module, out IReadOnlyList<string> errors) {
    ArgumentNullException.ThrowIfNull(module);
    if (module.RepresentationStage > IrRepresentationStage.Mir) {
      errors = [];
      return true;
    }
    if (module.RepresentationStage != IrRepresentationStage.Mir) {
      errors = [$"SSA formation requires Mir stage, got {module.RepresentationStage}"];
      return false;
    }

    errors = IrVerifier.Verify(module);
    if (errors.Count != 0)
      return false;
    if (module.TryAdvanceRepresentationStage(IrRepresentationStage.Ssa, out var error))
      return true;
    errors = [error ?? "unable to advance MIR to SSA"];
    return false;
  }
}
