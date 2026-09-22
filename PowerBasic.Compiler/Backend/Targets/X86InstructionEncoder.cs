namespace PowerBasic.Compiler.Backend.Targets;

/// <summary>Small, specification-derived x86 encoder used by the target machine emitter.</summary>
public sealed class X86InstructionEncoder(X86Mode mode) : IMachineInstructionEncoder {
  public byte[] Ret() => [0xC3];

  public byte[] Push(MachineRegister register) {
    Validate(register);
    if (register.Encoding < 8)
      return [(byte)(0x50 + register.Encoding)];
    return [0x41, (byte)(0x50 + (register.Encoding & 7))];
  }

  public byte[] Pop(MachineRegister register) {
    Validate(register);
    if (register.Encoding < 8)
      return [(byte)(0x58 + register.Encoding)];
    return [0x41, (byte)(0x58 + (register.Encoding & 7))];
  }

  public byte[] MoveImmediate(MachineRegister register, ulong value) {
    Validate(register);
    if (this.mode == X86Mode.Bit32)
      return [(byte)(0xB8 + register.Encoding), .. BitConverter.GetBytes((uint)value)];
    if (register.Encoding < 8)
      return [0x48, (byte)(0xB8 + register.Encoding), .. BitConverter.GetBytes(value)];
    return [0x49, (byte)(0xB8 + (register.Encoding & 7)), .. BitConverter.GetBytes(value)];
  }

  private void Validate(MachineRegister register) {
    if (register.Bits != (this.mode == X86Mode.Bit64 ? 64 : 32))
      throw new ArgumentException("Register width does not match the target mode.", nameof(register));
  }
}
