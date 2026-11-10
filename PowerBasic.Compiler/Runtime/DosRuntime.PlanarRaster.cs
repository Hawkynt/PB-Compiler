using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

public sealed partial class DosRuntime {
  /// <summary>
  /// Enter a certified SCREEN 12 planar drawing region. The caller must
  /// preserve the register values it needs after entry, keep the region
  /// free of calls that modify VGA state, and balance with End. Register
  /// indices and values are stacked in precisely the reverse restoration order.
  /// </summary>
  private static void EmitPlanarRasterBegin(Assembler asm) {
    asm.Push(Reg.ES);

    asm.Mov(Reg.DX, 0x3CE);
    asm.In(Reg.AL, Reg.DX);
    asm.Push(Reg.AX);
    asm.Mov(Reg.DX, 0x3C4);
    asm.In(Reg.AL, Reg.DX);
    asm.Push(Reg.AX);

    SaveAndSet(asm, 0x3CE, 5, -2); // Mode 3; preserve read-mode bits.
    SaveAndSet(asm, 0x3CE, 3, 0);  // Rotate 0, replace.
    SaveAndSet(asm, 0x3CE, 8, 255);
    SaveAndSet(asm, 0x3CE, 0, -3); // Constant color for this primitive.
    SaveAndSet(asm, 0x3C4, 2, 15); // All four planes.

    asm.Mov(Mem.Word(asm.Lbl("rt_planar_raster_pending")), (Imm)0xFFFF);
    asm.Mov(Mem.Byte(asm.Lbl("rt_planar_raster_mask")), (Imm)0);
    asm.Mov(Reg.AX, 0xA000);
    asm.Mov(Reg.ES, Reg.AX);
  }

  /// <summary>
  /// Commit the last byte, restore all changed VGA register values and index
  /// selectors, then restore ES. Everything else is scratch inside the
  /// caller's saved-register envelope.
  /// </summary>
  private static void EmitPlanarRasterEnd(Assembler asm) {
    asm.Call(asm.Lbl("rt_planar_raster_flush"));

    Restore(asm, 0x3C4, 2);
    Restore(asm, 0x3CE, 0);
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
  }

  /// <summary>
  /// Common mask accumulator for Bresenham lines, CIRCLE and elliptical
  /// arcs. AX=x, BX=y, coordinates already clipped to SCREEN 12. Preserves
  /// every caller register. The output order follows the input pixel stream:
  /// a pending byte is written before switching to any different VGA byte.
  /// </summary>
  private static void EmitPlanarRasterHelpers(Assembler asm) {
    var sameByte = asm.DefineLabel();
    var empty = asm.DefineLabel();

    asm.MarkLabel("rt_planar_raster_plot");
    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);

    asm.Mov(Reg.SI, Reg.AX);
    asm.Mov(Reg.AX, Reg.BX);
    asm.Mov(Reg.BX, 80);
    asm.Mul(Reg.BX);
    asm.Mov(Reg.DI, Reg.AX);
    asm.Mov(Reg.AX, Reg.SI);
    asm.Mov(Reg.CL, (Imm)3);
    asm.Shr(Reg.AX, Reg.CL);
    asm.Add(Reg.DI, Reg.AX);

    asm.Cmp(Reg.DI, Mem.Word(asm.Lbl("rt_planar_raster_pending")));
    asm.Je(sameByte);
    asm.Call(asm.Lbl("rt_planar_raster_flush"));
    asm.Mov(Mem.Word(asm.Lbl("rt_planar_raster_pending")), Reg.DI);

    asm.MarkLabel(sameByte);
    asm.Mov(Reg.CX, Reg.SI);
    asm.And(Reg.CX, (Imm)7);
    asm.Mov(Reg.AL, (Imm)0x80);
    asm.Shr(Reg.AL, Reg.CL);
    asm.Or(Mem.Byte(asm.Lbl("rt_planar_raster_mask")), Reg.AL);

    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Ret();

    asm.MarkLabel("rt_planar_raster_flush");
    asm.Push(Reg.AX);
    asm.Push(Reg.DI);
    asm.Cmp(Mem.Byte(asm.Lbl("rt_planar_raster_mask")), (Imm)0);
    asm.Je(empty);

    asm.Mov(Reg.DI, Mem.Word(asm.Lbl("rt_planar_raster_pending")));
    asm.Mov(Reg.AL, Mem.Byte(Reg.DI).Seg(Reg.ES)); // Load all VGA latches.
    asm.Mov(Reg.AL, Mem.Byte(asm.Lbl("rt_planar_raster_mask")));
    asm.Mov(Mem.Byte(Reg.DI).Seg(Reg.ES), Reg.AL);
    asm.Mov(Mem.Byte(asm.Lbl("rt_planar_raster_mask")), (Imm)0);

    asm.MarkLabel(empty);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.AX);
    asm.Ret();

    asm.MarkLabel("rt_planar_raster_pending");
    asm.Dw(0xFFFF);
    asm.MarkLabel("rt_planar_raster_mask");
    asm.Db(0);
  }
}
