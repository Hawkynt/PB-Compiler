using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

public sealed partial class DosRuntime {
  /// <summary>
  /// Solid Mode 12h rectangles: configure VGA write mode 2 exactly once, broadcast
  /// each four-bit color into all four planes, and stream complete 8-pixel bytes
  /// with REP STOSB. Only the two boundary bytes need latch-preserving masks.
  /// Restores every VGA register and both index selectors that it touches.
  /// </summary>
  private void EmitPlanarLineFill(Assembler asm, Label planar, Label finish) {
    var row = asm.DefineLabel();
    var next = asm.DefineLabel();
    var single = asm.DefineLabel();
    var firstDone = asm.DefineLabel();
    var full = asm.DefineLabel();
    var tailDone = asm.DefineLabel();
    var end = asm.DefineLabel();

    var width = asm.Lbl("rt_planar_width");
    var stride = asm.Lbl("rt_planar_stride");
    var left = asm.Lbl("rt_planar_left");
    var right = asm.Lbl("rt_planar_right");

    asm.MarkLabel(planar);
    asm.Push(Reg.ES);
    asm.Push(Reg.DI);

    // Save indexed-port selectors and the four registers changed by this path.
    asm.Mov(Reg.DX, 0x3CE);
    asm.In(Reg.AL, Reg.DX);
    asm.Push(Reg.AX);
    asm.Mov(Reg.DX, 0x3C4);
    asm.In(Reg.AL, Reg.DX);
    asm.Push(Reg.AX);

    SaveAndSet(asm, 0x3CE, 5, -1); // Write mode 2, preserve read mode and the other bits.
    SaveAndSet(asm, 0x3CE, 3, 0);  // Replace (not AND/OR/XOR) without rotate.
    SaveAndSet(asm, 0x3CE, 8, 255);
    SaveAndSet(asm, 0x3C4, 2, 15); // All four planes, one host access.

    // Inclusive boundaries, in VGA bytes rather than BASIC pixels.
    asm.Mov(Reg.CL, (Imm)3);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gbx1")));
    asm.Shr(Reg.AX, Reg.CL);
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_gbx2")));
    asm.Shr(Reg.BX, Reg.CL);
    asm.Sub(Reg.BX, Reg.AX);
    asm.Inc(Reg.BX);
    asm.Mov(Mem.Word(width), Reg.BX);
    asm.Mov(Reg.CX, 80);
    asm.Sub(Reg.CX, Reg.BX);
    asm.Mov(Mem.Word(stride), Reg.CX);

    // Left mask: FF >> (x1 & 7); right mask: FF << (7 - (x2 & 7)).
    asm.Mov(Reg.CX, Mem.Word(asm.Lbl("rt_gbx1")));
    asm.And(Reg.CX, (Imm)7);
    asm.Mov(Reg.AH, (Imm)255);
    asm.Shr(Reg.AH, Reg.CL);
    asm.Mov(Mem.Byte(left), Reg.AH);
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_gbx2")));
    asm.And(Reg.BX, (Imm)7);
    asm.Mov(Reg.CX, 7);
    asm.Sub(Reg.CX, Reg.BX);
    asm.Mov(Reg.AH, (Imm)255);
    asm.Shl(Reg.AH, Reg.CL);
    asm.Mov(Mem.Byte(right), Reg.AH);

    // DI = 80*y1 + floor(x1/8). Every row advances exactly 80 bytes.
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gby1")));
    asm.Mov(Reg.CX, 80);
    asm.Mul(Reg.CX);
    asm.Mov(Reg.DI, Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gbx1")));
    asm.Mov(Reg.CL, (Imm)3);
    asm.Shr(Reg.AX, Reg.CL);
    asm.Add(Reg.DI, Reg.AX);
    asm.Mov(Reg.AX, 0xA000);
    asm.Mov(Reg.ES, Reg.AX);
    asm.Mov(Reg.SI, Mem.Word(asm.Lbl("rt_gby1")));

    asm.MarkLabel(row);
    asm.Mov(Reg.BX, Mem.Word(width));
    asm.Cmp(Reg.BX, (Imm)1);
    asm.Je(single);

    // A partial starting byte must read the destination first to fill VGA
    // latches, otherwise the unselected pixels would be destroyed.
    asm.Cmp(Mem.Byte(left), (Imm)255);
    asm.Je(firstDone);
    SetBitMask(asm, left);
    asm.Mov(Reg.AH, Mem.Byte(Reg.DI).Seg(Reg.ES));
    asm.Mov(Reg.AL, Mem.Byte(asm.Lbl("rt_gcolor")));
    asm.Mov(Mem.Byte(Reg.DI).Seg(Reg.ES), Reg.AL);
    asm.Inc(Reg.DI);
    asm.Dec(Reg.BX);
    asm.MarkLabel(firstDone);

    asm.Mov(Reg.CX, Reg.BX);
    asm.Cmp(Mem.Byte(right), (Imm)255);
    asm.Je(full);
    asm.Dec(Reg.CX);
    asm.MarkLabel(full);
    SetBitMask(asm, 255);
    asm.Mov(Reg.AL, Mem.Byte(asm.Lbl("rt_gcolor")));
    asm.Cld();
    asm.Rep();
    asm.Stosb();

    asm.Cmp(Mem.Byte(right), (Imm)255);
    asm.Je(tailDone);
    SetBitMask(asm, right);
    asm.Mov(Reg.AH, Mem.Byte(Reg.DI).Seg(Reg.ES));
    asm.Mov(Reg.AL, Mem.Byte(asm.Lbl("rt_gcolor")));
    asm.Mov(Mem.Byte(Reg.DI).Seg(Reg.ES), Reg.AL);
    asm.Inc(Reg.DI);
    asm.MarkLabel(tailDone);
    asm.Jmp(next);

    // The starting and ending pixels share one byte: combine masks first.
    asm.MarkLabel(single);
    asm.Mov(Reg.AL, Mem.Byte(left));
    asm.And(Reg.AL, Mem.Byte(right));
    asm.Mov(Reg.DX, 0x3CE);
    asm.Mov(Reg.AH, Reg.AL);
    asm.Mov(Reg.AL, (Imm)8);
    asm.Out(Reg.DX, Reg.AL);
    asm.Inc(Reg.DX);
    asm.Mov(Reg.AL, Reg.AH);
    asm.Out(Reg.DX, Reg.AL);
    asm.Mov(Reg.AH, Mem.Byte(Reg.DI).Seg(Reg.ES));
    asm.Mov(Reg.AL, Mem.Byte(asm.Lbl("rt_gcolor")));
    asm.Mov(Mem.Byte(Reg.DI).Seg(Reg.ES), Reg.AL);
    asm.Inc(Reg.DI);

    asm.MarkLabel(next);
    asm.Add(Reg.DI, Mem.Word(stride));
    asm.Inc(Reg.SI);
    asm.Cmp(Reg.SI, Mem.Word(asm.Lbl("rt_gby2")));
    asm.Jle(row);

    asm.MarkLabel(end);
    Restore(asm, 0x3C4, 2);
    Restore(asm, 0x3CE, 8);
    Restore(asm, 0x3CE, 3);
    Restore(asm, 0x3CE, 5);

    asm.Pop(Reg.BX); // Sequencer selector.
    asm.Mov(Reg.DX, 0x3C4);
    asm.Mov(Reg.AL, Reg.BL);
    asm.Out(Reg.DX, Reg.AL);
    asm.Pop(Reg.BX); // Graphics controller selector.
    asm.Mov(Reg.DX, 0x3CE);
    asm.Mov(Reg.AL, Reg.BL);
    asm.Out(Reg.DX, Reg.AL);

    asm.Pop(Reg.DI);
    asm.Pop(Reg.ES);
    asm.Jmp(finish); // Shared final cursor update and caller register restoration.

    asm.MarkLabel("rt_planar_width");
    asm.Dw(0);
    asm.MarkLabel("rt_planar_stride");
    asm.Dw(0);
    asm.MarkLabel("rt_planar_left");
    asm.Db(0);
    asm.MarkLabel("rt_planar_right");
    asm.Db(0);
  }

  private static void SaveAndSet(Assembler asm, int port, int index, int value) {
    asm.Mov(Reg.DX, port);
    asm.Mov(Reg.AL, (Imm)index);
    asm.Out(Reg.DX, Reg.AL);
    asm.Inc(Reg.DX);
    asm.In(Reg.AL, Reg.DX);
    asm.Push(Reg.AX);
    if (value < 0) {
      asm.And(Reg.AL, (Imm)0xFC);
      asm.Or(Reg.AL, (Imm)2);
    } else
      asm.Mov(Reg.AL, (Imm)value);
    asm.Out(Reg.DX, Reg.AL);
  }

  private static void Restore(Assembler asm, int port, int index) {
    asm.Pop(Reg.BX);
    asm.Mov(Reg.DX, port);
    asm.Mov(Reg.AL, (Imm)index);
    asm.Out(Reg.DX, Reg.AL);
    asm.Inc(Reg.DX);
    asm.Mov(Reg.AL, Reg.BL);
    asm.Out(Reg.DX, Reg.AL);
  }

  private static void SetBitMask(Assembler asm, Label mask) {
    asm.Mov(Reg.DX, 0x3CE);
    asm.Mov(Reg.AL, (Imm)8);
    asm.Out(Reg.DX, Reg.AL);
    asm.Inc(Reg.DX);
    asm.Mov(Reg.AL, Mem.Byte(mask));
    asm.Out(Reg.DX, Reg.AL);
  }

  private static void SetBitMask(Assembler asm, byte mask) {
    asm.Mov(Reg.DX, 0x3CE);
    asm.Mov(Reg.AL, (Imm)8);
    asm.Out(Reg.DX, Reg.AL);
    asm.Inc(Reg.DX);
    asm.Mov(Reg.AL, (Imm)mask);
    asm.Out(Reg.DX, Reg.AL);
  }
}
