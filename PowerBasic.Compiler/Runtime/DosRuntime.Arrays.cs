using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

/// <summary>
/// Dynamic array storage: a bump allocator over the far array heap segment
/// (CS+0x2000). REDIM/ERASE roll the bump pointer back when the released
/// block is the most recent allocation (the dominant REDIM-in-a-loop pattern);
/// interleaved frees still leak (documented limitation).
///   ArrAlloc: DX:AX = byte count -> AX = offset within rt_arrseg (zero-filled)
///   ArrFree:  AX = block offset, CX = byte count (no-op unless topmost)
/// </summary>
public sealed partial class DosRuntime {

  public Label ArrAlloc { get; private set; } = null!;
  public Label ArrAllocNoZero { get; private set; } = null!;
  public Label ArrFree { get; private set; } = null!;

  private void EmitArrayProcedures(Assembler asm) {
    this.ArrFree = asm.MarkLabel("rt_arr_free");
    {
      var done = asm.DefineLabel();
      asm.Push(Reg.BX);
      asm.Mov(Reg.BX, Reg.AX);
      asm.Add(Reg.BX, Reg.CX);
      asm.Cmp(Reg.BX, Mem.Word(asm.Lbl("rt_arrtop")));
      asm.Jne(done);
      asm.Mov(Mem.Word(asm.Lbl("rt_arrtop")), Reg.AX);
      asm.MarkLabel(done);
      asm.Pop(Reg.BX);
      asm.Ret();
    }
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

  /// <summary>
  /// O0068: array allocation WITHOUT the zero-fill, in its own trimmer section so it is emitted only
  /// when the codegen references it - a coverage proof having shown the program writes every element
  /// before reading one, making the fill unobservable. Same bump allocation as rt_arr_alloc, minus
  /// the REP STOSB; self-contained (its own OOM edge to rt_err_arr) so it needs no arrays-section
  /// label. DX:AX = byte count -> AX = block offset within rt_arrseg. Under $OPTIMIZE OFF nothing
  /// references it, so the section is trimmed and the faithful image is byte-identical.
  /// </summary>
  private void EmitArrayAllocNoZero(Assembler asm) {
    this.ArrAllocNoZero = asm.MarkLabel("rt_arr_alloc_nz");
    var oom = asm.DefineLabel();
    asm.Test(Reg.DX, Reg.DX);
    asm.Jnz(oom);
    asm.Push(Reg.BX);
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_arrtop")));
    asm.Add(Reg.AX, Reg.BX);
    asm.Jc(oom);
    asm.Cmp(Reg.AX, 0xFFF0);
    asm.Ja(oom);
    asm.Mov(Mem.Word(asm.Lbl("rt_arrtop")), Reg.AX);
    asm.Mov(Reg.AX, Reg.BX);
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
