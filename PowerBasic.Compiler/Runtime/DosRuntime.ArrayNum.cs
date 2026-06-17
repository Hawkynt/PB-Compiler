using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

/// <summary>
/// ARRAY SORT / ARRAY SCAN over non-string (numeric) arrays. Every element kind
/// (signed/unsigned integer 1..8 bytes, SINGLE/DOUBLE/EXT float) is compared on
/// the x87: an element is widened into a 10-byte staging cell and pushed with the
/// matching FILD/FLD, so the comparison is value-correct across all widths. The
/// parameter block lives in rt_num_* (kind/size/descend/relop/match/tag) beside
/// the shared rt_arpb fields (descriptor +0, start +2, count +4, data base +16,
/// data segment +18) filled by rt_num_setup.
/// Conventions: SortNum / ScanNum take all parameters from memory; ScanNum
/// returns the 1-based relative position in AX (0 = no match).
/// </summary>
public sealed partial class DosRuntime {

  public Label SortNum { get; private set; } = null!;
  public Label ScanNum { get; private set; } = null!;

  private void EmitArrayNum(Assembler asm) {
    EmitNumSetup(asm);
    this.EmitNumStage(asm);
    EmitNumPush(asm);
    EmitNumElemAddr(asm);
    this.EmitNumCmp(asm);
    this.EmitSortNum(asm);
    this.EmitScanNum(asm);
  }

  /// <summary>
  /// Common prologue: turn rt_arpb start index into a byte offset of the first
  /// element and stash the data base offset (+16) / segment (+18); also resolves
  /// the optional TAGARRAY base offset/segment (rt_num_tagoff/seg).
  /// </summary>
  private static void EmitNumSetup(Assembler asm) {
    asm.MarkLabel("rt_num_setup");
    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_arpb")));     // descriptor ptr
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_arpb"), 2));  // start index
    asm.Sub(Reg.AX, Mem.Word(Reg.BX, 8));              // - lower bound
    asm.Xor(Reg.CX, Reg.CX);
    asm.Mov(Reg.CL, Mem.Byte(asm.Lbl("rt_num_size"))); // * element size
    asm.Mul(Reg.CX);                                   // AX = (idx-lbound)*size
    asm.Add(Reg.AX, Mem.Word(Reg.BX, 2));              // + data offset
    asm.Mov(Mem.Word(asm.Lbl("rt_arpb"), 16), Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(Reg.BX));                 // data segment
    asm.Mov(Mem.Word(asm.Lbl("rt_arpb"), 18), Reg.AX);
    // TAGARRAY base (shares the key array's start index, with its own lbound/size)
    asm.Cmp(Mem.Word(asm.Lbl("rt_num_tagdesc")), (Imm)0);
    var noTag = asm.DefineLabel();
    asm.Je(noTag);
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_num_tagdesc")));
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_arpb"), 2));
    asm.Sub(Reg.AX, Mem.Word(Reg.BX, 8));
    asm.Mul(Mem.Word(asm.Lbl("rt_num_tagsize")));
    asm.Add(Reg.AX, Mem.Word(Reg.BX, 2));
    asm.Mov(Mem.Word(asm.Lbl("rt_num_tagoff")), Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(Reg.BX));
    asm.Mov(Mem.Word(asm.Lbl("rt_num_tagseg")), Reg.AX);
    asm.MarkLabel(noTag);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Ret();
  }

  /// <summary>
  /// AX = key byte offset of element index AX (0-based): index*rt_num_size +
  /// rt_arpb data base (+16). Clobbers CX, DX.
  /// </summary>
  private static void EmitNumElemAddr(Assembler asm) {
    asm.MarkLabel("rt_num_keyaddr");
    asm.Xor(Reg.CX, Reg.CX);
    asm.Mov(Reg.CL, Mem.Byte(asm.Lbl("rt_num_size")));
    asm.Mul(Reg.CX);
    asm.Add(Reg.AX, Mem.Word(asm.Lbl("rt_arpb"), 16));
    asm.Ret();

    // AX = tag byte offset of element index AX: index*rt_num_tagsize + rt_num_tagoff
    asm.MarkLabel("rt_num_tagaddr");
    asm.Mul(Mem.Word(asm.Lbl("rt_num_tagsize")));
    asm.Add(Reg.AX, Mem.Word(asm.Lbl("rt_num_tagoff")));
    asm.Ret();
  }

  /// <summary>
  /// Copies rt_num_size bytes from ES:SI into the 10-byte staging cell at DS
  /// offset DI, zero-filling the rest so the x87 load sees a widened value.
  /// </summary>
  private void EmitNumStage(Assembler asm) {
    asm.MarkLabel("rt_num_stage");
    asm.Push(Reg.AX);
    asm.Push(Reg.CX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Xor(Reg.AX, Reg.AX);
    asm.Mov(Mem.Word(Reg.DI), Reg.AX);
    asm.Mov(Mem.Word(Reg.DI, 2), Reg.AX);
    asm.Mov(Mem.Word(Reg.DI, 4), Reg.AX);
    asm.Mov(Mem.Word(Reg.DI, 6), Reg.AX);
    asm.Mov(Mem.Word(Reg.DI, 8), Reg.AX);
    asm.Xor(Reg.CX, Reg.CX);
    asm.Mov(Reg.CL, Mem.Byte(asm.Lbl("rt_num_size")));
    var copy = asm.DefineLabel();
    var copyDone = asm.DefineLabel();
    asm.MarkLabel(copy);
    asm.Jcxz(copyDone);
    asm.Mov(Reg.AL, Mem.Byte(Reg.SI).Es());
    asm.Mov(Mem.Byte(Reg.DI), Reg.AL);
    asm.Inc(Reg.SI);
    asm.Inc(Reg.DI);
    asm.Dec(Reg.CX);
    asm.Jmp(copy);
    asm.MarkLabel(copyDone);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.AX);
    asm.Ret();
  }

  /// <summary>
  /// Pushes the value staged in the 10-byte cell at DS offset BX onto the x87,
  /// selecting FILD/FLD by rt_num_kind and the FPU load width rt_num_load. The
  /// staging cell zero-pads above rt_num_size, so an unsigned width loads through
  /// the next wider signed FILD (rt_num_load > rt_num_size) and stays positive.
  /// </summary>
  private static void EmitNumPush(Assembler asm) {
    asm.MarkLabel("rt_num_push");
    var isFloat = asm.DefineLabel();
    var done = asm.DefineLabel();
    var f4 = asm.DefineLabel();
    var f8 = asm.DefineLabel();
    var i8 = asm.DefineLabel();
    var i4 = asm.DefineLabel();

    asm.Cmp(Mem.Byte(asm.Lbl("rt_num_kind")), (Imm)2);
    asm.Je(isFloat);

    asm.Cmp(Mem.Byte(asm.Lbl("rt_num_load")), (Imm)8);
    asm.Je(i8);
    asm.Cmp(Mem.Byte(asm.Lbl("rt_num_load")), (Imm)4);
    asm.Je(i4);
    asm.Fild(Mem.Word(Reg.BX));      // load as 16-bit signed (1/2-byte unsigned fits)
    asm.Jmp(done);
    asm.MarkLabel(i4);
    asm.Fild(Mem.Dword(Reg.BX));     // load as 32-bit signed (WORD unsigned / LONG)
    asm.Jmp(done);
    asm.MarkLabel(i8);
    asm.Fild(Mem.Qword(Reg.BX));     // load as 64-bit signed (DWORD unsigned / QUAD)
    asm.Jmp(done);

    asm.MarkLabel(isFloat);
    asm.Cmp(Mem.Byte(asm.Lbl("rt_num_load")), (Imm)4);
    asm.Je(f4);
    asm.Cmp(Mem.Byte(asm.Lbl("rt_num_load")), (Imm)8);
    asm.Je(f8);
    asm.Fld(Mem.Tbyte(Reg.BX));      // EXT (80-bit)
    asm.Jmp(done);
    asm.MarkLabel(f4);
    asm.Fld(Mem.Dword(Reg.BX));      // SINGLE
    asm.Jmp(done);
    asm.MarkLabel(f8);
    asm.Fld(Mem.Qword(Reg.BX));      // DOUBLE
    asm.MarkLabel(done);
    asm.Ret();
  }

  /// <summary>
  /// Compares element a (ES:SI) against element b (ES:DI), ascending sense:
  /// AX = -1 (a&lt;b), 0 (a=b), +1 (a&gt;b). Both element pointers preserved.
  /// </summary>
  private void EmitNumCmp(Assembler asm) {
    asm.MarkLabel("rt_numcmp");
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);

    asm.Push(Reg.DI);
    asm.Mov(Reg.DI, Imm.OffsetOf(asm.Lbl("rt_num_a")));
    asm.Call(asm.Lbl("rt_num_stage"));   // a (ES:SI) -> rt_num_a
    asm.Pop(Reg.DI);
    asm.Mov(Reg.SI, Reg.DI);
    asm.Mov(Reg.DI, Imm.OffsetOf(asm.Lbl("rt_num_b")));
    asm.Call(asm.Lbl("rt_num_stage"));   // b (ES:DI) -> rt_num_b

    asm.Mov(Reg.BX, Imm.OffsetOf(asm.Lbl("rt_num_a")));
    asm.Call(asm.Lbl("rt_num_push"));    // ST0 = a
    asm.Mov(Reg.BX, Imm.OffsetOf(asm.Lbl("rt_num_b")));
    asm.Call(asm.Lbl("rt_num_push"));    // ST0 = b, ST1 = a
    asm.Fxch();                          // ST0 = a, ST1 = b
    asm.Fcompp();                        // compare a ? b, pop both
    asm.FstswAx();
    asm.Sahf();
    var lt = asm.DefineLabel();
    var eq = asm.DefineLabel();
    var cmpDone = asm.DefineLabel();
    asm.Jb(lt);
    asm.Je(eq);
    asm.Mov(Reg.AX, 1);
    asm.Jmp(cmpDone);
    asm.MarkLabel(lt);
    asm.Mov(Reg.AX, -1);
    asm.Jmp(cmpDone);
    asm.MarkLabel(eq);
    asm.Xor(Reg.AX, Reg.AX);
    asm.MarkLabel(cmpDone);

    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Ret();
  }

  /// <summary>
  /// Swaps the rt_num_size bytes at ES:SI with ES:DI (key element exchange).
  /// </summary>
  private static void EmitNumSwapBytes(Assembler asm, string sizeCell) {
    asm.Push(Reg.AX);
    asm.Push(Reg.CX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Xor(Reg.CX, Reg.CX);
    asm.Mov(Reg.CL, Mem.Byte(asm.Lbl(sizeCell)));
    var loop = asm.DefineLabel();
    var loopDone = asm.DefineLabel();
    asm.MarkLabel(loop);
    asm.Jcxz(loopDone);
    asm.Mov(Reg.AL, Mem.Byte(Reg.SI).Es());
    asm.Xchg(Reg.AL, Mem.Byte(Reg.DI).Es());
    asm.Mov(Mem.Byte(Reg.SI).Es(), Reg.AL);
    asm.Inc(Reg.SI);
    asm.Inc(Reg.DI);
    asm.Dec(Reg.CX);
    asm.Jmp(loop);
    asm.MarkLabel(loopDone);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.AX);
  }

  /// <summary>
  /// Insertion sort over the numeric array (and the parallel TAGARRAY, if any):
  /// each key swap mirrors onto the tag element. SI = i, DI = j (element indices).
  /// </summary>
  private void EmitSortNum(Assembler asm) {
    this.SortNum = asm.MarkLabel("rt_sortnum");
    var done = asm.DefineLabel();
    var outer = asm.DefineLabel();
    var inner = asm.DefineLabel();
    var nextI = asm.DefineLabel();
    var doSwap = asm.DefineLabel();
    var descend = asm.DefineLabel();
    var noTagSwap = asm.DefineLabel();

    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);
    asm.Call(asm.Lbl("rt_num_setup"));
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_arpb"), 4)); // count
    asm.Mov(Mem.Word(asm.Lbl("rt_num_n")), Reg.AX);
    asm.Cmp(Reg.AX, 2);
    asm.Jl(done);

    // i, j kept in memory so the rt_numcmp / rt_num_* helper calls are free to
    // use SI/DI/AX/BX/CX/DX as element-offset scratch.
    asm.Mov(Mem.Word(asm.Lbl("rt_num_i")), (Imm)1);
    asm.MarkLabel(outer);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_num_i")));
    asm.Cmp(Reg.AX, Mem.Word(asm.Lbl("rt_num_n")));
    asm.Jae(done);
    asm.Mov(Mem.Word(asm.Lbl("rt_num_j")), Reg.AX);   // j = i
    asm.MarkLabel(inner);
    asm.Cmp(Mem.Word(asm.Lbl("rt_num_j")), (Imm)0);
    asm.Je(nextI);

    // a = key[j-1] (ES:SI), b = key[j] (ES:DI)
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_arpb"), 18));
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_num_j")));
    asm.Dec(Reg.AX);
    asm.Call(asm.Lbl("rt_num_keyaddr"));
    asm.Mov(Reg.SI, Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_num_j")));
    asm.Call(asm.Lbl("rt_num_keyaddr"));
    asm.Mov(Reg.DI, Reg.AX);
    asm.Call(asm.Lbl("rt_numcmp"));                   // AX = cmp(a,b) ascending; SI/DI preserved

    asm.Cmp(Mem.Byte(asm.Lbl("rt_num_desc")), (Imm)0);
    asm.Jne(descend);
    asm.Cmp(Reg.AX, 1);                               // ascend: out of order when a > b
    asm.Je(doSwap);
    asm.Jmp(nextI);
    asm.MarkLabel(descend);
    asm.Cmp(Reg.AX, -1);                              // descend: out of order when a < b
    asm.Je(doSwap);
    asm.Jmp(nextI);

    asm.MarkLabel(doSwap);
    // swap key[j-1] <-> key[j] : ES:SI / ES:DI already hold their offsets
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_arpb"), 18));
    EmitNumSwapBytes(asm, "rt_num_size");
    // mirror on the tag array if present
    asm.Cmp(Mem.Word(asm.Lbl("rt_num_tagdesc")), (Imm)0);
    asm.Je(noTagSwap);
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_num_tagseg")));
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_num_j")));
    asm.Dec(Reg.AX);
    asm.Call(asm.Lbl("rt_num_tagaddr"));
    asm.Mov(Reg.SI, Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_num_j")));
    asm.Call(asm.Lbl("rt_num_tagaddr"));
    asm.Mov(Reg.DI, Reg.AX);
    EmitNumSwapBytesWord(asm, "rt_num_tagsize");
    asm.MarkLabel(noTagSwap);
    asm.Dec(Mem.Word(asm.Lbl("rt_num_j")));
    asm.Jmp(inner);

    asm.MarkLabel(nextI);
    asm.Inc(Mem.Word(asm.Lbl("rt_num_i")));
    asm.Jmp(outer);

    asm.MarkLabel(done);
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Ret();
  }

  /// <summary>Byte-swap helper with a word-sized size cell (rt_num_tagsize).</summary>
  private static void EmitNumSwapBytesWord(Assembler asm, string sizeCell) {
    asm.Push(Reg.AX);
    asm.Push(Reg.CX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Mov(Reg.CX, Mem.Word(asm.Lbl(sizeCell)));
    var loop = asm.DefineLabel();
    var loopDone = asm.DefineLabel();
    asm.MarkLabel(loop);
    asm.Jcxz(loopDone);
    asm.Mov(Reg.AL, Mem.Byte(Reg.SI).Es());
    asm.Xchg(Reg.AL, Mem.Byte(Reg.DI).Es());
    asm.Mov(Mem.Byte(Reg.SI).Es(), Reg.AL);
    asm.Inc(Reg.SI);
    asm.Inc(Reg.DI);
    asm.Dec(Reg.CX);
    asm.Jmp(loop);
    asm.MarkLabel(loopDone);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.AX);
  }

  /// <summary>
  /// ARRAY SCAN over the numeric array: walks elements, comparing each (a) with
  /// the staged match value (rt_num_match, b) under the relop in rt_num_relop;
  /// returns the 1-based relative position in AX (0 = none).
  /// </summary>
  private void EmitScanNum(Assembler asm) {
    this.ScanNum = asm.MarkLabel("rt_scannum");
    var loop = asm.DefineLabel();
    var none = asm.DefineLabel();
    var found = asm.DefineLabel();
    var next = asm.DefineLabel();
    var outl = asm.DefineLabel();

    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);
    asm.Call(asm.Lbl("rt_num_setup"));
    asm.Mov(Reg.SI, (Imm)0);                               // 0-based index

    asm.MarkLabel(loop);
    asm.Mov(Reg.AX, Reg.SI);
    asm.Cmp(Reg.AX, Mem.Word(asm.Lbl("rt_arpb"), 4)); // count
    asm.Jae(none);

    // stage element[SI] -> rt_num_a ; rt_num_match already holds b
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_arpb"), 18));
    asm.Mov(Reg.AX, Reg.SI);
    asm.Call(asm.Lbl("rt_num_keyaddr"));              // AX = element offset
    asm.Push(Reg.SI);
    asm.Mov(Reg.SI, Reg.AX);
    asm.Mov(Reg.DI, Imm.OffsetOf(asm.Lbl("rt_num_a")));
    asm.Call(asm.Lbl("rt_num_stage"));
    asm.Pop(Reg.SI);

    // compare a (rt_num_a) vs b (rt_num_match) on the x87
    asm.Mov(Reg.BX, Imm.OffsetOf(asm.Lbl("rt_num_a")));
    asm.Call(asm.Lbl("rt_num_push"));                 // ST0 = a
    asm.Mov(Reg.BX, Imm.OffsetOf(asm.Lbl("rt_num_match")));
    asm.Call(asm.Lbl("rt_num_push"));                 // ST0 = b, ST1 = a
    asm.Fxch();
    asm.Fcompp();
    asm.FstswAx();
    asm.Sahf();
    var aLt = asm.DefineLabel();
    var aEq = asm.DefineLabel();
    var haveCmp = asm.DefineLabel();
    asm.Jb(aLt);
    asm.Je(aEq);
    asm.Mov(Reg.DX, 1);          // a > b
    asm.Jmp(haveCmp);
    asm.MarkLabel(aLt);
    asm.Mov(Reg.DX, -1);
    asm.Jmp(haveCmp);
    asm.MarkLabel(aEq);
    asm.Xor(Reg.DX, Reg.DX);
    asm.MarkLabel(haveCmp);

    // relop: 0 = / 1 <> / 2 < / 3 <= / 4 > / 5 >=
    asm.Xor(Reg.BX, Reg.BX);
    asm.Mov(Reg.BL, Mem.Byte(asm.Lbl("rt_num_relop")));
    var r1 = asm.DefineLabel();
    var r2 = asm.DefineLabel();
    var r3 = asm.DefineLabel();
    var r4 = asm.DefineLabel();
    var r5 = asm.DefineLabel();
    asm.Cmp(Reg.BL, (Imm)0);
    asm.Jne(r1);
    asm.Cmp(Reg.DX, (Imm)0); asm.Je(found); asm.Jmp(next);
    asm.MarkLabel(r1);
    asm.Cmp(Reg.BL, (Imm)1);
    asm.Jne(r2);
    asm.Cmp(Reg.DX, (Imm)0); asm.Jne(found); asm.Jmp(next);
    asm.MarkLabel(r2);
    asm.Cmp(Reg.BL, (Imm)2);
    asm.Jne(r3);
    asm.Cmp(Reg.DX, (Imm)0); asm.Jl(found); asm.Jmp(next);
    asm.MarkLabel(r3);
    asm.Cmp(Reg.BL, (Imm)3);
    asm.Jne(r4);
    asm.Cmp(Reg.DX, (Imm)1); asm.Jl(found); asm.Jmp(next);  // a <= b  <=> cmp < 1
    asm.MarkLabel(r4);
    asm.Cmp(Reg.BL, (Imm)4);
    asm.Jne(r5);
    asm.Cmp(Reg.DX, (Imm)0); asm.Jg(found); asm.Jmp(next);
    asm.MarkLabel(r5);
    asm.Cmp(Reg.DX, (Imm)0); asm.Jge(found);

    asm.MarkLabel(next);
    asm.Inc(Reg.SI);
    asm.Jmp(loop);

    asm.MarkLabel(found);
    asm.Mov(Reg.AX, Reg.SI);
    asm.Inc(Reg.AX);
    asm.Jmp(outl);
    asm.MarkLabel(none);
    asm.Xor(Reg.AX, Reg.AX);
    asm.MarkLabel(outl);
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Ret();
  }
}
