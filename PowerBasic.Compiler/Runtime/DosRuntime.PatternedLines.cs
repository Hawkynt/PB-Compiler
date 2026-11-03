using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

public sealed partial class DosRuntime {
  /// <summary>
  /// SCREEN 13 horizontal patterned LINE. Keep the original 16-bit ROL style
  /// clock and travel direction, but calculate the framebuffer base once and
  /// write selected pixels directly without the per-pixel BIOS mode query,
  /// register save/restore and y*320 calculation.
  /// </summary>
  private static void EmitMode13StyledHorizontalLine(Assembler asm, Label entry, Label finish) {
    var forward = asm.DefineLabel();
    var loop = asm.DefineLabel();
    var skipped = asm.DefineLabel();
    var done = asm.DefineLabel();

    asm.MarkLabel(entry);
    asm.Push(Reg.ES);
    asm.Mov(Reg.SI, Mem.Word(asm.Lbl("rt_gx1")));
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gy1")));
    asm.Mov(Reg.CX, 320);
    asm.Mul(Reg.CX);
    asm.Mov(Reg.DI, Reg.AX);
    asm.Add(Reg.DI, Reg.SI);
    asm.Mov(Reg.AX, 0xA000);
    asm.Mov(Reg.ES, Reg.AX);

    asm.Mov(Reg.BX, 1);
    asm.Cmp(Reg.SI, Mem.Word(asm.Lbl("rt_gx2")));
    asm.Jle(forward);
    asm.Mov(Reg.BX, 0xFFFF);
    asm.MarkLabel(forward);

    asm.MarkLabel(loop);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gstyle")));
    asm.Rol(Reg.AX, 1);
    asm.Mov(Mem.Word(asm.Lbl("rt_gstyle")), Reg.AX);
    asm.Test(Reg.AX, (Imm)1);
    asm.Jz(skipped);
    asm.Mov(Reg.AL, Mem.Byte(asm.Lbl("rt_gcolor")));
    asm.Mov(Mem.Byte(Reg.DI).Seg(Reg.ES), Reg.AL);
    asm.MarkLabel(skipped);
    asm.Cmp(Reg.SI, Mem.Word(asm.Lbl("rt_gx2")));
    asm.Je(done);
    asm.Add(Reg.SI, Reg.BX);
    asm.Add(Reg.DI, Reg.BX);
    asm.Jmp(loop);

    asm.MarkLabel(done);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gx2")));
    asm.Mov(Mem.Word(asm.Lbl("rt_gx1")), Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gy2")));
    asm.Mov(Mem.Word(asm.Lbl("rt_gy1")), Reg.AX);
    asm.Pop(Reg.ES);
    asm.Jmp(finish);
  }
}
