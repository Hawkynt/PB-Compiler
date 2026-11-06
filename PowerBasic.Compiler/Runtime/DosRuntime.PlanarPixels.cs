using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

public sealed partial class DosRuntime {
  /// <summary>
  /// Single-pixel Mode 12h write using VGA write mode 2: one bit-mask
  /// write selects the addressed pixel in all four planes at once.
  /// The caller already certified the 640x480 bounds, a normal color
  /// and the active VGA memory map. BIOS fallback handles all other cases.
  /// </summary>
  private static void EmitPlanarPixelWrite(Assembler asm, Label entry) {
    asm.MarkLabel(entry);
    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);

    asm.Mov(Mem.Byte(asm.Lbl("rt_planar_pset_color")), Reg.DL);
    asm.Mov(Reg.DI, Reg.AX);
    asm.Mov(Reg.CX, Reg.AX);
    asm.And(Reg.CX, (Imm)7);
    asm.Mov(Reg.AL, (Imm)0x80);
    asm.Shr(Reg.AL, Reg.CL);
    asm.Mov(Mem.Byte(asm.Lbl("rt_planar_pset_mask")), Reg.AL);

    asm.Mov(Reg.AX, Reg.BX);
    asm.Mov(Reg.BX, 80);
    asm.Mul(Reg.BX);
    asm.Mov(Reg.CL, (Imm)3);
    asm.Shr(Reg.DI, Reg.CL);
    asm.Add(Reg.DI, Reg.AX);

    asm.Mov(Reg.DX, 0x3CE);
    asm.In(Reg.AL, Reg.DX);
    asm.Push(Reg.AX); // Graphics Controller index.
    asm.Mov(Reg.DX, 0x3C4);
    asm.In(Reg.AL, Reg.DX);
    asm.Push(Reg.AX); // Sequencer index.

    SaveAndSet(asm, 0x3CE, 5, -1); // Write mode 2, all four color bits.
    SaveAndSet(asm, 0x3CE, 3, 0);  // Replace, no data rotation.
    SaveAndSet(asm, 0x3CE, 8, -4); // One selected pixel.
    SaveAndSet(asm, 0x3C4, 2, 15); // Enable all four planes.

    asm.Mov(Reg.AX, 0xA000);
    asm.Mov(Reg.ES, Reg.AX);
    asm.Mov(Reg.AL, Mem.Byte(Reg.DI).Seg(Reg.ES)); // Load VGA latches.
    asm.Mov(Reg.AL, Mem.Byte(asm.Lbl("rt_planar_pset_color")));
    asm.Mov(Mem.Byte(Reg.DI).Seg(Reg.ES), Reg.AL);

    Restore(asm, 0x3C4, 2);
    Restore(asm, 0x3CE, 8);
    Restore(asm, 0x3CE, 3);
    Restore(asm, 0x3CE, 5);

    asm.Pop(Reg.BX);
    asm.Mov(Reg.DX, 0x3C4);
    asm.Mov(Reg.AL, Reg.BL);
    asm.Out(Reg.DX, Reg.AL);
    asm.Pop(Reg.BX);
    asm.Mov(Reg.DX, 0x3CE);
    asm.Mov(Reg.AL, Reg.BL);
    asm.Out(Reg.DX, Reg.AL);

    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Ret();

    asm.MarkLabel("rt_planar_pset_color");
    asm.Db(0);
    asm.MarkLabel("rt_planar_pset_mask");
    asm.Db(0);
  }

  /// <summary>
  /// Single-pixel Mode 12h read. VGA read mode 0 selects exactly one
  /// plane through GC register 4; gather the four bits for a palette
  /// index while retaining the original four-plane readback semantics.
  /// </summary>
  private static void EmitPlanarPixelRead(Assembler asm, Label entry) {
    var skip = new Label[4];
    for (var plane = 0; plane < skip.Length; ++plane)
      skip[plane] = asm.DefineLabel();

    asm.MarkLabel(entry);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);

    asm.Mov(Reg.SI, Reg.AX);
    asm.Mov(Reg.CX, Reg.AX);
    asm.And(Reg.CX, (Imm)7);
    asm.Mov(Reg.AL, (Imm)0x80);
    asm.Shr(Reg.AL, Reg.CL);
    asm.Mov(Mem.Byte(asm.Lbl("rt_planar_point_mask")), Reg.AL);

    asm.Mov(Reg.AX, Reg.BX);
    asm.Mov(Reg.BX, 80);
    asm.Mul(Reg.BX);
    asm.Mov(Reg.DI, Reg.SI);
    asm.Mov(Reg.CL, (Imm)3);
    asm.Shr(Reg.DI, Reg.CL);
    asm.Add(Reg.DI, Reg.AX);

    asm.Mov(Reg.DX, 0x3CE);
    asm.In(Reg.AL, Reg.DX);
    asm.Push(Reg.AX); // GC selector.
    SaveAndSet(asm, 0x3CE, 5, -5); // Read mode 0.
    SaveAndSet(asm, 0x3CE, 4, 0);  // Read Map Select, plane zero.

    asm.Mov(Reg.AX, 0xA000);
    asm.Mov(Reg.ES, Reg.AX);
    asm.Xor(Reg.BX, Reg.BX); // Palette result.

    // Keep GC register 4 selected while visiting four plane bytes, not
    // one controller-register selection per pixel bit.
    asm.Mov(Reg.DX, 0x3CE);
    asm.Mov(Reg.AL, (Imm)4);
    asm.Out(Reg.DX, Reg.AL);
    asm.Inc(Reg.DX);
    for (var plane = 0; plane < 4; ++plane) {
      asm.Mov(Reg.AL, (Imm)plane);
      asm.Out(Reg.DX, Reg.AL);
      asm.Mov(Reg.AL, Mem.Byte(Reg.DI).Seg(Reg.ES));
      asm.And(Reg.AL, Mem.Byte(asm.Lbl("rt_planar_point_mask")));
      asm.Jz(skip[plane]);
      asm.Or(Reg.BX, (Imm)(1 << plane));
      asm.MarkLabel(skip[plane]);
    }

    asm.Mov(Reg.SI, Reg.BX); // Retain result through register restoration.
    Restore(asm, 0x3CE, 4);
    Restore(asm, 0x3CE, 5);

    asm.Pop(Reg.BX);
    asm.Mov(Reg.DX, 0x3CE);
    asm.Mov(Reg.AL, Reg.BL);
    asm.Out(Reg.DX, Reg.AL);

    asm.Mov(Reg.AX, Reg.SI);
    asm.Xor(Reg.DX, Reg.DX);
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Ret();

    asm.MarkLabel("rt_planar_point_mask");
    asm.Db(0);
  }
}
