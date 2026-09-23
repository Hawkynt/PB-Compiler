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
    if (mode == X86Mode.Bit32)
      return [(byte)(0xB8 + register.Encoding), .. BitConverter.GetBytes((uint)value)];
    if (register.Encoding < 8)
      return [0x48, (byte)(0xB8 + register.Encoding), .. BitConverter.GetBytes(value)];
    return [0x49, (byte)(0xB8 + (register.Encoding & 7)), .. BitConverter.GetBytes(value)];
  }

  public byte[] AdjustStack(int bytes, bool allocate) {
    if (bytes is <= 0 or > 127)
      throw new ArgumentOutOfRangeException(nameof(bytes), bytes, "The compact stack adjustment supports 1..127 bytes.");
    var opcode = allocate ? (byte)0xEC : (byte)0xC4;
    return mode == X86Mode.Bit64
      ? [0x48, 0x83, opcode, (byte)bytes]
      : [0x83, opcode, (byte)bytes];
  }

  public byte[] MoveRegister(MachineRegister destination, MachineRegister source) {
    Validate(destination);
    Validate(source);
    if (destination.Bits != source.Bits)
      throw new ArgumentException("Register widths must match.");
    var rex = Rex(destination, source, w: mode == X86Mode.Bit64);
    var result = new List<byte>(rex is null ? 2 : 3);
    if (rex is { } prefix)
      result.Add(prefix);
    result.Add(0x89);
    result.Add(ModRm(destination.Encoding, source.Encoding));
    return [.. result];
  }

  public byte[] AddImmediate(MachineRegister destination, int value)
    => AluImmediate(destination, value, extension: 0);

  public byte[] SubImmediate(MachineRegister destination, int value)
    => AluImmediate(destination, value, extension: 5);

  public byte[] AndImmediate(MachineRegister destination, int value)
    => AluImmediate(destination, value, extension: 4);

  public byte[] OrImmediate(MachineRegister destination, int value)
    => AluImmediate(destination, value, extension: 1);

  public byte[] XorImmediate(MachineRegister destination, int value)
    => AluImmediate(destination, value, extension: 6);

  public byte[] CompareImmediate(MachineRegister destination, int value)
    => AluImmediate(destination, value, extension: 7);

  public byte[] PushImmediate(int value) {
    if (value is >= sbyte.MinValue and <= sbyte.MaxValue)
      return [0x6A, (byte)value];
    return [0x68, .. BitConverter.GetBytes(value)];
  }

  private byte[] AluImmediate(MachineRegister destination, int value, int extension) {
    Validate(destination);
    var rex = Rex(destination, destination, w: mode == X86Mode.Bit64);
    var result = new List<byte>(rex is null ? 7 : 8);
    if (rex is { } prefix)
      result.Add(prefix);
    if (value is >= sbyte.MinValue and <= sbyte.MaxValue) {
      result.Add(0x83);
      result.Add(ModRm(extension, destination.Encoding));
      result.Add((byte)value);
    } else {
      result.Add(0x81);
      result.Add(ModRm(extension, destination.Encoding));
      result.AddRange(BitConverter.GetBytes(value));
    }
    return [.. result];
  }

  private static byte ModRm(int reg, int rm) => (byte)(0xC0 | ((reg & 7) << 3) | (rm & 7));

  private static byte? Rex(MachineRegister destination, MachineRegister source, bool w) {
    var rex = (byte)(0x40 | (w ? 0x08 : 0)
      | (source.Encoding >= 8 ? 0x04 : 0)
      | (destination.Encoding >= 8 ? 0x01 : 0));
    return rex == 0x40 ? null : rex;
  }

  private void Validate(MachineRegister register) {
    if (register.Bits != (mode == X86Mode.Bit64 ? 64 : 32))
      throw new ArgumentException("Register width does not match the target mode.", nameof(register));
  }
}
