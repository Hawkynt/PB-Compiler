using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

public sealed partial class DosRuntime {

  /// <summary>
  /// O0302's two searches for a compile-time needle, the target half of
  /// <c>Ir.Passes.ConstantInstrSpecialization</c>. Both take AX = the haystack handle (consumed),
  /// CX = the 1-based start, BX = the needle's offset in DS and DX = its length (at least 2), and answer
  /// AX = the first 1-based match or 0.
  ///
  /// <para>
  /// <c>rt_instr_short</c> lets REPNE SCASB find each candidate first byte and REPE CMPSB verify the
  /// rest, so a two-to-four-byte needle is never probed position by position. <c>rt_instr_horspool</c>
  /// (SI = a 256-byte skip table in DS) compares a candidate's LAST byte, verifies the prefix only on a
  /// hit, and otherwise advances by the table's shift for the byte it saw, fetched with XLAT. A shift is
  /// saturated at 255, which is conservative - a shorter jump than the mathematical one - so very long
  /// strings stay correct without a second table. The needle is read in place from the literal pool;
  /// neither search allocates it.
  /// </para>
  /// </summary>
  private void EmitConstantInstr(Assembler asm) {
    this.EmitShortConstantInstr(asm);
    this.EmitHorspoolConstantInstr(asm);
  }

  /// <summary>The haystack's base (SI) and length (DX) in the string segment (ES), from the handle in AX.</summary>
  private static void LoadHaystack(Assembler asm) {
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));
    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 1);
    asm.Shl(Reg.BX, 1);
    asm.Mov(Reg.SI, Mem.Word(Reg.BX, asm.Lbl("rt_strtab")));
    asm.Mov(Reg.DX, Mem.Word(Reg.BX, asm.Lbl("rt_strtab"), 2));
  }

  /// <summary>Frees the consumed haystack (its handle was pushed last) and keeps AX, the answer.</summary>
  private void FreeHaystack(Assembler asm) {
    asm.Pop(Reg.BX);
    asm.Push(Reg.AX);
    asm.Mov(Reg.AX, Reg.BX);
    asm.Call(asm.Lbl("rt_strfree"));   // by name: the strings section may come after this one
    asm.Pop(Reg.AX);
  }

  private void EmitShortConstantInstr(Assembler asm) {
    // frame: [BP-2] needle offset, [BP-4] needle length
    var needle = Mem.Word(Reg.BP, -2);
    var length = Mem.Word(Reg.BP, -4);
    var startOk = asm.DefineLabel();
    var scan = asm.DefineLabel();
    var none = asm.DefineLabel();
    var found = asm.DefineLabel();
    var output = asm.DefineLabel();

    asm.MarkLabel("rt_instr_short");
    asm.Push(Reg.BP);
    asm.Mov(Reg.BP, Reg.SP);
    asm.Push(Reg.BX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);
    asm.Push(Reg.AX);                                // owned haystack handle
    asm.Cmp(Reg.CX, 1);
    asm.Jge(startOk);
    asm.Mov(Reg.CX, 1);
    asm.MarkLabel(startOk);
    asm.Test(Reg.AX, Reg.AX);
    asm.Jz(none);
    LoadHaystack(asm);
    asm.Dec(Reg.CX);                                 // zero-based start
    asm.Cmp(Reg.CX, Reg.DX);
    asm.Jae(none);                                   // start past the end
    asm.Mov(Reg.DI, Reg.SI);
    asm.Add(Reg.DI, Reg.CX);                         // first candidate address
    asm.Sub(Reg.DX, Reg.CX);                         // bytes from the start on
    asm.Cmp(Reg.DX, length);
    asm.Jb(none);
    asm.Sub(Reg.DX, length);
    asm.Inc(Reg.DX);                                 // candidate-start count
    asm.Mov(Reg.CX, Reg.DX);
    asm.Mov(Reg.BX, Reg.SI);                         // haystack base, for the answer
    asm.Cld();

    asm.MarkLabel(scan);
    asm.Mov(Reg.SI, needle);
    asm.Mov(Reg.AL, Mem.Byte(Reg.SI));
    asm.Repne();
    asm.Scasb();                                     // ES:DI one past the candidate, CX starts left
    asm.Jne(none);
    asm.Push(Reg.CX);
    asm.Push(Reg.DI);                                // resume at candidate + 1 after a failed verify
    asm.Inc(Reg.SI);                                 // the first byte already matched
    asm.Mov(Reg.CX, length);
    asm.Dec(Reg.CX);
    asm.Repe();
    asm.Cmpsb();                                     // DS:needle[1..] vs ES:haystack[candidate+1..]
    asm.Pop(Reg.DI);
    asm.Pop(Reg.CX);
    asm.Je(found);
    asm.Or(Reg.CX, Reg.CX);
    asm.Jz(none);
    asm.Jmp(scan);

    asm.MarkLabel(found);
    asm.Mov(Reg.AX, Reg.DI);
    asm.Sub(Reg.AX, Reg.BX);                         // candidate + 1 - base = the 1-based position
    asm.Jmp(output);
    asm.MarkLabel(none);
    asm.Xor(Reg.AX, Reg.AX);
    asm.MarkLabel(output);
    this.FreeHaystack(asm);
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Mov(Reg.SP, Reg.BP);
    asm.Pop(Reg.BP);
    asm.Ret();
  }

  private void EmitHorspoolConstantInstr(Assembler asm) {
    // frame: [BP-2] needle offset, [BP-4] needle length, [BP-6] skip table offset
    var needle = Mem.Word(Reg.BP, -2);
    var length = Mem.Word(Reg.BP, -4);
    var table = Mem.Word(Reg.BP, -6);
    var startOk = asm.DefineLabel();
    var loop = asm.DefineLabel();
    var shift = asm.DefineLabel();
    var none = asm.DefineLabel();
    var found = asm.DefineLabel();
    var output = asm.DefineLabel();

    asm.MarkLabel("rt_instr_horspool");
    asm.Push(Reg.BP);
    asm.Mov(Reg.BP, Reg.SP);
    asm.Push(Reg.BX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);
    asm.Push(Reg.AX);                                // owned haystack handle
    asm.Cmp(Reg.CX, 1);
    asm.Jge(startOk);
    asm.Mov(Reg.CX, 1);
    asm.MarkLabel(startOk);
    asm.Test(Reg.AX, Reg.AX);
    asm.Jz(none);
    LoadHaystack(asm);
    asm.Cmp(Reg.DX, length);
    asm.Jb(none);
    asm.Sub(Reg.DX, length);                         // last valid zero-based start
    asm.Dec(Reg.CX);                                 // zero-based current start
    asm.Cmp(Reg.CX, Reg.DX);
    asm.Ja(none);
    asm.Cld();

    asm.MarkLabel(loop);
    asm.Mov(Reg.DI, Reg.SI);
    asm.Add(Reg.DI, Reg.CX);
    asm.Add(Reg.DI, length);
    asm.Dec(Reg.DI);                                 // the candidate's last text byte
    asm.Mov(Reg.AL, Mem.Byte(Reg.DI).Es());
    asm.Mov(Reg.BX, needle);
    asm.Add(Reg.BX, length);
    asm.Cmp(Reg.AL, Mem.Byte(Reg.BX, -1));           // ...against the needle's last byte
    asm.Jne(shift);

    // Last byte matched: verify the prefix. CX/DX/SI are the loop state and must survive CMPSB, and AL
    // (the text byte) survives it for the shift below.
    asm.Mov(Reg.DI, Reg.SI);
    asm.Add(Reg.DI, Reg.CX);
    asm.Push(Reg.SI);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Mov(Reg.SI, needle);
    asm.Mov(Reg.CX, length);
    asm.Dec(Reg.CX);
    asm.Repe();
    asm.Cmpsb();
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.SI);
    asm.Je(found);

    asm.MarkLabel(shift);
    asm.Mov(Reg.BX, table);
    asm.Xlat();                                      // AL = the safe shift for the byte seen
    asm.Xor(Reg.AH, Reg.AH);
    asm.Add(Reg.CX, Reg.AX);
    asm.Cmp(Reg.CX, Reg.DX);
    asm.Jbe(loop);
    asm.Jmp(none);

    asm.MarkLabel(found);
    asm.Mov(Reg.AX, Reg.CX);
    asm.Inc(Reg.AX);
    asm.Jmp(output);
    asm.MarkLabel(none);
    asm.Xor(Reg.AX, Reg.AX);
    asm.MarkLabel(output);
    this.FreeHaystack(asm);
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Mov(Reg.SP, Reg.BP);
    asm.Pop(Reg.BP);
    asm.Ret();
  }
}
