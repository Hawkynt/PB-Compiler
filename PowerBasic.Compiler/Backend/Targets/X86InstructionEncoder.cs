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
    if (mode == X86Mode.Bit16)
      return [(byte)(0xB8 + register.Encoding), .. BitConverter.GetBytes((ushort)value)];
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

  public byte[] MoveMemory(MachineRegister register, X86TargetAddress address, bool load) {
    Validate(register);
    var bytes = new List<byte>();
    var rex = MemoryRex(register, address, w: mode == X86Mode.Bit64 && address.WidthBits == 64);
    if (rex is { } prefix)
      bytes.Add(prefix);
    bytes.Add((byte)(load ? 0x8B : 0x89));
    AppendAddress(bytes, register.Encoding, address);
    return [.. bytes];
  }

  public byte[] LeaMemory(MachineRegister register, X86TargetAddress address) {
    Validate(register);
    var bytes = new List<byte>();
    if (MemoryRex(register, address, w: mode == X86Mode.Bit64) is { } rex)
      bytes.Add(rex);
    bytes.Add(0x8D);
    AppendAddress(bytes, register.Encoding, address);
    return [.. bytes];
  }

  public byte[] AluMemory(MachineRegister register, X86TargetAddress address, int opcode, bool load) {
    Validate(register);
    var bytes = new List<byte>();
    var rex = MemoryRex(register, address, w: mode == X86Mode.Bit64 && address.WidthBits == 64);
    if (rex is { } prefix)
      bytes.Add(prefix);
    bytes.Add((byte)opcode);
    AppendAddress(bytes, register.Encoding, address);
    return [.. bytes];
  }

  private byte? MemoryRex(MachineRegister register, X86TargetAddress address, bool w) {
    if (mode != X86Mode.Bit64)
      return null;
    var rex = (byte)(0x40 | (w ? 8 : 0) | (register.Encoding >= 8 ? 4 : 0)
      | (address.Base is { Encoding: >= 8 } ? 1 : 0)
      | (address.Index is { Encoding: >= 8 } ? 2 : 0));
    return rex == 0x40 ? null : rex;
  }

  private void AppendAddress(List<byte> bytes, int reg, X86TargetAddress address) {
    if (mode == X86Mode.Bit16) {
      var rm = (address.Base?.Encoding, address.Index?.Encoding) switch {
        (3, 6) or (6, 3) => 0,
        (3, 7) or (7, 3) => 1,
        (5, 6) or (6, 5) => 2,
        (5, 7) or (7, 5) => 3,
        (6, null) => 4,
        (7, null) => 5,
        (5, null) => 6,
        (3, null) => 7,
        _ => throw new NotSupportedException("x86-16 address requires BX/BP/SI/DI"),
      };
      AppendModRm(bytes, reg, rm, address.Displacement, address.Base?.Encoding == 5 && address.Index is null);
      return;
    }
    var baseReg = address.Base?.Encoding ?? 5;
    var indexReg = address.Index?.Encoding ?? 4;
    var needsSib = address.Index is not null || (baseReg & 7) == 4;
    var displacement = address.Displacement;
    var mod = displacement == 0 && (baseReg & 7) != 5 ? 0 : displacement is >= sbyte.MinValue and <= sbyte.MaxValue ? 1 : 2;
    if (address.Base is null)
      mod = 0;
    var rm = needsSib ? 4 : baseReg & 7;
    bytes.Add((byte)((mod << 6) | ((reg & 7) << 3) | rm));
    if (needsSib)
      bytes.Add((byte)(0xC0 | (((ScaleBits(address.Scale)) & 3) << 6) | ((indexReg & 7) << 3) | (baseReg & 7)));
    if (mod == 1)
      bytes.Add((byte)displacement);
    else if (mod == 2 || address.Base is null)
      bytes.AddRange(BitConverter.GetBytes(displacement));
  }

  private static int ScaleBits(byte scale) => scale switch { 1 => 0, 2 => 1, 4 => 2, 8 => 3, _ => 0 };

  private static void AppendModRm(List<byte> bytes, int reg, int rm, int displacement, bool forceDisp) {
    var mod = forceDisp ? 1 : displacement == 0 ? 0 : displacement is >= sbyte.MinValue and <= sbyte.MaxValue ? 1 : 2;
    bytes.Add((byte)((mod << 6) | ((reg & 7) << 3) | (rm & 7)));
    if (mod == 1)
      bytes.Add((byte)displacement);
    else if (mod == 2)
      bytes.AddRange(BitConverter.GetBytes((short)displacement));
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

  public byte[] AluRegister(MachineRegister destination, MachineRegister source, int opcode) {
    Validate(destination);
    Validate(source);
    var rex = Rex(destination, source, w: mode == X86Mode.Bit64);
    var bytes = new List<byte>(3);
    if (rex is { } prefix)
      bytes.Add(prefix);
    bytes.Add((byte)opcode);
    bytes.Add(ModRm(source.Encoding, destination.Encoding));
    return [.. bytes];
  }

  public byte[] UnaryRegister(MachineRegister register, int extension) {
    Validate(register);
    var rex = RexRm(register, w: mode == X86Mode.Bit64);
    var bytes = new List<byte>(3);
    if (rex is { } prefix)
      bytes.Add(prefix);
    bytes.Add(0xF7);
    bytes.Add(ModRm(extension, register.Encoding));
    return [.. bytes];
  }

  public byte[] UnaryMultiplyDivide(MachineRegister register, int extension) {
    Validate(register);
    var rex = RexRm(register, w: mode == X86Mode.Bit64);
    var bytes = new List<byte>(3);
    if (rex is { } prefix)
      bytes.Add(prefix);
    bytes.Add(0xF7);
    bytes.Add(ModRm(extension, register.Encoding));
    return [.. bytes];
  }

  public byte[] ShiftRegister(MachineRegister register, int extension, int count) {
    Validate(register);
    var rex = RexRm(register, w: mode == X86Mode.Bit64);
    var bytes = new List<byte>(4);
    if (rex is { } prefix)
      bytes.Add(prefix);
    if (count == 1) {
      bytes.Add(0xD1);
      bytes.Add(ModRm(extension, register.Encoding));
    } else {
      bytes.Add(0xC1);
      bytes.Add(ModRm(extension, register.Encoding));
      bytes.Add((byte)count);
    }
    return [.. bytes];
  }

  public byte[] Cwd() => mode == X86Mode.Bit16 ? [0x99] : [0x99];
  public byte[] Cbw() => mode == X86Mode.Bit16 ? [0x98] : [0x98];
  public byte[] Nop() => [0x90];

  public byte[] X87Memory(X86TargetAddress address, byte opcode, int extension) {
    var bytes = new List<byte>(4);
    if (MemoryRex(RegistersForAddress(address), address, w: false) is { } rex)
      bytes.Add(rex);
    bytes.Add(opcode);
    AppendAddress(bytes, extension, address);
    return [.. bytes];
  }

  private static MachineRegister RegistersForAddress(X86TargetAddress address)
    => address.Base ?? address.Index ?? new MachineRegister("rax", 0, 64);

  public byte[] PushImmediate(int value) {
    if (value is >= sbyte.MinValue and <= sbyte.MaxValue)
      return [0x6A, (byte)value];
    return [0x68, .. BitConverter.GetBytes(value)];
  }

  private byte[] AluImmediate(MachineRegister destination, int value, int extension) {
    Validate(destination);
    var rex = RexRm(destination, w: mode == X86Mode.Bit64);
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
      if (mode == X86Mode.Bit16)
        result.AddRange(BitConverter.GetBytes((short)value));
      else
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

  private static byte? RexRm(MachineRegister register, bool w) {
    var rex = (byte)(0x40 | (w ? 0x08 : 0) | (register.Encoding >= 8 ? 0x01 : 0));
    return rex == 0x40 ? null : rex;
  }

  private void Validate(MachineRegister register) {
    var bits = mode switch { X86Mode.Bit16 => 16, X86Mode.Bit64 => 64, _ => 32 };
    if (register.Bits != bits)
      throw new ArgumentException("Register width does not match the target mode.", nameof(register));
  }
}
