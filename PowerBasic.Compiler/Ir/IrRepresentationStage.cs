namespace PowerBasic.Compiler.Ir;

/// <summary>
/// The semantic contract currently carried by an <see cref="IrModule"/>. The repository deliberately
/// does not manufacture separate object models for every conceptual layer, but consumers can still
/// state which boundary they require while the lowering split proceeds incrementally.
/// </summary>
public enum IrRepresentationStage {
  Lowered,
  OptimizedSsa,
  LowIr,
  MachineSsa,
  MachineIr,
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
      error = null;
      return true;
    }

    if (next < current) {
      error = $"representation stage cannot move backwards from {current} to {next}";
      return false;
    }

    if ((int)next != (int)current + 1) {
      error = $"representation stage cannot skip the boundary from {current} to {next}";
      return false;
    }

    module.TrySetRepresentationStage(next);
    error = null;
    return true;
  }
}
