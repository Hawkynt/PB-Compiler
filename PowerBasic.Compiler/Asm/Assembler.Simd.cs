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

  #region SSE2 packed-integer (128-bit XMM)

  // The SSE2 packed-integer ops are the MMX opcodes with a mandatory 66 prefix and XMM operands.
  private void Sse2RegReg(byte op, Reg dest, Reg src) {
    this.EmitByte(0x66);
    this.EmitByte(0x0F);
    this.EmitByte(op);
    this.EmitModRmRegister(dest.Index(), src);
  }

  private void Sse2RegMem(byte op, Reg dest, Mem src) {
    this.EmitByte(0x66);
    this.EmitByte(0x0F);
    this.EmitByte(op);
    this.EmitModRmMemory(dest.Index(), src);
  }

  /// <summary>MOVDQA xmm, xmm/m128 (66 0F 6F): aligned 128-bit move.</summary>
  public void Movdqa(Reg dst, Reg src) => this.Sse2RegReg(0x6F, dst, src);
  public void Movdqa(Reg dst, Mem src) => this.Sse2RegMem(0x6F, dst, src);
  /// <summary>MOVDQA xmm/m128, xmm (66 0F 7F): aligned 128-bit store.</summary>
  public void MovdqaStore(Mem dst, Reg src) => this.Sse2RegMem(0x7F, src, dst);

  /// <summary>MOVDQU xmm, m128 (F3 0F 6F): unaligned 128-bit load.</summary>
  public void Movdqu(Reg dst, Mem src) {
    this.EmitByte(0xF3);
    this.EmitByte(0x0F);
    this.EmitByte(0x6F);
    this.EmitModRmMemory(dst.Index(), src);
  }

  /// <summary>MOVDQU m128, xmm (F3 0F 7F): unaligned 128-bit store.</summary>
  public void MovdquStore(Mem dst, Reg src) {
    this.EmitByte(0xF3);
    this.EmitByte(0x0F);
    this.EmitByte(0x7F);
    this.EmitModRmMemory(src.Index(), dst);
  }

  /// <summary>MOVD xmm, r/m32 (66 0F 6E).</summary>
  public void MovdX(Reg dstXmm, Reg srcGp32) {
    this.EmitByte(0x66);
    this.EmitByte(0x0F);
    this.EmitByte(0x6E);
    this.EmitModRmRegister(dstXmm.Index(), srcGp32);
  }
  public void MovdX(Reg dstXmm, Mem src) => this.Sse2RegMem(0x6E, dstXmm, src);

  /// <summary>MOVD r/m32, xmm (66 0F 7E).</summary>
  public void MovdXStore(Reg dstGp32, Reg srcXmm) {
    this.EmitByte(0x66);
    this.EmitByte(0x0F);
    this.EmitByte(0x7E);
    this.EmitModRmRegister(srcXmm.Index(), dstGp32);
  }
  public void MovdXStore(Mem dst, Reg srcXmm) => this.Sse2RegMem(0x7E, srcXmm, dst);

  // packed add/subtract (byte/word/dword/qword lanes; SSE2 adds the 64-bit-lane PADDQ/PSUBQ)
  public void PaddbX(Reg d, Reg s) => this.Sse2RegReg(0xFC, d, s);
  public void PaddbX(Reg d, Mem s) => this.Sse2RegMem(0xFC, d, s);
  public void PaddwX(Reg d, Reg s) => this.Sse2RegReg(0xFD, d, s);
  public void PaddwX(Reg d, Mem s) => this.Sse2RegMem(0xFD, d, s);
  public void PadddX(Reg d, Reg s) => this.Sse2RegReg(0xFE, d, s);
  public void PadddX(Reg d, Mem s) => this.Sse2RegMem(0xFE, d, s);
  public void PaddqX(Reg d, Reg s) => this.Sse2RegReg(0xD4, d, s);
  public void PaddqX(Reg d, Mem s) => this.Sse2RegMem(0xD4, d, s);
  public void PsubbX(Reg d, Reg s) => this.Sse2RegReg(0xF8, d, s);
  public void PsubbX(Reg d, Mem s) => this.Sse2RegMem(0xF8, d, s);
  public void PsubwX(Reg d, Reg s) => this.Sse2RegReg(0xF9, d, s);
  public void PsubwX(Reg d, Mem s) => this.Sse2RegMem(0xF9, d, s);
  public void PsubdX(Reg d, Reg s) => this.Sse2RegReg(0xFA, d, s);
  public void PsubdX(Reg d, Mem s) => this.Sse2RegMem(0xFA, d, s);
  public void PsubqX(Reg d, Reg s) => this.Sse2RegReg(0xFB, d, s);
  public void PsubqX(Reg d, Mem s) => this.Sse2RegMem(0xFB, d, s);

  public void PmullwX(Reg d, Reg s) => this.Sse2RegReg(0xD5, d, s);
  public void PmullwX(Reg d, Mem s) => this.Sse2RegMem(0xD5, d, s);

  public void PandX(Reg d, Reg s) => this.Sse2RegReg(0xDB, d, s);
  public void PandX(Reg d, Mem s) => this.Sse2RegMem(0xDB, d, s);
  public void PandnX(Reg d, Reg s) => this.Sse2RegReg(0xDF, d, s);
  public void PandnX(Reg d, Mem s) => this.Sse2RegMem(0xDF, d, s);
  public void PorX(Reg d, Reg s) => this.Sse2RegReg(0xEB, d, s);
  public void PorX(Reg d, Mem s) => this.Sse2RegMem(0xEB, d, s);
  public void PxorX(Reg d, Reg s) => this.Sse2RegReg(0xEF, d, s);
  public void PxorX(Reg d, Mem s) => this.Sse2RegMem(0xEF, d, s);

  public void PcmpeqbX(Reg d, Reg s) => this.Sse2RegReg(0x74, d, s);
  public void PcmpeqwX(Reg d, Reg s) => this.Sse2RegReg(0x75, d, s);
  public void PcmpeqdX(Reg d, Reg s) => this.Sse2RegReg(0x76, d, s);
  public void PcmpgtbX(Reg d, Reg s) => this.Sse2RegReg(0x64, d, s);
  public void PcmpgtwX(Reg d, Reg s) => this.Sse2RegReg(0x65, d, s);
  public void PcmpgtdX(Reg d, Reg s) => this.Sse2RegReg(0x66, d, s);

  // packed shift by immediate (group 66 0F 71/72/73 with a /digit reg field)
  public void PsllwX(Reg d, byte n) => this.Sse2ShiftImm(0x71, 6, d, n);
  public void PslldX(Reg d, byte n) => this.Sse2ShiftImm(0x72, 6, d, n);
  public void PsllqX(Reg d, byte n) => this.Sse2ShiftImm(0x73, 6, d, n);
  public void PsrlwX(Reg d, byte n) => this.Sse2ShiftImm(0x71, 2, d, n);
  public void PsrldX(Reg d, byte n) => this.Sse2ShiftImm(0x72, 2, d, n);
  public void PsrlqX(Reg d, byte n) => this.Sse2ShiftImm(0x73, 2, d, n);
  public void PsrawX(Reg d, byte n) => this.Sse2ShiftImm(0x71, 4, d, n);
  public void PsradX(Reg d, byte n) => this.Sse2ShiftImm(0x72, 4, d, n);

  private void Sse2ShiftImm(byte op, int subOp, Reg dest, byte count) {
    this.EmitByte(0x66);
    this.EmitByte(0x0F);
    this.EmitByte(op);
    this.EmitByte((byte)(0xC0 | subOp << 3 | dest.Index()));
    this.EmitByte(count);
  }

  #endregion

  #region width-dispatched packed ops (inline asm: same mnemonic, MMX or XMM by operand)

  /// <summary>Emits a packed-integer op (<c>0F op /r</c>) as MMX or, for an XMM destination, the 66-prefixed SSE2 form.</summary>
  public void EmitPacked(byte op, Reg dest, Reg src) {
    if (dest.IsXmm())
      this.Sse2RegReg(op, dest, src);
    else
      this.MmxRegReg(op, dest, src);
  }

  public void EmitPacked(byte op, Reg dest, Mem src) {
    if (dest.IsXmm())
      this.Sse2RegMem(op, dest, src);
    else
      this.MmxRegMem(op, dest, src);
  }

  /// <summary>Emits a packed shift-by-immediate (group <c>0F op /subOp ib</c>) as MMX or, for XMM, the 66-prefixed SSE2 form.</summary>
  public void EmitPackedShiftImm(byte op, int subOp, Reg dest, byte count) {
    if (dest.IsXmm())
      this.Sse2ShiftImm(op, subOp, dest, count);
    else
      this.MmxShiftImm(op, subOp, dest, count);
  }

  #endregion

  #region AVX (VEX-encoded, 3-operand, 128/256-bit)

  /// <summary>
  /// Emits the 2-byte VEX prefix (<c>C5</c>) for a <c>VEX.&lt;L&gt;.66.0F.WIG</c> packed-integer op.
  /// Valid for registers 0..7 in 16-bit mode (no REX.R/B/X extension, the <c>0F</c> escape, W ignored).
  /// <paramref name="src1"/> is the non-destructive first source encoded in VEX.vvvv (use the destination
  /// itself for a two-operand op, with vvvv = 1111 / "unused").
  /// </summary>
  private void VexPrefix(Reg src1OrNone, bool l256, int pp = 0b01) {
    var vvvv = src1OrNone == Reg.AL ? 0xF : src1OrNone.Index(); // AL sentinel = "no vvvv" (1111)
    var byte1 = (1 << 7)                         // ~VEX.R (R=0 for reg<8)
              | ((~vvvv & 0xF) << 3)             // ~vvvv
              | ((l256 ? 1 : 0) << 2)            // L: 1 = 256-bit, 0 = 128-bit
              | pp;                              // pp: 01 = 66 prefix
    this.EmitByte(0xC5);
    this.EmitByte((byte)byte1);
  }

  /// <summary>VEX 3-operand packed op: <c>dest = src1 OP src2</c> (src2 an XMM/YMM register).</summary>
  public void VexPacked(byte op, Reg dest, Reg src1, Reg src2) {
    this.VexPrefix(src1, dest.IsYmm());
    this.EmitByte(op);
    this.EmitModRmRegister(dest.Index(), src2);
  }

  /// <summary>VEX 3-operand packed op with a memory second source.</summary>
  public void VexPacked(byte op, Reg dest, Reg src1, Mem src2) {
    this.VexPrefix(src1, dest.IsYmm());
    this.EmitByte(op);
    this.EmitModRmMemory(dest.Index(), src2);
  }

  /// <summary>VMOVDQA dest, src (VEX.&lt;L&gt;.66.0F.WIG 6F): aligned 128/256-bit move (two-operand, vvvv unused).</summary>
  public void Vmovdqa(Reg dest, Reg src) {
    this.VexPrefix(Reg.AL, dest.IsYmm());
    this.EmitByte(0x6F);
    this.EmitModRmRegister(dest.Index(), src);
  }
  public void Vmovdqa(Reg dest, Mem src) {
    this.VexPrefix(Reg.AL, dest.IsYmm());
    this.EmitByte(0x6F);
    this.EmitModRmMemory(dest.Index(), src);
  }
  public void VmovdqaStore(Mem dest, Reg src) {
    this.VexPrefix(Reg.AL, src.IsYmm());
    this.EmitByte(0x7F);
    this.EmitModRmMemory(src.Index(), dest);
  }

  /// <summary>VMOVDQU dest, src (VEX.&lt;L&gt;.F3.0F.WIG 6F): unaligned 128/256-bit move (pp = 10).</summary>
  public void Vmovdqu(Reg dest, Mem src) {
    this.VexPrefix(Reg.AL, dest.IsYmm(), pp: 0b10);
    this.EmitByte(0x6F);
    this.EmitModRmMemory(dest.Index(), src);
  }
  public void VmovdquStore(Mem dest, Reg src) {
    this.VexPrefix(Reg.AL, src.IsYmm(), pp: 0b10);
    this.EmitByte(0x7F);
    this.EmitModRmMemory(src.Index(), dest);
  }

  #endregion

  #region AVX-512 (EVEX-encoded, 512-bit ZMM)

  /// <summary>
  /// Emits the 4-byte EVEX prefix (<c>62 P0 P1 P2</c>) for an <c>EVEX.512.66.0F.W0</c> packed-integer
  /// op with no mask, broadcast or zeroing - the AVX-512 form of the VEX ops on the 512-bit ZMM
  /// registers. Valid for ZMM0..ZMM7 (no R/X/B/R'/V' high-register extension in 16-bit mode).
  /// </summary>
  private void EvexPrefix(Reg src1OrNone, int pp = 0b01) {
    var unused = src1OrNone == Reg.AL;
    var vvvv = unused ? 0xF : src1OrNone.Index();
    this.EmitByte(0x62);
    // P0 = R̄ X̄ B̄ R̄' 0 0 mm   (all inverted high bits = 1 for low regs; mm = 01 -> the 0F map)
    this.EmitByte(0b1111_00_01);
    // P1 = W vvvv̄ 1 pp          (W = 0; the mandatory bit-2 set; pp = 66 -> 01)
    this.EmitByte((byte)((0 << 7) | ((~vvvv & 0xF) << 3) | (1 << 2) | pp));
    // P2 = z L'L b V̄' aaa       (z=0, L'L=10 -> 512-bit, b=0, mask aaa=000). V' is the inverted 5th
    // vvvv bit: a low-register src1 (v4=0) -> V'=1; the unused (1111) vvvv of a 2-operand move -> V'=0.
    this.EmitByte((byte)(0b0_10_0_0_000 | ((unused ? 0 : 1) << 3)));
  }

  /// <summary>EVEX 3-operand packed op on ZMM: <c>dest = src1 OP src2</c> (src2 a ZMM register).</summary>
  public void EvexPacked(byte op, Reg dest, Reg src1, Reg src2) {
    this.EvexPrefix(src1);
    this.EmitByte(op);
    this.EmitModRmRegister(dest.Index(), src2);
  }

  public void EvexPacked(byte op, Reg dest, Reg src1, Mem src2) {
    this.EvexPrefix(src1);
    this.EmitByte(op);
    this.EmitModRmMemory(dest.Index(), src2);
  }

  /// <summary>VMOVDQA32 zmm, zmm/m512 (EVEX.512.66.0F.W0 6F) - the 512-bit aligned move (two-operand).</summary>
  public void Vmovdqa512(Reg dest, Reg src) {
    this.EvexPrefix(Reg.AL);
    this.EmitByte(0x6F);
    this.EmitModRmRegister(dest.Index(), src);
  }
  public void Vmovdqa512(Reg dest, Mem src) {
    this.EvexPrefix(Reg.AL);
    this.EmitByte(0x6F);
    this.EmitModRmMemory(dest.Index(), src);
  }
  public void Vmovdqa512Store(Mem dest, Reg src) {
    this.EvexPrefix(Reg.AL);
    this.EmitByte(0x7F);
    this.EmitModRmMemory(src.Index(), dest);
  }

  /// <summary>VMOVDQU32 zmm, m512 (EVEX.512.F3.0F.W0 6F) - the 512-bit unaligned move (pp = 10).</summary>
  public void Vmovdqu512(Reg dest, Mem src) {
    this.EvexPrefix(Reg.AL, pp: 0b10);
    this.EmitByte(0x6F);
    this.EmitModRmMemory(dest.Index(), src);
  }
  public void Vmovdqu512Store(Mem dest, Reg src) {
    this.EvexPrefix(Reg.AL, pp: 0b10);
    this.EmitByte(0x7F);
    this.EmitModRmMemory(src.Index(), dest);
  }

  #endregion
}
