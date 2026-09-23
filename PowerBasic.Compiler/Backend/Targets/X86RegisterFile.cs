namespace PowerBasic.Compiler.Backend.Targets;

public enum X86Mode {
  Bit16,
  Bit32,
  Bit64,
}

/// <summary>Architectural register sets for IA-32 and Intel 64 encodings.</summary>
public static class X86RegisterFile {
  public static IReadOnlyList<MachineRegister> Gpr16 { get; } = [
    new("ax", 0, 16, 0), new("cx", 1, 16, 1), new("dx", 2, 16, 2), new("bx", 3, 16, 3),
    new("sp", 4, 16, 4), new("bp", 5, 16, 5), new("si", 6, 16, 6), new("di", 7, 16, 7),
  ];

  public static IReadOnlyList<MachineRegister> Gpr32 { get; } = [
    new("eax", 0, 32, 0), new("ecx", 1, 32, 1), new("edx", 2, 32, 2), new("ebx", 3, 32, 3),
    new("esp", 4, 32, 4), new("ebp", 5, 32, 5), new("esi", 6, 32, 6), new("edi", 7, 32, 7),
  ];

  public static IReadOnlyList<MachineRegister> Gpr64 { get; } = [
    new("rax", 0, 64, 0), new("rcx", 1, 64, 1), new("rdx", 2, 64, 2), new("rbx", 3, 64, 3),
    new("rsp", 4, 64, 4), new("rbp", 5, 64, 5), new("rsi", 6, 64, 6), new("rdi", 7, 64, 7),
    new("r8", 8, 64, 8), new("r9", 9, 64, 9), new("r10", 10, 64, 10), new("r11", 11, 64, 11),
    new("r12", 12, 64, 12), new("r13", 13, 64, 13), new("r14", 14, 64, 14), new("r15", 15, 64, 15),
  ];

  public static IReadOnlyList<MachineRegister> LowBytes { get; } = [
    new("al", 0, 8, 0), new("cl", 1, 8, 1), new("dl", 2, 8, 2), new("bl", 3, 8, 3),
  ];

  /// <summary>Legacy high-byte registers. They are unavailable in any instruction carrying a REX prefix.</summary>
  public static IReadOnlyList<MachineRegister> HighBytes { get; } = [
    new("ah", 4, 8, 0), new("ch", 5, 8, 1), new("dh", 6, 8, 2), new("bh", 7, 8, 3),
  ];

  public static IReadOnlyList<MachineRegister> Gpr64LowBytes { get; } =
    new[] { "al", "cl", "dl", "bl", "spl", "bpl", "sil", "dil", "r8b", "r9b", "r10b", "r11b", "r12b", "r13b", "r14b", "r15b" }
      .Select((name, index) => new MachineRegister(name, index, 8, index)).ToArray();

  public static IReadOnlyList<MachineRegister> Gpr64Words { get; } =
    new[] { "ax", "cx", "dx", "bx", "sp", "bp", "si", "di", "r8w", "r9w", "r10w", "r11w", "r12w", "r13w", "r14w", "r15w" }
      .Select((name, index) => new MachineRegister(name, index, 16, index)).ToArray();

  public static IReadOnlyList<MachineRegister> Gpr64Dwords { get; } =
    new[] { "eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi", "r8d", "r9d", "r10d", "r11d", "r12d", "r13d", "r14d", "r15d" }
      .Select((name, index) => new MachineRegister(name, index, 32, index)).ToArray();

  public static MachineRegister Ax => Gpr16[0];
  public static MachineRegister Al => LowBytes[0];
  public static MachineRegister Ah => HighBytes[0];
  public static MachineRegister Eax => Gpr32[0];
  public static MachineRegister Rax => Gpr64[0];
  public static MachineRegister Cx => Gpr16[1];
  public static MachineRegister Cl => LowBytes[1];
  public static MachineRegister Ch => HighBytes[1];
  public static MachineRegister Ecx => Gpr32[1];
  public static MachineRegister Dx => Gpr16[2];
  public static MachineRegister Dl => LowBytes[2];
  public static MachineRegister Dh => HighBytes[2];
  public static MachineRegister Edx => Gpr32[2];
  public static MachineRegister Bx => Gpr16[3];
  public static MachineRegister Bl => LowBytes[3];
  public static MachineRegister Bh => HighBytes[3];
  public static MachineRegister Ebx => Gpr32[3];

  public static IEnumerable<MachineRegister> All { get; } = Gpr16.Concat(Gpr32).Concat(Gpr64)
    .Concat(LowBytes).Concat(HighBytes).Concat(Gpr64LowBytes).Concat(Gpr64Words).Concat(Gpr64Dwords).Distinct();

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

  public static MachineRegister Get(X86Mode mode, X86RegisterView view, int encoding) {
    var registers = (mode, view) switch {
      (_, X86RegisterView.LowByte) when mode == X86Mode.Bit64 => Gpr64LowBytes,
      (_, X86RegisterView.LowByte) => LowBytes,
      (_, X86RegisterView.HighByte) => HighBytes,
      (_, X86RegisterView.Word) when mode == X86Mode.Bit64 => Gpr64Words,
      (_, X86RegisterView.Word) => Gpr16,
      (X86Mode.Bit64, X86RegisterView.Dword) => Gpr64Dwords,
      (_, X86RegisterView.Dword) => Gpr32,
      (_, X86RegisterView.Qword) => Gpr64,
      _ => throw new ArgumentOutOfRangeException(nameof(view)),
    };
    if ((uint)encoding >= (uint)registers.Count)
      throw new ArgumentOutOfRangeException(nameof(encoding));
    return registers[encoding];
  }
}

public enum X86RegisterView { LowByte, HighByte, Word, Dword, Qword }
