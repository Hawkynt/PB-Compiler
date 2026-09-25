namespace PowerBasic.Compiler.Backend.Targets;

/// <summary>
/// Everything that follows from a <see cref="MachineTargetFamily"/>, in one place.
///
/// <para>
/// The family used to be flattened into a display string - <c>"x86-16"</c> - carried on
/// <see cref="MachineTargetDescription"/>, and four consumers each parsed that string back into an
/// <see cref="X86Mode"/> with a switch of their own. A misspelt literal in any of them compiled, and
/// failed at run time as "not an x86 target". The family is now the identity and the string is only
/// ever produced here, for messages.
/// </para>
/// </summary>
public static class MachineTargetFamilyFacts {

  /// <summary>The name diagnostics print. Never parse it: compare the family instead.</summary>
  public static string DisplayName(this MachineTargetFamily family) => family switch {
    MachineTargetFamily.X86_16 => "x86-16",
    MachineTargetFamily.X86_32 => "x86-32",
    MachineTargetFamily.X86_64 => "x86-64",
    MachineTargetFamily.Mos6502 => "6502",
    _ => throw new ArgumentOutOfRangeException(nameof(family), family, "unknown machine target family"),
  };

  /// <summary>The width of an address.</summary>
  public static int PointerBits(this MachineTargetFamily family) => family switch {
    MachineTargetFamily.X86_16 => 16,
    MachineTargetFamily.X86_32 => 32,
    MachineTargetFamily.X86_64 => 64,
    MachineTargetFamily.Mos6502 => 16,
    _ => throw new ArgumentOutOfRangeException(nameof(family), family, "unknown machine target family"),
  };

  /// <summary>The width of a general-purpose register - which is not the address width on a 6502.</summary>
  public static int RegisterBits(this MachineTargetFamily family) => family switch {
    MachineTargetFamily.Mos6502 => 8,
    _ => family.PointerBits(),
  };

  /// <summary>The x86 operating mode, or null for a family that is not x86.</summary>
  public static X86Mode? X86Mode(this MachineTargetFamily family) => family switch {
    MachineTargetFamily.X86_16 => Targets.X86Mode.Bit16,
    MachineTargetFamily.X86_32 => Targets.X86Mode.Bit32,
    MachineTargetFamily.X86_64 => Targets.X86Mode.Bit64,
    _ => null,
  };

  /// <summary>The x86 mode, for a caller that has already established the family is x86.</summary>
  public static X86Mode RequireX86Mode(this MachineTargetFamily family)
    => family.X86Mode() ?? throw new NotSupportedException($"target '{family.DisplayName()}' is not an x86 target");

  /// <summary>The family an x86 mode belongs to - the inverse of <see cref="X86Mode"/>.</summary>
  public static MachineTargetFamily Family(this X86Mode mode) => mode switch {
    Targets.X86Mode.Bit16 => MachineTargetFamily.X86_16,
    Targets.X86Mode.Bit32 => MachineTargetFamily.X86_32,
    Targets.X86Mode.Bit64 => MachineTargetFamily.X86_64,
    _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "unknown x86 mode"),
  };

  /// <summary>
  /// Whether this family's bytes are produced from the HOSTED target function. True for x86-32 and
  /// x86-64. False for x86-16, which DOS emits through MachineEmitter on the allocated MFunction - the
  /// stage that owns the PowerBASIC stack ABI, far operands, verbatim inline assembly and 386
  /// operand-size prefixes - so a hosted build that fails there costs nothing the program needs.
  /// </summary>
  public static bool EmitsFromHostedFunction(this MachineTargetFamily family)
    => family is MachineTargetFamily.X86_32 or MachineTargetFamily.X86_64;
}
