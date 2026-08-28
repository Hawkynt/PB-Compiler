namespace PowerBasic.Compiler.Asm;

public sealed partial class Assembler {

  /// <summary>MOVDQU with an explicit memory-segment prefix when present.</summary>
  public void MovdquTarget(Reg destination, Mem source) {
    this.EmitSegmentPrefix(source);
    this.EmitByte(0xF3);
    this.EmitByte(0x0F);
    this.EmitByte(0x6F);
    this.EmitModRmMemory(destination.Index(), source);
  }

  /// <summary>MOVDQU store with an explicit memory-segment prefix when present.</summary>
  public void MovdquTargetStore(Mem destination, Reg source) {
    this.EmitSegmentPrefix(destination);
    this.EmitByte(0xF3);
    this.EmitByte(0x0F);
    this.EmitByte(0x7F);
    this.EmitModRmMemory(source.Index(), destination);
  }

  /// <summary>VMOVDQU XMM/YMM load with an explicit memory-segment prefix when present.</summary>
  public void VmovdquTarget(Reg destination, Mem source) {
    this.EmitSegmentPrefix(source);
    this.VexPrefix(Reg.AL, destination.IsYmm(), pp: 0b10);
    this.EmitByte(0x6F);
    this.EmitModRmMemory(destination.Index(), source);
  }

  /// <summary>VMOVDQU XMM/YMM store with an explicit memory-segment prefix when present.</summary>
  public void VmovdquTargetStore(Mem destination, Reg source) {
    this.EmitSegmentPrefix(destination);
    this.VexPrefix(Reg.AL, source.IsYmm(), pp: 0b10);
    this.EmitByte(0x7F);
    this.EmitModRmMemory(source.Index(), destination);
  }

  /// <summary>EVEX VMOVDQU ZMM load with an explicit memory-segment prefix when present.</summary>
  public void Vmovdqu512Target(Reg destination, Mem source) {
    this.EmitSegmentPrefix(source);
    this.EvexPrefix(Reg.AL, pp: 0b10);
    this.EmitByte(0x6F);
    this.EmitModRmMemory(destination.Index(), source);
  }

  /// <summary>EVEX VMOVDQU ZMM store with an explicit memory-segment prefix when present.</summary>
  public void Vmovdqu512TargetStore(Mem destination, Reg source) {
    this.EmitSegmentPrefix(destination);
    this.EvexPrefix(Reg.AL, pp: 0b10);
    this.EmitByte(0x7F);
    this.EmitModRmMemory(source.Index(), destination);
  }

  /// <summary>Zeros an XMM/YMM/ZMM register using the cheapest ISA-appropriate xor idiom.</summary>
  public void VectorZeroTarget(Reg vector) {
    if (vector.IsZmm()) {
      // VXORPS zmm,zmm,zmm: EVEX.512.0F.W0 57 /r. AVX-512F is sufficient.
      this.EvexPrefix(vector, pp: 0b00);
      this.EmitByte(0x57);
      this.EmitModRmRegister(vector.Index(), vector);
      return;
    }
    if (vector.IsYmm()) {
      // VXORPS ymm,ymm,ymm: AVX, unlike VPXOR ymm which would unnecessarily require AVX2.
      this.VexPrefix(vector, l256: true, pp: 0b00);
      this.EmitByte(0x57);
      this.EmitModRmRegister(vector.Index(), vector);
      return;
    }
    this.PxorX(vector, vector); // SSE2 PXOR xmm,xmm
  }
}
