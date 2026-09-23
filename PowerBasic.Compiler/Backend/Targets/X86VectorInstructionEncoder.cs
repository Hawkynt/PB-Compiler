namespace PowerBasic.Compiler.Backend.Targets;

/// <summary>Standalone VEX/EVEX encoder.  It never routes through the text assembler.</summary>
public sealed class X86VectorInstructionEncoder {
  public byte[] LegacyMmx(MachineRegister destination, MachineRegister right, byte opcode) {
    ValidateVector(destination, 64); ValidateVector(right, 64);
    if (destination.Encoding > 7 || right.Encoding > 7)
      throw new ArgumentException("MMX register encoding out of range");
    return [0x0F, opcode, (byte)(0xC0 | ((destination.Encoding & 7) << 3) | (right.Encoding & 7))];
  }
  public byte[] Vex3(MachineRegister destination, MachineRegister left, MachineRegister right,
      byte map, byte opcode, byte pp = 1, bool wide = false, int vectorBits = 128) {
    ValidateVector(destination, vectorBits); ValidateVector(left, vectorBits); ValidateVector(right, vectorBits);
    if (destination.Encoding > 15 || left.Encoding > 15 || right.Encoding > 15)
      throw new ArgumentException("three-byte VEX cannot encode registers above 15");
    // VEX.~R extends ModRM.reg, VEX.~X is unused for register operands,
    // VEX.~B extends ModRM.r/m, and vvvv carries the first source.
    var vex = new List<byte> { 0xC4,
      (byte)((((~destination.Encoding) & 8) << 4) | 0x40 | ((~right.Encoding & 8) << 2) | (map & 0x1F)),
      (byte)((wide ? 0x80 : 0) | ((~left.Encoding & 0xF) << 3)
        | (LValue(vectorBits) << 2) | (pp & 3)), opcode };
    vex.Add((byte)(0xC0 | ((destination.Encoding & 7) << 3) | (right.Encoding & 7)));
    return [.. vex];
  }

  public byte[] Evex(MachineRegister destination, MachineRegister left, MachineRegister right,
      byte map, byte opcode, byte pp = 1, bool wide = false, int vectorBits = 512) {
    ValidateVector(destination, vectorBits); ValidateVector(left, vectorBits); ValidateVector(right, vectorBits);
    var bytes = new List<byte> { 0x62,
      (byte)((((~destination.Encoding) & 0x10) << 3) | 0x40 | 0x20 | 0x08
        | (((~right.Encoding) & 0x10) << 1) | (((~destination.Encoding) & 8) << 4)
        | (((~right.Encoding) & 8) << 2) | (map & 3)),
      (byte)((wide ? 0x80 : 0) | ((~left.Encoding & 0xF) << 3) | 0x04 | (pp & 3)),
      (byte)((LValue(vectorBits) == 2 ? 0x40 : LValue(vectorBits) << 5)
        | (((~left.Encoding) & 0x10) >> 1)), opcode,
      (byte)(0xC0 | ((destination.Encoding & 7) << 3) | (right.Encoding & 7)) };
    return [.. bytes];
  }

  private static int LValue(int bits) => bits switch { 128 => 0, 256 => 1, 512 => 2, _ => throw new ArgumentOutOfRangeException(nameof(bits)) };
  private static void ValidateVector(MachineRegister register, int bits) {
    if (register.Bits != bits || register.Encoding is < 0 or > 31)
      throw new ArgumentException("vector register class mismatch", nameof(register));
  }
}
