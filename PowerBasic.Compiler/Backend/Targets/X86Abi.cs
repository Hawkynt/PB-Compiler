namespace PowerBasic.Compiler.Backend.Targets;

/// <summary>Concrete IA-32 and Intel 64 calling conventions used by machine targets.</summary>
public sealed record X86Abi(
    string Name,
    int PointerBits,
    int StackAlignment,
    int ShadowSpaceBytes,
    IReadOnlyList<MachineRegister> ArgumentRegisters,
    MachineRegister ReturnRegister,
    IReadOnlySet<MachineRegister> CalleeSavedRegisters) : IMachineAbi {

  public static X86Abi I8086Cdecl { get; } = new(
    "i8086-cdecl", 16, 2, 0, [], X86RegisterFile.Gpr16[0],
    new HashSet<MachineRegister>([X86RegisterFile.Gpr16[3], X86RegisterFile.Gpr16[5], X86RegisterFile.Gpr16[6], X86RegisterFile.Gpr16[7]]));

  public static X86Abi I386Cdecl { get; } = new(
    "i386-cdecl", 32, 4, 0, [], X86RegisterFile.Gpr32[0],
    new HashSet<MachineRegister>([X86RegisterFile.Gpr32[3], X86RegisterFile.Gpr32[5], X86RegisterFile.Gpr32[6], X86RegisterFile.Gpr32[7]]));

  public static X86Abi SysV64 { get; } = new(
    "x86-64-sysv", 64, 16, 0,
    [X86RegisterFile.Gpr64[7], X86RegisterFile.Gpr64[6], X86RegisterFile.Gpr64[2], X86RegisterFile.Gpr64[1],
      X86RegisterFile.Gpr64[8], X86RegisterFile.Gpr64[9]],
    X86RegisterFile.Gpr64[0],
    new HashSet<MachineRegister>([X86RegisterFile.Gpr64[3], X86RegisterFile.Gpr64[5], X86RegisterFile.Gpr64[6],
      X86RegisterFile.Gpr64[7], X86RegisterFile.Gpr64[12], X86RegisterFile.Gpr64[13], X86RegisterFile.Gpr64[14], X86RegisterFile.Gpr64[15]]));

  public static X86Abi Windows64 { get; } = new(
    "x86-64-windows", 64, 16, 32,
    [X86RegisterFile.Gpr64[1], X86RegisterFile.Gpr64[2], X86RegisterFile.Gpr64[8], X86RegisterFile.Gpr64[9]],
    X86RegisterFile.Gpr64[0],
    new HashSet<MachineRegister>([X86RegisterFile.Gpr64[3], X86RegisterFile.Gpr64[5], X86RegisterFile.Gpr64[6],
      X86RegisterFile.Gpr64[7], X86RegisterFile.Gpr64[12], X86RegisterFile.Gpr64[13], X86RegisterFile.Gpr64[14], X86RegisterFile.Gpr64[15]]));
}
