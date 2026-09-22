namespace PowerBasic.Compiler.Backend.Targets;

/// <summary>Relocatable x86 call/absolute operand encodings.</summary>
public static class X86RelocationEncoder {
  public static MachineCode CallRelative32(string symbol, int addend = -4) {
    ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
    return new(
      [0xE8, 0, 0, 0, 0],
      [new MachineRelocation(1, MachineRelocationKind.Relative32, symbol, addend)]);
  }
}
