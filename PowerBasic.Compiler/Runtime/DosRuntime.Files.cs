using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

/// <summary>
/// DOS handle-based file I/O. PB file numbers 1..15 map through the word table
/// <c>rt_files</c> (0 = closed) to DOS handles. Register conventions:
///   FOpen:     AX=filename handle (consumed), BX=PB file number, CX=mode
///              (0=INPUT, 1=OUTPUT, 2=APPEND)
///   FClose:    AX=PB file number (no-op when closed)
///   FCloseAll: -
///   FSelect:   AX=PB file number - routes PrintStr/StrPrint to that file
///              (rt_curout; reset to 1 = stdout by the caller)
///   FreeFile:  -> AX=first unused PB file number
///   Eof:       AX=PB file number -> AX=-1/0
///   Kill:      AX=filename handle (consumed)
///   LInput:    AX=PB file number -> AX=string handle (one line, CR/LF stripped)
/// Failures raise the fatal I/O error (message + exit 3).
/// </summary>
public sealed partial class DosRuntime {

  public Label FOpen { get; private set; } = null!;
  public Label FClose { get; private set; } = null!;
  public Label FCloseAll { get; private set; } = null!;
  public Label FSelect { get; private set; } = null!;
  public Label FreeFile { get; private set; } = null!;
  public Label Eof { get; private set; } = null!;
  public Label Kill { get; private set; } = null!;
  public Label LInput { get; private set; } = null!;

  private void EmitFileProcedures(Assembler asm) {
    var files = asm.Lbl("rt_files");
    var ioError = asm.Lbl("rt_err_io");

    // rt_name_z: AX=string handle -> ASCIIZ filename in rt_namebuf (consumes)
    asm.MarkLabel("rt_name_z");
    {
      var copy = asm.DefineLabel();
      var terminate = asm.DefineLabel();
      asm.Push(Reg.AX);
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Push(Reg.SI);
      asm.Push(Reg.DI);
      asm.Push(Reg.ES);
      asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));
      asm.Mov(Reg.BX, Reg.AX);
      asm.Shl(Reg.BX, 2);
      asm.Mov(Reg.CX, this.Descriptor(Reg.BX, 2));
      asm.Mov(Reg.SI, this.Descriptor(Reg.BX));
      asm.Mov(Reg.DI, Imm.OffsetOf(asm.Lbl("rt_namebuf")));
      asm.Jcxz(terminate);
      asm.MarkLabel(copy);
      asm.Mov(Reg.AL, Mem.Byte(Reg.SI).Es());
      asm.Mov(Mem.Byte(Reg.DI), Reg.AL);
      asm.Inc(Reg.SI);
      asm.Inc(Reg.DI);
      asm.Loop(copy);
      asm.MarkLabel(terminate);
      asm.Mov(Mem.Byte(Reg.DI), (Imm)0);
      asm.Pop(Reg.ES);
      asm.Pop(Reg.DI);
      asm.Pop(Reg.SI);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.BX);
      asm.Pop(Reg.AX);
      asm.Call(this.StrFree);
      asm.Ret();
    }

    this.FOpen = asm.MarkLabel("rt_fopen");
    {
      var notInput = asm.DefineLabel();
      var append = asm.DefineLabel();
      var store = asm.DefineLabel();
      var done = asm.DefineLabel();
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      asm.Mov(Mem.Word(asm.Lbl("rt_st0")), Reg.BX);
      asm.Mov(Mem.Word(asm.Lbl("rt_st1")), Reg.CX);
      asm.Cmp(Reg.BX, 1);
      asm.Jl(ioError);
      asm.Cmp(Reg.BX, 15);
      asm.Jg(ioError);
      asm.Call(asm.Lbl("rt_name_z"));
      asm.Mov(Reg.DX, Imm.OffsetOf(asm.Lbl("rt_namebuf")));
      asm.Cmp(Mem.Word(asm.Lbl("rt_st1")), (Imm)0);
      asm.Jne(notInput);
      asm.Mov(Reg.AX, 0x3D00);
      asm.Int(0x21);
      asm.Jc(ioError);
      asm.Jmp(store);
      asm.MarkLabel(notInput);
      asm.Cmp(Mem.Word(asm.Lbl("rt_st1")), (Imm)2);
      asm.Je(append);
      asm.Mov(Reg.AH, 0x3C);
      asm.Xor(Reg.CX, Reg.CX);
      asm.Int(0x21);
      asm.Jc(ioError);
      asm.Jmp(store);
      asm.MarkLabel(append);
      asm.Mov(Reg.AX, 0x3D01);
      asm.Int(0x21);
      asm.Jnc(store);
      asm.Mov(Reg.AH, 0x3C);
      asm.Xor(Reg.CX, Reg.CX);
      asm.Int(0x21);
      asm.Jc(ioError);
      asm.MarkLabel(store);
      asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_st0")));
      asm.Shl(Reg.BX, 1);
      asm.Mov(Mem.Word(Reg.BX, files), Reg.AX);
      // APPEND: position at the end
      asm.Cmp(Mem.Word(asm.Lbl("rt_st1")), (Imm)2);
      asm.Jne(done);
      asm.Mov(Reg.BX, Reg.AX);
      asm.Mov(Reg.AX, 0x4202);
      asm.Xor(Reg.CX, Reg.CX);
      asm.Xor(Reg.DX, Reg.DX);
      asm.Int(0x21);
      asm.MarkLabel(done);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.BX);
      asm.Ret();
    }

    this.FClose = asm.MarkLabel("rt_fclose");
    {
      var done = asm.DefineLabel();
      asm.Push(Reg.AX);
      asm.Push(Reg.BX);
      asm.Mov(Reg.BX, Reg.AX);
      asm.Shl(Reg.BX, 1);
      asm.Mov(Reg.AX, Mem.Word(Reg.BX, files));
      asm.Test(Reg.AX, Reg.AX);
      asm.Jz(done);
      asm.Mov(Mem.Word(Reg.BX, files), (Imm)0);
      asm.Mov(Reg.BX, Reg.AX);
      asm.Mov(Reg.AH, 0x3E);
      asm.Int(0x21);
      asm.MarkLabel(done);
      asm.Pop(Reg.BX);
      asm.Pop(Reg.AX);
      asm.Ret();
    }

    this.FCloseAll = asm.MarkLabel("rt_fcloseall");
    {
      var loop = asm.DefineLabel();
      asm.Push(Reg.AX);
      asm.Push(Reg.CX);
      asm.Mov(Reg.CX, 15);
      asm.MarkLabel(loop);
      asm.Mov(Reg.AX, Reg.CX);
      asm.Call(this.FClose);
      asm.Loop(loop);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.AX);
      asm.Ret();
    }

    this.FSelect = asm.MarkLabel("rt_fselect");
    {
      asm.Push(Reg.BX);
      asm.Cmp(Reg.AX, 1);
      asm.Jl(ioError);
      asm.Cmp(Reg.AX, 15);
      asm.Jg(ioError);
      asm.Mov(Reg.BX, Reg.AX);
      asm.Shl(Reg.BX, 1);
      asm.Mov(Reg.AX, Mem.Word(Reg.BX, files));
      asm.Test(Reg.AX, Reg.AX);
      asm.Jz(ioError);
      asm.Mov(Mem.Word(asm.Lbl("rt_curout")), Reg.AX);
      asm.Pop(Reg.BX);
      asm.Ret();
    }

    this.FreeFile = asm.MarkLabel("rt_freefile");
    {
      var scan = asm.DefineLabel();
      var found = asm.DefineLabel();
      asm.Push(Reg.BX);
      asm.Mov(Reg.AX, 1);
      asm.MarkLabel(scan);
      asm.Mov(Reg.BX, Reg.AX);
      asm.Shl(Reg.BX, 1);
      asm.Cmp(Mem.Word(Reg.BX, files), (Imm)0);
      asm.Je(found);
      asm.Inc(Reg.AX);
      asm.Cmp(Reg.AX, 15);
      asm.Jle(scan);
      asm.Jmp(ioError);
      asm.MarkLabel(found);
      asm.Pop(Reg.BX);
      asm.Ret();
    }

    this.Eof = asm.MarkLabel("rt_eof");
    {
      var atEnd = asm.DefineLabel();
      var output = asm.DefineLabel();
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      asm.Push(Reg.SI);
      asm.Push(Reg.DI);
      asm.Mov(Reg.BX, Reg.AX);
      asm.Shl(Reg.BX, 1);
      asm.Mov(Reg.BX, Mem.Word(Reg.BX, files));
      asm.Test(Reg.BX, Reg.BX);
      asm.Jz(ioError);
      asm.Mov(Reg.AX, 0x4201);                  // current position
      asm.Xor(Reg.CX, Reg.CX);
      asm.Xor(Reg.DX, Reg.DX);
      asm.Int(0x21);
      asm.Mov(Reg.SI, Reg.AX);                  // cur.lo
      asm.Mov(Reg.DI, Reg.DX);                  // cur.hi
      asm.Mov(Reg.AX, 0x4202);                  // end position
      asm.Xor(Reg.CX, Reg.CX);
      asm.Xor(Reg.DX, Reg.DX);
      asm.Int(0x21);
      // restore the read position
      asm.Push(Reg.AX);
      asm.Push(Reg.DX);
      asm.Mov(Reg.AX, 0x4200);
      asm.Mov(Reg.CX, Reg.DI);
      asm.Mov(Reg.DX, Reg.SI);
      asm.Int(0x21);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.AX);
      // EOF when cur >= end  <=>  end <= cur
      asm.Cmp(Reg.DX, Reg.DI);
      asm.Jb(atEnd);
      asm.Ja(output);                           // end.hi > cur.hi -> not EOF
      asm.Cmp(Reg.AX, Reg.SI);
      asm.Jbe(atEnd);
      asm.MarkLabel(output);
      asm.Xor(Reg.AX, Reg.AX);
      asm.Jmp(asm.Lbl("rt_eof_done"));
      asm.MarkLabel(atEnd);
      asm.Mov(Reg.AX, -1);
      asm.MarkLabel("rt_eof_done");
      asm.Pop(Reg.DI);
      asm.Pop(Reg.SI);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.BX);
      asm.Ret();
    }

    this.Kill = asm.MarkLabel("rt_kill");
    {
      asm.Push(Reg.AX);
      asm.Push(Reg.DX);
      asm.Call(asm.Lbl("rt_name_z"));
      asm.Mov(Reg.DX, Imm.OffsetOf(asm.Lbl("rt_namebuf")));
      asm.Mov(Reg.AH, 0x41);
      asm.Int(0x21);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.AX);
      asm.Ret();
    }

    this.LInput = asm.MarkLabel("rt_linput");
    {
      var read = asm.DefineLabel();
      var finish = asm.DefineLabel();
      var lineFeed = asm.DefineLabel();
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      asm.Push(Reg.SI);
      asm.Push(Reg.DI);
      asm.Mov(Reg.BX, Reg.AX);
      asm.Shl(Reg.BX, 1);
      asm.Mov(Reg.BX, Mem.Word(Reg.BX, files));
      asm.Test(Reg.BX, Reg.BX);
      asm.Jz(ioError);
      asm.Xor(Reg.DI, Reg.DI);
      asm.MarkLabel(read);
      asm.Cmp(Reg.DI, 255);
      asm.Jae(finish);
      asm.Mov(Reg.DX, Imm.OffsetOf(asm.Lbl("rt_linebuf")));
      asm.Add(Reg.DX, Reg.DI);
      asm.Mov(Reg.CX, 1);
      asm.Mov(Reg.AH, 0x3F);
      asm.Int(0x21);
      asm.Jc(finish);
      asm.Test(Reg.AX, Reg.AX);
      asm.Jz(finish);                            // EOF
      asm.Mov(Reg.SI, Reg.DI);
      asm.Mov(Reg.AL, Mem.Byte(Reg.SI, asm.Lbl("rt_linebuf")));
      asm.Cmp(Reg.AL, (Imm)10);
      asm.Je(lineFeed);
      asm.Inc(Reg.DI);
      asm.Jmp(read);
      asm.MarkLabel(lineFeed);
      // strip a CR before the LF
      asm.Test(Reg.DI, Reg.DI);
      asm.Jz(finish);
      asm.Mov(Reg.SI, Reg.DI);
      asm.Dec(Reg.SI);
      asm.Cmp(Mem.Byte(Reg.SI, asm.Lbl("rt_linebuf")), (byte)13);
      asm.Jne(finish);
      asm.Dec(Reg.DI);
      asm.MarkLabel(finish);
      asm.Mov(Reg.CX, Reg.DI);
      asm.Mov(Reg.SI, Imm.OffsetOf(asm.Lbl("rt_linebuf")));
      asm.Mov(Reg.DX, Reg.DS);
      asm.Call(this.StrMem);
      asm.Pop(Reg.DI);
      asm.Pop(Reg.SI);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.BX);
      asm.Ret();
    }
  }

  private void EmitFileData(Assembler asm) {
    asm.Align(2);
    asm.MarkLabel("rt_files");
    asm.Db(new byte[32]);
    asm.MarkLabel("rt_namebuf");
    asm.Db(new byte[128]);
    asm.MarkLabel("rt_linebuf");
    asm.Db(new byte[256]);
  }
}
