namespace PowerBasic.Compiler.Backend.Targets;

/// <summary>Width-specific general-purpose register file for the hosted x86 targets.</summary>
public sealed class X86TargetRegisterFile {
  public X86TargetRegisterFile(X86Mode mode) {
    this.Mode = mode;
    var names = mode == X86Mode.Bit64
      ? new[] { "rax", "rcx", "rdx", "rbx", "rsp", "rbp", "rsi", "rdi", "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15" }
      : new[] { "eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi" };
    this.Registers = names.Select((name, index) =>
      new MachineRegister(name, index, mode == X86Mode.Bit64 ? 64 : 32)).ToArray();
  }

  public X86Mode Mode { get; }
  public IReadOnlyList<MachineRegister> Registers { get; }
  public MachineRegister StackPointer => this.Registers[4];
  public MachineRegister FramePointer => this.Registers[5];
  public MachineRegister ReturnValue => this.Registers[0];
}

/// <summary>Target-specific memory address; no segment-register or 16-bit addressing assumptions.</summary>
public readonly record struct X86TargetAddress(
    MachineRegister? Base,
    MachineRegister? Index,
    byte Scale,
    int Displacement,
    int WidthBits = 0) {
  public X86TargetAddress {
    if (Scale is not (1 or 2 or 4 or 8))
      throw new ArgumentOutOfRangeException(nameof(Scale));
    if (Base is null && Index is null && Displacement == 0)
      throw new ArgumentException("An address must contain a base, index, or displacement.");
  }
}

/// <summary>Explicit ABI facts consumed by lowering and emission.</summary>
public sealed record X86TargetAbi(
    X86Mode Mode,
    string Name,
    int StackAlignment,
    int ShadowSpaceBytes,
    IReadOnlyList<MachineRegister> ArgumentRegisters,
    MachineRegister ReturnRegister,
    IReadOnlySet<MachineRegister> CalleeSavedRegisters);

/// <summary>Target machine instruction independent of the legacy DOS assembler.</summary>
public sealed record X86TargetInstruction(
    X86TargetOpcode Opcode,
    IReadOnlyList<MachineRegister> Registers,
    long Immediate = 0,
    X86TargetAddress? Address = null,
    string? Symbol = null);

public enum X86TargetOpcode {
  MoveImmediate,
  MoveRegister,
  AddImmediate,
  SubImmediate,
  AndImmediate,
  OrImmediate,
  XorImmediate,
  CompareImmediate,
  PushRegister,
  PopRegister,
  PushImmediate,
  CallRelative,
  Return,
}

/// <summary>Independent target machine function used by hosted x86 emitters.</summary>
public sealed class X86TargetMachineFunction(
    X86Mode mode,
    X86TargetAbi abi,
    IReadOnlyList<X86TargetInstruction> instructions) {
  public X86Mode Mode { get; } = mode;
  public X86TargetAbi Abi { get; } = abi;
  public IReadOnlyList<X86TargetInstruction> Instructions { get; } = instructions;
}
