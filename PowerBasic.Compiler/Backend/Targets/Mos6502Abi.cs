namespace PowerBasic.Compiler.Backend.Targets;

public sealed class Mos6502Abi : IMachineAbi {
  public int PointerBits => 16;
  public int StackAlignment => 1;
  public int ShadowSpaceBytes => 0;
  public IReadOnlyList<MachineRegister> ArgumentRegisters => [
    Mos6502RegisterFile.Accumulator, Mos6502RegisterFile.X, Mos6502RegisterFile.Y,
    .. Mos6502RegisterFile.ZeroPage.Take(8)];
  public MachineRegister ReturnRegister => Mos6502RegisterFile.Accumulator;
  public IReadOnlySet<MachineRegister> CalleeSavedRegisters => new HashSet<MachineRegister>([
    .. Mos6502RegisterFile.ZeroPage.Skip(10), Mos6502RegisterFile.FramePointer]);
}
