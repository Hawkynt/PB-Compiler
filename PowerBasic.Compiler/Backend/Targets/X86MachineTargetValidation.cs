using PowerBasic.Compiler.Backend;

namespace PowerBasic.Compiler.Backend.Targets;

/// <summary>Validates that a selected x86 machine function belongs to the target consuming it.</summary>
public static class X86MachineTargetValidation {
  public static bool TryValidate(X86MachineFunction function, MachineTargetDescription target,
      out string? error) {
    ArgumentNullException.ThrowIfNull(function);
    var expected = target.PointerBits switch {
      16 => MachineTargetFamily.X86_16,
      32 => MachineTargetFamily.X86_32,
      64 => MachineTargetFamily.X86_64,
      _ => (MachineTargetFamily?)null,
    };
    if (expected is null) {
      error = $"target '{target.Name}' has unsupported pointer width {target.PointerBits}";
      return false;
    }
    if (function.TargetFamily != expected.Value) {
      error = $"machine function is '{function.TargetFamily}', target requires '{expected.Value}'";
      return false;
    }
    error = null;
    return true;
  }
}
