namespace PowerBasic.Compiler.Asm;

/// <summary>Scalar bit-manipulation encoders that post-date the historical inline-assembler table.</summary>
public sealed partial class Assembler {

  /// <summary>POPCNT r16/r32, r16/r32 (F3 0F B8 /r).</summary>
  public void Popcnt(Reg destination, Reg source) {
    if (destination.IsDword())
      this.EmitByte(0x66);
    this.EmitByte(0xF3);
    this.EmitByte(0x0F);
    this.EmitByte(0xB8);
    this.EmitModRmRegister(destination.Index(), source);
  }

  /// <summary>POPCNT r16/r32, m16/m32 (F3 0F B8 /r).</summary>
  public void Popcnt(Reg destination, Mem source) {
    this.EmitSegmentPrefix(source);
    if (destination.IsDword())
      this.EmitByte(0x66);
    this.EmitByte(0xF3);
    this.EmitByte(0x0F);
    this.EmitByte(0xB8);
    this.EmitModRmMemory(destination.Index(), source);
  }
}
