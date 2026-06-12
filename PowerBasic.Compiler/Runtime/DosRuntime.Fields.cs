using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

/// <summary>
/// FIELD support for RANDOM files. Field strings are ordinary heap strings of
/// the declared width; a small table associates them with their file so a
/// bare GET #n / PUT #n distributes/gathers the record bytes through them.
///   FieldAdd: AX = PB file number, CX = width, BX = address of the string
///             handle cell (assigns a fresh space-filled string of width CX)
///   FieldGet: AX = PB file number - reads one record, fills the field strings
///   FieldPut: AX = PB file number - gathers the field strings, writes a record
/// Records are capped at 512 bytes (rt_fieldbuf); 32 field entries total.
/// </summary>
public sealed partial class DosRuntime {

  public Label FieldAdd { get; private set; } = null!;
  public Label FieldGet { get; private set; } = null!;
  public Label FieldPut { get; private set; } = null!;

  private void EmitFieldProcedures(Assembler asm) {
    var count = asm.Lbl("rt_fldcnt");
    var files = asm.Lbl("rt_fldfile");
    var widths = asm.Lbl("rt_fldwidth");
    var cells = asm.Lbl("rt_fldcell");

    this.FieldAdd = asm.MarkLabel("rt_fldadd");
    {
      var full = asm.DefineLabel();
      asm.Push(Reg.AX);
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Push(Reg.SI);
      asm.Mov(Reg.SI, Mem.Word(count));
      asm.Cmp(Reg.SI, 32);
      asm.Jae(full);
      asm.Inc(Mem.Word(count));
      asm.Push(Reg.BX);
      asm.Mov(Reg.BX, Reg.SI);
      asm.Mov(Mem.Byte(Reg.BX, files), Reg.AL);    // file number (byte table)
      asm.Shl(Reg.BX, 1);
      asm.Mov(Mem.Word(Reg.BX, widths), Reg.CX);
      asm.Pop(Reg.AX);                             // the handle cell address
      asm.Mov(Mem.Word(Reg.BX, cells), Reg.AX);
      // assign a fresh space-filled string of the field width into the cell
      asm.Push(Reg.AX);
      asm.Mov(Reg.DL, (Imm)' ');
      asm.Call(this.StrFill);                      // CX = width -> AX = handle
      asm.Pop(Reg.BX);
      asm.Call(this.StrAssign);                    // [BX] <- AX (frees the old)
      asm.MarkLabel(full);
      asm.Pop(Reg.SI);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.BX);
      asm.Pop(Reg.AX);
      asm.Ret();
    }

    // shared: AX = file number -> CX = reclen (capped 512), BX = DOS handle
    asm.MarkLabel("rt_fld_setup");
    {
      var capOk = asm.DefineLabel();
      asm.Push(Reg.SI);
      asm.Mov(Reg.SI, Reg.AX);
      asm.Shl(Reg.SI, 1);
      asm.Mov(Reg.CX, Mem.Word(Reg.SI, asm.Lbl("rt_reclen")));
      asm.Cmp(Reg.CX, 512);
      asm.Jbe(capOk);
      asm.Mov(Reg.CX, 512);
      asm.MarkLabel(capOk);
      asm.Call(this.FHandle);                      // AX = file -> BX = DOS handle
      asm.Pop(Reg.SI);
      asm.Ret();
    }

    // copies the field strings from/to rt_fieldbuf in declaration order
    //   rt_st0 = file number, rt_st1 = buffer cursor, rt_st2 = entry index, BL flag: 0 = scatter (GET), 1 = gather (PUT)
    asm.MarkLabel("rt_fld_walk");
    {
      var loop = asm.DefineLabel();
      var skip = asm.DefineLabel();
      var done = asm.DefineLabel();
      var copy = asm.DefineLabel();
      asm.Mov(Mem.Word(asm.Lbl("rt_st1")), (Imm)0);
      asm.Mov(Mem.Word(asm.Lbl("rt_st2")), (Imm)0);
      var inRange = asm.DefineLabel();
      var matches = asm.DefineLabel();
      asm.MarkLabel(loop);
      asm.Mov(Reg.SI, Mem.Word(asm.Lbl("rt_st2")));
      asm.Cmp(Reg.SI, Mem.Word(count));
      asm.Jb(inRange);                             // the body exceeds Jcc's 8-bit range
      asm.Jmp(done);
      asm.MarkLabel(inRange);
      asm.Mov(Reg.BX, Reg.SI);
      asm.Mov(Reg.AL, Mem.Byte(Reg.BX, files));
      asm.Cmp(Reg.AL, Mem.Byte(asm.Lbl("rt_st0")));
      asm.Je(matches);
      asm.Jmp(skip);
      asm.MarkLabel(matches);
      asm.Shl(Reg.BX, 1);
      asm.Mov(Reg.CX, Mem.Word(Reg.BX, widths));   // width
      asm.Mov(Reg.DI, Mem.Word(Reg.BX, cells));
      asm.Mov(Reg.DI, Mem.Word(Reg.DI));           // string handle
      asm.Mov(Reg.BX, Reg.DI);
      asm.Shl(Reg.BX, 2);
      asm.Mov(Reg.DI, Mem.Word(Reg.BX, asm.Lbl("rt_strtab")));   // data offset in strseg
      asm.Mov(Reg.SI, Imm.OffsetOf(asm.Lbl("rt_fieldbuf")));
      asm.Add(Reg.SI, Mem.Word(asm.Lbl("rt_st1")));
      asm.Add(Mem.Word(asm.Lbl("rt_st1")), Reg.CX);
      asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));
      asm.Jcxz(skip);
      asm.MarkLabel(copy);
      asm.Test(Mem.Byte(asm.Lbl("rt_st3")), (Imm)1);
      asm.Jnz(asm.Lbl("rt_fld_gather"));
      asm.Mov(Reg.AL, Mem.Byte(Reg.SI));           // scatter: DS buffer -> ES string
      asm.Mov(Mem.Byte(Reg.DI).Es(), Reg.AL);
      asm.Jmp(asm.Lbl("rt_fld_step"));
      asm.MarkLabel("rt_fld_gather");
      asm.Mov(Reg.AL, Mem.Byte(Reg.DI).Es());      // gather: ES string -> DS buffer
      asm.Mov(Mem.Byte(Reg.SI), Reg.AL);
      asm.MarkLabel("rt_fld_step");
      asm.Inc(Reg.SI);
      asm.Inc(Reg.DI);
      asm.Loop(copy);
      asm.MarkLabel(skip);
      asm.Inc(Mem.Word(asm.Lbl("rt_st2")));
      asm.Jmp(loop);
      asm.MarkLabel(done);
      asm.Ret();
    }

    this.FieldGet = asm.MarkLabel("rt_fldget");
    {
      asm.Push(Reg.AX);
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      asm.Push(Reg.SI);
      asm.Push(Reg.DI);
      asm.Push(Reg.ES);
      asm.Mov(Mem.Word(asm.Lbl("rt_st0")), Reg.AX);
      asm.Call(asm.Lbl("rt_fld_setup"));           // CX = reclen, BX = DOS handle
      asm.Mov(Reg.DX, Imm.OffsetOf(asm.Lbl("rt_fieldbuf")));
      asm.Mov(Reg.SI, Reg.DS);
      asm.Call(this.FRead);
      asm.Mov(Mem.Word(asm.Lbl("rt_st3")), (Imm)0); // scatter
      asm.Call(asm.Lbl("rt_fld_walk"));
      asm.Pop(Reg.ES);
      asm.Pop(Reg.DI);
      asm.Pop(Reg.SI);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.BX);
      asm.Pop(Reg.AX);
      asm.Ret();
    }

    this.FieldPut = asm.MarkLabel("rt_fldput");
    {
      asm.Push(Reg.AX);
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      asm.Push(Reg.SI);
      asm.Push(Reg.DI);
      asm.Push(Reg.ES);
      asm.Mov(Mem.Word(asm.Lbl("rt_st0")), Reg.AX);
      asm.Mov(Mem.Word(asm.Lbl("rt_st3")), 1);     // gather
      asm.Call(asm.Lbl("rt_fld_walk"));
      asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_st0")));
      asm.Call(asm.Lbl("rt_fld_setup"));
      asm.Mov(Reg.DX, Imm.OffsetOf(asm.Lbl("rt_fieldbuf")));
      asm.Mov(Reg.SI, Reg.DS);
      asm.Call(this.FWrite);
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

  private void EmitFieldData(Assembler asm) {
    asm.Align(2);
    asm.MarkLabel("rt_fldcnt");
    asm.Dw(0);
    this.ZeroBlob(asm, "rt_fldfile", 32);
    this.ZeroBlob(asm, "rt_fldwidth", 64);
    this.ZeroBlob(asm, "rt_fldcell", 64);
    asm.MarkLabel("rt_fieldbuf");
    asm.Db(new byte[512]);
  }
}
