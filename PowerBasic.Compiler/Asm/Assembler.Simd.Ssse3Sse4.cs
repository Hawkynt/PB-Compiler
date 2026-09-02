namespace PowerBasic.Compiler.Asm;

/// <summary>SSSE3, SSE4.1 and SSE4.2 packed-integer/string encoders used by target-aware inline asm.</summary>
public sealed partial class Assembler {
  private void Simd38RegReg(bool mandatory66, byte opcode, Reg destination, Reg source) {
    if (mandatory66) this.EmitByte(0x66);
    this.EmitByte(0x0F); this.EmitByte(0x38); this.EmitByte(opcode);
    this.EmitModRmRegister(destination.Index(), source);
  }

  private void Simd38RegMem(bool mandatory66, byte opcode, Reg destination, Mem source) {
    this.EmitSegmentPrefix(source);
    if (mandatory66) this.EmitByte(0x66);
    this.EmitByte(0x0F); this.EmitByte(0x38); this.EmitByte(opcode);
    this.EmitModRmMemory(destination.Index(), source);
  }

  private void Simd3ARegReg(bool mandatory66, byte opcode, Reg destination, Reg source, byte immediate) {
    if (mandatory66) this.EmitByte(0x66);
    this.EmitByte(0x0F); this.EmitByte(0x3A); this.EmitByte(opcode);
    this.EmitModRmRegister(destination.Index(), source);
    this.EmitByte(immediate);
  }

  private void Simd3ARegMem(bool mandatory66, byte opcode, Reg destination, Mem source, byte immediate) {
    this.EmitSegmentPrefix(source);
    if (mandatory66) this.EmitByte(0x66);
    this.EmitByte(0x0F); this.EmitByte(0x3A); this.EmitByte(opcode);
    this.EmitModRmMemory(destination.Index(), source);
    this.EmitByte(immediate);
  }

  private static bool Ssse3Needs66(Reg register) => register.IsXmm();

  // SSSE3 0F 38 map (MMX form has no mandatory prefix; XMM form has 66).
  public void Pshufb(Reg d, Reg s) => this.Simd38RegReg(Ssse3Needs66(d), 0x00, d, s);
  public void Pshufb(Reg d, Mem s) => this.Simd38RegMem(Ssse3Needs66(d), 0x00, d, s);
  public void Phaddw(Reg d, Reg s) => this.Simd38RegReg(Ssse3Needs66(d), 0x01, d, s);
  public void Phaddw(Reg d, Mem s) => this.Simd38RegMem(Ssse3Needs66(d), 0x01, d, s);
  public void Phaddd(Reg d, Reg s) => this.Simd38RegReg(Ssse3Needs66(d), 0x02, d, s);
  public void Phaddd(Reg d, Mem s) => this.Simd38RegMem(Ssse3Needs66(d), 0x02, d, s);
  public void Phaddsw(Reg d, Reg s) => this.Simd38RegReg(Ssse3Needs66(d), 0x03, d, s);
  public void Phaddsw(Reg d, Mem s) => this.Simd38RegMem(Ssse3Needs66(d), 0x03, d, s);
  public void Pmaddubsw(Reg d, Reg s) => this.Simd38RegReg(Ssse3Needs66(d), 0x04, d, s);
  public void Pmaddubsw(Reg d, Mem s) => this.Simd38RegMem(Ssse3Needs66(d), 0x04, d, s);
  public void Phsubw(Reg d, Reg s) => this.Simd38RegReg(Ssse3Needs66(d), 0x05, d, s);
  public void Phsubw(Reg d, Mem s) => this.Simd38RegMem(Ssse3Needs66(d), 0x05, d, s);
  public void Phsubd(Reg d, Reg s) => this.Simd38RegReg(Ssse3Needs66(d), 0x06, d, s);
  public void Phsubd(Reg d, Mem s) => this.Simd38RegMem(Ssse3Needs66(d), 0x06, d, s);
  public void Phsubsw(Reg d, Reg s) => this.Simd38RegReg(Ssse3Needs66(d), 0x07, d, s);
  public void Phsubsw(Reg d, Mem s) => this.Simd38RegMem(Ssse3Needs66(d), 0x07, d, s);
  public void Psignb(Reg d, Reg s) => this.Simd38RegReg(Ssse3Needs66(d), 0x08, d, s);
  public void Psignb(Reg d, Mem s) => this.Simd38RegMem(Ssse3Needs66(d), 0x08, d, s);
  public void Psignw(Reg d, Reg s) => this.Simd38RegReg(Ssse3Needs66(d), 0x09, d, s);
  public void Psignw(Reg d, Mem s) => this.Simd38RegMem(Ssse3Needs66(d), 0x09, d, s);
  public void Psignd(Reg d, Reg s) => this.Simd38RegReg(Ssse3Needs66(d), 0x0A, d, s);
  public void Psignd(Reg d, Mem s) => this.Simd38RegMem(Ssse3Needs66(d), 0x0A, d, s);
  public void Pmulhrsw(Reg d, Reg s) => this.Simd38RegReg(Ssse3Needs66(d), 0x0B, d, s);
  public void Pmulhrsw(Reg d, Mem s) => this.Simd38RegMem(Ssse3Needs66(d), 0x0B, d, s);
  public void Pabsb(Reg d, Reg s) => this.Simd38RegReg(Ssse3Needs66(d), 0x1C, d, s);
  public void Pabsb(Reg d, Mem s) => this.Simd38RegMem(Ssse3Needs66(d), 0x1C, d, s);
  public void Pabsw(Reg d, Reg s) => this.Simd38RegReg(Ssse3Needs66(d), 0x1D, d, s);
  public void Pabsw(Reg d, Mem s) => this.Simd38RegMem(Ssse3Needs66(d), 0x1D, d, s);
  public void Pabsd(Reg d, Reg s) => this.Simd38RegReg(Ssse3Needs66(d), 0x1E, d, s);
  public void Pabsd(Reg d, Mem s) => this.Simd38RegMem(Ssse3Needs66(d), 0x1E, d, s);
  public void Palignr(Reg d, Reg s, byte n) => this.Simd3ARegReg(Ssse3Needs66(d), 0x0F, d, s, n);
  public void Palignr(Reg d, Mem s, byte n) => this.Simd3ARegMem(Ssse3Needs66(d), 0x0F, d, s, n);

  // SSE4.1 (XMM only).
  public void Pcmpeqq(Reg d, Reg s) => this.Simd38RegReg(true, 0x29, d, s);
  public void Pcmpeqq(Reg d, Mem s) => this.Simd38RegMem(true, 0x29, d, s);
  public void Packusdw(Reg d, Reg s) => this.Simd38RegReg(true, 0x2B, d, s);
  public void Packusdw(Reg d, Mem s) => this.Simd38RegMem(true, 0x2B, d, s);
  public void Pminsb(Reg d, Reg s) => this.Simd38RegReg(true, 0x38, d, s);
  public void Pminsb(Reg d, Mem s) => this.Simd38RegMem(true, 0x38, d, s);
  public void Pminuw(Reg d, Reg s) => this.Simd38RegReg(true, 0x3A, d, s);
  public void Pminuw(Reg d, Mem s) => this.Simd38RegMem(true, 0x3A, d, s);
  public void Pminud(Reg d, Reg s) => this.Simd38RegReg(true, 0x3B, d, s);
  public void Pminud(Reg d, Mem s) => this.Simd38RegMem(true, 0x3B, d, s);
  public void Pmaxsb(Reg d, Reg s) => this.Simd38RegReg(true, 0x3C, d, s);
  public void Pmaxsb(Reg d, Mem s) => this.Simd38RegMem(true, 0x3C, d, s);
  public void Pmaxuw(Reg d, Reg s) => this.Simd38RegReg(true, 0x3E, d, s);
  public void Pmaxuw(Reg d, Mem s) => this.Simd38RegMem(true, 0x3E, d, s);
  public void Pmaxud(Reg d, Reg s) => this.Simd38RegReg(true, 0x3F, d, s);
  public void Pmaxud(Reg d, Mem s) => this.Simd38RegMem(true, 0x3F, d, s);
  public void Pmulld(Reg d, Reg s) => this.Simd38RegReg(true, 0x40, d, s);
  public void Pmulld(Reg d, Mem s) => this.Simd38RegMem(true, 0x40, d, s);
  public void Phminposuw(Reg d, Reg s) => this.Simd38RegReg(true, 0x41, d, s);
  public void Phminposuw(Reg d, Mem s) => this.Simd38RegMem(true, 0x41, d, s);
  public void Pblendw(Reg d, Reg s, byte mask) => this.Simd3ARegReg(true, 0x0E, d, s, mask);
  public void Pblendw(Reg d, Mem s, byte mask) => this.Simd3ARegMem(true, 0x0E, d, s, mask);

  // SSE4.2.
  public void Pcmpgtq(Reg d, Reg s) => this.Simd38RegReg(true, 0x37, d, s);
  public void Pcmpgtq(Reg d, Mem s) => this.Simd38RegMem(true, 0x37, d, s);
  public void Pcmpestrm(Reg a, Reg b, byte control) => this.Simd3ARegReg(true, 0x60, a, b, control);
  public void Pcmpestrm(Reg a, Mem b, byte control) => this.Simd3ARegMem(true, 0x60, a, b, control);
  public void Pcmpestri(Reg a, Reg b, byte control) => this.Simd3ARegReg(true, 0x61, a, b, control);
  public void Pcmpestri(Reg a, Mem b, byte control) => this.Simd3ARegMem(true, 0x61, a, b, control);
  public void Pcmpistrm(Reg a, Reg b, byte control) => this.Simd3ARegReg(true, 0x62, a, b, control);
  public void Pcmpistrm(Reg a, Mem b, byte control) => this.Simd3ARegMem(true, 0x62, a, b, control);
  public void Pcmpistri(Reg a, Reg b, byte control) => this.Simd3ARegReg(true, 0x63, a, b, control);
  public void Pcmpistri(Reg a, Mem b, byte control) => this.Simd3ARegMem(true, 0x63, a, b, control);

  /// <summary>CRC32 r32,r/m8 (F2 0F 38 F0 /r).</summary>
  public void Crc32Byte(Reg destination, Reg source) {
    this.EmitByte(0xF2); this.EmitByte(0x0F); this.EmitByte(0x38); this.EmitByte(0xF0);
    this.EmitModRmRegister(destination.Index(), source);
  }
  public void Crc32Byte(Reg destination, Mem source) {
    this.EmitSegmentPrefix(source);
    this.EmitByte(0xF2); this.EmitByte(0x0F); this.EmitByte(0x38); this.EmitByte(0xF0);
    this.EmitModRmMemory(destination.Index(), source);
  }

  /// <summary>CRC32 r32,r/m16 or r/m32 (F2 0F 38 F1 /r, operand-size selects the source width).</summary>
  public void Crc32(Reg destination, Reg source) {
    if (source.IsDword()) this.EmitByte(0x66);
    this.EmitByte(0xF2); this.EmitByte(0x0F); this.EmitByte(0x38); this.EmitByte(0xF1);
    this.EmitModRmRegister(destination.Index(), source);
  }
  public void Crc32(Reg destination, Mem source) {
    this.EmitSegmentPrefix(source);
    if (source.Size == OperandSize.Dword) this.EmitByte(0x66);
    this.EmitByte(0xF2); this.EmitByte(0x0F); this.EmitByte(0x38); this.EmitByte(0xF1);
    this.EmitModRmMemory(destination.Index(), source);
  }
}
