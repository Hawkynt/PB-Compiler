namespace PowerBasic.Compiler.Backend.Targets;

/// <summary>Standalone VEX/EVEX encoder.  It never routes through the text assembler.</summary>
public sealed class X86VectorInstructionEncoder {
  public byte[] Vex3(MachineRegister destination, MachineRegister left, MachineRegister right,
      byte map, byte opcode, byte pp = 1, bool wide = false, int vectorBits = 128) {
    ValidateVector(destination, vectorBits); ValidateVector(left, vectorBits); ValidateVector(right, vectorBits);
    var vex = new List<byte> { 0xC4,
      (byte)(0xE0 | (((~left.Encoding) & 8) << 2) | (map & 0x1F)),
      (byte)((wide ? 0x80 : 0) | (((~destination.Encoding) & 8) << 4)
        | (((~right.Encoding) & 8) << 3) | (LValue(vectorBits) << 2) | (pp & 3)), opcode };
    vex.Add((byte)(0xC0 | ((destination.Encoding & 7) << 3) | (right.Encoding & 7)));
    return [.. vex];
  }

  public byte[] Evex(MachineRegister destination, MachineRegister left, MachineRegister right,
      byte map, byte opcode, byte pp = 1, bool wide = false, int vectorBits = 512) {
    ValidateVector(destination, vectorBits); ValidateVector(left, vectorBits); ValidateVector(right, vectorBits);
    var bytes = new List<byte> { 0x62,
      (byte)(0xE0 | (((~left.Encoding) & 8) << 4) | (map & 3)),
      (byte)(((~destination.Encoding) & 8) << 4 | (((~right.Encoding) & 8) << 3) | (pp & 3)),
      (byte)((wide ? 0x80 : 0) | (LValue(vectorBits) << 5)) , opcode,
      (byte)(0xC0 | ((destination.Encoding & 7) << 3) | (right.Encoding & 7)) };
    return [.. bytes];
  }

  private static int LValue(int bits) => bits switch { 128 => 0, 256 => 1, 512 => 2, _ => throw new ArgumentOutOfRangeException(nameof(bits)) };
  private static void ValidateVector(MachineRegister register, int bits) {
    if (register.Bits != bits || register.Encoding is < 0 or > 31)
      throw new ArgumentException("vector register class mismatch", nameof(register));
  }
}
