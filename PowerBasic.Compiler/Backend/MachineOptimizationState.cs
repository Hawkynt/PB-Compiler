using System.Runtime.CompilerServices;

namespace PowerBasic.Compiler.Backend;

/// <summary>
/// Carries the optimizer-on decision from selection into the late machine pipeline without baking a
/// code-generation policy bit into <see cref="MFunction"/> itself. The marker is attached only by
/// <see cref="Peephole.Run"/>, which the selector invokes exclusively for optimized selections.
/// </summary>
internal static class MachineOptimizationState {

  private sealed class Marker;

  private static readonly ConditionalWeakTable<MFunction, Marker> _optimized = new();

  public static void Mark(MFunction function) {
    ArgumentNullException.ThrowIfNull(function);
    _optimized.GetValue(function, static _ => new Marker());
  }

  public static bool IsMarked(MFunction function) {
    ArgumentNullException.ThrowIfNull(function);
    return _optimized.TryGetValue(function, out _);
  }
}
