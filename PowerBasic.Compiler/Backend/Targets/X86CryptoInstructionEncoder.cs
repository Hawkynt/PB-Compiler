namespace PowerBasic.Compiler.Backend.Targets;

public static class X86CryptoInstructionEncoder {
  public static byte[] Aes(MachineRegister destination, MachineRegister source, bool decrypt, bool inverseMix) {
    var opcode = inverseMix ? (byte)0xDB : decrypt ? (byte)0xDE : (byte)0xDC;
    return [0x66, 0x0F, 0x38, opcode,
      (byte)(0xC0 | ((destination.Encoding & 7) << 3) | (source.Encoding & 7))];
  }

  public static byte[] Pclmul(MachineRegister destination, MachineRegister source, byte immediate) => [
    0x66, 0x0F, 0x3A, 0x44,
    (byte)(0xC0 | ((destination.Encoding & 7) << 3) | (source.Encoding & 7)), immediate];
}
