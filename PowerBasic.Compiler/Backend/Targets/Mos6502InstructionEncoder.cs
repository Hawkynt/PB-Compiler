namespace PowerBasic.Compiler.Backend.Targets;

public sealed class Mos6502InstructionEncoder : IMachineInstructionEncoder {
  public byte[] Ret() => [0x60];
  public byte[] Push(MachineRegister register) => register == Mos6502RegisterFile.Accumulator ? [0x48] : throw Unsupported(register);
  public byte[] Pop(MachineRegister register) => register == Mos6502RegisterFile.Accumulator ? [0x68] : throw Unsupported(register);
  public byte[] MoveImmediate(MachineRegister register, ulong value) => register switch {
    var r when r == Mos6502RegisterFile.Accumulator => [0xA9, (byte)value],
    var r when r == Mos6502RegisterFile.X => [0xA2, (byte)value],
    var r when r == Mos6502RegisterFile.Y => [0xA0, (byte)value],
    _ => throw Unsupported(register),
  };
  public byte[] AdjustStack(int bytes, bool allocate) => throw new NotSupportedException("6502 stack adjustment is frame-layout specific.");

  private static Exception Unsupported(MachineRegister register) => new ArgumentException($"6502 does not support this register operation: {register}", nameof(register));
}
