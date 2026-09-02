namespace PowerBasic.Compiler.Asm;

/// <summary>Legacy XMM AES-NI and PCLMULQDQ encoders used by target-aware inline assembly.</summary>
public sealed partial class Assembler {
  private void Crypto38(byte opcode, Reg destination, Reg source) {
    RequireXmm(destination, nameof(destination));
    RequireXmm(source, nameof(source));
    this.EmitByte(0x66);
    this.EmitByte(0x0F);
    this.EmitByte(0x38);
    this.EmitByte(opcode);
    this.EmitModRmRegister(destination.Index(), source);
  }

  private void Crypto38(byte opcode, Reg destination, Mem source) {
    RequireXmm(destination, nameof(destination));
    this.EmitSegmentPrefix(source);
    this.EmitByte(0x66);
    this.EmitByte(0x0F);
    this.EmitByte(0x38);
    this.EmitByte(opcode);
    this.EmitModRmMemory(destination.Index(), source);
  }

  private void Crypto3A(byte opcode, Reg destination, Reg source, byte immediate) {
    RequireXmm(destination, nameof(destination));
    RequireXmm(source, nameof(source));
    this.EmitByte(0x66);
    this.EmitByte(0x0F);
    this.EmitByte(0x3A);
    this.EmitByte(opcode);
    this.EmitModRmRegister(destination.Index(), source);
    this.EmitByte(immediate);
  }

  private void Crypto3A(byte opcode, Reg destination, Mem source, byte immediate) {
    RequireXmm(destination, nameof(destination));
    this.EmitSegmentPrefix(source);
    this.EmitByte(0x66);
    this.EmitByte(0x0F);
    this.EmitByte(0x3A);
    this.EmitByte(opcode);
    this.EmitModRmMemory(destination.Index(), source);
    this.EmitByte(immediate);
  }

  private static void RequireXmm(Reg register, string parameterName) {
    if (!register.IsXmm())
      throw new ArgumentException($"{register} must be an XMM register.", parameterName);
  }

  public void Aesimc(Reg destination, Reg source) => this.Crypto38(0xDB, destination, source);
  public void Aesimc(Reg destination, Mem source) => this.Crypto38(0xDB, destination, source);
  public void Aesenc(Reg destination, Reg source) => this.Crypto38(0xDC, destination, source);
  public void Aesenc(Reg destination, Mem source) => this.Crypto38(0xDC, destination, source);
  public void Aesenclast(Reg destination, Reg source) => this.Crypto38(0xDD, destination, source);
  public void Aesenclast(Reg destination, Mem source) => this.Crypto38(0xDD, destination, source);
  public void Aesdec(Reg destination, Reg source) => this.Crypto38(0xDE, destination, source);
  public void Aesdec(Reg destination, Mem source) => this.Crypto38(0xDE, destination, source);
  public void Aesdeclast(Reg destination, Reg source) => this.Crypto38(0xDF, destination, source);
  public void Aesdeclast(Reg destination, Mem source) => this.Crypto38(0xDF, destination, source);
  public void Aeskeygenassist(Reg destination, Reg source, byte roundConstant) =>
    this.Crypto3A(0xDF, destination, source, roundConstant);
  public void Aeskeygenassist(Reg destination, Mem source, byte roundConstant) =>
    this.Crypto3A(0xDF, destination, source, roundConstant);
  public void Pclmulqdq(Reg destination, Reg source, byte control) =>
    this.Crypto3A(0x44, destination, source, control);
  public void Pclmulqdq(Reg destination, Mem source, byte control) =>
    this.Crypto3A(0x44, destination, source, control);
}
