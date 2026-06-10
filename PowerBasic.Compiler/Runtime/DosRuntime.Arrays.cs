using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

/// <summary>
/// Dynamic array storage: a bump allocator over the far array heap segment
/// (CS+0x2000). REDIM allocates a fresh zero-filled block; freed blocks are
/// not reclaimed (documented limitation - 64 KiB is ample for test programs).
///   ArrAlloc: DX:AX = byte count -> AX = offset within rt_arrseg (zero-filled)
/// </summary>
public sealed partial class DosRuntime {

  public Label ArrAlloc { get; private set; } = null!;

  private void EmitArrayProcedures(Assembler asm) {
    this.ArrAlloc = asm.MarkLabel("rt_arr_alloc");
    var oom = asm.DefineLabel();
    asm.Test(Reg.DX, Reg.DX);
    asm.Jnz(oom);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_arrtop")));
    asm.Mov(Reg.CX, Reg.AX);
    asm.Add(Reg.AX, Reg.BX);
    asm.Jc(oom);
    asm.Cmp(Reg.AX, 0xFFF0);
    asm.Ja(oom);
    asm.Mov(Mem.Word(asm.Lbl("rt_arrtop")), Reg.AX);
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_arrseg")));
    asm.Mov(Reg.DI, Reg.BX);
    asm.Xor(Reg.AL, Reg.AL);
    asm.Rep();
    asm.Stosb();
    asm.Mov(Reg.AX, Reg.BX);
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Ret();
    asm.MarkLabel(oom);
    asm.Jmp(asm.Lbl("rt_err_arr"));
  }

  private void EmitArrayData(Assembler asm) {
    asm.Align(2);
    asm.MarkLabel("rt_arrseg");
    asm.Dw(0);
    asm.MarkLabel("rt_arrtop");
    asm.Dw(0);
  }
}
