namespace PowerBasic.Compiler.Backend.Targets;

public static class Mos6502RegisterFile {
  public static readonly MachineRegister Accumulator = new("A", 0, 8);
  public static readonly MachineRegister X = new("X", 1, 8);
  public static readonly MachineRegister Y = new("Y", 2, 8);
  public static readonly MachineRegister StackPointer = new("SP", 3, 8);
}
