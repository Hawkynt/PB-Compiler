namespace PowerBasic.Compiler.Backend.Targets;

public enum X86Mode {
  Bit16,
  Bit32,
  Bit64,
}

/// <summary>Architectural register sets for IA-32 and Intel 64 encodings.</summary>
public static class X86RegisterFile {
  public static IReadOnlyList<MachineRegister> Gpr16 { get; } = [
    new("ax", 0, 16), new("cx", 1, 16), new("dx", 2, 16), new("bx", 3, 16),
    new("sp", 4, 16), new("bp", 5, 16), new("si", 6, 16), new("di", 7, 16),
  ];

  public static IReadOnlyList<MachineRegister> Gpr32 { get; } = [
    new("eax", 0, 32), new("ecx", 1, 32), new("edx", 2, 32), new("ebx", 3, 32),
    new("esp", 4, 32), new("ebp", 5, 32), new("esi", 6, 32), new("edi", 7, 32),
  ];

  public static IReadOnlyList<MachineRegister> Gpr64 { get; } = [
    new("rax", 0, 64), new("rcx", 1, 64), new("rdx", 2, 64), new("rbx", 3, 64),
    new("rsp", 4, 64), new("rbp", 5, 64), new("rsi", 6, 64), new("rdi", 7, 64),
    new("r8", 8, 64), new("r9", 9, 64), new("r10", 10, 64), new("r11", 11, 64),
    new("r12", 12, 64), new("r13", 13, 64), new("r14", 14, 64), new("r15", 15, 64),
  ];

  public static MachineRegister Get(X86Mode mode, int encoding) {
    var registers = mode switch {
      X86Mode.Bit16 => Gpr16,
      X86Mode.Bit64 => Gpr64,
      _ => Gpr32,
    };
    if ((uint)encoding >= (uint)registers.Count)
      throw new ArgumentOutOfRangeException(nameof(encoding));
    return registers[encoding];
  }
}
