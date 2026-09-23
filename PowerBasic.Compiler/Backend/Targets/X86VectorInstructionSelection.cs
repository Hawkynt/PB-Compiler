namespace PowerBasic.Compiler.Backend.Targets;

public enum X86VectorOpcode {
  Move, Add, AddW, Sub, SubW, And, Or, Xor, Multiply,
  MinUnsignedDword, MaxUnsignedDword, BlendWord, AlignRight,
  AesEnc, AesDec, Pclmul,
}

public sealed record X86VectorInstruction(
    X86VectorOpcode Opcode,
    X86VectorRegisterClass Class,
    MachineRegister Destination,
    MachineRegister Left,
    MachineRegister Right,
    byte Immediate = 0);

public static class X86VectorInstructionSelection {
  public static X86VectorInstruction SelectBinary(X86VectorOpcode opcode,
      X86VectorRegisterClass registerClass, int destination, int left, int right, byte immediate = 0) {
    var file = new X86VectorRegisterFile(registerClass);
    if ((uint)destination >= (uint)file.Registers.Count || (uint)left >= (uint)file.Registers.Count
        || (uint)right >= (uint)file.Registers.Count)
      throw new ArgumentOutOfRangeException("vector register index");
    return new(opcode, registerClass, file.Registers[destination], file.Registers[left], file.Registers[right], immediate);
  }

  public static X86TargetInstruction Lower(X86VectorInstruction instruction) =>
    new(X86TargetOpcode.VectorBinary,
      [instruction.Destination, instruction.Left, instruction.Right], instruction.Immediate,
      VectorOperation: instruction.Opcode);
}
