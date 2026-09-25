namespace PowerBasic.Compiler.Backend.Targets;

/// <summary>
/// Everything that follows from a <see cref="MachineTargetFamily"/>, in one place.
///
/// <para>
/// The family used to be flattened into a display string - <c>"x86-16"</c> - carried on
/// <see cref="MachineTargetDescription"/>, and four consumers each parsed that string back into
/// an x86 mode with a switch of their own. A misspelt literal in any of them compiled, and
/// failed at run time as "not an x86 target". The family is now the identity and the string is only
/// ever produced here, for messages.
/// </para>
/// </summary>
public static class MachineTargetFamilyFacts {

  /// <summary>The name diagnostics print. Never parse it: compare the family instead.</summary>
  public static string DisplayName(this MachineTargetFamily family) => family switch {
    MachineTargetFamily.X86_16 => "x86-16",
    _ => throw new ArgumentOutOfRangeException(nameof(family), family, "unknown machine target family"),
  };

  /// <summary>The width of an address.</summary>
  public static int PointerBits(this MachineTargetFamily family) => family switch {
    MachineTargetFamily.X86_16 => 16,
    _ => throw new ArgumentOutOfRangeException(nameof(family), family, "unknown machine target family"),
  };

  /// <summary>The width of a general-purpose register - which is not the address width on a 6502.</summary>
  public static int RegisterBits(this MachineTargetFamily family) => family switch {
    _ => family.PointerBits(),
  };
}
