namespace PowerBasic.Compiler.Asm;

/// <summary>
/// Explicit integer overloads keep generated code unambiguous when the literal zero could otherwise
/// bind either to <see cref="Reg"/> or <see cref="Imm"/>. They are intentionally thin: encoding and
/// range handling remain owned by the existing Imm overloads.
/// </summary>
public sealed partial class Assembler {
  public void Mov(Reg destination, int value) => this.Mov(destination, (Imm)value);
  public void Mov(Mem destination, int value) => this.Mov(destination, (Imm)value);

  public void Add(Reg destination, int value) => this.Add(destination, (Imm)value);
  public void Add(Mem destination, int value) => this.Add(destination, (Imm)value);
  public void Adc(Reg destination, int value) => this.Adc(destination, (Imm)value);
  public void Adc(Mem destination, int value) => this.Adc(destination, (Imm)value);
  public void Sub(Reg destination, int value) => this.Sub(destination, (Imm)value);
  public void Sub(Mem destination, int value) => this.Sub(destination, (Imm)value);
  public void Sbb(Reg destination, int value) => this.Sbb(destination, (Imm)value);
  public void Sbb(Mem destination, int value) => this.Sbb(destination, (Imm)value);

  public void And(Reg destination, int value) => this.And(destination, (Imm)value);
  public void And(Mem destination, int value) => this.And(destination, (Imm)value);
  public void Or(Reg destination, int value) => this.Or(destination, (Imm)value);
  public void Or(Mem destination, int value) => this.Or(destination, (Imm)value);
  public void Xor(Reg destination, int value) => this.Xor(destination, (Imm)value);
  public void Xor(Mem destination, int value) => this.Xor(destination, (Imm)value);

  public void Cmp(Reg destination, int value) => this.Cmp(destination, (Imm)value);
  public void Cmp(Mem destination, int value) => this.Cmp(destination, (Imm)value);
  public void Test(Reg destination, int value) => this.Test(destination, (Imm)value);
  public void Test(Mem destination, int value) => this.Test(destination, (Imm)value);
}
