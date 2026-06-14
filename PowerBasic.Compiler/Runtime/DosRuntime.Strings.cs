using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Runtime;

/// <summary>
/// Dynamic string runtime. Representation: a string value is a 2-byte handle,
/// an index into the descriptor table <c>rt_strtab</c> in DS (512 entries of
/// [offset word][length word]); the character data lives in the far string
/// heap segment (CS+0x1000). Handle 0 is the empty string; a descriptor with
/// length 0 is free. Heap blocks carry a 4-byte header [handle][length] so the
/// compacting collector (bump allocator + compaction on exhaustion) can walk
/// and relocate live blocks.
///
/// Ownership: every string value in generated code is an owned temporary;
/// routines marked "consumes" free their handle inputs. Register conventions
/// (registers not listed as outputs are preserved; LTrim/RTrim additionally
/// clobber CX/DX, StrI16 clobbers DX, Val clobbers AX):
///   StrAlloc:    CX=length -> AX=handle (data uninitialized; 0 for CX=0)
///   StrFree:     AX=handle (0 ok)
///   StrDup:      AX=handle -> AX=copy
///   StrMem:      DX=segment, SI=offset, CX=length -> AX (source must not be
///                inside the string heap - allocation may compact)
///   StrCat:      AX=left, DX=right -> AX (consumes both)
///   StrCmp:      AX=left, DX=right -> AX=-1/0/1 bytewise (consumes both)
///   StrMid:      AX=handle, CX=start(1-based), DX=length -> AX (consumes; clamps)
///   StrLeft/StrRight: AX=handle, CX=count -> AX (consumes)
///   Instr:       AX=haystack, DX=needle, CX=start -> AX=position/0 (consumes both)
///   StrUpr/StrLwr: AX=handle -> AX (transforms in place)
///   LTrim/RTrim: AX=handle -> AX (consumes)
///   StrFill:     CX=count, DL=char -> AX
///   Chr:         DL=char -> AX
///   Repeat:      AX=handle, CX=count -> AX (consumes)
///   Asc:         AX=handle -> AX=first byte or 0 (consumes)
///   Len:         AX=handle -> AX=length (consumes)
///   Val:         AX=handle -> ST0 (consumes; integer and simple x.y forms)
///   Radix:       DX:AX=value, CL=bits/digit (1/3/4), CH=min digits -> AX
///   StrI16:      AX=value -> AX=STR$ text (clobbers DX)
///   StrI32:      DX:AX=value -> AX=STR$ text
///   StrF64:      ST0=value (popped) -> AX=STR$ text
///   StrPrint:    AX=handle - writes to current output (consumes)
///   StrAssign:   BX=ptr to handle cell in DS, AX=new handle (frees old)
///   StrAssignEs: BX=ptr to handle cell in ES, AX=new handle (frees old)
///   StoreFixed:  AX=handle, DX:DI=dest, CX=field length (copy + blank pad; consumes)
///   MidSet:      AX=target handle, CX=start, BX=length limit, DX=value handle
///                (in-place replace; consumes the value handle only)
/// </summary>
public sealed partial class DosRuntime {

  private const int _STRING_HANDLES = 512;

  public Label StrAlloc { get; private set; } = null!;
  public Label StrFree { get; private set; } = null!;
  public Label StrDup { get; private set; } = null!;
  public Label StrMem { get; private set; } = null!;
  public Label StrCat { get; private set; } = null!;
  public Label StrCmp { get; private set; } = null!;
  public Label StrMid { get; private set; } = null!;
  public Label StrLeft { get; private set; } = null!;
  public Label StrRight { get; private set; } = null!;
  public Label Instr { get; private set; } = null!;
  public Label StrUpr { get; private set; } = null!;
  public Label StrLwr { get; private set; } = null!;
  public Label LTrim { get; private set; } = null!;
  public Label RTrim { get; private set; } = null!;
  public Label StrFill { get; private set; } = null!;
  public Label Chr { get; private set; } = null!;
  public Label Repeat { get; private set; } = null!;
  public Label Asc { get; private set; } = null!;
  public Label Len { get; private set; } = null!;
  public Label Val { get; private set; } = null!;
  public Label Radix { get; private set; } = null!;
  public Label StrI16 { get; private set; } = null!;
  public Label StrI32 { get; private set; } = null!;
  public Label StrF64 { get; private set; } = null!;
  public Label StrF32 { get; private set; } = null!;
  public Label StrPrint { get; private set; } = null!;
  public Label StrAssign { get; private set; } = null!;
  public Label StrAssignEs { get; private set; } = null!;
  public Label StoreFixed { get; private set; } = null!;
  public Label MidSet { get; private set; } = null!;

  private Mem Descriptor(Reg index, int delta = 0) => Mem.Word(index, this._asmStrTab, delta);
  private Label _asmStrTab = null!;

  private void EmitStringProcedures(Assembler asm) {
    this._asmStrTab = asm.Lbl("rt_strtab");
    this.EmitStrAlloc(asm);
    this.EmitStrFree(asm);
    this.EmitStrCompact(asm);
    this.EmitStrCopyInto(asm);
    this.EmitStrMem(asm);
    this.EmitStrDup(asm);
    this.EmitStrCat(asm);
    this.EmitStrCmp(asm);
    this.EmitStrMid(asm);
    this.EmitStrLeftRight(asm);
    this.EmitInstr(asm);
    this.EmitStrCase(asm);
    this.EmitTrims(asm);
    this.EmitStrFill(asm);
    this.EmitRepeat(asm);
    this.EmitAscLen(asm);
    this.EmitVal(asm);
    this.EmitRadix(asm);
    this.EmitStrFromNumber(asm);
    this.EmitStrPrint(asm);
    this.EmitStrAssign(asm);
    this.EmitStoreFixed(asm);
    this.EmitMidSet(asm);
  }

  /// <summary>Console/string bookkeeping cells - small, needed by the entry stub and PrintStr.</summary>
  private void EmitStringCells(Assembler asm) {
    asm.Align(2);
    asm.MarkLabel("rt_strseg");
    asm.Dw(0);
    asm.MarkLabel("rt_strtop");
    asm.Dw(0);
    asm.MarkLabel("rt_curout");
    asm.Dw(0);
    asm.MarkLabel("rt_col");
    asm.Dw(0);
    asm.MarkLabel("rt_caplen");
    asm.Dw(0);
    asm.MarkLabel("rt_capmode");
    asm.Db(0, 0);
    asm.MarkLabel("rt_st0");
    asm.Dw(0);
    asm.MarkLabel("rt_st1");
    asm.Dw(0);
    asm.MarkLabel("rt_st2");
    asm.Dw(0);
    asm.MarkLabel("rt_st3");
    asm.Dw(0);
    this.ZeroBlob(asm, "rt_capbuf", 64);
  }

  /// <summary>The 2 KiB string descriptor table - needed only by the string kernel itself.</summary>
  private void EmitStringTable(Assembler asm) {
    asm.Align(2);
    this.ZeroBlob(asm, "rt_strtab", _STRING_HANDLES * 4);
  }

  private void EmitStrAlloc(Assembler asm) {
    this.StrAlloc = asm.MarkLabel("rt_stralloc");
    var empty = asm.DefineLabel();
    var scan = asm.DefineLabel();
    var found = asm.DefineLabel();
    var full = asm.DefineLabel();
    var fits = asm.DefineLabel();
    var oom = asm.DefineLabel();

    asm.Test(Reg.CX, Reg.CX);   // (Jcxz reaches only +-128 - the body outgrew it)
    asm.Jz(empty);
    // $STRING n caps individual string length (default 32750) -> Error 14
    var lengthOk = asm.DefineLabel();
    asm.Cmp(Reg.CX, Mem.Word(asm.Lbl("rt_strmaxlen")));
    asm.Jbe(lengthOk);
    asm.Mov(Reg.AX, 15);        // "String too long" (oracle-verified)
    asm.Call(asm.Lbl("rt_raise"));
    asm.MarkLabel(lengthOk);
    asm.Push(Reg.BX);
    asm.Push(Reg.DX);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));

    asm.Mov(Reg.BX, 4);
    asm.MarkLabel(scan);
    asm.Cmp(this.Descriptor(Reg.BX, 2), (Imm)0);
    asm.Je(found);
    asm.Add(Reg.BX, 4);
    asm.Cmp(Reg.BX, _STRING_HANDLES * 4);
    asm.Jb(scan);
    asm.Jmp(asm.Lbl("rt_err_oss"));

    asm.MarkLabel(found);
    asm.Mov(Reg.DI, Mem.Word(asm.Lbl("rt_strtop")));
    asm.Mov(Reg.DX, Reg.DI);
    asm.Add(Reg.DX, Reg.CX);
    asm.Jc(full);
    asm.Add(Reg.DX, 4);
    asm.Jc(full);
    asm.Cmp(Reg.DX, 0xFFF0);
    asm.Jbe(fits);

    asm.MarkLabel(full);
    asm.Call(asm.Lbl("rt_strcompact"));
    asm.Mov(Reg.DI, Mem.Word(asm.Lbl("rt_strtop")));
    asm.Mov(Reg.DX, Reg.DI);
    asm.Add(Reg.DX, Reg.CX);
    asm.Jc(oom);
    asm.Add(Reg.DX, 4);
    asm.Jc(oom);
    asm.Cmp(Reg.DX, 0xFFF0);
    asm.Ja(oom);

    asm.MarkLabel(fits);
    asm.Mov(Reg.AX, Reg.BX);
    asm.Shr(Reg.AX, 2);
    asm.Mov(Mem.Word(Reg.DI).Es(), Reg.AX);
    asm.Mov(Mem.Word(Reg.DI, 2).Es(), Reg.CX);
    asm.Add(Reg.DI, 4);
    asm.Mov(this.Descriptor(Reg.BX), Reg.DI);
    asm.Mov(this.Descriptor(Reg.BX, 2), Reg.CX);
    asm.Mov(Mem.Word(asm.Lbl("rt_strtop")), Reg.DX);
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.BX);
    asm.Ret();

    asm.MarkLabel(empty);
    asm.Xor(Reg.AX, Reg.AX);
    asm.Ret();

    asm.MarkLabel(oom);
    asm.Jmp(asm.Lbl("rt_err_oss"));
  }

  private void EmitStrFree(Assembler asm) {
    this.StrFree = asm.MarkLabel("rt_strfree");
    var done = asm.DefineLabel();
    var restore = asm.DefineLabel();
    asm.Test(Reg.AX, Reg.AX);
    asm.Jz(done);
    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));
    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 2);
    asm.Cmp(this.Descriptor(Reg.BX, 2), (Imm)0);
    asm.Je(restore);
    asm.Mov(Reg.DI, this.Descriptor(Reg.BX));
    asm.Mov(Mem.Word(Reg.DI, -4).Es(), (Imm)0);
    asm.Mov(this.Descriptor(Reg.BX, 2), (Imm)0);
    asm.MarkLabel(restore);
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.MarkLabel(done);
    asm.Ret();
  }

  /// <summary>Compacts the string heap: walks blocks, slides live ones down, fixes descriptors.</summary>
  private void EmitStrCompact(Assembler asm) {
    asm.MarkLabel("rt_strcompact");
    var loop = asm.DefineLabel();
    var same = asm.DefineLabel();
    var dead = asm.DefineLabel();
    var done = asm.DefineLabel();

    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));
    asm.Xor(Reg.SI, Reg.SI);
    asm.Xor(Reg.DI, Reg.DI);

    asm.MarkLabel(loop);
    asm.Cmp(Reg.SI, Mem.Word(asm.Lbl("rt_strtop")));
    asm.Jae(done);
    asm.Mov(Reg.AX, Mem.Word(Reg.SI).Es());
    asm.Mov(Reg.CX, Mem.Word(Reg.SI, 2).Es());
    asm.Add(Reg.CX, 4);
    asm.Test(Reg.AX, Reg.AX);
    asm.Jz(dead);
    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.DX, Reg.DI);
    asm.Add(Reg.DX, 4);
    asm.Mov(this.Descriptor(Reg.BX), Reg.DX);
    asm.Cmp(Reg.SI, Reg.DI);
    asm.Je(same);
    asm.Push(Reg.DS);
    asm.Mov(Reg.AX, Reg.ES);
    asm.Mov(Reg.DS, Reg.AX);
    this.EmitRepMovsbWidened(asm);
    asm.Pop(Reg.DS);
    asm.Jmp(loop);
    asm.MarkLabel(same);
    asm.Add(Reg.SI, Reg.CX);
    asm.Mov(Reg.DI, Reg.SI);
    asm.Jmp(loop);
    asm.MarkLabel(dead);
    asm.Add(Reg.SI, Reg.CX);
    asm.Jmp(loop);

    asm.MarkLabel(done);
    asm.Mov(Mem.Word(asm.Lbl("rt_strtop")), Reg.DI);
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Ret();
  }

  /// <summary>BX=source handle, DI=heap write offset (ES=string segment): copies the data, advances DI. Clobbers BX.</summary>
  private void EmitStrCopyInto(Assembler asm) {
    asm.MarkLabel("rt_strcopyinto");
    var done = asm.DefineLabel();
    asm.Push(Reg.AX);
    asm.Push(Reg.CX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DS);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.CX, this.Descriptor(Reg.BX, 2));
    asm.Mov(Reg.SI, this.Descriptor(Reg.BX));
    asm.Jcxz(done);
    asm.Mov(Reg.AX, Reg.ES);
    asm.Mov(Reg.DS, Reg.AX);
    asm.Rep();
    asm.Movsb();
    asm.MarkLabel(done);
    asm.Pop(Reg.DS);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.AX);
    asm.Ret();
  }

  private void EmitStrMem(Assembler asm) {
    this.StrMem = asm.MarkLabel("rt_strmem");
    var done = asm.DefineLabel();
    asm.Call(this.StrAlloc);
    asm.Test(Reg.AX, Reg.AX);
    asm.Jz(done);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.DS);
    asm.Push(Reg.ES);
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));
    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.DI, this.Descriptor(Reg.BX));
    asm.Mov(Reg.DS, Reg.DX);
    asm.Rep();
    asm.Movsb();
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DS);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.MarkLabel(done);
    asm.Ret();
  }

  private void EmitStrDup(Assembler asm) {
    this.StrDup = asm.MarkLabel("rt_strdup");
    var ret = asm.DefineLabel();
    var nocopy = asm.DefineLabel();
    asm.Test(Reg.AX, Reg.AX);
    asm.Jz(ret);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.DS);
    asm.Push(Reg.ES);
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));
    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.CX, this.Descriptor(Reg.BX, 2));
    asm.Mov(Reg.DX, Reg.BX);                 // source descriptor offset survives the alloc
    asm.Call(this.StrAlloc);
    asm.Mov(Reg.BX, Reg.DX);
    asm.Mov(Reg.SI, this.Descriptor(Reg.BX)); // re-fetched - alloc may have compacted
    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.DI, this.Descriptor(Reg.BX));
    asm.Jcxz(nocopy);
    asm.Push(Reg.DS);
    asm.Mov(Reg.BX, Reg.ES);
    asm.Mov(Reg.DS, Reg.BX);
    asm.Rep();
    asm.Movsb();
    asm.Pop(Reg.DS);
    asm.MarkLabel(nocopy);
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DS);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.MarkLabel(ret);
    asm.Ret();
  }

  private void EmitStrCat(Assembler asm) {
    this.StrCat = asm.MarkLabel("rt_strcat");
    var leftSet = asm.DefineLabel();
    var go = asm.DefineLabel();
    var oom = asm.DefineLabel();
    asm.Test(Reg.AX, Reg.AX);
    asm.Jnz(leftSet);
    asm.Mov(Reg.AX, Reg.DX);
    asm.Ret();
    asm.MarkLabel(leftSet);
    asm.Test(Reg.DX, Reg.DX);
    asm.Jnz(go);
    asm.Ret();

    asm.MarkLabel(go);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);
    asm.Mov(Mem.Word(asm.Lbl("rt_st0")), Reg.AX);
    asm.Mov(Mem.Word(asm.Lbl("rt_st1")), Reg.DX);
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));
    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.CX, this.Descriptor(Reg.BX, 2));
    asm.Mov(Reg.BX, Reg.DX);
    asm.Shl(Reg.BX, 2);
    asm.Add(Reg.CX, this.Descriptor(Reg.BX, 2));
    asm.Jc(oom);
    asm.Call(this.StrAlloc);
    asm.Mov(Mem.Word(asm.Lbl("rt_st2")), Reg.AX);
    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.DI, this.Descriptor(Reg.BX));
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_st0")));
    asm.Call(asm.Lbl("rt_strcopyinto"));
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_st1")));
    asm.Call(asm.Lbl("rt_strcopyinto"));
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_st0")));
    asm.Call(this.StrFree);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_st1")));
    asm.Call(this.StrFree);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_st2")));
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Ret();
    asm.MarkLabel(oom);
    asm.Jmp(asm.Lbl("rt_err_oss"));
  }

  private void EmitStrCmp(Assembler asm) {
    this.StrCmp = asm.MarkLabel("rt_strcmp");
    var minOk = asm.DefineLabel();
    var prefix = asm.DefineLabel();
    var diff = asm.DefineLabel();
    var less = asm.DefineLabel();
    var equal = asm.DefineLabel();
    var greater = asm.DefineLabel();
    var output = asm.DefineLabel();

    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);
    asm.Mov(Mem.Word(asm.Lbl("rt_st0")), Reg.AX);
    asm.Mov(Mem.Word(asm.Lbl("rt_st1")), Reg.DX);
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));
    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.AX, this.Descriptor(Reg.BX, 2));   // left length
    asm.Mov(Reg.SI, this.Descriptor(Reg.BX));
    asm.Mov(Reg.BX, Reg.DX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.DX, this.Descriptor(Reg.BX, 2));   // right length
    asm.Mov(Reg.DI, this.Descriptor(Reg.BX));
    asm.Mov(Reg.CX, Reg.AX);
    asm.Cmp(Reg.CX, Reg.DX);
    asm.Jbe(minOk);
    asm.Mov(Reg.CX, Reg.DX);
    asm.MarkLabel(minOk);
    asm.Jcxz(prefix);
    asm.Push(Reg.DS);
    asm.Mov(Reg.BX, Reg.ES);
    asm.Mov(Reg.DS, Reg.BX);
    asm.Repe();
    asm.Cmpsb();
    asm.Pop(Reg.DS);
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
    asm.Push(Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_st0")));
    asm.Call(this.StrFree);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_st1")));
    asm.Call(this.StrFree);
    asm.Pop(Reg.AX);
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Ret();
  }

  private void EmitStrMid(Assembler asm) {
    this.StrMid = asm.MarkLabel("rt_strmid");
    var startOk = asm.DefineLabel();
    var lenPos = asm.DefineLabel();
    var lenOk = asm.DefineLabel();
    var empty = asm.DefineLabel();
    var output = asm.DefineLabel();

    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);
    asm.Mov(Mem.Word(asm.Lbl("rt_st0")), Reg.AX);
    asm.Cmp(Reg.CX, 1);
    asm.Jge(startOk);
    asm.Mov(Reg.CX, 1);
    asm.MarkLabel(startOk);
    asm.Test(Reg.DX, Reg.DX);
    asm.Jns(lenPos);
    asm.Xor(Reg.DX, Reg.DX);
    asm.MarkLabel(lenPos);
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));
    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.AX, this.Descriptor(Reg.BX, 2));   // source length
    asm.Sub(Reg.AX, Reg.CX);
    asm.Js(empty);
    asm.Inc(Reg.AX);                                // available
    asm.Cmp(Reg.DX, Reg.AX);
    asm.Jbe(lenOk);
    asm.Mov(Reg.DX, Reg.AX);
    asm.MarkLabel(lenOk);
    asm.Test(Reg.DX, Reg.DX);
    asm.Jz(empty);
    asm.Mov(Mem.Word(asm.Lbl("rt_st1")), Reg.CX);   // start
    asm.Mov(Reg.CX, Reg.DX);
    asm.Call(this.StrAlloc);
    asm.Mov(Mem.Word(asm.Lbl("rt_st2")), Reg.AX);
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_st0")));
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.SI, this.Descriptor(Reg.BX));       // re-fetched after alloc
    asm.Add(Reg.SI, Mem.Word(asm.Lbl("rt_st1")));
    asm.Dec(Reg.SI);
    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.DI, this.Descriptor(Reg.BX));
    asm.Push(Reg.DS);
    asm.Mov(Reg.BX, Reg.ES);
    asm.Mov(Reg.DS, Reg.BX);
    asm.Rep();
    asm.Movsb();
    asm.Pop(Reg.DS);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_st0")));
    asm.Call(this.StrFree);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_st2")));
    asm.Jmp(output);
    asm.MarkLabel(empty);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_st0")));
    asm.Call(this.StrFree);
    asm.Xor(Reg.AX, Reg.AX);
    asm.MarkLabel(output);
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Ret();
  }

  private void EmitStrLeftRight(Assembler asm) {
    this.StrLeft = asm.MarkLabel("rt_strleft");
    asm.Mov(Reg.DX, Reg.CX);
    asm.Mov(Reg.CX, 1);
    asm.Jmp(this.StrMid);

    this.StrRight = asm.MarkLabel("rt_strright");
    asm.Push(Reg.BX);
    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.DX, this.Descriptor(Reg.BX, 2));
    asm.Pop(Reg.BX);
    asm.Sub(Reg.DX, Reg.CX);
    asm.Inc(Reg.DX);          // start = len - count + 1 (StrMid clamps low values)
    asm.Xchg(Reg.CX, Reg.DX);
    asm.Jmp(this.StrMid);
  }

  private void EmitInstr(Assembler asm) {
    this.Instr = asm.MarkLabel("rt_instr");
    var clamped = asm.DefineLabel();
    var probe = asm.DefineLabel();
    var found = asm.DefineLabel();
    var none = asm.DefineLabel();
    var output = asm.DefineLabel();

    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);
    asm.Mov(Mem.Word(asm.Lbl("rt_st0")), Reg.AX);
    asm.Mov(Mem.Word(asm.Lbl("rt_st1")), Reg.DX);
    asm.Cmp(Reg.CX, 1);
    asm.Jge(clamped);
    asm.Mov(Reg.CX, 1);
    asm.MarkLabel(clamped);
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));
    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.AX, this.Descriptor(Reg.BX, 2));   // haystack length
    asm.Mov(Reg.SI, this.Descriptor(Reg.BX));
    asm.Mov(Mem.Word(asm.Lbl("rt_st2")), Reg.SI);
    asm.Mov(Reg.BX, Reg.DX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.DX, this.Descriptor(Reg.BX, 2));   // needle length
    asm.Mov(Reg.SI, this.Descriptor(Reg.BX));
    asm.Mov(Mem.Word(asm.Lbl("rt_st3")), Reg.SI);
    asm.Test(Reg.DX, Reg.DX);
    asm.Jz(none);
    asm.Sub(Reg.AX, Reg.DX);                        // last valid 0-based start
    asm.Js(none);
    asm.Dec(Reg.CX);                                // 0-based probe position
    asm.MarkLabel(probe);
    asm.Cmp(Reg.CX, Reg.AX);
    asm.Jg(none);
    asm.Mov(Reg.SI, Mem.Word(asm.Lbl("rt_st2")));
    asm.Add(Reg.SI, Reg.CX);
    asm.Mov(Reg.DI, Mem.Word(asm.Lbl("rt_st3")));
    asm.Push(Reg.CX);
    asm.Mov(Reg.CX, Reg.DX);
    asm.Push(Reg.DS);
    asm.Mov(Reg.BX, Reg.ES);
    asm.Mov(Reg.DS, Reg.BX);
    asm.Repe();
    asm.Cmpsb();
    asm.Pop(Reg.DS);
    asm.Pop(Reg.CX);
    asm.Je(found);
    asm.Inc(Reg.CX);
    asm.Jmp(probe);
    asm.MarkLabel(found);
    asm.Inc(Reg.CX);
    asm.Mov(Reg.AX, Reg.CX);
    asm.Jmp(output);
    asm.MarkLabel(none);
    asm.Xor(Reg.AX, Reg.AX);
    asm.MarkLabel(output);
    asm.Push(Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_st0")));
    asm.Call(this.StrFree);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_st1")));
    asm.Call(this.StrFree);
    asm.Pop(Reg.AX);
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Ret();
  }

  private void EmitStrCase(Assembler asm) {
    void Emit(bool toUpper) {
      var ret = asm.DefineLabel();
      var done = asm.DefineLabel();
      var next = asm.DefineLabel();
      var loop = asm.DefineLabel();
      asm.Test(Reg.AX, Reg.AX);
      asm.Jz(ret);
      asm.Push(Reg.AX);
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Push(Reg.SI);
      asm.Push(Reg.ES);
      asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));
      asm.Mov(Reg.BX, Reg.AX);
      asm.Shl(Reg.BX, 2);
      asm.Mov(Reg.CX, this.Descriptor(Reg.BX, 2));
      asm.Mov(Reg.SI, this.Descriptor(Reg.BX));
      asm.Jcxz(done);
      asm.MarkLabel(loop);
      asm.Mov(Reg.AL, Mem.Byte(Reg.SI).Es());
      asm.Cmp(Reg.AL, toUpper ? 'a' : 'A');
      asm.Jb(next);
      asm.Cmp(Reg.AL, toUpper ? 'z' : 'Z');
      asm.Ja(next);
      if (toUpper)
        asm.Sub(Reg.AL, 32);
      else
        asm.Add(Reg.AL, 32);
      asm.Mov(Mem.Byte(Reg.SI).Es(), Reg.AL);
      asm.MarkLabel(next);
      asm.Inc(Reg.SI);
      asm.Loop(loop);
      asm.MarkLabel(done);
      asm.Pop(Reg.ES);
      asm.Pop(Reg.SI);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.BX);
      asm.Pop(Reg.AX);
      asm.MarkLabel(ret);
      asm.Ret();
    }

    this.StrUpr = asm.MarkLabel("rt_strupr");
    Emit(toUpper: true);
    this.StrLwr = asm.MarkLabel("rt_strlwr");
    Emit(toUpper: false);
  }

  private void EmitTrims(Assembler asm) {
    this.LTrim = asm.MarkLabel("rt_ltrim");
    {
      var ret = asm.DefineLabel();
      var scan = asm.DefineLabel();
      var done = asm.DefineLabel();
      asm.Test(Reg.AX, Reg.AX);
      asm.Jnz(scan);
      asm.MarkLabel(ret);
      asm.Ret();
      asm.MarkLabel(scan);
      asm.Push(Reg.BX);
      asm.Push(Reg.SI);
      asm.Push(Reg.ES);
      asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));
      asm.Mov(Reg.BX, Reg.AX);
      asm.Shl(Reg.BX, 2);
      asm.Mov(Reg.CX, this.Descriptor(Reg.BX, 2));
      asm.Mov(Reg.SI, this.Descriptor(Reg.BX));
      asm.Xor(Reg.DX, Reg.DX);
      var loop = asm.DefineLabel();
      asm.MarkLabel(loop);
      asm.Jcxz(done);
      asm.Cmp(Mem.Byte(Reg.SI).Es(), (byte)' ');
      asm.Jne(done);
      asm.Inc(Reg.SI);
      asm.Inc(Reg.DX);
      asm.Dec(Reg.CX);
      asm.Jmp(loop);
      asm.MarkLabel(done);
      asm.Mov(Reg.CX, Reg.DX);
      asm.Inc(Reg.CX);          // start = leading spaces + 1
      asm.Mov(Reg.DX, 0x7FFF);
      asm.Pop(Reg.ES);
      asm.Pop(Reg.SI);
      asm.Pop(Reg.BX);
      asm.Jmp(this.StrMid);
    }

    this.RTrim = asm.MarkLabel("rt_rtrim");
    {
      var scan = asm.DefineLabel();
      var done = asm.DefineLabel();
      var loop = asm.DefineLabel();
      asm.Test(Reg.AX, Reg.AX);
      asm.Jnz(scan);
      asm.Ret();
      asm.MarkLabel(scan);
      asm.Push(Reg.BX);
      asm.Push(Reg.SI);
      asm.Push(Reg.ES);
      asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));
      asm.Mov(Reg.BX, Reg.AX);
      asm.Shl(Reg.BX, 2);
      asm.Mov(Reg.CX, this.Descriptor(Reg.BX, 2));
      asm.Mov(Reg.SI, this.Descriptor(Reg.BX));
      asm.Add(Reg.SI, Reg.CX);  // one past the end
      asm.MarkLabel(loop);
      asm.Jcxz(done);
      asm.Cmp(Mem.Byte(Reg.SI, -1).Es(), (byte)' ');
      asm.Jne(done);
      asm.Dec(Reg.SI);
      asm.Dec(Reg.CX);
      asm.Jmp(loop);
      asm.MarkLabel(done);
      asm.Mov(Reg.DX, Reg.CX);  // remaining length
      asm.Mov(Reg.CX, 1);
      asm.Pop(Reg.ES);
      asm.Pop(Reg.SI);
      asm.Pop(Reg.BX);
      asm.Jmp(this.StrMid);
    }
  }

  private void EmitStrFill(Assembler asm) {
    this.StrFill = asm.MarkLabel("rt_strfill");
    var go = asm.DefineLabel();
    asm.Test(Reg.CX, Reg.CX);
    asm.Jg(go);
    asm.Xor(Reg.AX, Reg.AX);
    asm.Ret();
    asm.MarkLabel(go);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);
    asm.Call(this.StrAlloc);
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));
    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.DI, this.Descriptor(Reg.BX));
    asm.Push(Reg.AX);
    asm.Mov(Reg.AL, Reg.DL);
    asm.Rep();
    asm.Stosb();
    asm.Pop(Reg.AX);
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Ret();

    this.Chr = asm.MarkLabel("rt_chr");
    asm.Push(Reg.CX);
    asm.Mov(Reg.CX, 1);
    asm.Call(this.StrFill);
    asm.Pop(Reg.CX);
    asm.Ret();
  }

  private void EmitRepeat(Assembler asm) {
    this.Repeat = asm.MarkLabel("rt_repeat");
    var go = asm.DefineLabel();
    var loop = asm.DefineLabel();
    var oom = asm.DefineLabel();
    asm.Test(Reg.CX, Reg.CX);
    asm.Jg(go);
    asm.Call(this.StrFree);
    asm.Xor(Reg.AX, Reg.AX);
    asm.Ret();
    asm.MarkLabel(go);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);
    asm.Mov(Mem.Word(asm.Lbl("rt_st0")), Reg.AX);
    asm.Mov(Mem.Word(asm.Lbl("rt_st1")), Reg.CX);
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));
    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.AX, this.Descriptor(Reg.BX, 2));
    asm.Mul(Reg.CX);
    asm.Test(Reg.DX, Reg.DX);
    asm.Jnz(oom);
    asm.Mov(Reg.CX, Reg.AX);
    asm.Call(this.StrAlloc);
    asm.Mov(Mem.Word(asm.Lbl("rt_st2")), Reg.AX);
    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.DI, this.Descriptor(Reg.BX));
    asm.Mov(Reg.CX, Mem.Word(asm.Lbl("rt_st1")));
    asm.MarkLabel(loop);
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_st0")));
    asm.Call(asm.Lbl("rt_strcopyinto"));
    asm.Loop(loop);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_st0")));
    asm.Call(this.StrFree);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_st2")));
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Ret();
    asm.MarkLabel(oom);
    asm.Jmp(asm.Lbl("rt_err_oss"));
  }

  private void EmitAscLen(Assembler asm) {
    this.Asc = asm.MarkLabel("rt_asc");
    {
      var ret = asm.DefineLabel();
      asm.Test(Reg.AX, Reg.AX);
      asm.Jz(ret);
      asm.Push(Reg.BX);
      asm.Push(Reg.SI);
      asm.Push(Reg.ES);
      asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));
      asm.Mov(Reg.BX, Reg.AX);
      asm.Shl(Reg.BX, 2);
      asm.Mov(Reg.SI, this.Descriptor(Reg.BX));
      asm.Mov(Reg.BX, Reg.AX);                       // handle for the free below
      asm.Mov(Reg.AL, Mem.Byte(Reg.SI).Es());
      asm.Xor(Reg.AH, Reg.AH);
      asm.Push(Reg.AX);
      asm.Mov(Reg.AX, Reg.BX);
      asm.Call(this.StrFree);
      asm.Pop(Reg.AX);
      asm.Pop(Reg.ES);
      asm.Pop(Reg.SI);
      asm.Pop(Reg.BX);
      asm.MarkLabel(ret);
      asm.Ret();
    }

    this.Len = asm.MarkLabel("rt_len");
    {
      var ret = asm.DefineLabel();
      asm.Test(Reg.AX, Reg.AX);
      asm.Jz(ret);
      asm.Push(Reg.BX);
      asm.Push(Reg.SI);
      asm.Mov(Reg.BX, Reg.AX);
      asm.Shl(Reg.BX, 2);
      asm.Mov(Reg.SI, this.Descriptor(Reg.BX, 2));
      asm.Call(this.StrFree);
      asm.Mov(Reg.AX, Reg.SI);
      asm.Pop(Reg.SI);
      asm.Pop(Reg.BX);
      asm.MarkLabel(ret);
      asm.Ret();
    }
  }

  private void EmitVal(Assembler asm) {
    this.Val = asm.MarkLabel("rt_val");
    var spaces = asm.DefineLabel();
    var sign = asm.DefineLabel();
    var plus = asm.DefineLabel();
    var digits = asm.DefineLabel();
    var point = asm.DefineLabel();
    var noFrac = asm.DefineLabel();
    var scale = asm.DefineLabel();
    var scaleLoop = asm.DefineLabel();
    var applySign = asm.DefineLabel();
    var finish = asm.DefineLabel();
    var expCheck = asm.DefineLabel();
    var exponent = asm.DefineLabel();
    var expDigits = asm.DefineLabel();
    var expApply = asm.DefineLabel();
    var expUp = asm.DefineLabel();
    var expDown = asm.DefineLabel();
    var radix = asm.DefineLabel();
    var radixBase = asm.DefineLabel();
    var radixLoop = asm.DefineLabel();
    var radixDigit09 = asm.DefineLabel();
    var radixGot = asm.DefineLabel();
    var radixFix = asm.DefineLabel();
    var radixFix32 = asm.DefineLabel();
    var expSkipSign = asm.DefineLabel();
    var expPositive = asm.DefineLabel();
    var radixNotHex = asm.DefineLabel();
    var radixNotOctal = asm.DefineLabel();
    var noRadix = asm.DefineLabel();
    var expNonZero = asm.DefineLabel();
    var begin = asm.DefineLabel();
    var toScale = asm.DefineLabel();
    var prefix = asm.DefineLabel();

    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.ES);
    asm.Mov(Mem.Word(asm.Lbl("rt_st2")), (Imm)0);   // seen-decimal-point flag
    asm.Fldz();
    asm.Test(Reg.AX, Reg.AX);
    asm.Jnz(begin);
    asm.Jmp(finish);
    asm.MarkLabel(begin);
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));
    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.CX, this.Descriptor(Reg.BX, 2));
    asm.Mov(Reg.SI, this.Descriptor(Reg.BX));
    asm.Xor(Reg.DX, Reg.DX);                        // DL=sign, DH=fraction digit count

    asm.MarkLabel(spaces);
    asm.Jcxz(toScale);
    asm.Cmp(Mem.Byte(Reg.SI).Es(), (byte)' ');
    asm.Jne(sign);
    asm.Inc(Reg.SI);
    asm.Dec(Reg.CX);
    asm.Jmp(spaces);

    asm.MarkLabel(sign);
    asm.Cmp(Mem.Byte(Reg.SI).Es(), (byte)'-');
    asm.Jne(plus);
    asm.Mov(Reg.DL, 1);
    asm.Inc(Reg.SI);
    asm.Dec(Reg.CX);
    asm.Jmp(prefix);
    asm.MarkLabel(toScale);
    asm.Jmp(scale);
    asm.MarkLabel(plus);
    asm.Cmp(Mem.Byte(Reg.SI).Es(), (byte)'+');
    asm.Jne(prefix);
    asm.Inc(Reg.SI);
    asm.Dec(Reg.CX);

    // a leading '&' selects literal-style radix parsing (&H hex, &O/& octal, &B binary)
    asm.MarkLabel(prefix);
    asm.Jcxz(toScale);
    asm.Cmp(Mem.Byte(Reg.SI).Es(), (byte)'&');
    asm.Jne(noRadix);
    asm.Jmp(radix);
    asm.MarkLabel(noRadix);

    asm.MarkLabel(digits);
    asm.Jcxz(toScale);
    asm.Mov(Reg.AL, Mem.Byte(Reg.SI).Es());
    asm.Cmp(Reg.AL, '.');
    asm.Je(point);
    asm.Cmp(Reg.AL, '0');
    asm.Jb(expCheck);
    asm.Cmp(Reg.AL, '9');
    asm.Ja(expCheck);
    asm.Sub(Reg.AL, '0');
    asm.Xor(Reg.AH, Reg.AH);
    asm.Mov(Mem.Word(this._scratch), Reg.AX);
    asm.Fmul(Mem.Qword(asm.Lbl("rt_const_ten_m64")));
    asm.Fiadd(Mem.Word(this._scratch));
    asm.Cmp(Mem.Word(asm.Lbl("rt_st2")), (Imm)0);
    asm.Je(noFrac);
    asm.Add(Reg.DH, (Imm)1);
    asm.MarkLabel(noFrac);
    asm.Inc(Reg.SI);
    asm.Dec(Reg.CX);
    asm.Jmp(digits);

    asm.MarkLabel(point);
    asm.Cmp(Mem.Word(asm.Lbl("rt_st2")), (Imm)0);
    asm.Jne(toScale);
    asm.Mov(Mem.Word(asm.Lbl("rt_st2")), 1);
    asm.Inc(Reg.SI);
    asm.Dec(Reg.CX);
    asm.Jmp(digits);

    // E/e/D/d introduces a decimal exponent ("1E3", "1.5e-2", "1D20")
    asm.MarkLabel(expCheck);
    asm.Cmp(Reg.AL, 'E');
    asm.Je(exponent);
    asm.Cmp(Reg.AL, 'e');
    asm.Je(exponent);
    asm.Cmp(Reg.AL, 'D');
    asm.Je(exponent);
    asm.Cmp(Reg.AL, 'd');
    asm.Je(exponent);
    asm.Jmp(scale);

    asm.MarkLabel(exponent);
    asm.Inc(Reg.SI);
    asm.Dec(Reg.CX);
    asm.Xor(Reg.BX, Reg.BX);                        // exponent magnitude
    asm.Mov(Mem.Word(asm.Lbl("rt_st2")), (Imm)0);   // reuse: exponent-negative flag
    asm.Jcxz(expApply);
    asm.Cmp(Mem.Byte(Reg.SI).Es(), (byte)'+');
    asm.Je(expSkipSign);
    asm.Cmp(Mem.Byte(Reg.SI).Es(), (byte)'-');
    asm.Jne(expDigits);
    asm.Mov(Mem.Word(asm.Lbl("rt_st2")), 1);
    asm.MarkLabel(expSkipSign);
    asm.Inc(Reg.SI);
    asm.Dec(Reg.CX);
    asm.MarkLabel(expDigits);
    asm.Jcxz(expApply);
    asm.Mov(Reg.AL, Mem.Byte(Reg.SI).Es());
    asm.Cmp(Reg.AL, '0');
    asm.Jb(expApply);
    asm.Cmp(Reg.AL, '9');
    asm.Ja(expApply);
    asm.Sub(Reg.AL, '0');
    asm.Xor(Reg.AH, Reg.AH);
    asm.Mov(Mem.Word(this._scratch, 2), Reg.AX);
    asm.Mov(Reg.AX, Reg.BX);                        // BX = BX*10 + digit
    asm.Shl(Reg.AX, 2);
    asm.Add(Reg.AX, Reg.BX);
    asm.Shl(Reg.AX, 1);
    asm.Add(Reg.AX, Mem.Word(this._scratch, 2));
    asm.Mov(Reg.BX, Reg.AX);
    asm.Inc(Reg.SI);
    asm.Dec(Reg.CX);
    asm.Jmp(expDigits);

    asm.MarkLabel(expApply);
    asm.Mov(Reg.AX, Reg.BX);                        // AX = signed exponent - fraction digits
    asm.Cmp(Mem.Word(asm.Lbl("rt_st2")), (Imm)0);
    asm.Je(expPositive);
    asm.Neg(Reg.AX);
    asm.MarkLabel(expPositive);
    asm.Mov(Reg.BL, Reg.DH);
    asm.Xor(Reg.BH, Reg.BH);
    asm.Sub(Reg.AX, Reg.BX);
    asm.Jnz(expNonZero);
    asm.Jmp(applySign);
    asm.MarkLabel(expNonZero);
    asm.Jns(expUp);
    asm.Neg(Reg.AX);
    asm.MarkLabel(expDown);
    asm.Fdiv(Mem.Qword(asm.Lbl("rt_const_ten_m64")));
    asm.Dec(Reg.AX);
    asm.Jnz(expDown);
    asm.Jmp(applySign);
    asm.MarkLabel(expUp);
    asm.Fmul(Mem.Qword(asm.Lbl("rt_const_ten_m64")));
    asm.Dec(Reg.AX);
    asm.Jnz(expUp);
    asm.Jmp(applySign);

    // radix parsing follows source-literal rules: 16-bit window signed
    // (VAL("&HFFFF") = -1), wider values widen like literals (&H10000 = 65536)
    asm.MarkLabel(radix);
    asm.Inc(Reg.SI);
    asm.Dec(Reg.CX);
    asm.Mov(Mem.Word(this._scratch, 2), (Imm)8);    // default: bare & is octal
    asm.Jcxz(radixFix);
    asm.Mov(Reg.AL, Mem.Byte(Reg.SI).Es());
    asm.And(Reg.AL, 0xDF);                          // ASCII uppercase
    asm.Cmp(Reg.AL, 'H');
    asm.Jne(radixNotHex);
    asm.Mov(Mem.Word(this._scratch, 2), (Imm)16);
    asm.Jmp(radixBase);
    asm.MarkLabel(radixNotHex);
    asm.Cmp(Reg.AL, 'O');
    asm.Jne(radixNotOctal);
    asm.Jmp(radixBase);
    asm.MarkLabel(radixNotOctal);
    asm.Cmp(Reg.AL, 'B');
    asm.Jne(radixLoop);                             // bare &777: digit, keep base 8
    asm.Mov(Mem.Word(this._scratch, 2), (Imm)2);
    asm.MarkLabel(radixBase);
    asm.Inc(Reg.SI);
    asm.Dec(Reg.CX);
    asm.MarkLabel(radixLoop);
    asm.Jcxz(radixFix);
    asm.Mov(Reg.AL, Mem.Byte(Reg.SI).Es());
    asm.Cmp(Reg.AL, '0');
    asm.Jb(radixFix);
    asm.Cmp(Reg.AL, '9');
    asm.Jbe(radixDigit09);
    asm.And(Reg.AL, 0xDF);
    asm.Cmp(Reg.AL, 'A');
    asm.Jb(radixFix);
    asm.Cmp(Reg.AL, 'F');
    asm.Ja(radixFix);
    asm.Sub(Reg.AL, (byte)('A' - 10));
    asm.Jmp(radixGot);
    asm.MarkLabel(radixDigit09);
    asm.Sub(Reg.AL, '0');
    asm.MarkLabel(radixGot);
    asm.Xor(Reg.AH, Reg.AH);
    asm.Cmp(Reg.AX, Mem.Word(this._scratch, 2));
    asm.Jae(radixFix);                              // digit not valid in this base
    asm.Mov(Mem.Word(this._scratch), Reg.AX);
    asm.Fimul(Mem.Word(this._scratch, 2));
    asm.Fiadd(Mem.Word(this._scratch));
    asm.Inc(Reg.SI);
    asm.Dec(Reg.CX);
    asm.Jmp(radixLoop);

    asm.MarkLabel(radixFix);
    if (this.Dialect <= Dialect.Pb21) {
      // TB and PB 2.x wrap radix values to 16 bits (VAL("&H10000") = 0,
      // VAL("&HFFFF") = -1); the literal-style wider windows arrived with PB 3.x
      asm.Fld(Mem.Qword(asm.Lbl("rt_const_65536")));
      asm.Fxch();
      asm.MarkLabel("rt_val_tbwrap");
      asm.Fprem();
      asm.FstswAx();
      asm.Test(Reg.AX, 0x0400);              // C2 set -> partial remainder, loop
      asm.Jnz(asm.Lbl("rt_val_tbwrap"));
      asm.Fstp(St.St1);                      // drop the 65536 divisor
    }
    asm.Fcom(Mem.Qword(asm.Lbl("rt_const_65536")));
    asm.FstswAx();
    asm.Sahf();
    asm.Jae(radixFix32);
    asm.Fcom(Mem.Qword(asm.Lbl("rt_const_32768")));
    asm.FstswAx();
    asm.Sahf();
    asm.Jb(applySign);
    asm.Fsub(Mem.Qword(asm.Lbl("rt_const_65536")));
    asm.Jmp(applySign);
    asm.MarkLabel(radixFix32);
    asm.Fcom(Mem.Qword(asm.Lbl("rt_const_2p32")));
    asm.FstswAx();
    asm.Sahf();
    asm.Jae(applySign);
    asm.Fcom(Mem.Qword(asm.Lbl("rt_const_2p31")));
    asm.FstswAx();
    asm.Sahf();
    asm.Jb(applySign);
    asm.Fsub(Mem.Qword(asm.Lbl("rt_const_2p32")));
    asm.Jmp(applySign);

    asm.MarkLabel(scale);
    asm.MarkLabel(scaleLoop);
    asm.Test(Reg.DH, Reg.DH);
    asm.Jz(applySign);
    asm.Fdiv(Mem.Qword(asm.Lbl("rt_const_ten_m64")));
    asm.Sub(Reg.DH, (Imm)1);
    asm.Jmp(scaleLoop);

    asm.MarkLabel(applySign);
    asm.Test(Reg.DL, Reg.DL);
    asm.Jz(finish);
    asm.Fchs();

    asm.MarkLabel(finish);
    asm.Pop(Reg.ES);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Call(this.StrFree);
    asm.Ret();
  }

  private void EmitRadix(Assembler asm) {
    this.Radix = asm.MarkLabel("rt_radix");
    var digit = asm.DefineLabel();
    var isNumeral = asm.DefineLabel();
    var shift = asm.DefineLabel();
    var build = asm.DefineLabel();

    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Mov(Reg.SI, Imm.OffsetOf(this._numBuffer, 34));
    asm.Xor(Reg.DI, Reg.DI);                        // emitted digit count

    // genuine HEX$/OCT$/BIN$ render at a 16-bit width whenever the 32-bit value
    // fits in [-32768, 65535] - a small negative arrives sign-extended as
    // DX=FFFF with AX's high bit set, so fold the redundant FFFF away and let
    // the digit loop stop after the low word. (DX=0000 already renders 16-bit;
    // any other DX is a genuine 32-bit magnitude and is left intact.)
    var keep32 = asm.DefineLabel();
    asm.Cmp(Reg.DX, 0xFFFF);
    asm.Jne(keep32);
    asm.Cmp(Reg.AX, 0x8000);                        // unsigned: AX>=8000 => negative 16-bit
    asm.Jb(keep32);
    asm.Xor(Reg.DX, Reg.DX);
    asm.MarkLabel(keep32);

    asm.MarkLabel(digit);
    asm.Push(Reg.CX);
    asm.Mov(Reg.BX, 1);
    asm.Shl(Reg.BX, Reg.CL);
    asm.Dec(Reg.BX);
    asm.And(Reg.BX, Reg.AX);
    asm.Cmp(Reg.BL, (Imm)10);
    asm.Jb(isNumeral);
    asm.Add(Reg.BL, (Imm)7);
    asm.MarkLabel(isNumeral);
    asm.Add(Reg.BL, (Imm)'0');
    asm.Dec(Reg.SI);
    asm.Mov(Mem.Byte(Reg.SI), Reg.BL);
    asm.Inc(Reg.DI);
    asm.Mov(Reg.BX, Reg.CX);
    asm.And(Reg.BX, 0xFF);
    asm.MarkLabel(shift);
    asm.Shr(Reg.DX, 1);
    asm.Rcr(Reg.AX, 1);
    asm.Dec(Reg.BX);
    asm.Jnz(shift);
    asm.Pop(Reg.CX);
    asm.Mov(Reg.BX, Reg.AX);
    asm.Or(Reg.BX, Reg.DX);
    asm.Jnz(digit);
    asm.Mov(Reg.BL, Reg.CH);
    asm.Xor(Reg.BH, Reg.BH);
    asm.Cmp(Reg.DI, Reg.BX);
    asm.Jb(digit);

    asm.MarkLabel(build);
    asm.Mov(Reg.CX, Reg.DI);
    asm.Mov(Reg.DX, Reg.DS);
    asm.Call(this.StrMem);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Ret();
  }

  /// <summary>STR$ via capture mode: routes the print formatters into rt_capbuf, strips the trailing space.</summary>
  private void EmitStrFromNumber(Assembler asm) {
    this.StrI16 = asm.MarkLabel("rt_str_i16");
    asm.Cwd();
    // falls through into rt_str_i32
    this.StrI32 = asm.MarkLabel("rt_str_i32");
    asm.Mov(Mem.Byte(asm.Lbl("rt_capmode")), (Imm)1);
    asm.Mov(Mem.Word(asm.Lbl("rt_caplen")), (Imm)0);
    asm.Call(this.PrintInt32);
    asm.Jmp(asm.Lbl("rt_str_cap"));

    this.StrF32 = asm.MarkLabel("rt_str_f32");
    asm.Mov(Mem.Byte(asm.Lbl("rt_capmode")), (Imm)1);
    asm.Mov(Mem.Word(asm.Lbl("rt_caplen")), (Imm)0);
    asm.Call(this.PrintSingle);
    asm.Jmp(asm.Lbl("rt_str_cap"));

    this.StrF64 = asm.MarkLabel("rt_str_f64");
    asm.Mov(Mem.Byte(asm.Lbl("rt_capmode")), (Imm)1);
    asm.Mov(Mem.Word(asm.Lbl("rt_caplen")), (Imm)0);
    asm.Call(this.PrintDouble);

    asm.MarkLabel("rt_str_cap");
    asm.Mov(Mem.Byte(asm.Lbl("rt_capmode")), (Imm)0);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Mov(Reg.CX, Mem.Word(asm.Lbl("rt_caplen")));
    asm.Dec(Reg.CX);                                // strip the PRINT trailing space
    asm.Mov(Reg.SI, Imm.OffsetOf(asm.Lbl("rt_capbuf")));
    asm.Mov(Reg.DX, Reg.DS);
    asm.Call(this.StrMem);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Ret();
  }

  private void EmitStrPrint(Assembler asm) {
    this.StrPrint = asm.MarkLabel("rt_str_print");
    var ret = asm.DefineLabel();
    var capture = asm.DefineLabel();
    var copy = asm.DefineLabel();
    var done = asm.DefineLabel();
    asm.Test(Reg.AX, Reg.AX);
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
    asm.Mov(Reg.CX, this.Descriptor(Reg.BX, 2));
    asm.Mov(Reg.SI, this.Descriptor(Reg.BX));
    asm.Jcxz(done);
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
    asm.Add(Mem.Word(asm.Lbl("rt_col")), Reg.CX);
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
    asm.Call(this.StrFree);
    asm.MarkLabel(ret);
    asm.Ret();
  }

  private void EmitStrAssign(Assembler asm) {
    this.StrAssign = asm.MarkLabel("rt_str_assign");
    asm.Push(Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(Reg.BX));
    asm.Call(this.StrFree);
    asm.Pop(Reg.AX);
    asm.Mov(Mem.Word(Reg.BX), Reg.AX);
    asm.Ret();

    this.StrAssignEs = asm.MarkLabel("rt_str_assign_es");
    asm.Push(Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(Reg.BX).Es());
    asm.Call(this.StrFree);
    asm.Pop(Reg.AX);
    asm.Mov(Mem.Word(Reg.BX).Es(), Reg.AX);
    asm.Ret();
  }

  private void EmitStoreFixed(Assembler asm) {
    this.StoreFixed = asm.MarkLabel("rt_store_fixed");
    var copyDone = asm.DefineLabel();
    var padDone = asm.DefineLabel();
    var clamp = asm.DefineLabel();
    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.DS);
    asm.Push(Reg.ES);
    asm.Mov(Mem.Word(asm.Lbl("rt_st0")), Reg.AX);
    asm.Mov(Reg.ES, Reg.DX);
    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.AX, this.Descriptor(Reg.BX, 2));
    asm.Mov(Reg.SI, this.Descriptor(Reg.BX));
    asm.Cmp(Reg.AX, Reg.CX);
    asm.Jbe(clamp);
    asm.Mov(Reg.AX, Reg.CX);
    asm.MarkLabel(clamp);
    asm.Sub(Reg.CX, Reg.AX);                        // pad count
    asm.Push(Reg.CX);
    asm.Mov(Reg.CX, Reg.AX);
    asm.Jcxz(copyDone);
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_strseg")));
    asm.Push(Reg.DS);
    asm.Mov(Reg.DS, Reg.BX);
    asm.Rep();
    asm.Movsb();
    asm.Pop(Reg.DS);
    asm.MarkLabel(copyDone);
    asm.Pop(Reg.CX);
    asm.Jcxz(padDone);
    asm.Mov(Reg.AL, ' ');
    asm.Rep();
    asm.Stosb();
    asm.MarkLabel(padDone);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_st0")));
    asm.Call(this.StrFree);
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DS);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Ret();
  }

  private void EmitMidSet(Assembler asm) {
    this.MidSet = asm.MarkLabel("rt_midset");
    var clamped = asm.DefineLabel();
    var fitsTarget = asm.DefineLabel();
    var fitsLimit = asm.DefineLabel();
    var done = asm.DefineLabel();
    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);
    asm.Mov(Mem.Word(asm.Lbl("rt_st0")), Reg.DX);   // replacement (freed at the end)
    asm.Mov(Mem.Word(asm.Lbl("rt_st1")), Reg.BX);   // length limit
    asm.Cmp(Reg.CX, 1);
    asm.Jge(clamped);
    asm.Mov(Reg.CX, 1);
    asm.MarkLabel(clamped);
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));
    asm.Test(Reg.AX, Reg.AX);
    asm.Jz(done);
    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.AX, this.Descriptor(Reg.BX, 2));    // target length
    asm.Mov(Reg.DI, this.Descriptor(Reg.BX));
    asm.Add(Reg.DI, Reg.CX);
    asm.Dec(Reg.DI);
    asm.Sub(Reg.AX, Reg.CX);
    asm.Js(done);
    asm.Inc(Reg.AX);                                // available room in the target
    asm.Mov(Reg.BX, Reg.DX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.CX, this.Descriptor(Reg.BX, 2));    // replacement length
    asm.Mov(Reg.SI, this.Descriptor(Reg.BX));
    asm.Cmp(Reg.CX, Reg.AX);
    asm.Jbe(fitsTarget);
    asm.Mov(Reg.CX, Reg.AX);
    asm.MarkLabel(fitsTarget);
    asm.Cmp(Reg.CX, Mem.Word(asm.Lbl("rt_st1")));
    asm.Jbe(fitsLimit);
    asm.Mov(Reg.CX, Mem.Word(asm.Lbl("rt_st1")));
    asm.MarkLabel(fitsLimit);
    asm.Jcxz(done);
    asm.Mov(Reg.BX, Reg.ES);
    asm.Push(Reg.DS);
    asm.Mov(Reg.DS, Reg.BX);
    asm.Rep();
    asm.Movsb();
    asm.Pop(Reg.DS);
    asm.MarkLabel(done);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_st0")));
    asm.Call(this.StrFree);
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Ret();
  }
}
