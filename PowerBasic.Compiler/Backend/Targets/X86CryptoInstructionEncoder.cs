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

  public static byte[] AesVector(MachineRegister destination, MachineRegister left, MachineRegister right,
      bool decrypt, bool inverseMix, int bits) {
    var opcode = inverseMix ? (byte)0xDB : decrypt ? (byte)0xDE : (byte)0xDC;
    var encoder = new X86VectorInstructionEncoder();
    return bits == 512
      ? encoder.Evex(destination, left, right, 2, opcode, pp: 1, vectorBits: bits)
      : encoder.Vex3(destination, left, right, 2, opcode, pp: 1, vectorBits: bits);
  }

  public static byte[] PclmulVector(MachineRegister destination, MachineRegister left, MachineRegister right,
      byte immediate, int bits) {
    var encoder = new X86VectorInstructionEncoder();
    var prefix = bits == 512
      ? encoder.Evex(destination, left, right, 3, 0x44, pp: 1, vectorBits: bits)
      : encoder.Vex3(destination, left, right, 3, 0x44, pp: 1, vectorBits: bits);
    return [.. prefix, immediate];
  }
}
