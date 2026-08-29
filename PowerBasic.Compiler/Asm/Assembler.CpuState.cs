namespace PowerBasic.Compiler.Asm;

public sealed partial class Assembler {
  /// <summary>
  /// Enables the architectural state required by SSE/AVX in real mode. A DOS image has no host OS
  /// to set CR0/CR4/XCR0 for it, so merely emitting a VEX instruction is not sufficient. The target
  /// contract guarantees that these control registers/instructions exist on the selected CPU.
  /// EAX/ECX/EDX are preserved; flags are intentionally unspecified during process startup.
  /// </summary>
  public void EnableExtendedVectorState(bool avx, bool avx512) {
    this.Push(Reg.EAX);
    this.Push(Reg.ECX);
    this.Push(Reg.EDX);

    // MOV EAX,CR0 ; clear EM+TS, set MP ; MOV CR0,EAX
    this.EmitByte(0x0F); this.EmitByte(0x20); this.EmitByte(0xC0);
    this.And(Reg.EAX, unchecked((int)0xFFFFFFF3));
    this.Or(Reg.EAX, 0x00000002);
    this.EmitByte(0x0F); this.EmitByte(0x22); this.EmitByte(0xC0);

    // MOV EAX,CR4 ; OSFXSR | OSXMMEXCPT [+ OSXSAVE] ; MOV CR4,EAX
    this.EmitByte(0x0F); this.EmitByte(0x20); this.EmitByte(0xE0);
    this.Or(Reg.EAX, avx ? 0x00040600 : 0x00000600);
    this.EmitByte(0x0F); this.EmitByte(0x22); this.EmitByte(0xE0);

    if (avx) {
      this.Xor(Reg.ECX, Reg.ECX);
      // XGETBV (ECX=0) -> EDX:EAX
      this.EmitByte(0x0F); this.EmitByte(0x01); this.EmitByte(0xD0);
      // x87 + SSE + YMM; AVX-512 additionally requires opmask, ZMM_Hi256 and Hi16_ZMM state.
      this.Or(Reg.EAX, avx512 ? 0x000000E7 : 0x00000007);
      // XSETBV
      this.EmitByte(0x0F); this.EmitByte(0x01); this.EmitByte(0xD1);
    }

    this.Pop(Reg.EDX);
    this.Pop(Reg.ECX);
    this.Pop(Reg.EAX);
  }
}
