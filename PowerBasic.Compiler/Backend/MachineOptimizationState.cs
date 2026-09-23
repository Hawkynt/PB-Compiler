using System.Runtime.CompilerServices;

namespace PowerBasic.Compiler.Backend;

/// <summary>
/// Carries the optimizer-on decision from selection into the late machine pipeline without baking a
/// code-generation policy bit into <see cref="X86MachineFunction"/> itself. The marker is attached only by
/// <see cref="Peephole.Run"/>, which the selector invokes exclusively for optimized selections.
/// It also records the selected function's frame boundary: slots appended after this point belong to
/// allocation/spilling, which gives O0358 a precise compiler-private region without guessing from size.
/// </summary>
internal static class MachineOptimizationState {

  private sealed class Marker(int selectedStackSlots) {
    public int SelectedStackSlots { get; } = selectedStackSlots;
  }

  private static readonly ConditionalWeakTable<X86MachineFunction, Marker> _optimized = new();

  public static void Mark(X86MachineFunction function) {
    ArgumentNullException.ThrowIfNull(function);
    _optimized.GetValue(function, fn => new Marker(fn.StackSlots.Count));
  }

  public static bool IsMarked(X86MachineFunction function) {
    ArgumentNullException.ThrowIfNull(function);
    return _optimized.TryGetValue(function, out _);
  }

  public static bool TryGetFirstSpillSlot(X86MachineFunction function, out int firstSpillSlot) {
    ArgumentNullException.ThrowIfNull(function);
    if (_optimized.TryGetValue(function, out var marker)) {
      firstSpillSlot = marker.SelectedStackSlots;
      return true;
    }
    firstSpillSlot = 0;
    return false;
  }
}
