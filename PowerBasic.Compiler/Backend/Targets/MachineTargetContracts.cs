using PowerBasic.Compiler.Backend;

namespace PowerBasic.Compiler.Backend.Targets;

/// <summary>
/// The machine a function is lowered for. Identified by its <see cref="MachineTargetFamily"/>; the name
/// and widths are derived from it in <see cref="MachineTargetFamilyFacts"/>, so there is one answer to
/// each and no string to compare.
/// </summary>
public readonly record struct MachineTargetDescription(MachineTargetFamily Family) {
  public string Name => this.Family.DisplayName();
  public int PointerBits => this.Family.PointerBits();
  public int RegisterBits => this.Family.RegisterBits();
}

public readonly record struct MachineCode(
    byte[] Bytes,
    IReadOnlyList<MachineRelocation> Relocations,
    IReadOnlyDictionary<string, int>? Labels = null);

public readonly record struct MachineRelocation(
    int Offset,
    MachineRelocationKind Kind,
    string Symbol,
    int Addend = 0);

public enum MachineRelocationKind {
  Relative16,
  Absolute16,
  Relative32,
  Absolute32,
  Absolute64,
}
