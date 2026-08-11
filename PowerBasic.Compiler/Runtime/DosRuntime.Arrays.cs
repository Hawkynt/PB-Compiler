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

  /// <summary>
  /// The entries the IR path needs and the direct emitter does not: it open-codes REDIM PRESERVE and
  /// knows a handle is two bytes, while the IR declares the family portably and lets the runtime scale
  /// a pointer count. Each lives in its own trimmer section, so a faithful image carries none of them.
  /// </summary>
  public Label ArrRealloc { get; private set; } = null!;
  public Label ArrAllocPtr { get; private set; } = null!;
  public Label ArrReallocPtr { get; private set; } = null!;
  public Label ArrFreePtr { get; private set; } = null!;

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
    this.EmitArrayDescriptors(asm);
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

  /// <summary>
  /// REDIM PRESERVE: a new block with the old one's prefix copied into it.
  ///   BX = old block offset, CX = old byte count, DX:AX = new byte count -> AX = new block offset.
  ///
  /// <para>
  /// The bump allocator cannot grow a block in place, so this is what "realloc" means here and what
  /// the direct emitter open-codes at every REDIM PRESERVE: allocate, copy <c>min(old, new)</c>, leave
  /// the old block where it is (the documented leak). Copying the MINIMUM is not an optimization - PB
  /// allows the outer bound to shrink, and copying the old length into a shorter block would run past
  /// the end of it. The new block arrives zeroed from <c>rt_arr_alloc</c>, so a grown array's tail
  /// reads as zero rather than as whatever the heap last held there.
  /// </para>
  /// <para>
  /// The old count's high half never arrives, because a block that exists is under 64 KiB - the
  /// allocator refuses anything larger. The new count's does, in DX, and goes straight through to the
  /// allocator, which is what turns an oversized REDIM into Error 7 instead of a wrapped short block.
  /// </para>
  /// <para>
  /// Its own section: nothing the direct emitter writes references it, so the trimmer leaves it out of
  /// every faithful image and those stay byte-identical.
  /// </para>
  /// </summary>
  private void EmitArrayRealloc(Assembler asm) {
    this.ArrRealloc = asm.MarkLabel("rt_arr_realloc");
    var fits = asm.DefineLabel();
    var done = asm.DefineLabel();
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.CX);                            // old byte count
    asm.Push(Reg.AX);                            // new byte count (low half)
    asm.Call(asm.Lbl("rt_arr_alloc"));           // AX = the new block; BX, CX, DX, SI, DI all survive it
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Cmp(Reg.CX, Reg.DX);
    asm.Jbe(fits);
    asm.Mov(Reg.CX, Reg.DX);                     // a shrinking REDIM copies only what the new block holds
    asm.MarkLabel(fits);
    asm.Jcxz(done);
    asm.Mov(Reg.SI, Reg.BX);
    asm.Mov(Reg.DI, Reg.AX);
    asm.Push(Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_arrseg")));   // read through DS while DS is still DGROUP
    asm.Push(Reg.DS);
    asm.Mov(Reg.ES, Reg.AX);
    asm.Mov(Reg.DS, Reg.AX);
    asm.Rep();
    asm.Movsb();
    asm.Pop(Reg.DS);
    asm.Pop(Reg.AX);
    asm.MarkLabel(done);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Ret();
  }

  /// <summary>
  /// The COUNT-taking half of the allocation family, for an array whose element is a target pointer -
  /// a dynamic array of strings, whose elements are handles. Only the runtime knows how wide one is,
  /// which is the entire reason these entries take a count where the others take bytes; on this target
  /// the scaling is a doubling, so each shim is the doubling and a jump.
  ///
  /// <para>
  /// The doubling is done at 32 bits (<c>SHL</c> then <c>RCL</c>), so a count above 32767 carries into
  /// DX and the allocator refuses it, rather than wrapping to a block a quarter the size asked for.
  /// </para>
  /// </summary>
  private void EmitArrayPointerHelpers(Assembler asm) {
    this.ArrAllocPtr = asm.MarkLabel("rt_arr_alloc_ptr");
    asm.Shl(Reg.AX, 1);
    asm.Rcl(Reg.DX, 1);
    asm.Jmp(asm.Lbl("rt_arr_alloc"));

    this.ArrReallocPtr = asm.MarkLabel("rt_arr_realloc_ptr");
    asm.Shl(Reg.CX, 1);                          // old element count -> old byte count
    asm.Shl(Reg.AX, 1);
    asm.Rcl(Reg.DX, 1);
    asm.Jmp(asm.Lbl("rt_arr_realloc"));

    this.ArrFreePtr = asm.MarkLabel("rt_arr_free_ptr");
    asm.Shl(Reg.CX, 1);
    asm.Jmp(asm.Lbl("rt_arr_free"));
  }

  private void EmitArrayData(Assembler asm) {
    asm.Align(2);
    asm.MarkLabel("rt_arrseg");
    asm.Dw(0);
    asm.MarkLabel("rt_arrtop");
    asm.Dw(0);
    EmitArrayDescriptorData(asm);
  }
}
