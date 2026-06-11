using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

/// <summary>
/// CHAIN / RUN support. COMMON variables travel through the temp file
/// PBCHAIN.$$$ (written by CHAIN, read and deleted at startup of the next
/// image); the target program runs via DOS EXEC and the parent exits with the
/// child's code - matching CHAIN's no-return semantics.
///   ChainOpenWrite/ChainOpenRead: create/open the handoff file (read: AX=1 ok, 0 none)
///   ChainWrite/ChainRead: DS:DX buffer, CX bytes through the handoff handle
///   ChainWriteStr: AX = string handle (kept) - length word + data
///   ChainReadStr:  -> AX = freshly allocated handle
///   ChainCloseDelete(delete flag in AL: 1 = unlink after closing)
///   ChainExec: AX = target path handle (consumed; ".PBC" appended when the
///              name has no extension) - EXECs and exits with the child's code
/// </summary>
public sealed partial class DosRuntime {

  public Label ChainOpenWrite { get; private set; } = null!;
  public Label ChainOpenRead { get; private set; } = null!;
  public Label ChainWrite { get; private set; } = null!;
  public Label ChainRead { get; private set; } = null!;
  public Label ChainWriteStr { get; private set; } = null!;
  public Label ChainReadStr { get; private set; } = null!;
  public Label ChainClose { get; private set; } = null!;
  public Label ChainExec { get; private set; } = null!;

  private void EmitChainProcedures(Assembler asm) {
    var handle = asm.Lbl("rt_chfh");
    var name = asm.Lbl("rt_chname");

    this.ChainOpenWrite = asm.MarkLabel("rt_chopenw");
    {
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      asm.Mov(Reg.DX, Imm.OffsetOf(name));
      asm.Xor(Reg.CX, Reg.CX);
      asm.Mov(Reg.AH, 0x3C);                  // create/truncate
      asm.Int(0x21);
      asm.Mov(Mem.Word(handle), Reg.AX);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.CX);
      asm.Ret();
    }

    this.ChainOpenRead = asm.MarkLabel("rt_chopenr");
    {
      var missing = asm.DefineLabel();
      var output = asm.DefineLabel();
      asm.Push(Reg.DX);
      asm.Mov(Reg.DX, Imm.OffsetOf(name));
      asm.Mov(Reg.AX, 0x3D00);                // open read-only
      asm.Int(0x21);
      asm.Jc(missing);
      asm.Mov(Mem.Word(handle), Reg.AX);
      asm.Mov(Reg.AX, 1);
      asm.Jmp(output);
      asm.MarkLabel(missing);
      asm.Xor(Reg.AX, Reg.AX);
      asm.MarkLabel(output);
      asm.Pop(Reg.DX);
      asm.Ret();
    }

    this.ChainWrite = asm.MarkLabel("rt_chwrite");
    {
      asm.Push(Reg.AX);
      asm.Push(Reg.BX);
      asm.Mov(Reg.BX, Mem.Word(handle));
      asm.Mov(Reg.AH, 0x40);
      asm.Int(0x21);
      asm.Pop(Reg.BX);
      asm.Pop(Reg.AX);
      asm.Ret();
    }

    this.ChainRead = asm.MarkLabel("rt_chread");
    {
      asm.Push(Reg.AX);
      asm.Push(Reg.BX);
      asm.Mov(Reg.BX, Mem.Word(handle));
      asm.Mov(Reg.AH, 0x3F);
      asm.Int(0x21);
      asm.Pop(Reg.BX);
      asm.Pop(Reg.AX);
      asm.Ret();
    }

    this.ChainWriteStr = asm.MarkLabel("rt_chwstr");
    {
      asm.Push(Reg.AX);
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      asm.Push(Reg.SI);
      asm.Mov(Reg.BX, Reg.AX);
      asm.Shl(Reg.BX, 2);
      asm.Mov(Reg.AX, Mem.Word(Reg.BX, asm.Lbl("rt_strtab"), 2));  // length
      asm.Mov(Mem.Word(asm.Lbl("rt_st0")), Reg.AX);
      asm.Mov(Reg.DX, Imm.OffsetOf(asm.Lbl("rt_st0")));
      asm.Mov(Reg.CX, 2);
      asm.Call(this.ChainWrite);
      asm.Mov(Reg.CX, Mem.Word(asm.Lbl("rt_st0")));
      asm.Jcxz(asm.Lbl("rt_chwstr_done"));
      asm.Mov(Reg.DX, Mem.Word(Reg.BX, asm.Lbl("rt_strtab")));     // data offset
      asm.Push(Reg.DS);
      asm.Mov(Reg.SI, Mem.Word(asm.Lbl("rt_strseg")));
      asm.Push(Reg.BX);
      asm.Mov(Reg.BX, Mem.Word(handle));
      asm.Mov(Reg.DS, Reg.SI);
      asm.Mov(Reg.AH, 0x40);
      asm.Int(0x21);
      asm.Pop(Reg.BX);
      asm.Pop(Reg.DS);
      asm.MarkLabel("rt_chwstr_done");
      asm.Pop(Reg.SI);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.BX);
      asm.Pop(Reg.AX);
      asm.Ret();
    }

    this.ChainReadStr = asm.MarkLabel("rt_chrstr");
    {
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      asm.Push(Reg.SI);
      asm.Mov(Reg.DX, Imm.OffsetOf(asm.Lbl("rt_st0")));
      asm.Mov(Reg.CX, 2);
      asm.Call(this.ChainRead);
      asm.Mov(Reg.CX, Mem.Word(asm.Lbl("rt_st0")));
      asm.Call(this.StrAlloc);                                     // -> AX handle
      asm.Jcxz(asm.Lbl("rt_chrstr_done"));
      asm.Mov(Reg.BX, Reg.AX);
      asm.Shl(Reg.BX, 2);
      asm.Mov(Reg.DX, Mem.Word(Reg.BX, asm.Lbl("rt_strtab")));     // data offset
      asm.Push(Reg.DS);
      asm.Mov(Reg.SI, Mem.Word(asm.Lbl("rt_strseg")));
      asm.Mov(Reg.BX, Mem.Word(handle));
      asm.Mov(Reg.DS, Reg.SI);
      asm.Push(Reg.AX);
      asm.Mov(Reg.AH, 0x3F);
      asm.Int(0x21);
      asm.Pop(Reg.AX);
      asm.Pop(Reg.DS);
      asm.MarkLabel("rt_chrstr_done");
      asm.Pop(Reg.SI);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.BX);
      asm.Ret();
    }

    this.ChainClose = asm.MarkLabel("rt_chclose");
    {
      var keep = asm.DefineLabel();
      asm.Push(Reg.AX);
      asm.Push(Reg.BX);
      asm.Push(Reg.DX);
      asm.Push(Reg.CX);
      asm.Mov(Reg.CL, Reg.AL);                // delete flag
      asm.Mov(Reg.BX, Mem.Word(handle));
      asm.Mov(Reg.AH, 0x3E);
      asm.Int(0x21);
      asm.Test(Reg.CL, (Imm)1);
      asm.Jz(keep);
      asm.Mov(Reg.DX, Imm.OffsetOf(name));
      asm.Mov(Reg.AH, 0x41);                  // unlink
      asm.Int(0x21);
      asm.MarkLabel(keep);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.BX);
      asm.Pop(Reg.AX);
      asm.Ret();
    }

    this.ChainExec = asm.MarkLabel("rt_chainexec");
    {
      var scan = asm.DefineLabel();
      var hasExt = asm.DefineLabel();
      var run = asm.DefineLabel();
      asm.Call(asm.Lbl("rt_name_z"));         // path -> ASCIIZ in rt_namebuf (consumes)
      // append ".PBC" when the name has no extension
      asm.Mov(Reg.SI, Imm.OffsetOf(asm.Lbl("rt_namebuf")));
      asm.MarkLabel(scan);
      asm.Mov(Reg.AL, Mem.Byte(Reg.SI));
      asm.Test(Reg.AL, Reg.AL);
      asm.Jz(asm.Lbl("rt_chx_noext"));
      asm.Cmp(Reg.AL, (Imm)'.');
      asm.Je(hasExt);
      asm.Inc(Reg.SI);
      asm.Jmp(scan);
      asm.MarkLabel("rt_chx_noext");
      asm.Mov(Mem.Byte(Reg.SI), (Imm)'.');
      asm.Mov(Mem.Byte(Reg.SI, 1), (Imm)'P');
      asm.Mov(Mem.Byte(Reg.SI, 2), (Imm)'B');
      asm.Mov(Mem.Byte(Reg.SI, 3), (Imm)'C');
      asm.Mov(Mem.Byte(Reg.SI, 4), (Imm)0);
      asm.MarkLabel(hasExt);

      asm.MarkLabel(run);
      // empty command tail
      asm.Mov(Mem.Byte(asm.Lbl("rt_shellbuf")), (Imm)0);
      asm.Mov(Mem.Byte(asm.Lbl("rt_shellbuf"), 1), (Imm)0x0D);
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
      // rebuild segments, then leave with the child's exit code
      asm.Mov(Reg.AX, Reg.CS);
      asm.Mov(Reg.DS, Reg.AX);
      asm.Mov(Reg.ES, Reg.AX);
      asm.Cli();
      asm.Mov(Reg.SS, Reg.AX);
      asm.Mov(Reg.SP, Mem.Word(asm.Lbl("rt_spsave")));
      asm.Sti();
      asm.Mov(Reg.AH, 0x4D);                  // child's return code -> AL
      asm.Int(0x21);
      asm.Jmp(this.Exit);
    }
  }

  private void EmitChainData(Assembler asm) {
    asm.Align(2);
    asm.MarkLabel("rt_chfh");
    asm.Dw(0);
    asm.MarkLabel("rt_chname");
    asm.Db("PBCHAIN.$$$");
    asm.Db(0);
  }
}
