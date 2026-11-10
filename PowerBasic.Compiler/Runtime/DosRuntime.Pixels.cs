using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

public sealed partial class DosRuntime {
  private static void EmitGraphicsMode(Assembler asm) {
    asm.MarkLabel("rt_graphics_mode");
    asm.Push(Reg.BX);
    asm.Mov(Reg.AH, (Imm)0x0F);
    asm.Int(0x10);
    asm.Xor(Reg.AH, Reg.AH);
    asm.Pop(Reg.BX);
    asm.Ret();
  }

  private static void EmitPackedModeBranch(Assembler asm, Label packed) {
    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Mov(Reg.AH, (Imm)0x0F);
    asm.Int(0x10);
    asm.Cmp(Reg.AL, (Imm)0x13);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Je(packed);
  }

  /// <summary>Checks the active BIOS mode while preserving the caller's coordinates.</summary>
  private static void EmitPlanarModeBranch(Assembler asm, Label planar) {
    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Mov(Reg.AH, (Imm)0x0F);
    asm.Int(0x10);
    asm.Cmp(Reg.AL, (Imm)0x12);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Je(planar);
  }

  /// <summary>
  /// Certify the *addressing hardware*, not merely BIOS SCREEN 12.
  /// Programs can reprogram VGA registers with OUT while AH=0Fh still
  /// reports 12h. Direct A000 planar writes require the 64K A000 map,
  /// no odd/even or chain-four addressing and planar shift mode.
  /// Restore both index selectors on every exit without changing any
  /// controller data register. All scratch registers are preserved.
  /// </summary>
  private static void EmitCertifiedPlanarBranch(Assembler asm, Label planar) {
    var noPlanar = asm.DefineLabel();

    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Push(Reg.DX);
    asm.Mov(Reg.AH, (Imm)0x0F);
    asm.Int(0x10);
    asm.Cmp(Reg.AL, (Imm)0x12);
    asm.Jne(noPlanar);

    // Graphics Controller 06h: A0000-AFFFF window, standard addressing,
    // graphics mode. Index port 3CEh is itself readable.
    asm.Mov(Reg.DX, 0x3CE);
    asm.In(Reg.AL, Reg.DX);
    asm.Mov(Reg.BL, Reg.AL);
    asm.Mov(Reg.AL, (Imm)6);
    asm.Out(Reg.DX, Reg.AL);
    asm.Inc(Reg.DX);
    asm.In(Reg.AL, Reg.DX);
    asm.And(Reg.AL, (Imm)0x0F);
    asm.Cmp(Reg.AL, (Imm)0x05);
    asm.Mov(Reg.DX, 0x3CE);
    asm.Mov(Reg.AL, Reg.BL);
    asm.Out(Reg.DX, Reg.AL);
    asm.Jne(noPlanar);

    // Sequencer 04h: odd/even disabled, chain four disabled.
    asm.Mov(Reg.DX, 0x3C4);
    asm.In(Reg.AL, Reg.DX);
    asm.Mov(Reg.BL, Reg.AL);
    asm.Mov(Reg.AL, (Imm)4);
    asm.Out(Reg.DX, Reg.AL);
    asm.Inc(Reg.DX);
    asm.In(Reg.AL, Reg.DX);
    asm.And(Reg.AL, (Imm)0x0C);
    asm.Cmp(Reg.AL, (Imm)0x04);
    asm.Mov(Reg.DX, 0x3C4);
    asm.Mov(Reg.AL, Reg.BL);
    asm.Out(Reg.DX, Reg.AL);
    asm.Jne(noPlanar);

    // Graphics Controller 05h: no odd/even or packed shift mode.
    asm.Mov(Reg.DX, 0x3CE);
    asm.In(Reg.AL, Reg.DX);
    asm.Mov(Reg.BL, Reg.AL);
    asm.Mov(Reg.AL, (Imm)5);
    asm.Out(Reg.DX, Reg.AL);
    asm.Inc(Reg.DX);
    asm.In(Reg.AL, Reg.DX);
    asm.Test(Reg.AL, (Imm)0x70);
    asm.Mov(Reg.DX, 0x3CE);
    asm.Mov(Reg.AL, Reg.BL);
    asm.Out(Reg.DX, Reg.AL);
    asm.Jne(noPlanar);

    asm.Pop(Reg.DX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Jmp(planar);

    asm.MarkLabel(noPlanar);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
  }

  private static void EmitPackedPixelWriter(Assembler asm) {
    // The IR runtime body computes the offset; only the volatile video write stays opaque.
    asm.MarkLabel("rt_pset_mode13_offset");
    asm.Push(Reg.ES);
    asm.Push(Reg.DI);
    asm.Push(Reg.BX);
    asm.Mov(Reg.DI, Reg.AX);
    asm.Mov(Reg.BX, 0xA000);
    asm.Mov(Reg.ES, Reg.BX);
    asm.Mov(Mem.Byte(Reg.DI).Seg(Reg.ES), Reg.DL);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.ES);
    asm.Ret();
  }

  private void EmitSetPixel(Assembler asm) {
    this.Pset = asm.MarkLabel("rt_pset"); // AX=x, BX=y, DL=color; retain the caller's coordinates.
    var packed = asm.DefineLabel();
    var planar = this.EnableTargetOptimizations ? asm.DefineLabel() : null;
    var bios = asm.DefineLabel();
    EmitPackedModeBranch(asm, packed);
    if (planar is not null) {
      asm.Cmp(Reg.AX, (Imm)0);
      asm.Jl(bios);
      asm.Cmp(Reg.AX, (Imm)639);
      asm.Jg(bios);
      asm.Cmp(Reg.BX, (Imm)0);
      asm.Jl(bios);
      asm.Cmp(Reg.BX, (Imm)479);
      asm.Jg(bios);
      asm.Cmp(Reg.DL, (Imm)15);
      asm.Ja(bios); // Preserve high-bit XOR and unknown color semantics.
      EmitCertifiedPlanarBranch(asm, planar);
    }
    asm.MarkLabel(bios);
    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Mov(Reg.CX, Reg.AX);
    asm.Mov(Reg.AX, Reg.DX);
    asm.Mov(Reg.DX, Reg.BX);
    asm.Xor(Reg.BX, Reg.BX); // Page zero; firmware handles the active mode's pixel encoding.
    asm.Mov(Reg.AH, (Imm)0x0C);
    asm.Int(0x10);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Ret();
    asm.MarkLabel(packed);
    asm.Push(Reg.ES);
    asm.Push(Reg.DI);
    asm.Push(Reg.CX);
    asm.Push(Reg.BX);
    asm.Mov(Reg.DI, Reg.BX); // Retain the existing packed y*320+x implementation.
    asm.Mov(Reg.CL, (Imm)8);
    asm.Shl(Reg.DI, Reg.CL);
    asm.Mov(Reg.CL, (Imm)6);
    asm.Shl(Reg.BX, Reg.CL);
    asm.Add(Reg.DI, Reg.BX);
    asm.Add(Reg.DI, Reg.AX);
    asm.Mov(Reg.BX, 0xA000);
    asm.Mov(Reg.ES, Reg.BX);
    asm.Mov(Mem.Byte(Reg.DI).Seg(Reg.ES), Reg.DL);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.ES);
    asm.Ret();
    if (planar is not null)
      EmitPlanarPixelWrite(asm, planar);
  }

  private void EmitReadPixel(Assembler asm) {
    this.Point = asm.MarkLabel("rt_point"); // AX=x, BX=y -> DX:AX=color.
    var packed = asm.DefineLabel();
    // This entry may also be called by PAINT after it has already certified
    // the VGA addressing configuration for its entire bounded flood fill.
    var planar = this.EnableTargetOptimizations ? asm.Lbl("rt_point_planar_certified") : null;
    var bios = asm.DefineLabel();
    EmitPackedModeBranch(asm, packed);
    if (planar is not null) {
      asm.Cmp(Reg.AX, (Imm)0);
      asm.Jl(bios);
      asm.Cmp(Reg.AX, (Imm)639);
      asm.Jg(bios);
      asm.Cmp(Reg.BX, (Imm)0);
      asm.Jl(bios);
      asm.Cmp(Reg.BX, (Imm)479);
      asm.Jg(bios);
      EmitCertifiedPlanarBranch(asm, planar);
    }
    asm.MarkLabel(bios);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Mov(Reg.CX, Reg.AX);
    asm.Mov(Reg.DX, Reg.BX);
    asm.Xor(Reg.BX, Reg.BX);
    asm.Mov(Reg.AH, (Imm)0x0D);
    asm.Int(0x10);
    asm.Xor(Reg.AH, Reg.AH);
    asm.Xor(Reg.DX, Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Ret();
    asm.MarkLabel(packed);
    asm.Push(Reg.ES);
    asm.Push(Reg.DI);
    asm.Push(Reg.CX);
    asm.Push(Reg.BX);
    asm.Mov(Reg.DI, Reg.BX);
    asm.Mov(Reg.CL, (Imm)8);
    asm.Shl(Reg.DI, Reg.CL);
    asm.Mov(Reg.CL, (Imm)6);
    asm.Shl(Reg.BX, Reg.CL);
    asm.Add(Reg.DI, Reg.BX);
    asm.Add(Reg.DI, Reg.AX);
    asm.Mov(Reg.BX, 0xA000);
    asm.Mov(Reg.ES, Reg.BX);
    asm.Mov(Reg.AL, Mem.Byte(Reg.DI).Seg(Reg.ES));
    asm.Xor(Reg.AH, Reg.AH);
    asm.Xor(Reg.DX, Reg.DX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.ES);
    asm.Ret();
    if (planar is not null)
      EmitPlanarPixelRead(asm, planar);
  }
}
