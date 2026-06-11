using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

/// <summary>
/// String runtime, part 2: character-set scanning (INSTR ANY / VERIFY),
/// EXTRACT$, TALLY, DIR$ and the ARRAY SORT/SCAN engine. Conventions:
///   ScanSet:   AX=haystack, DX=set, CX=start(1-based), BL=0 find member /
///              1 find non-member -> AX = position or 0 (consumes both)
///   Extract:   AX=main, DX=match, BL=0 substring / 1 any-set -> AX = handle (consumes both)
///   Tally:     AX=main, DX=match, BL flag as above -> AX = count (consumes both)
///   Dir:       AX=mask handle (0 = find-next), CX=attribute -> AX = name handle ("" = none; consumes)
///   StrCmpRange: AX=left, DX=right (NOT consumed) -> AX = -1/0/1; reads the
///              rt_arpb block: collate +6, from +8, to +10, flags +12 (bit1 =
///              the FROM/TO range clamps the left side only)
///   SortStr / ScanStr: parameters entirely in rt_arpb (see DosRuntime.Internals)
/// </summary>
public sealed partial class DosRuntime {

  public Label ScanSet { get; private set; } = null!;
  public Label Extract { get; private set; } = null!;
  public Label Tally { get; private set; } = null!;
  public Label Dir { get; private set; } = null!;
  public Label CurDir { get; private set; } = null!;
  public Label SortStr { get; private set; } = null!;
  public Label ScanStr { get; private set; } = null!;

  private void EmitString2Procedures(Assembler asm) {
    this.EmitScanSet(asm);
    this.EmitStrCmpRange(asm);
    this.EmitExtract(asm);
    this.EmitTally(asm);
    this.EmitDir(asm);
    this.EmitCurDir(asm);
    this.EmitPokeStr(asm);
    this.EmitRename(asm);
    this.EmitReplace(asm);
    this.EmitJustify(asm);
    this.EmitStoreFixedR(asm);
    this.EmitStringManagerExports(asm);
    this.EmitCommand(asm);
    this.EmitEnviron(asm);
    this.EmitShell(asm);   // uses Environ - must follow it
    this.EmitTimeDate(asm);
    this.EmitKeyInput(asm);
    this.EmitSortScan(asm);
  }

  public Label Command { get; private set; } = null!;
  public Label Environ { get; private set; } = null!;
  public Label TimeStr { get; private set; } = null!;
  public Label DateStr { get; private set; } = null!;
  public Label KeyInput { get; private set; } = null!;

  /// <summary>COMMAND$: PSP command tail, leading blanks stripped, uppercased.</summary>
  private void EmitCommand(Assembler asm) {
    this.Command = asm.MarkLabel("rt_command");
    var skip = asm.DefineLabel();
    var make = asm.DefineLabel();
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.ES);
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_pspseg")));
    asm.Mov(Reg.CL, Mem.Byte(0x80).Es());
    asm.Xor(Reg.CH, Reg.CH);
    asm.Mov(Reg.SI, 0x81);
    asm.MarkLabel(skip);
    asm.Jcxz(make);
    asm.Cmp(Mem.Byte(Reg.SI).Es(), (Imm)' ');
    asm.Jne(make);
    asm.Inc(Reg.SI);
    asm.Dec(Reg.CX);
    asm.Jmp(skip);
    asm.MarkLabel(make);
    asm.Mov(Reg.DX, Reg.ES);
    asm.Call(this.StrMem);
    asm.Call(this.StrUpr);
    asm.Pop(Reg.ES);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Ret();
  }

  /// <summary>ENVIRON$(name$): value of the environment variable (consumes the name handle).</summary>
  private void EmitEnviron(Assembler asm) {
    this.Environ = asm.MarkLabel("rt_environ");
    var entry = asm.DefineLabel();
    var notFound = asm.DefineLabel();
    var nextEntry = asm.DefineLabel();
    var compare = asm.DefineLabel();
    var matched = asm.DefineLabel();
    var output = asm.DefineLabel();

    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);

    asm.Call(this.StrUpr);                            // env names are uppercase
    // copy the name into rt_dirspec (DS) so one far segment suffices below
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));
    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.SI, Mem.Word(Reg.BX, asm.Lbl("rt_strtab")));
    asm.Mov(Reg.CX, Mem.Word(Reg.BX, asm.Lbl("rt_strtab"), 2));
    asm.Cmp(Reg.CX, 78);
    asm.Jbe(asm.Lbl("rt_env_lenok"));
    asm.Mov(Reg.CX, 78);
    asm.MarkLabel("rt_env_lenok");
    asm.Mov(Mem.Word(asm.Lbl("rt_ext2")), Reg.CX);    // name length
    asm.Mov(Reg.DI, Imm.OffsetOf(asm.Lbl("rt_dirspec")));
    asm.Jcxz(asm.Lbl("rt_env_copied"));
    asm.MarkLabel("rt_env_copy");
    asm.Mov(Reg.DL, Mem.Byte(Reg.SI).Es());
    asm.Mov(Mem.Byte(Reg.DI), Reg.DL);
    asm.Inc(Reg.SI);
    asm.Inc(Reg.DI);
    asm.Loop(asm.Lbl("rt_env_copy"));
    asm.MarkLabel("rt_env_copied");
    asm.Call(this.StrFree);                           // name handle no longer needed

    // walk the environment block (segment at PSP:2Ch)
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_pspseg")));
    asm.Mov(Reg.ES, Mem.Word(0x2C).Es());
    asm.Xor(Reg.SI, Reg.SI);
    asm.MarkLabel(entry);
    asm.Cmp(Mem.Byte(Reg.SI).Es(), (Imm)0);
    asm.Je(notFound);
    // compare entry against name + '='
    asm.Mov(Reg.DI, Imm.OffsetOf(asm.Lbl("rt_dirspec")));
    asm.Mov(Reg.CX, Mem.Word(asm.Lbl("rt_ext2")));
    asm.Mov(Reg.BX, Reg.SI);
    asm.MarkLabel(compare);
    asm.Jcxz(asm.Lbl("rt_env_nameend"));
    asm.Mov(Reg.DL, Mem.Byte(Reg.BX).Es());
    asm.Cmp(Reg.DL, Mem.Byte(Reg.DI));
    asm.Jne(nextEntry);
    asm.Inc(Reg.BX);
    asm.Inc(Reg.DI);
    asm.Dec(Reg.CX);
    asm.Jmp(compare);
    asm.MarkLabel("rt_env_nameend");
    asm.Cmp(Mem.Byte(Reg.BX).Es(), (Imm)'=');
    asm.Je(matched);

    asm.MarkLabel(nextEntry);                         // skip to the next ASCIIZ entry
    asm.Cmp(Mem.Byte(Reg.SI).Es(), (Imm)0);
    asm.Je(asm.Lbl("rt_env_skipped"));
    asm.Inc(Reg.SI);
    asm.Jmp(nextEntry);
    asm.MarkLabel("rt_env_skipped");
    asm.Inc(Reg.SI);
    asm.Jmp(entry);

    asm.MarkLabel(matched);                           // value = after '=' up to NUL
    asm.Inc(Reg.BX);
    asm.Mov(Reg.SI, Reg.BX);
    asm.Xor(Reg.CX, Reg.CX);
    asm.MarkLabel("rt_env_vlen");
    asm.Cmp(Mem.Byte(Reg.BX).Es(), (Imm)0);
    asm.Je(asm.Lbl("rt_env_vdone"));
    asm.Inc(Reg.BX);
    asm.Inc(Reg.CX);
    asm.Jmp(asm.Lbl("rt_env_vlen"));
    asm.MarkLabel("rt_env_vdone");
    asm.Mov(Reg.DX, Reg.ES);
    asm.Call(this.StrMem);
    asm.Jmp(output);

    asm.MarkLabel(notFound);
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

  /// <summary>TIME$ ("HH:MM:SS") and DATE$ ("MM-DD-YYYY") from the DOS clock.</summary>
  private void EmitTimeDate(Assembler asm) {
    // writes two decimal digits of AL at DS:DI (clobbers AX)
    asm.MarkLabel("rt_two_digits");
    asm.Push(Reg.BX);
    asm.Xor(Reg.AH, Reg.AH);
    asm.Mov(Reg.BL, (Imm)10);
    asm.Div(Reg.BL);                                  // AL = tens, AH = ones
    asm.Add(Reg.AX, 0x3030);
    asm.Mov(Mem.Byte(Reg.DI), Reg.AL);
    asm.Mov(Mem.Byte(Reg.DI, 1), Reg.AH);
    asm.Add(Reg.DI, 2);
    asm.Pop(Reg.BX);
    asm.Ret();

    this.TimeStr = asm.MarkLabel("rt_timestr");
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Mov(Reg.AH, 0x2C);                            // CH=hour CL=minute DH=second
    asm.Int(0x21);
    asm.Mov(Reg.DI, Imm.OffsetOf(asm.Lbl("rt_dirspec")));
    asm.Mov(Reg.AL, Reg.CH);
    asm.Call(asm.Lbl("rt_two_digits"));
    asm.Mov(Mem.Byte(Reg.DI), (Imm)':');
    asm.Inc(Reg.DI);
    asm.Mov(Reg.AL, Reg.CL);
    asm.Call(asm.Lbl("rt_two_digits"));
    asm.Mov(Mem.Byte(Reg.DI), (Imm)':');
    asm.Inc(Reg.DI);
    asm.Mov(Reg.AL, Reg.DH);
    asm.Call(asm.Lbl("rt_two_digits"));
    asm.Mov(Reg.SI, Imm.OffsetOf(asm.Lbl("rt_dirspec")));
    asm.Mov(Reg.CX, 8);
    asm.Mov(Reg.DX, Reg.DS);
    asm.Call(this.StrMem);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Ret();

    this.DateStr = asm.MarkLabel("rt_datestr");
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Mov(Reg.AH, 0x2A);                            // CX=year DH=month DL=day
    asm.Int(0x21);
    asm.Mov(Reg.DI, Imm.OffsetOf(asm.Lbl("rt_dirspec")));
    asm.Mov(Reg.AL, Reg.DH);
    asm.Call(asm.Lbl("rt_two_digits"));
    asm.Mov(Mem.Byte(Reg.DI), (Imm)'-');
    asm.Inc(Reg.DI);
    asm.Mov(Reg.AL, Reg.DL);
    asm.Call(asm.Lbl("rt_two_digits"));
    asm.Mov(Mem.Byte(Reg.DI), (Imm)'-');
    asm.Inc(Reg.DI);
    asm.Mov(Reg.AX, Reg.CX);                          // year 1980..2099
    asm.Push(Reg.DX);
    asm.Xor(Reg.DX, Reg.DX);
    asm.Mov(Reg.BX, 100);
    asm.Div(Reg.BX);                                  // AX = century, DX = year-in-century
    asm.Push(Reg.DX);
    asm.Call(asm.Lbl("rt_two_digits"));
    asm.Pop(Reg.AX);
    asm.Call(asm.Lbl("rt_two_digits"));
    asm.Pop(Reg.DX);
    asm.Mov(Reg.SI, Imm.OffsetOf(asm.Lbl("rt_dirspec")));
    asm.Mov(Reg.CX, 10);
    asm.Mov(Reg.DX, Reg.DS);
    asm.Call(this.StrMem);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Ret();
  }

  /// <summary>INPUT$(n) keyboard form: blocking-reads CX characters (no echo).</summary>
  private void EmitKeyInput(Assembler asm) {
    this.KeyInput = asm.MarkLabel("rt_keyinput");
    var loop = asm.DefineLabel();
    var done = asm.DefineLabel();
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.DI);
    asm.Cmp(Reg.CX, 78);
    asm.Jbe(asm.Lbl("rt_ki_lenok"));
    asm.Mov(Reg.CX, 78);
    asm.MarkLabel("rt_ki_lenok");
    asm.Mov(Reg.DX, Reg.CX);                          // requested count
    asm.Mov(Reg.DI, Imm.OffsetOf(asm.Lbl("rt_dirspec")));
    asm.MarkLabel(loop);
    asm.Test(Reg.DX, Reg.DX);
    asm.Jz(done);
    asm.Xor(Reg.AH, Reg.AH);                          // BIOS blocking read
    asm.Int(0x16);
    asm.Mov(Mem.Byte(Reg.DI), Reg.AL);
    asm.Inc(Reg.DI);
    asm.Dec(Reg.DX);
    asm.Jmp(loop);
    asm.MarkLabel(done);
    asm.Push(Reg.SI);
    asm.Mov(Reg.SI, Imm.OffsetOf(asm.Lbl("rt_dirspec")));
    asm.Mov(Reg.DX, Reg.DS);
    asm.Call(this.StrMem);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Ret();
  }

  /// <summary>
  /// Inline-asm-callable string manager exports (PB manual ABI): the argument
  /// is pushed on the stack (callee cleans, RET 2);
  ///   GetStrLoc(handle)  -> DX:AX = far data pointer, CX = length
  ///   GetStrLen(handle)  -> AX = CX = length
  ///   GetStrAlloc(count) -> AX = handle, DX:AX would-be pointer, CX = count
  ///   RlsStrAlloc(handle)
  /// </summary>
  private void EmitStringManagerExports(Assembler asm) {
    asm.MarkLabel("GetStrLoc");
    asm.Push(Reg.BP);
    asm.Mov(Reg.BP, Reg.SP);
    asm.Push(Reg.BX);
    asm.Mov(Reg.BX, Mem.Word(Reg.BP, 4));
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.CX, Mem.Word(Reg.BX, asm.Lbl("rt_strtab"), 2));
    asm.Mov(Reg.AX, Mem.Word(Reg.BX, asm.Lbl("rt_strtab")));
    asm.Mov(Reg.DX, Mem.Word(asm.Lbl("rt_strseg")));
    asm.Pop(Reg.BX);
    asm.Pop(Reg.BP);
    asm.Ret(2);

    asm.MarkLabel("GetStrLen");
    asm.Push(Reg.BP);
    asm.Mov(Reg.BP, Reg.SP);
    asm.Push(Reg.BX);
    asm.Mov(Reg.BX, Mem.Word(Reg.BP, 4));
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.CX, Mem.Word(Reg.BX, asm.Lbl("rt_strtab"), 2));
    asm.Mov(Reg.AX, Reg.CX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.BP);
    asm.Ret(2);

    asm.MarkLabel("GetStrAlloc");
    asm.Push(Reg.BP);
    asm.Mov(Reg.BP, Reg.SP);
    asm.Push(Reg.CX);
    asm.Mov(Reg.CX, Mem.Word(Reg.BP, 4));
    asm.Call(this.StrAlloc);                        // CX = length -> AX = handle
    asm.Pop(Reg.CX);
    asm.Mov(Reg.DX, Mem.Word(asm.Lbl("rt_strseg")));
    asm.Pop(Reg.BP);
    asm.Ret(2);

    asm.MarkLabel("RlsStrAlloc");
    asm.Push(Reg.BP);
    asm.Mov(Reg.BP, Reg.SP);
    asm.Push(Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(Reg.BP, 4));
    asm.Call(this.StrFree);
    asm.Pop(Reg.AX);
    asm.Pop(Reg.BP);
    asm.Ret(2);
  }

  public Label Replace { get; private set; } = null!;
  public Label Justify { get; private set; } = null!;
  public Label Shell { get; private set; } = null!;
  public Label StoreFixedR { get; private set; } = null!;
  public Label UsingDyn { get; private set; } = null!;

  /// <summary>
  /// USING$ with a runtime format string holding ONE numeric field:
  /// AX = format handle (consumed), ST0 = value (popped) -> AX = result handle.
  /// Literal text around the field is preserved.
  /// </summary>
  private void EmitUsingDyn(Assembler asm) {
    this.UsingDyn = asm.MarkLabel("rt_usingdyn");
    var ud = asm.Lbl("rt_ud");
    var findField = asm.DefineLabel();
    var fieldScan = asm.DefineLabel();
    var fieldDone = asm.DefineLabel();
    var decimalsDone = asm.DefineLabel();
    var emit = asm.DefineLabel();

    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);

    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));
    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.SI, Mem.Word(Reg.BX, asm.Lbl("rt_strtab"))); // format data offset
    asm.Mov(Reg.CX, Mem.Word(Reg.BX, asm.Lbl("rt_strtab"), 2)); // format length
    asm.Mov(Mem.Word(ud, 10), Reg.SI);
    asm.Mov(Mem.Word(ud, 12), Reg.CX);
    asm.Push(Reg.AX);                                        // format handle - freed at the end

    // scan: DI = index, BX walks; width cells: +0 digits+commas, +2 decimals, +4 group
    asm.Mov(Mem.Word(ud, 0), (Imm)0);
    asm.Mov(Mem.Word(ud, 2), (Imm)0);
    asm.Mov(Mem.Word(ud, 4), (Imm)0);
    asm.Xor(Reg.DI, Reg.DI);
    asm.MarkLabel(findField);
    asm.Cmp(Reg.DI, Reg.CX);
    asm.Jae(fieldDone);                                      // no field at all
    asm.Mov(Reg.BX, Reg.SI);
    asm.Add(Reg.BX, Reg.DI);
    asm.Cmp(Mem.Byte(Reg.BX).Es(), (Imm)'#');
    asm.Je(fieldScan);
    asm.Inc(Reg.DI);
    asm.Jmp(findField);

    asm.MarkLabel(fieldScan);
    asm.Mov(Mem.Word(ud, 6), Reg.DI);                        // field start
    asm.MarkLabel("rt_ud_floop");
    asm.Cmp(Reg.DI, Reg.CX);
    asm.Jae(fieldDone);
    asm.Mov(Reg.BX, Reg.SI);
    asm.Add(Reg.BX, Reg.DI);
    asm.Cmp(Mem.Byte(Reg.BX).Es(), (Imm)'#');
    asm.Jne(asm.Lbl("rt_ud_comma"));
    asm.Inc(Mem.Word(ud, 0));
    asm.Inc(Reg.DI);
    asm.Jmp(asm.Lbl("rt_ud_floop"));
    asm.MarkLabel("rt_ud_comma");
    asm.Cmp(Mem.Byte(Reg.BX).Es(), (Imm)',');
    asm.Jne(asm.Lbl("rt_ud_point"));
    asm.Mov(Reg.DX, Reg.DI);
    asm.Inc(Reg.DX);
    asm.Cmp(Reg.DX, Reg.CX);
    asm.Jae(fieldDone);
    asm.Cmp(Mem.Byte(Reg.BX, 1).Es(), (Imm)'#');
    asm.Jne(fieldDone);
    asm.Inc(Mem.Word(ud, 0));                                // a comma occupies one char
    asm.Mov(Mem.Word(ud, 4), 1);                             // grouping on
    asm.Inc(Reg.DI);
    asm.Jmp(asm.Lbl("rt_ud_floop"));
    asm.MarkLabel("rt_ud_point");
    asm.Cmp(Mem.Byte(Reg.BX).Es(), (Imm)'.');
    asm.Jne(fieldDone);
    asm.Inc(Reg.DI);
    asm.MarkLabel("rt_ud_dloop");
    asm.Cmp(Reg.DI, Reg.CX);
    asm.Jae(decimalsDone);
    asm.Mov(Reg.BX, Reg.SI);
    asm.Add(Reg.BX, Reg.DI);
    asm.Cmp(Mem.Byte(Reg.BX).Es(), (Imm)'#');
    asm.Jne(decimalsDone);
    asm.Inc(Mem.Word(ud, 2));
    asm.Inc(Reg.DI);
    asm.Jmp(asm.Lbl("rt_ud_dloop"));
    asm.MarkLabel(decimalsDone);
    asm.Cmp(Mem.Word(ud, 2), (Imm)0);
    asm.Jne(fieldDone);
    asm.Dec(Reg.DI);                                         // lone '.' stays literal
    asm.MarkLabel(fieldDone);
    asm.Mov(Mem.Word(ud, 8), Reg.DI);                        // field end

    asm.MarkLabel(emit);
    // capture on; emit prefix literal, the formatted field, then the suffix
    asm.Mov(Mem.Byte(asm.Lbl("rt_capmode")), (Imm)1);
    asm.Mov(Mem.Word(asm.Lbl("rt_caplen")), (Imm)0);

    // prefix: fmt[0 .. fieldStart) - PrintStr wants DS:SI, so copy via the capture path char-wise
    asm.Mov(Reg.CX, Mem.Word(ud, 6));
    asm.Mov(Reg.SI, Mem.Word(ud, 10));
    asm.Call(asm.Lbl("rt_ud_emitchars"));

    // the field itself (when present)
    asm.Mov(Reg.AX, Mem.Word(ud, 6));
    asm.Cmp(Reg.AX, Mem.Word(ud, 8));
    asm.Je(asm.Lbl("rt_ud_novalue"));
    // scale ST0 by 10^decimals and convert
    asm.Mov(Reg.CX, Mem.Word(ud, 2));
    asm.Jcxz(asm.Lbl("rt_ud_scaled"));
    asm.MarkLabel("rt_ud_scale");
    asm.Fmul(Mem.Qword(asm.Lbl("rt_const_ten_m64")));
    asm.Loop(asm.Lbl("rt_ud_scale"));
    asm.MarkLabel("rt_ud_scaled");
    asm.Fistp(Mem.Dword(asm.Lbl("rt_scratch")));
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_scratch")));
    asm.Mov(Reg.DX, Mem.Word(asm.Lbl("rt_scratch"), 2));
    // CX = (width << 8) | decimals | group flag
    asm.Mov(Reg.CX, Mem.Word(ud, 0));                        // digit+comma chars
    asm.Add(Reg.CX, Mem.Word(ud, 2));
    asm.Cmp(Mem.Word(ud, 2), (Imm)0);
    asm.Je(asm.Lbl("rt_ud_w"));
    asm.Inc(Reg.CX);                                         // the point
    asm.MarkLabel("rt_ud_w");
    asm.Mov(Reg.CH, Reg.CL);                                 // CH = width
    asm.Mov(Reg.CL, Mem.Byte(ud, 2));                        // CL = decimals
    asm.Cmp(Mem.Word(ud, 4), (Imm)0);
    asm.Je(asm.Lbl("rt_ud_nogrp"));
    asm.Or(Reg.CL, (Imm)0x80);
    asm.MarkLabel("rt_ud_nogrp");
    asm.Call(this.UseFmt);
    asm.Jmp(asm.Lbl("rt_ud_suffix"));
    asm.MarkLabel("rt_ud_novalue");
    asm.Fstp(St.St0);                                        // no field: drop the value

    asm.MarkLabel("rt_ud_suffix");
    asm.Mov(Reg.SI, Mem.Word(ud, 10));
    asm.Add(Reg.SI, Mem.Word(ud, 8));
    asm.Mov(Reg.CX, Mem.Word(ud, 12));
    asm.Sub(Reg.CX, Mem.Word(ud, 8));
    asm.Call(asm.Lbl("rt_ud_emitchars"));

    asm.Mov(Mem.Byte(asm.Lbl("rt_capmode")), (Imm)0);
    asm.Mov(Reg.CX, Mem.Word(asm.Lbl("rt_caplen")));
    asm.Mov(Reg.SI, Imm.OffsetOf(asm.Lbl("rt_capbuf")));
    asm.Mov(Reg.DX, Reg.DS);
    asm.Call(this.StrMem);
    asm.Pop(Reg.BX);                                         // format handle
    asm.Push(Reg.AX);
    asm.Mov(Reg.AX, Reg.BX);
    asm.Call(this.StrFree);
    asm.Pop(Reg.AX);

    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Ret();

    // helper: append CX chars at strseg:SI to the capture buffer (capture is on)
    asm.MarkLabel("rt_ud_emitchars");
    var charLoop = asm.DefineLabel();
    var charsDone = asm.DefineLabel();
    asm.Jcxz(charsDone);
    asm.MarkLabel(charLoop);
    asm.Push(Reg.CX);
    asm.Push(Reg.SI);
    asm.Mov(Reg.AL, Mem.Byte(Reg.SI).Es());
    asm.Mov(Mem.Byte(asm.Lbl("rt_scratch"), 10), Reg.AL);
    asm.Mov(Reg.SI, Imm.OffsetOf(asm.Lbl("rt_scratch"), 10));
    asm.Push(Reg.CX);
    asm.Mov(Reg.CX, 1);
    asm.Call(this.PrintStr);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.CX);
    asm.Inc(Reg.SI);
    asm.Loop(charLoop);
    asm.MarkLabel(charsDone);
    asm.Ret();
  }

  /// <summary>RSET into a fixed field: AX=handle (consumed), DX:DI=dest, CX=field length; space-pad left.</summary>
  private void EmitStoreFixedR(Assembler asm) {
    this.StoreFixedR = asm.MarkLabel("rt_storefixed_r");
    var copy = asm.DefineLabel();
    var done = asm.DefineLabel();
    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);

    asm.Mov(Reg.ES, Reg.DX);
    asm.Push(Reg.DI);
    asm.Push(Reg.CX);
    asm.Push(Reg.AX);
    asm.Mov(Reg.AL, (Imm)' ');                    // blank the field
    asm.Rep();
    asm.Stosb();
    asm.Pop(Reg.AX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.DI);

    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.SI, Mem.Word(Reg.BX, asm.Lbl("rt_strtab")));
    asm.Mov(Reg.DX, Mem.Word(Reg.BX, asm.Lbl("rt_strtab"), 2));  // value length
    asm.Cmp(Reg.DX, Reg.CX);
    asm.Jbe(asm.Lbl("rt_sfr_nok"));
    asm.Mov(Reg.DX, Reg.CX);
    asm.MarkLabel("rt_sfr_nok");
    asm.Add(Reg.DI, Reg.CX);                      // right-justify
    asm.Sub(Reg.DI, Reg.DX);
    asm.Mov(Reg.CX, Reg.DX);
    asm.Jcxz(done);
    asm.Push(Reg.DS);
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_strseg")));
    asm.Mov(Reg.DS, Reg.BX);
    asm.MarkLabel(copy);
    asm.Movsb();                                  // DS:SI -> ES:DI
    asm.Loop(copy);
    asm.Pop(Reg.DS);
    asm.MarkLabel(done);
    asm.Call(this.StrFree);                       // AX still = handle
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Ret();
  }

  /// <summary>
  /// REPLACE find WITH repl IN subject: AX=subject, DX=find, CX=repl (all
  /// consumed) -> AX = new handle with every occurrence replaced.
  /// </summary>
  private void EmitReplace(Assembler asm) {
    this.Replace = asm.MarkLabel("rt_replace");
    var rp = asm.Lbl("rt_rp");
    var loop = asm.DefineLabel();
    var tail = asm.DefineLabel();
    var unchanged = asm.DefineLabel();

    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Mov(Mem.Word(rp, 0), Reg.AX);                 // subject
    asm.Mov(Mem.Word(rp, 2), Reg.DX);                 // find
    asm.Mov(Mem.Word(rp, 4), Reg.CX);                 // repl
    asm.Mov(Mem.Word(rp, 6), (Imm)0);                 // result = ""
    asm.Mov(Mem.Word(rp, 8), 1);                      // pos
    asm.Mov(Reg.BX, Reg.DX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.AX, Mem.Word(Reg.BX, asm.Lbl("rt_strtab"), 2));
    asm.Mov(Mem.Word(rp, 10), Reg.AX);                // find length
    asm.Test(Reg.AX, Reg.AX);
    asm.Jz(unchanged);

    asm.MarkLabel(loop);
    asm.Mov(Reg.AX, Mem.Word(rp, 0));                 // i = INSTR(pos, subject, find)
    asm.Call(this.StrDup);
    asm.Push(Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(rp, 2));
    asm.Call(this.StrDup);
    asm.Mov(Reg.DX, Reg.AX);
    asm.Pop(Reg.AX);
    asm.Mov(Reg.CX, Mem.Word(rp, 8));
    asm.Call(this.Instr);
    asm.Test(Reg.AX, Reg.AX);
    asm.Jz(tail);
    asm.Mov(Mem.Word(rp, 12), Reg.AX);                // hit position
    // result += MID$(subject, pos, i - pos)
    asm.Mov(Reg.AX, Mem.Word(rp, 0));
    asm.Call(this.StrDup);
    asm.Mov(Reg.CX, Mem.Word(rp, 8));
    asm.Mov(Reg.DX, Mem.Word(rp, 12));
    asm.Sub(Reg.DX, Reg.CX);
    asm.Call(this.StrMid);
    asm.Mov(Reg.DX, Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(rp, 6));
    asm.Call(this.StrCat);
    asm.Mov(Mem.Word(rp, 6), Reg.AX);
    // result += repl
    asm.Mov(Reg.AX, Mem.Word(rp, 4));
    asm.Call(this.StrDup);
    asm.Mov(Reg.DX, Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(rp, 6));
    asm.Call(this.StrCat);
    asm.Mov(Mem.Word(rp, 6), Reg.AX);
    // pos = i + LEN(find)
    asm.Mov(Reg.AX, Mem.Word(rp, 12));
    asm.Add(Reg.AX, Mem.Word(rp, 10));
    asm.Mov(Mem.Word(rp, 8), Reg.AX);
    asm.Jmp(loop);

    asm.MarkLabel(tail);                              // result += MID$(subject, pos)
    asm.Mov(Reg.AX, Mem.Word(rp, 0));
    asm.Mov(Reg.CX, Mem.Word(rp, 8));
    asm.Mov(Reg.DX, 0x7FFF);
    asm.Call(this.StrMid);                            // consumes the subject
    asm.Mov(Reg.DX, Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(rp, 6));
    asm.Call(this.StrCat);
    asm.Push(Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(rp, 2));
    asm.Call(this.StrFree);
    asm.Mov(Reg.AX, Mem.Word(rp, 4));
    asm.Call(this.StrFree);
    asm.Pop(Reg.AX);
    asm.Jmp(asm.Lbl("rt_replace_out"));

    asm.MarkLabel(unchanged);                         // empty find: subject unchanged
    asm.Mov(Reg.AX, Mem.Word(rp, 2));
    asm.Call(this.StrFree);
    asm.Mov(Reg.AX, Mem.Word(rp, 4));
    asm.Call(this.StrFree);
    asm.Mov(Reg.AX, Mem.Word(rp, 0));
    asm.MarkLabel("rt_replace_out");
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Ret();
  }

  /// <summary>
  /// LSET/RSET into a dynamic string: AX=target handle (mutated in place, not
  /// consumed), DX=value handle (consumed), BL=0 left / 1 right justified.
  /// </summary>
  private void EmitJustify(Assembler asm) {
    this.Justify = asm.MarkLabel("rt_justify");
    var copyLeft = asm.DefineLabel();
    var copy = asm.DefineLabel();
    var done = asm.DefineLabel();

    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));
    asm.Mov(Mem.Byte(asm.Lbl("rt_capmode"), 1), Reg.BL);     // justify flag
    EmitLoadDescriptor(asm, Reg.AX, "rt_cmp_loff", "rt_cmp_llen"); // target
    asm.Mov(Reg.AX, Reg.DX);
    EmitLoadDescriptor(asm, Reg.AX, "rt_cmp_roff", "rt_cmp_rlen"); // value

    // blank the target field
    asm.Mov(Reg.DI, Mem.Word(asm.Lbl("rt_cmp_loff")));
    asm.Mov(Reg.CX, Mem.Word(asm.Lbl("rt_cmp_llen")));
    asm.Mov(Reg.AL, (Imm)' ');
    asm.Rep();
    asm.Stosb();

    // n = min(target length, value length)
    asm.Mov(Reg.CX, Mem.Word(asm.Lbl("rt_cmp_rlen")));
    asm.Cmp(Reg.CX, Mem.Word(asm.Lbl("rt_cmp_llen")));
    asm.Jbe(asm.Lbl("rt_just_nok"));
    asm.Mov(Reg.CX, Mem.Word(asm.Lbl("rt_cmp_llen")));
    asm.MarkLabel("rt_just_nok");
    asm.Jcxz(done);

    asm.Mov(Reg.SI, Mem.Word(asm.Lbl("rt_cmp_roff")));
    asm.Mov(Reg.DI, Mem.Word(asm.Lbl("rt_cmp_loff")));
    asm.Cmp(Mem.Byte(asm.Lbl("rt_capmode"), 1), (Imm)0);
    asm.Je(copyLeft);
    asm.Add(Reg.DI, Mem.Word(asm.Lbl("rt_cmp_llen")));       // right-justify
    asm.Sub(Reg.DI, Reg.CX);
    asm.MarkLabel(copyLeft);
    asm.MarkLabel(copy);
    asm.Mov(Reg.AL, Mem.Byte(Reg.SI).Es());
    asm.Mov(Mem.Byte(Reg.DI).Es(), Reg.AL);
    asm.Inc(Reg.SI);
    asm.Inc(Reg.DI);
    asm.Loop(copy);

    asm.MarkLabel(done);
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Mov(Reg.AX, Reg.DX);                                 // free the value handle
    asm.Call(this.StrFree);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Ret();
  }

  /// <summary>
  /// SHELL: runs "%COMSPEC% /C command" via DOS EXEC (AX = command handle,
  /// consumed). The MZ header releases unused memory, so EXEC has room.
  /// </summary>
  private void EmitShell(Assembler asm) {
    this.Shell = asm.MarkLabel("rt_shell");
    var run = asm.DefineLabel();
    var fail = asm.DefineLabel();

    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);
    asm.Push(Reg.BP);

    // command tail: " /C " + command + CR, count byte first
    asm.Push(Reg.AX);
    asm.Mov(Reg.DI, Imm.OffsetOf(asm.Lbl("rt_shellbuf"), 1));
    asm.Mov(Mem.Byte(Reg.DI), (Imm)' ');
    asm.Mov(Mem.Byte(Reg.DI, 1), (Imm)'/');
    asm.Mov(Mem.Byte(Reg.DI, 2), (Imm)'C');
    asm.Mov(Mem.Byte(Reg.DI, 3), (Imm)' ');
    asm.Add(Reg.DI, 4);
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));
    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.SI, Mem.Word(Reg.BX, asm.Lbl("rt_strtab")));
    asm.Mov(Reg.CX, Mem.Word(Reg.BX, asm.Lbl("rt_strtab"), 2));
    asm.Cmp(Reg.CX, 120);
    asm.Jbe(asm.Lbl("rt_shell_lenok"));
    asm.Mov(Reg.CX, 120);
    asm.MarkLabel("rt_shell_lenok");
    asm.Mov(Reg.DX, Reg.CX);
    asm.Jcxz(asm.Lbl("rt_shell_copied"));
    asm.MarkLabel("rt_shell_copy");
    asm.Mov(Reg.AL, Mem.Byte(Reg.SI).Es());
    asm.Mov(Mem.Byte(Reg.DI), Reg.AL);
    asm.Inc(Reg.SI);
    asm.Inc(Reg.DI);
    asm.Loop(asm.Lbl("rt_shell_copy"));
    asm.MarkLabel("rt_shell_copied");
    asm.Mov(Mem.Byte(Reg.DI), (Imm)0x0D);
    asm.Add(Reg.DX, 4);
    asm.Mov(Reg.BX, Imm.OffsetOf(asm.Lbl("rt_shellbuf")));
    asm.Mov(Mem.Byte(Reg.BX), Reg.DL);                       // tail length
    asm.Pop(Reg.AX);
    asm.Call(this.StrFree);

    // COMSPEC -> ASCIIZ program path in rt_namebuf
    asm.Mov(Reg.SI, Imm.OffsetOf(asm.Lbl("rt_comspec")));
    asm.Mov(Reg.CX, 7);
    asm.Mov(Reg.DX, Reg.DS);
    asm.Call(this.StrMem);
    asm.Call(this.Environ);                                  // consumes; AX = value handle
    asm.Test(Reg.AX, Reg.AX);
    asm.Jz(fail);
    asm.Call(asm.Lbl("rt_name_z"));                          // -> rt_namebuf (consumes)

    asm.MarkLabel(run);
    // EXEC parameter block: inherit environment, our tail, two null FCBs
    asm.Mov(Mem.Word(asm.Lbl("rt_execpb")), (Imm)0);
    asm.Mov(Mem.Word(asm.Lbl("rt_execpb"), 2), Imm.OffsetOf(asm.Lbl("rt_shellbuf")));
    asm.Mov(Mem.Word(asm.Lbl("rt_execpb"), 4), Reg.DS);
    asm.Mov(Mem.Word(asm.Lbl("rt_execpb"), 6), Imm.OffsetOf(asm.Lbl("rt_fcb")));
    asm.Mov(Mem.Word(asm.Lbl("rt_execpb"), 8), Reg.DS);
    asm.Mov(Mem.Word(asm.Lbl("rt_execpb"), 10), Imm.OffsetOf(asm.Lbl("rt_fcb")));
    asm.Mov(Mem.Word(asm.Lbl("rt_execpb"), 12), Reg.DS);
    asm.Mov(Reg.AX, Reg.SS);
    asm.Mov(Mem.Word(asm.Lbl("rt_sssave")), Reg.AX);
    asm.Mov(Mem.Word(asm.Lbl("rt_spsave")), Reg.SP);

    asm.Mov(Reg.DX, Imm.OffsetOf(asm.Lbl("rt_namebuf")));
    asm.Push(Reg.DS);
    asm.Pop(Reg.ES);
    asm.Mov(Reg.BX, Imm.OffsetOf(asm.Lbl("rt_execpb")));
    asm.Mov(Reg.AX, 0x4B00);
    asm.Int(0x21);

    // DOS trashed everything except CS:IP - rebuild segments (CS=DS=SS model)
    asm.Mov(Reg.AX, Reg.CS);
    asm.Mov(Reg.DS, Reg.AX);
    asm.Mov(Reg.ES, Reg.AX);
    asm.Cli();
    asm.Mov(Reg.SS, Reg.AX);
    asm.Mov(Reg.SP, Mem.Word(asm.Lbl("rt_spsave")));
    asm.Sti();

    asm.MarkLabel(fail);
    asm.Pop(Reg.BP);
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Ret();
  }

  public Label Rename { get; private set; } = null!;

  /// <summary>NAME old$ AS new$: DOS rename (consumes both handles; failure raises ERR 57).</summary>
  private void EmitRename(Assembler asm) {
    this.Rename = asm.MarkLabel("rt_rename");
    var ok = asm.DefineLabel();
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);

    asm.Push(Reg.DX);                                  // new-name handle
    asm.Call(asm.Lbl("rt_name_z"));                    // old -> rt_namebuf
    asm.Mov(Reg.SI, Imm.OffsetOf(asm.Lbl("rt_namebuf")));
    asm.Mov(Reg.DI, Imm.OffsetOf(asm.Lbl("rt_dirspec")));
    asm.Push(Reg.DS);
    asm.Pop(Reg.ES);
    asm.Mov(Reg.CX, 80);
    asm.Rep();
    asm.Movsb();
    asm.Pop(Reg.AX);                                   // new-name handle
    asm.Call(asm.Lbl("rt_name_z"));                    // new -> rt_namebuf

    asm.Mov(Reg.DX, Imm.OffsetOf(asm.Lbl("rt_dirspec")));   // DS:DX = old
    asm.Mov(Reg.DI, Imm.OffsetOf(asm.Lbl("rt_namebuf")));   // ES:DI = new
    asm.Mov(Reg.AH, 0x56);
    asm.Int(0x21);
    asm.Jnc(ok);
    asm.Mov(Reg.AX, 57);
    asm.Call(asm.Lbl("rt_raise"));
    asm.MarkLabel(ok);
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Ret();
  }

  public Label PokeStr { get; private set; } = null!;

  /// <summary>POKE$ support: copies the string's bytes to DEF SEG:DI (consumes the handle).</summary>
  private void EmitPokeStr(Assembler asm) {
    this.PokeStr = asm.MarkLabel("rt_pokestr");
    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);
    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.SI, Mem.Word(Reg.BX, asm.Lbl("rt_strtab")));
    asm.Mov(Reg.CX, Mem.Word(Reg.BX, asm.Lbl("rt_strtab"), 2));
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_defseg")));
    asm.Mov(Reg.DX, Mem.Word(asm.Lbl("rt_strseg")));
    asm.Push(Reg.DS);
    asm.Mov(Reg.DS, Reg.DX);
    asm.Rep();
    asm.Movsb();
    asm.Pop(Reg.DS);
    asm.Call(this.StrFree);              // AX still holds the handle
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Ret();
  }

  /// <summary>CURDIR$: "X:\path" of the current directory (no arguments -> default drive).</summary>
  private void EmitCurDir(Assembler asm) {
    this.CurDir = asm.MarkLabel("rt_curdir");
    var fail = asm.DefineLabel();
    var lenLoop = asm.DefineLabel();
    var lenDone = asm.DefineLabel();
    var output = asm.DefineLabel();

    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);

    asm.Mov(Reg.AH, 0x19);                    // current drive (0 = A)
    asm.Int(0x21);
    asm.Add(Reg.AL, (Imm)'A');
    asm.Mov(Reg.SI, Imm.OffsetOf(asm.Lbl("rt_dirspec")));
    asm.Mov(Mem.Byte(Reg.SI), Reg.AL);
    asm.Mov(Mem.Byte(Reg.SI, 1), (Imm)':');
    asm.Mov(Mem.Byte(Reg.SI, 2), (Imm)'\\');
    asm.Add(Reg.SI, 3);
    asm.Xor(Reg.DL, Reg.DL);                  // default drive
    asm.Mov(Reg.AH, 0x47);                    // get current directory -> ASCIIZ at DS:SI
    asm.Int(0x21);
    asm.Jc(fail);

    asm.Mov(Reg.SI, Imm.OffsetOf(asm.Lbl("rt_dirspec")));
    asm.Xor(Reg.CX, Reg.CX);
    asm.MarkLabel(lenLoop);
    asm.Cmp(Reg.CX, 67);
    asm.Jae(lenDone);
    asm.Mov(Reg.BX, Reg.SI);
    asm.Add(Reg.BX, Reg.CX);
    asm.Cmp(Mem.Byte(Reg.BX), (Imm)0);
    asm.Je(lenDone);
    asm.Inc(Reg.CX);
    asm.Jmp(lenLoop);
    asm.MarkLabel(lenDone);
    asm.Mov(Reg.DX, Reg.DS);
    asm.Call(this.StrMem);
    asm.Jmp(output);

    asm.MarkLabel(fail);
    asm.Xor(Reg.AX, Reg.AX);
    asm.MarkLabel(output);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Ret();
  }

  /// <summary>Loads descriptor (offset, length) of the handle in <paramref name="handle"/> into the two word cells.</summary>
  private static void EmitLoadDescriptor(Assembler asm, Reg handle, string offCell, string lenCell) {
    asm.Mov(Reg.BX, handle);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.SI, Mem.Word(Reg.BX, asm.Lbl("rt_strtab")));
    asm.Mov(Mem.Word(asm.Lbl(offCell)), Reg.SI);
    asm.Mov(Reg.SI, Mem.Word(Reg.BX, asm.Lbl("rt_strtab"), 2));
    asm.Mov(Mem.Word(asm.Lbl(lenCell)), Reg.SI);
  }

  /// <summary>INSTR ANY / VERIFY core: scan for the first member / non-member of a set.</summary>
  private void EmitScanSet(Assembler asm) {
    this.ScanSet = asm.MarkLabel("rt_scanset");
    var none = asm.DefineLabel();
    var output = asm.DefineLabel();
    var probe = asm.DefineLabel();
    var setLoop = asm.DefineLabel();
    var inSet = asm.DefineLabel();
    var notInSet = asm.DefineLabel();
    var advance = asm.DefineLabel();
    var clamped = asm.DefineLabel();

    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);
    asm.Mov(Mem.Word(asm.Lbl("rt_st0")), Reg.AX);   // haystack handle (freed at exit)
    asm.Mov(Mem.Word(asm.Lbl("rt_st1")), Reg.DX);   // set handle
    asm.Mov(Mem.Byte(asm.Lbl("rt_capmode"), 1), Reg.BL); // flag parked in the spare capture byte
    asm.Cmp(Reg.CX, 1);
    asm.Jge(clamped);
    asm.Mov(Reg.CX, 1);
    asm.MarkLabel(clamped);
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));
    EmitLoadDescriptor(asm, Reg.AX, "rt_cmp_loff", "rt_cmp_llen");
    asm.Mov(Reg.AX, Reg.DX);
    EmitLoadDescriptor(asm, Reg.AX, "rt_cmp_roff", "rt_cmp_rlen");

    asm.Dec(Reg.CX);                                 // CX = 0-based position
    asm.MarkLabel(probe);
    asm.Cmp(Reg.CX, Mem.Word(asm.Lbl("rt_cmp_llen")));
    asm.Jge(none);
    asm.Mov(Reg.SI, Mem.Word(asm.Lbl("rt_cmp_loff")));
    asm.Add(Reg.SI, Reg.CX);
    asm.Mov(Reg.AL, Mem.Byte(Reg.SI).Es());          // haystack char
    // scan the set
    asm.Mov(Reg.DI, Mem.Word(asm.Lbl("rt_cmp_roff")));
    asm.Mov(Reg.DX, Mem.Word(asm.Lbl("rt_cmp_rlen")));
    asm.Test(Reg.DX, Reg.DX);
    asm.Jz(notInSet);
    asm.MarkLabel(setLoop);
    asm.Cmp(Mem.Byte(Reg.DI).Es(), Reg.AL);
    asm.Je(inSet);
    asm.Inc(Reg.DI);
    asm.Dec(Reg.DX);
    asm.Jnz(setLoop);
    asm.MarkLabel(notInSet);
    asm.Cmp(Mem.Byte(asm.Lbl("rt_capmode"), 1), (Imm)0);
    asm.Jne(output);                                  // VERIFY: non-member found
    asm.Jmp(advance);
    asm.MarkLabel(inSet);
    asm.Cmp(Mem.Byte(asm.Lbl("rt_capmode"), 1), (Imm)0);
    asm.Je(output);                                   // INSTR ANY: member found
    asm.MarkLabel(advance);
    asm.Inc(Reg.CX);
    asm.Jmp(probe);

    asm.MarkLabel(output);
    asm.Inc(Reg.CX);                                  // back to 1-based
    asm.Mov(Reg.AX, Reg.CX);
    asm.Jmp(asm.Lbl("rt_scanset_done"));
    asm.MarkLabel(none);
    asm.Xor(Reg.AX, Reg.AX);
    asm.MarkLabel("rt_scanset_done");
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

  /// <summary>
  /// Range/collate-aware string compare for ARRAY SORT/SCAN. Handles in AX/DX
  /// are NOT consumed; options read from rt_arpb. Result in AX: -1/0/1.
  /// </summary>
  private void EmitStrCmpRange(Assembler asm) {
    asm.MarkLabel("rt_strcmprange");
    var noCollate = asm.DefineLabel();
    var rangeDone = asm.DefineLabel();
    var compare = asm.DefineLabel();
    var byteLoop = asm.DefineLabel();
    var noMapA = asm.DefineLabel();
    var noMapB = asm.DefineLabel();
    var tail = asm.DefineLabel();
    var less = asm.DefineLabel();
    var greater = asm.DefineLabel();
    var done = asm.DefineLabel();

    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);

    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));
    EmitLoadDescriptor(asm, Reg.AX, "rt_cmp_loff", "rt_cmp_llen");
    asm.Mov(Reg.AX, Reg.DX);
    EmitLoadDescriptor(asm, Reg.AX, "rt_cmp_roff", "rt_cmp_rlen");

    // collate table data offset (0 = identity)
    asm.Mov(Mem.Word(asm.Lbl("rt_cmp_col")), (Imm)0);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_arpb"), 6));
    asm.Test(Reg.AX, Reg.AX);
    asm.Jz(noCollate);
    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.AX, Mem.Word(Reg.BX, asm.Lbl("rt_strtab")));
    asm.Mov(Mem.Word(asm.Lbl("rt_cmp_col")), Reg.AX);
    asm.MarkLabel(noCollate);

    // FROM/TO range clamps the left side; the right side too unless flag bit1
    this.EmitRangeClamp(asm, "rt_cmp_loff", "rt_cmp_llen");
    asm.Test(Mem.Byte(asm.Lbl("rt_arpb"), 12), (Imm)2);
    asm.Jnz(rangeDone);
    this.EmitRangeClamp(asm, "rt_cmp_roff", "rt_cmp_rlen");
    asm.MarkLabel(rangeDone);

    asm.MarkLabel(compare);
    asm.Mov(Reg.SI, Mem.Word(asm.Lbl("rt_cmp_loff")));
    asm.Mov(Reg.DI, Mem.Word(asm.Lbl("rt_cmp_roff")));
    asm.Mov(Reg.CX, Mem.Word(asm.Lbl("rt_cmp_llen")));
    asm.Cmp(Reg.CX, Mem.Word(asm.Lbl("rt_cmp_rlen")));
    asm.Jle(byteLoop);
    asm.Mov(Reg.CX, Mem.Word(asm.Lbl("rt_cmp_rlen")));
    asm.MarkLabel(byteLoop);
    asm.Jcxz(tail);
    asm.Mov(Reg.AL, Mem.Byte(Reg.SI).Es());
    asm.Mov(Reg.DL, Mem.Byte(Reg.DI).Es());
    // collate-map both bytes
    asm.Cmp(Mem.Word(asm.Lbl("rt_cmp_col")), (Imm)0);
    asm.Je(noMapB);
    asm.Xor(Reg.AH, Reg.AH);
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_cmp_col")));
    asm.Add(Reg.BX, Reg.AX);
    asm.Mov(Reg.AL, Mem.Byte(Reg.BX).Es());
    asm.MarkLabel(noMapA);
    asm.Xor(Reg.DH, Reg.DH);
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_cmp_col")));
    asm.Add(Reg.BX, Reg.DX);
    asm.Mov(Reg.DL, Mem.Byte(Reg.BX).Es());
    asm.MarkLabel(noMapB);
    asm.Cmp(Reg.AL, Reg.DL);
    asm.Jb(less);
    asm.Ja(greater);
    asm.Inc(Reg.SI);
    asm.Inc(Reg.DI);
    asm.Dec(Reg.CX);
    asm.Jmp(byteLoop);

    asm.MarkLabel(tail); // common prefix equal: shorter sorts first
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_cmp_llen")));
    asm.Cmp(Reg.AX, Mem.Word(asm.Lbl("rt_cmp_rlen")));
    asm.Jl(less);
    asm.Jg(greater);
    asm.Xor(Reg.AX, Reg.AX);
    asm.Jmp(done);
    asm.MarkLabel(less);
    asm.Mov(Reg.AX, -1);
    asm.Jmp(done);
    asm.MarkLabel(greater);
    asm.Mov(Reg.AX, 1);
    asm.MarkLabel(done);
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Ret();
  }

  /// <summary>Applies the rt_arpb FROM/TO character range to an (offset, length) cell pair.</summary>
  private void EmitRangeClamp(Assembler asm, string offCell, string lenCell) {
    var skipOk = asm.DefineLabel();
    var lenOk = asm.DefineLabel();
    var widthOk = asm.DefineLabel();

    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_arpb"), 8));   // from (1-based)
    asm.Dec(Reg.AX);
    asm.Jns(skipOk);
    asm.Xor(Reg.AX, Reg.AX);
    asm.MarkLabel(skipOk);                              // AX = chars to skip
    asm.Cmp(Reg.AX, Mem.Word(asm.Lbl(lenCell)));
    asm.Jle(lenOk);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl(lenCell)));
    asm.MarkLabel(lenOk);
    asm.Add(Mem.Word(asm.Lbl(offCell)), Reg.AX);
    asm.Sub(Mem.Word(asm.Lbl(lenCell)), Reg.AX);

    // cap remaining length at to - from + 1
    asm.Mov(Reg.CX, Mem.Word(asm.Lbl("rt_arpb"), 10));
    asm.Sub(Reg.CX, Mem.Word(asm.Lbl("rt_arpb"), 8));
    asm.Inc(Reg.CX);
    asm.Jns(asm.Lbl($"rt_rc_pos_{lenCell}"));
    asm.Xor(Reg.CX, Reg.CX);
    asm.MarkLabel($"rt_rc_pos_{lenCell}");
    asm.Cmp(Mem.Word(asm.Lbl(lenCell)), Reg.CX);
    asm.Jle(widthOk);
    asm.Mov(Mem.Word(asm.Lbl(lenCell)), Reg.CX);
    asm.MarkLabel(widthOk);
  }

  /// <summary>EXTRACT$: chars of the main string before the first match (whole string when none).</summary>
  private void EmitExtract(Assembler asm) {
    this.Extract = asm.MarkLabel("rt_extract");
    var anySearch = asm.DefineLabel();
    var got = asm.DefineLabel();
    var cut = asm.DefineLabel();

    asm.Mov(Mem.Word(asm.Lbl("rt_ext0")), Reg.AX);   // main
    asm.Mov(Mem.Word(asm.Lbl("rt_ext1")), Reg.DX);   // match
    asm.Mov(Mem.Word(asm.Lbl("rt_ext2")), Reg.BX);   // flag in BL
    asm.Call(this.StrDup);                            // dup main for the search
    asm.Push(Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_ext1")));
    asm.Call(this.StrDup);
    asm.Mov(Reg.DX, Reg.AX);                          // dup match
    asm.Pop(Reg.AX);
    asm.Mov(Reg.CX, 1);
    asm.Test(Mem.Byte(asm.Lbl("rt_ext2")), (Imm)1);
    asm.Jnz(anySearch);
    asm.Call(this.Instr);
    asm.Jmp(got);
    asm.MarkLabel(anySearch);
    asm.Push(Reg.BX);
    asm.Xor(Reg.BL, Reg.BL);
    asm.Call(this.ScanSet);
    asm.Pop(Reg.BX);
    asm.MarkLabel(got);
    asm.Test(Reg.AX, Reg.AX);
    asm.Jnz(cut);
    // no match: result is the main string itself; free the match
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_ext1")));
    asm.Call(this.StrFree);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_ext0")));
    asm.Ret();
    asm.MarkLabel(cut);
    asm.Push(Reg.CX);
    asm.Mov(Reg.CX, Reg.AX);
    asm.Dec(Reg.CX);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_ext0")));
    asm.Call(this.StrLeft);                           // consumes main
    asm.Pop(Reg.CX);
    asm.Push(Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_ext1")));
    asm.Call(this.StrFree);
    asm.Pop(Reg.AX);
    asm.Ret();
  }

  /// <summary>TALLY: occurrence count of a substring (BL=0) or of set members (BL=1).</summary>
  private void EmitTally(Assembler asm) {
    this.Tally = asm.MarkLabel("rt_tally");
    var loop = asm.DefineLabel();
    var done = asm.DefineLabel();
    var anyMode = asm.DefineLabel();

    // rt_ext0 = main, rt_ext1 = match, rt_ext2 = count, CX tracks the position
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Mov(Mem.Word(asm.Lbl("rt_ext0")), Reg.AX);
    asm.Mov(Mem.Word(asm.Lbl("rt_ext1")), Reg.DX);
    asm.Mov(Mem.Word(asm.Lbl("rt_ext2")), (Imm)0);
    asm.Push(Reg.BX);                                  // BL = mode flag

    // needle length (for the advance step; 1 in any-mode)
    asm.Mov(Reg.BX, Reg.DX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.DX, Mem.Word(Reg.BX, asm.Lbl("rt_strtab"), 2));
    asm.Pop(Reg.BX);
    asm.Test(Reg.BL, (Imm)1);
    asm.Jz(anyMode);
    asm.Mov(Reg.DX, 1);
    asm.MarkLabel(anyMode);
    asm.Test(Reg.DX, Reg.DX);
    asm.Jz(done);                                      // empty needle: count 0
    asm.Push(Reg.DX);                                  // advance step

    asm.Mov(Reg.CX, 1);                                // search position
    asm.MarkLabel(loop);
    // search from CX: dup both handles for the probe call
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_ext0")));
    asm.Call(this.StrDup);
    asm.Push(Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_ext1")));
    asm.Call(this.StrDup);
    asm.Mov(Reg.DX, Reg.AX);
    asm.Pop(Reg.AX);
    asm.Push(Reg.BX);
    asm.Test(Reg.BL, (Imm)1);
    asm.Jnz(asm.Lbl("rt_tally_any"));
    asm.Call(this.Instr);
    asm.Jmp(asm.Lbl("rt_tally_got"));
    asm.MarkLabel("rt_tally_any");
    asm.Xor(Reg.BL, Reg.BL);
    asm.Call(this.ScanSet);
    asm.MarkLabel("rt_tally_got");
    asm.Pop(Reg.BX);
    asm.Test(Reg.AX, Reg.AX);
    asm.Jz(asm.Lbl("rt_tally_end"));
    asm.Inc(Mem.Word(asm.Lbl("rt_ext2")));
    asm.Mov(Reg.CX, Reg.AX);
    asm.Pop(Reg.DX);                                   // advance step
    asm.Push(Reg.DX);
    asm.Add(Reg.CX, Reg.DX);
    asm.Jmp(loop);

    asm.MarkLabel("rt_tally_end");
    asm.Pop(Reg.DX);                                   // drop advance step
    asm.MarkLabel(done);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_ext0")));
    asm.Call(this.StrFree);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_ext1")));
    asm.Call(this.StrFree);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_ext2")));
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Ret();
  }

  /// <summary>DIR$: DOS FindFirst (mask given) / FindNext (mask handle 0) through a private DTA.</summary>
  private void EmitDir(Assembler asm) {
    this.Dir = asm.MarkLabel("rt_dir");
    var next = asm.DefineLabel();
    var none = asm.DefineLabel();
    var found = asm.DefineLabel();
    var copy = asm.DefineLabel();
    var copyDone = asm.DefineLabel();
    var lenLoop = asm.DefineLabel();
    var lenDone = asm.DefineLabel();
    var output = asm.DefineLabel();

    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);

    asm.Push(Reg.AX);
    asm.Mov(Reg.DX, Imm.OffsetOf(asm.Lbl("rt_dta")));
    asm.Mov(Reg.AH, 0x1A);                            // set DTA
    asm.Int(0x21);
    asm.Pop(Reg.AX);

    asm.Test(Reg.AX, Reg.AX);
    asm.Jz(next);

    // copy the mask into rt_dirspec as ASCIIZ
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));
    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.SI, Mem.Word(Reg.BX, asm.Lbl("rt_strtab")));
    asm.Mov(Reg.DX, Mem.Word(Reg.BX, asm.Lbl("rt_strtab"), 2));
    asm.Cmp(Reg.DX, 78);
    asm.Jbe(copy);
    asm.Mov(Reg.DX, 78);
    asm.MarkLabel(copy);
    asm.Push(Reg.AX);                                 // mask handle - freed after the copy
    asm.Mov(Reg.DI, Imm.OffsetOf(asm.Lbl("rt_dirspec")));
    asm.Push(Reg.CX);
    asm.Mov(Reg.CX, Reg.DX);
    asm.Jcxz(copyDone);
    asm.MarkLabel("rt_dir_copy");
    asm.Mov(Reg.AL, Mem.Byte(Reg.SI).Es());
    asm.Mov(Mem.Byte(Reg.DI), Reg.AL);
    asm.Inc(Reg.SI);
    asm.Inc(Reg.DI);
    asm.Loop(asm.Lbl("rt_dir_copy"));
    asm.MarkLabel(copyDone);
    asm.Mov(Mem.Byte(Reg.DI), (Imm)0);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.AX);
    asm.Call(this.StrFree);

    asm.Mov(Reg.DX, Imm.OffsetOf(asm.Lbl("rt_dirspec")));
    asm.Mov(Reg.AH, 0x4E);                            // find first, CX = attribute
    asm.Int(0x21);
    asm.Jc(none);
    asm.Jmp(found);

    asm.MarkLabel(next);
    asm.Mov(Reg.AH, 0x4F);                            // find next
    asm.Int(0x21);
    asm.Jc(none);

    asm.MarkLabel(found);
    // name = ASCIIZ at rt_dta+30, up to 13 bytes
    asm.Mov(Reg.SI, Imm.OffsetOf(asm.Lbl("rt_dta"), 30));
    asm.Xor(Reg.CX, Reg.CX);
    asm.MarkLabel(lenLoop);
    asm.Cmp(Reg.CX, 13);
    asm.Jae(lenDone);
    asm.Mov(Reg.BX, Reg.SI);
    asm.Add(Reg.BX, Reg.CX);
    asm.Cmp(Mem.Byte(Reg.BX), (Imm)0);
    asm.Je(lenDone);
    asm.Inc(Reg.CX);
    asm.Jmp(lenLoop);
    asm.MarkLabel(lenDone);
    asm.Mov(Reg.DX, Reg.DS);
    asm.Call(this.StrMem);                            // AX = name handle
    asm.Jmp(output);

    asm.MarkLabel(none);
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

  /// <summary>
  /// ARRAY SORT (insertion sort over string handles) and ARRAY SCAN. All
  /// parameters live in rt_arpb; ScanStr returns the 1-based relative position
  /// in AX (0 = no match).
  /// </summary>
  private void EmitSortScan(Assembler asm) {
    // shared prologue: base offset of the start element + data segment into rt_arpb+16/18
    asm.MarkLabel("rt_ss_setup");
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_arpb")));    // descriptor ptr
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_arpb"), 2)); // start index
    asm.Sub(Reg.AX, Mem.Word(Reg.BX, 8));             // - lower bound
    asm.Shl(Reg.AX, 1);                               // * 2 (string handle cells)
    asm.Add(Reg.AX, Mem.Word(Reg.BX, 2));             // + data offset
    asm.Mov(Mem.Word(asm.Lbl("rt_arpb"), 16), Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(Reg.BX));                // data segment
    asm.Mov(Mem.Word(asm.Lbl("rt_arpb"), 18), Reg.AX);
    asm.Ret();

    this.SortStr = asm.MarkLabel("rt_sortstr");
    var sortDone = asm.DefineLabel();
    var outer = asm.DefineLabel();
    var inner = asm.DefineLabel();
    var nextI = asm.DefineLabel();
    var doSwap = asm.DefineLabel();

    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);
    asm.Call(asm.Lbl("rt_ss_setup"));
    asm.Mov(Reg.CX, Mem.Word(asm.Lbl("rt_arpb"), 4)); // count
    asm.Cmp(Reg.CX, 2);
    asm.Jl(sortDone);

    asm.Mov(Reg.SI, 1);                               // i
    asm.MarkLabel(outer);
    asm.Cmp(Reg.SI, Reg.CX);
    asm.Jae(sortDone);
    asm.Mov(Reg.DI, Reg.SI);                          // j
    asm.MarkLabel(inner);
    asm.Test(Reg.DI, Reg.DI);
    asm.Jz(nextI);
    // a = elem[j-1], b = elem[j]
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_arpb"), 18));
    asm.Mov(Reg.BX, Reg.DI);
    asm.Dec(Reg.BX);
    asm.Shl(Reg.BX, 1);
    asm.Add(Reg.BX, Mem.Word(asm.Lbl("rt_arpb"), 16));
    asm.Mov(Reg.AX, Mem.Word(Reg.BX).Es());
    asm.Mov(Reg.DX, Mem.Word(Reg.BX, 2).Es());
    asm.Call(asm.Lbl("rt_strcmprange"));              // AX = cmp(a, b)
    asm.Test(Mem.Byte(asm.Lbl("rt_arpb"), 12), (Imm)1);
    asm.Jnz(asm.Lbl("rt_sortstr_desc"));
    asm.Cmp(Reg.AX, 1);                               // ascending: in order when a <= b
    asm.Je(doSwap);
    asm.Jmp(nextI);
    asm.MarkLabel("rt_sortstr_desc");
    asm.Cmp(Reg.AX, -1);                              // descending: in order when a >= b
    asm.Je(doSwap);
    asm.Jmp(nextI);
    asm.MarkLabel(doSwap);
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_arpb"), 18));
    asm.Mov(Reg.BX, Reg.DI);
    asm.Dec(Reg.BX);
    asm.Shl(Reg.BX, 1);
    asm.Add(Reg.BX, Mem.Word(asm.Lbl("rt_arpb"), 16));
    asm.Mov(Reg.AX, Mem.Word(Reg.BX).Es());
    asm.Mov(Reg.DX, Mem.Word(Reg.BX, 2).Es());
    asm.Mov(Mem.Word(Reg.BX).Es(), Reg.DX);
    asm.Mov(Mem.Word(Reg.BX, 2).Es(), Reg.AX);
    asm.Dec(Reg.DI);
    asm.Jmp(inner);
    asm.MarkLabel(nextI);
    asm.Inc(Reg.SI);
    asm.Jmp(outer);

    asm.MarkLabel(sortDone);
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Ret();

    this.ScanStr = asm.MarkLabel("rt_scanstr");
    var scanLoop = asm.DefineLabel();
    var scanFound = asm.DefineLabel();
    var scanNone = asm.DefineLabel();
    var scanNext = asm.DefineLabel();
    var scanOut = asm.DefineLabel();

    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);
    asm.Call(asm.Lbl("rt_ss_setup"));
    asm.Mov(Reg.CX, Mem.Word(asm.Lbl("rt_arpb"), 4)); // count
    asm.Xor(Reg.SI, Reg.SI);                          // 0-based index
    asm.MarkLabel(scanLoop);
    asm.Cmp(Reg.SI, Reg.CX);
    asm.Jae(scanNone);
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_arpb"), 18));
    asm.Mov(Reg.BX, Reg.SI);
    asm.Shl(Reg.BX, 1);
    asm.Add(Reg.BX, Mem.Word(asm.Lbl("rt_arpb"), 16));
    asm.Mov(Reg.AX, Mem.Word(Reg.BX).Es());           // element
    asm.Mov(Reg.DX, Mem.Word(asm.Lbl("rt_arpb"), 14)); // match
    asm.Call(asm.Lbl("rt_strcmprange"));
    // relop in the flags high byte: 0 = / 1 <> / 2 < / 3 <= / 4 > / 5 >=
    asm.Mov(Reg.DL, Mem.Byte(asm.Lbl("rt_arpb"), 13));
    asm.Cmp(Reg.DL, (Imm)0);
    asm.Jne(asm.Lbl("rt_scanstr_r1"));
    asm.Test(Reg.AX, Reg.AX);
    asm.Jz(scanFound);
    asm.Jmp(scanNext);
    asm.MarkLabel("rt_scanstr_r1");
    asm.Cmp(Reg.DL, (Imm)1);
    asm.Jne(asm.Lbl("rt_scanstr_r2"));
    asm.Test(Reg.AX, Reg.AX);
    asm.Jnz(scanFound);
    asm.Jmp(scanNext);
    asm.MarkLabel("rt_scanstr_r2");
    asm.Cmp(Reg.DL, (Imm)2);
    asm.Jne(asm.Lbl("rt_scanstr_r3"));
    asm.Test(Reg.AX, Reg.AX);
    asm.Js(scanFound);
    asm.Jmp(scanNext);
    asm.MarkLabel("rt_scanstr_r3");
    asm.Cmp(Reg.DL, (Imm)3);
    asm.Jne(asm.Lbl("rt_scanstr_r4"));
    asm.Cmp(Reg.AX, 1);
    asm.Jl(scanFound);
    asm.Jmp(scanNext);
    asm.MarkLabel("rt_scanstr_r4");
    asm.Cmp(Reg.DL, (Imm)4);
    asm.Jne(asm.Lbl("rt_scanstr_r5"));
    asm.Cmp(Reg.AX, 1);
    asm.Je(scanFound);
    asm.Jmp(scanNext);
    asm.MarkLabel("rt_scanstr_r5");
    asm.Test(Reg.AX, Reg.AX);
    asm.Jns(scanFound);
    asm.MarkLabel(scanNext);
    asm.Inc(Reg.SI);
    asm.Jmp(scanLoop);

    asm.MarkLabel(scanFound);
    asm.Mov(Reg.AX, Reg.SI);
    asm.Inc(Reg.AX);
    asm.Jmp(scanOut);
    asm.MarkLabel(scanNone);
    asm.Xor(Reg.AX, Reg.AX);
    asm.MarkLabel(scanOut);
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Ret();
  }
}
