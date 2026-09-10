using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

public sealed partial class DosRuntime {

  /// <summary>Non-consuming descriptor length used while building an expression-local O0297 view.</summary>
  public Label StrLenBorrow { get; private set; } = null!;

  /// <summary>Bytewise three-way compare of two non-owning string ranges.</summary>
  public Label StrViewCmp { get; private set; } = null!;

  /// <summary>Equality-only compare of two non-owning string ranges.</summary>
  public Label StrViewCmpEq { get; private set; } = null!;

  /// <summary>PRINT of a non-owning string range.</summary>
  public Label StrPrintView { get; private set; } = null!;

  /// <summary>
  /// O0297 expression-local string-view helpers. A view is (handle, 1-based start, length); callers
  /// have already clamped the bounds to the descriptor, so these routines never allocate, copy into
  /// the string heap, or free the handle. Resolving the descriptor here - after any intervening heap
  /// allocation - is what makes compaction harmless while the stable handle remains live.
  /// </summary>
  private void EmitStringViewProcedures(Assembler asm) {
    this.EmitStrLenBorrow(asm);
    this.EmitStrViewCompare(asm);
    this.EmitStrViewCompareEq(asm);
    this.EmitStrPrintView(asm);
  }

  /// <summary>AX=handle -&gt; AX=length; unlike rt_len, does not consume the handle.</summary>
  private void EmitStrLenBorrow(Assembler asm) {
    this.StrLenBorrow = asm.MarkLabel("rt_len_borrow");
    var done = asm.DefineLabel();
    asm.Test(Reg.AX, Reg.AX);
    asm.Jz(done);
    asm.Push(Reg.BX);
    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.AX, this.Descriptor(Reg.BX, 2));
    asm.Pop(Reg.BX);
    asm.MarkLabel(done);
    asm.Ret();
  }

  /// <summary>
  /// AX=left handle, CX=left start, BX=left length, DX=right handle, SI=right start,
  /// DI=right length -&gt; AX=-1/0/1. Both operands are borrowed.
  /// </summary>
  private void EmitStrViewCompare(Assembler asm) {
    this.StrViewCmp = asm.MarkLabel("rt_strviewcmp");
    var minOk = asm.DefineLabel();
    var prefix = asm.DefineLabel();
    var diff = asm.DefineLabel();
    var less = asm.DefineLabel();
    var equal = asm.DefineLabel();
    var greater = asm.DefineLabel();
    var output = asm.DefineLabel();

    asm.Push(Reg.DS);
    asm.Push(Reg.ES);
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));

    // Save the right triple while the left descriptor is resolved. No pointer survives an allocation:
    // the pass calls us only after all operands have been evaluated, and we resolve both handles now.
    asm.Push(Reg.DX);                               // right handle
    asm.Push(Reg.SI);                               // right start
    asm.Push(Reg.DI);                               // right length
    asm.Mov(Reg.DI, Reg.BX);                       // DI = left length
    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.SI, this.Descriptor(Reg.BX));      // SI = left base
    asm.Add(Reg.SI, Reg.CX);
    asm.Dec(Reg.SI);                               // SI = left base + start - 1

    asm.Pop(Reg.BX);                               // BX = right length
    asm.Pop(Reg.CX);                               // CX = right start
    asm.Pop(Reg.AX);                               // AX = right handle
    asm.Push(Reg.DI);                              // save left length
    asm.Push(Reg.BX);                              // save right length
    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.DI, this.Descriptor(Reg.BX));      // DI = right base
    asm.Add(Reg.DI, Reg.CX);
    asm.Dec(Reg.DI);                               // DI = right base + start - 1
    asm.Pop(Reg.DX);                               // DX = right length
    asm.Pop(Reg.AX);                               // AX = left length

    asm.Mov(Reg.CX, Reg.AX);
    asm.Cmp(Reg.CX, Reg.DX);
    asm.Jbe(minOk);
    asm.Mov(Reg.CX, Reg.DX);
    asm.MarkLabel(minOk);
    asm.Jcxz(prefix);
    asm.Mov(Reg.BX, Reg.ES);
    asm.Mov(Reg.DS, Reg.BX);
    asm.Repe();
    asm.Cmpsb();                                   // unsigned bytes, exactly like rt_strcmp
    asm.Jne(diff);
    asm.MarkLabel(prefix);
    asm.Cmp(Reg.AX, Reg.DX);
    asm.Je(equal);
    asm.Jb(less);
    asm.Jmp(greater);
    asm.MarkLabel(diff);
    asm.Jb(less);
    asm.MarkLabel(greater);
    asm.Mov(Reg.AX, 1);
    asm.Jmp(output);
    asm.MarkLabel(less);
    asm.Mov(Reg.AX, -1);
    asm.Jmp(output);
    asm.MarkLabel(equal);
    asm.Xor(Reg.AX, Reg.AX);
    asm.MarkLabel(output);
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DS);
    asm.Ret();
  }

  /// <summary>
  /// Same six borrowed arguments as <see cref="EmitStrViewCompare"/>, but returns 0 equal / 1 unequal.
  /// Unequal lengths return immediately, composing O0297 with O0298's length guard.
  /// </summary>
  private void EmitStrViewCompareEq(Assembler asm) {
    this.StrViewCmpEq = asm.MarkLabel("rt_strviewcmpeq");
    var unequal = asm.DefineLabel();
    var equal = asm.DefineLabel();
    asm.Cmp(Reg.BX, Reg.DI);                        // left length / right length as passed
    asm.Jne(unequal);                               // no byte read for unequal lengths
    asm.Call(this.StrViewCmp);                      // equal lengths: compare exactly those bytes
    asm.Test(Reg.AX, Reg.AX);
    asm.Jz(equal);
    asm.MarkLabel(unequal);
    asm.Mov(Reg.AX, 1);
    asm.Ret();
    asm.MarkLabel(equal);
    asm.Xor(Reg.AX, Reg.AX);
    asm.Ret();
  }

  /// <summary>
  /// AX=handle, CX=1-based start, DX=length. Writes exactly that borrowed range to the current PRINT
  /// destination and does not consume the handle. This mirrors rt_str_print's DOS/capture behavior;
  /// the only removed operation is the final StrFree of the temporary substring.
  /// </summary>
  private void EmitStrPrintView(Assembler asm) {
    this.StrPrintView = asm.MarkLabel("rt_str_print_view");
    var ret = asm.DefineLabel();
    var capture = asm.DefineLabel();
    var copy = asm.DefineLabel();
    var done = asm.DefineLabel();
    asm.Test(Reg.DX, Reg.DX);
    asm.Jz(ret);
    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));
    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.SI, this.Descriptor(Reg.BX));
    asm.Add(Reg.SI, Reg.CX);
    asm.Dec(Reg.SI);
    asm.Mov(Reg.CX, Reg.DX);
    asm.Cmp(Mem.Byte(asm.Lbl("rt_capmode")), (Imm)0);
    asm.Jne(capture);
    asm.Mov(Reg.DX, Reg.SI);
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_curout")));
    asm.Mov(Reg.AX, Reg.ES);
    asm.Push(Reg.DS);
    asm.Mov(Reg.DS, Reg.AX);
    asm.Mov(Reg.AH, 0x40);
    asm.Int(0x21);
    asm.Pop(Reg.DS);
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_colptr")));
    asm.Add(Mem.Word(Reg.BX), Reg.CX);
    asm.Jmp(done);
    asm.MarkLabel(capture);
    asm.Mov(Reg.DI, Mem.Word(asm.Lbl("rt_caplen")));
    asm.Add(Mem.Word(asm.Lbl("rt_caplen")), Reg.CX);
    asm.Lea(Reg.DI, Mem.At(Reg.DI, asm.Lbl("rt_capbuf")));
    asm.MarkLabel(copy);
    asm.Mov(Reg.AL, Mem.Byte(Reg.SI).Es());
    asm.Mov(Mem.Byte(Reg.DI), Reg.AL);
    asm.Inc(Reg.SI);
    asm.Inc(Reg.DI);
    asm.Loop(copy);
    asm.MarkLabel(done);
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.MarkLabel(ret);
    asm.Ret();
  }
}
