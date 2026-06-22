namespace PowerBasic.Compiler.Asm;

/// <summary>
/// MMX integer SIMD instructions (Pentium MMX, 1997 - contemporary with PB 3.5).
/// Each operates on a 64-bit MMX register packed as 8 bytes / 4 words / 2 dwords.
/// Encoding is the two-byte <c>0F xx</c> escape with a ModRM whose reg field is the
/// destination MMX register and whose r/m is an MMX register or a 64-bit memory operand.
/// MMX aliases the x87 register stack, so a block of MMX work must end with <see cref="Emms"/>
/// before any subsequent floating-point use.
/// </summary>
public sealed partial class Assembler {

  /// <summary>Empties the MMX state (marks the x87 stack empty) - required before x87 use after MMX.</summary>
  public void Emms() {
    this.EmitByte(0x0F);
    this.EmitByte(0x77);
  }

  // 0F op /r with an MMX destination and an MMX-register or memory source
  private void MmxRegReg(byte op, Reg dest, Reg src) {
    this.EmitByte(0x0F);
    this.EmitByte(op);
    this.EmitModRmRegister(dest.Index(), src);
  }

  private void MmxRegMem(byte op, Reg dest, Mem src) {
    this.EmitByte(0x0F);
    this.EmitByte(op);
    this.EmitModRmMemory(dest.Index(), src);
  }

  /// <summary>MOVD mm, r/m32 (0F 6E): load the low 32 bits of an MMX register from a 32-bit GP register.</summary>
  public void Movd(Reg destMmx, Reg srcGp32) {
    this.EmitByte(0x0F);
    this.EmitByte(0x6E);
    this.EmitModRmRegister(destMmx.Index(), srcGp32);
  }

  /// <summary>MOVD mm, m32 (0F 6E): load the low 32 bits of an MMX register from memory.</summary>
  public void Movd(Reg destMmx, Mem src) => this.MmxRegMem(0x6E, destMmx, src);

  /// <summary>MOVD r/m32, mm (0F 7E): store the low 32 bits of an MMX register to a 32-bit GP register.</summary>
  public void MovdStore(Reg destGp32, Reg srcMmx) {
    this.EmitByte(0x0F);
    this.EmitByte(0x7E);
    this.EmitModRmRegister(srcMmx.Index(), destGp32);
  }

  /// <summary>MOVD m32, mm (0F 7E): store the low 32 bits of an MMX register to memory.</summary>
  public void MovdStore(Mem dest, Reg srcMmx) => this.MmxRegMem(0x7E, srcMmx, dest);

  /// <summary>MOVQ mm, mm/m64 (0F 6F): load a 64-bit MMX register.</summary>
  public void Movq(Reg destMmx, Reg srcMmx) => this.MmxRegReg(0x6F, destMmx, srcMmx);
  public void Movq(Reg destMmx, Mem src) => this.MmxRegMem(0x6F, destMmx, src);

  /// <summary>MOVQ mm/m64, mm (0F 7F): store a 64-bit MMX register to memory.</summary>
  public void MovqStore(Mem dest, Reg srcMmx) => this.MmxRegMem(0x7F, srcMmx, dest);

  // packed integer add/subtract (wrap-around): byte / word / dword lanes
  public void Paddb(Reg d, Reg s) => this.MmxRegReg(0xFC, d, s);
  public void Paddb(Reg d, Mem s) => this.MmxRegMem(0xFC, d, s);
  public void Paddw(Reg d, Reg s) => this.MmxRegReg(0xFD, d, s);
  public void Paddw(Reg d, Mem s) => this.MmxRegMem(0xFD, d, s);
  public void Paddd(Reg d, Reg s) => this.MmxRegReg(0xFE, d, s);
  public void Paddd(Reg d, Mem s) => this.MmxRegMem(0xFE, d, s);
  public void Psubb(Reg d, Reg s) => this.MmxRegReg(0xF8, d, s);
  public void Psubb(Reg d, Mem s) => this.MmxRegMem(0xF8, d, s);
  public void Psubw(Reg d, Reg s) => this.MmxRegReg(0xF9, d, s);
  public void Psubw(Reg d, Mem s) => this.MmxRegMem(0xF9, d, s);
  public void Psubd(Reg d, Reg s) => this.MmxRegReg(0xFA, d, s);
  public void Psubd(Reg d, Mem s) => this.MmxRegMem(0xFA, d, s);

  // packed word multiply (low 16 bits of each product)
  public void Pmullw(Reg d, Reg s) => this.MmxRegReg(0xD5, d, s);
  public void Pmullw(Reg d, Mem s) => this.MmxRegMem(0xD5, d, s);
  public void Pmulhw(Reg d, Reg s) => this.MmxRegReg(0xE5, d, s);
  public void Pmulhw(Reg d, Mem s) => this.MmxRegMem(0xE5, d, s);

  // packed bitwise
  public void Pand(Reg d, Reg s) => this.MmxRegReg(0xDB, d, s);
  public void Pand(Reg d, Mem s) => this.MmxRegMem(0xDB, d, s);
  public void Pandn(Reg d, Reg s) => this.MmxRegReg(0xDF, d, s);
  public void Pandn(Reg d, Mem s) => this.MmxRegMem(0xDF, d, s);
  public void Por(Reg d, Reg s) => this.MmxRegReg(0xEB, d, s);
  public void Por(Reg d, Mem s) => this.MmxRegMem(0xEB, d, s);
  public void Pxor(Reg d, Reg s) => this.MmxRegReg(0xEF, d, s);
  public void Pxor(Reg d, Mem s) => this.MmxRegMem(0xEF, d, s);

  // packed compares (per-lane all-ones / all-zeros mask)
  public void Pcmpeqb(Reg d, Reg s) => this.MmxRegReg(0x74, d, s);
  public void Pcmpeqb(Reg d, Mem s) => this.MmxRegMem(0x74, d, s);
  public void Pcmpeqw(Reg d, Reg s) => this.MmxRegReg(0x75, d, s);
  public void Pcmpeqw(Reg d, Mem s) => this.MmxRegMem(0x75, d, s);
  public void Pcmpeqd(Reg d, Reg s) => this.MmxRegReg(0x76, d, s);
  public void Pcmpeqd(Reg d, Mem s) => this.MmxRegMem(0x76, d, s);
  public void Pcmpgtb(Reg d, Reg s) => this.MmxRegReg(0x64, d, s);
  public void Pcmpgtb(Reg d, Mem s) => this.MmxRegMem(0x64, d, s);
  public void Pcmpgtw(Reg d, Reg s) => this.MmxRegReg(0x65, d, s);
  public void Pcmpgtw(Reg d, Mem s) => this.MmxRegMem(0x65, d, s);
  public void Pcmpgtd(Reg d, Reg s) => this.MmxRegReg(0x66, d, s);
  public void Pcmpgtd(Reg d, Mem s) => this.MmxRegMem(0x66, d, s);

  // saturating packed add/subtract (signed 's' / unsigned 'us')
  public void Paddsw(Reg d, Reg s) => this.MmxRegReg(0xED, d, s);
  public void Paddsw(Reg d, Mem s) => this.MmxRegMem(0xED, d, s);
  public void Paddusw(Reg d, Reg s) => this.MmxRegReg(0xDD, d, s);
  public void Paddusw(Reg d, Mem s) => this.MmxRegMem(0xDD, d, s);
  public void Psubsw(Reg d, Reg s) => this.MmxRegReg(0xE9, d, s);
  public void Psubsw(Reg d, Mem s) => this.MmxRegMem(0xE9, d, s);
  public void Psubusw(Reg d, Reg s) => this.MmxRegReg(0xD9, d, s);
  public void Psubusw(Reg d, Mem s) => this.MmxRegMem(0xD9, d, s);

  // packed shifts by an MMX/memory count
  public void Psllw(Reg d, Reg s) => this.MmxRegReg(0xF1, d, s);
  public void Pslld(Reg d, Reg s) => this.MmxRegReg(0xF2, d, s);
  public void Psllq(Reg d, Reg s) => this.MmxRegReg(0xF3, d, s);
  public void Psrlw(Reg d, Reg s) => this.MmxRegReg(0xD1, d, s);
  public void Psrld(Reg d, Reg s) => this.MmxRegReg(0xD2, d, s);
  public void Psrlq(Reg d, Reg s) => this.MmxRegReg(0xD3, d, s);
  public void Psraw(Reg d, Reg s) => this.MmxRegReg(0xE1, d, s);
  public void Psrad(Reg d, Reg s) => this.MmxRegReg(0xE2, d, s);

  // packed shifts by an immediate count (group 0F 71/72/73 with a /digit reg field)
  public void Psllw(Reg d, byte count) => this.MmxShiftImm(0x71, 6, d, count);
  public void Pslld(Reg d, byte count) => this.MmxShiftImm(0x72, 6, d, count);
  public void Psllq(Reg d, byte count) => this.MmxShiftImm(0x73, 6, d, count);
  public void Psrlw(Reg d, byte count) => this.MmxShiftImm(0x71, 2, d, count);
  public void Psrld(Reg d, byte count) => this.MmxShiftImm(0x72, 2, d, count);
  public void Psrlq(Reg d, byte count) => this.MmxShiftImm(0x73, 2, d, count);
  public void Psraw(Reg d, byte count) => this.MmxShiftImm(0x71, 4, d, count);
  public void Psrad(Reg d, byte count) => this.MmxShiftImm(0x72, 4, d, count);

  private void MmxShiftImm(byte op, int subOp, Reg dest, byte count) {
    this.EmitByte(0x0F);
    this.EmitByte(op);
    this.EmitByte((byte)(0xC0 | subOp << 3 | dest.Index()));
    this.EmitByte(count);
  }

  // pack/unpack
  public void Packsswb(Reg d, Reg s) => this.MmxRegReg(0x63, d, s);
  public void Packssdw(Reg d, Reg s) => this.MmxRegReg(0x6B, d, s);
  public void Packuswb(Reg d, Reg s) => this.MmxRegReg(0x67, d, s);
  public void Punpcklbw(Reg d, Reg s) => this.MmxRegReg(0x60, d, s);
  public void Punpcklwd(Reg d, Reg s) => this.MmxRegReg(0x61, d, s);
  public void Punpckldq(Reg d, Reg s) => this.MmxRegReg(0x62, d, s);
  public void Punpckhbw(Reg d, Reg s) => this.MmxRegReg(0x68, d, s);
  public void Punpckhwd(Reg d, Reg s) => this.MmxRegReg(0x69, d, s);
  public void Punpckhdq(Reg d, Reg s) => this.MmxRegReg(0x6A, d, s);
}
