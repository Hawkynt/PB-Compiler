namespace PowerBasic.Compiler.Backend.Targets;

public static class Mos6502RegisterFile {
  public static readonly MachineRegister Accumulator = new("A", 0, 8);
  public static readonly MachineRegister X = new("X", 1, 8);
  public static readonly MachineRegister Y = new("Y", 2, 8);
  public static readonly MachineRegister StackPointer = new("SP", 3, 8);
  public static readonly IReadOnlyList<MachineRegister> ZeroPage =
    Enumerable.Range(0, 16).Select(index => new MachineRegister($"RS{index}", 4 + index, 16)).ToArray();
  public static MachineRegister RS(int index) => ZeroPage[index];
  public static MachineRegister FramePointer => RS(15);
}
