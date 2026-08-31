namespace PowerBasic.Compiler.Asm;

/// <summary>32-bit BMI1/BMI2 encoders used by target-aware inline assembly.</summary>
public sealed partial class Assembler {
  private void EmitBmiVex(byte map, byte prefix, Reg? vexRegister) {
    this.EmitByte(0xC4);
    this.EmitByte((byte)(0xE0 | map)); // R/X/B are all inverted one: this backend exposes only the low eight GP registers.
    var vvvv = vexRegister is { } register ? (~register.Index() & 0x0F) << 3 : 0x78;
    this.EmitByte((byte)(vvvv | prefix));
  }

  private static void RequireDwordGp(Reg register, string parameterName) {
    if (!register.IsDword())
      throw new ArgumentException($"{register} must be a 32-bit general-purpose register.", parameterName);
  }

  private static void RequireDwordMemory(Mem memory, string parameterName) {
    if (memory.Size is not (OperandSize.None or OperandSize.Dword))
      throw new ArgumentException($"{memory} must be an unsized or dword memory operand.", parameterName);
  }

  private void BmiVexRegRm(byte opcode, byte prefix, Reg destination, Reg vexRegister, Reg source) {
    RequireDwordGp(destination, nameof(destination));
    RequireDwordGp(vexRegister, nameof(vexRegister));
    RequireDwordGp(source, nameof(source));
    this.EmitBmiVex(2, prefix, vexRegister);
    this.EmitByte(opcode);
    this.EmitModRmRegister(destination.Index(), source);
  }

  private void BmiVexRegRm(byte opcode, byte prefix, Reg destination, Reg vexRegister, Mem source) {
    RequireDwordGp(destination, nameof(destination));
    RequireDwordGp(vexRegister, nameof(vexRegister));
    RequireDwordMemory(source, nameof(source));
    this.EmitSegmentPrefix(source);
    this.EmitBmiVex(2, prefix, vexRegister);
    this.EmitByte(opcode);
    this.EmitModRmMemory(destination.Index(), source);
  }

  private void BmiVexFixedReg(byte extension, Reg destination, Reg source) {
    RequireDwordGp(destination, nameof(destination));
    RequireDwordGp(source, nameof(source));
    this.EmitBmiVex(2, 0, destination);
    this.EmitByte(0xF3);
    this.EmitModRmRegister(extension, source);
  }

  private void BmiVexFixedReg(byte extension, Reg destination, Mem source) {
    RequireDwordGp(destination, nameof(destination));
    RequireDwordMemory(source, nameof(source));
    this.EmitSegmentPrefix(source);
    this.EmitBmiVex(2, 0, destination);
    this.EmitByte(0xF3);
    this.EmitModRmMemory(extension, source);
  }

  public void Andn(Reg destination, Reg left, Reg right) => this.BmiVexRegRm(0xF2, 0, destination, left, right);
  public void Andn(Reg destination, Reg left, Mem right) => this.BmiVexRegRm(0xF2, 0, destination, left, right);
  public void Bextr(Reg destination, Reg source, Reg control) => this.BmiVexRegRm(0xF7, 0, destination, control, source);
  public void Bextr(Reg destination, Mem source, Reg control) => this.BmiVexRegRm(0xF7, 0, destination, control, source);
  public void Blsr(Reg destination, Reg source) => this.BmiVexFixedReg(1, destination, source);
  public void Blsr(Reg destination, Mem source) => this.BmiVexFixedReg(1, destination, source);
  public void Blsmsk(Reg destination, Reg source) => this.BmiVexFixedReg(2, destination, source);
  public void Blsmsk(Reg destination, Mem source) => this.BmiVexFixedReg(2, destination, source);
  public void Blsi(Reg destination, Reg source) => this.BmiVexFixedReg(3, destination, source);
  public void Blsi(Reg destination, Mem source) => this.BmiVexFixedReg(3, destination, source);

  public void Tzcnt(Reg destination, Reg source) {
    RequireDwordGp(destination, nameof(destination));
    RequireDwordGp(source, nameof(source));
    this.EmitByte(0x66);
    this.EmitByte(0xF3);
    this.EmitByte(0x0F);
    this.EmitByte(0xBC);
    this.EmitModRmRegister(destination.Index(), source);
  }

  public void Tzcnt(Reg destination, Mem source) {
    RequireDwordGp(destination, nameof(destination));
    RequireDwordMemory(source, nameof(source));
    this.EmitSegmentPrefix(source);
    this.EmitByte(0x66);
    this.EmitByte(0xF3);
    this.EmitByte(0x0F);
    this.EmitByte(0xBC);
    this.EmitModRmMemory(destination.Index(), source);
  }

  public void Bzhi(Reg destination, Reg source, Reg index) => this.BmiVexRegRm(0xF5, 0, destination, index, source);
  public void Bzhi(Reg destination, Mem source, Reg index) => this.BmiVexRegRm(0xF5, 0, destination, index, source);
  public void Pdep(Reg destination, Reg source, Reg mask) => this.BmiVexRegRm(0xF5, 3, destination, source, mask);
  public void Pdep(Reg destination, Reg source, Mem mask) => this.BmiVexRegRm(0xF5, 3, destination, source, mask);
  public void Pext(Reg destination, Reg source, Reg mask) => this.BmiVexRegRm(0xF5, 2, destination, source, mask);
  public void Pext(Reg destination, Reg source, Mem mask) => this.BmiVexRegRm(0xF5, 2, destination, source, mask);
  public void Sarx(Reg destination, Reg source, Reg count) => this.BmiVexRegRm(0xF7, 2, destination, count, source);
  public void Sarx(Reg destination, Mem source, Reg count) => this.BmiVexRegRm(0xF7, 2, destination, count, source);
  public void Shlx(Reg destination, Reg source, Reg count) => this.BmiVexRegRm(0xF7, 1, destination, count, source);
  public void Shlx(Reg destination, Mem source, Reg count) => this.BmiVexRegRm(0xF7, 1, destination, count, source);
  public void Shrx(Reg destination, Reg source, Reg count) => this.BmiVexRegRm(0xF7, 3, destination, count, source);
  public void Shrx(Reg destination, Mem source, Reg count) => this.BmiVexRegRm(0xF7, 3, destination, count, source);

  public void Mulx(Reg lowDestination, Reg highDestination, Reg source) =>
    this.BmiVexRegRm(0xF6, 3, lowDestination, highDestination, source);
  public void Mulx(Reg lowDestination, Reg highDestination, Mem source) =>
    this.BmiVexRegRm(0xF6, 3, lowDestination, highDestination, source);

  public void Rorx(Reg destination, Reg source, byte count) {
    RequireDwordGp(destination, nameof(destination));
    RequireDwordGp(source, nameof(source));
    this.EmitBmiVex(3, 3, null);
    this.EmitByte(0xF0);
    this.EmitModRmRegister(destination.Index(), source);
    this.EmitByte(count);
  }

  public void Rorx(Reg destination, Mem source, byte count) {
    RequireDwordGp(destination, nameof(destination));
    RequireDwordMemory(source, nameof(source));
    this.EmitSegmentPrefix(source);
    this.EmitBmiVex(3, 3, null);
    this.EmitByte(0xF0);
    this.EmitModRmMemory(destination.Index(), source);
    this.EmitByte(count);
  }
}
