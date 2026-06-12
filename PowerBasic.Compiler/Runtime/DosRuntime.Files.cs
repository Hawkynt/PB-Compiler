using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Runtime;

/// <summary>
/// DOS handle-based file I/O. PB file numbers 1..15 map through the word table
/// <c>rt_files</c> (0 = closed) to DOS handles; PB file number 0 denotes the
/// console (DOS handle 0). Register conventions:
///   FOpen:     AX=filename handle (consumed), BX=PB file number, CX=mode
///              (0=INPUT, 1=OUTPUT, 2=APPEND, 3=RANDOM with SI=reclen, 4=BINARY)
///   FClose:    AX=PB file number (no-op when closed)
///   FCloseAll: -
///   FSelect:   AX=PB file number - routes PrintStr/StrPrint to that file
///              (rt_curout; reset to 1 = stdout by the caller)
///   FreeFile:  -> AX=first unused PB file number
///   Eof:       AX=PB file number -> AX=-1/0
///   Kill:      AX=filename handle (consumed)
///   LInput:    AX=PB file number -> AX=string handle (one line, CR/LF stripped)
///   FHandle:   AX=PB file number -> BX=DOS handle (error when closed)
///   Lof:       AX=PB file number -> DX:AX=file length in bytes
///   FSetPos:   AX=PB file number, DX:CX=1-based position (record number when
///              the file's reclen > 1, byte position otherwise)
///   FPos:      AX=PB file number -> DX:AX=1-based byte position
///   FRead:     BX=DOS handle, CX=count, SI:DX=buffer -> AX=bytes read
///   FWrite:    BX=DOS handle, CX=count, SI:DX=buffer
///   FGetStr:   AX=PB file number, CX=count -> AX=string handle (truncated at EOF)
///   FPutStr:   AX=PB file number, DX=string handle (consumed)
///   FToken:    AX=PB file number -> AX=string handle (next INPUT item: skips
///              leading whitespace/CR/LF, stops at comma or end of line)
/// Failures raise ERR 57 through the ON ERROR machinery.
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
  public Label FHandle { get; private set; } = null!;
  public Label Lof { get; private set; } = null!;
  public Label FSetPos { get; private set; } = null!;
  public Label FPos { get; private set; } = null!;
  public Label FRead { get; private set; } = null!;
  public Label FWrite { get; private set; } = null!;
  public Label FGetStr { get; private set; } = null!;
  public Label FPutStr { get; private set; } = null!;
  public Label FPutRaw { get; private set; } = null!;
  public Label FGetInto { get; private set; } = null!;
  public Label FToken { get; private set; } = null!;

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
      var readWrite = asm.DefineLabel();
      var store = asm.DefineLabel();
      var random = asm.DefineLabel();
      var binary = asm.DefineLabel();
      var sequential = asm.DefineLabel();
      var setLen = asm.DefineLabel();
      var done = asm.DefineLabel();
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      asm.Mov(Mem.Word(asm.Lbl("rt_st0")), Reg.BX);
      asm.Mov(Mem.Word(asm.Lbl("rt_st1")), Reg.CX);
      asm.Mov(Mem.Word(asm.Lbl("rt_st2")), Reg.SI);
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
      asm.Cmp(Mem.Word(asm.Lbl("rt_st1")), (Imm)3);
      asm.Jge(readWrite);
      asm.Mov(Reg.AH, 0x3C);
      asm.Xor(Reg.CX, Reg.CX);
      asm.Int(0x21);
      asm.Jc(ioError);
      asm.Jmp(store);
      asm.MarkLabel(readWrite);          // RANDOM/BINARY: open r/w, create when missing
      asm.Mov(Reg.AX, 0x3D02);
      asm.Int(0x21);
      asm.Jnc(store);
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
      // record length: 0 sequential, 1 BINARY, LEN= (default 128) RANDOM
      asm.Mov(Reg.CX, Mem.Word(asm.Lbl("rt_st1")));
      asm.Cmp(Reg.CX, 3);
      asm.Je(random);
      asm.Cmp(Reg.CX, 4);
      asm.Je(binary);
      asm.Jmp(sequential);
      asm.MarkLabel(random);
      asm.Mov(Reg.CX, Mem.Word(asm.Lbl("rt_st2")));
      asm.Test(Reg.CX, Reg.CX);
      asm.Jnz(setLen);
      asm.Mov(Reg.CX, 128);
      asm.Jmp(setLen);
      asm.MarkLabel(binary);
      asm.Mov(Reg.CX, 1);
      asm.Jmp(setLen);
      asm.MarkLabel(sequential);
      asm.Xor(Reg.CX, Reg.CX);
      asm.MarkLabel(setLen);
      asm.Mov(Mem.Word(Reg.BX, asm.Lbl("rt_reclen")), Reg.CX);
      asm.Mov(Reg.CX, Mem.Word(asm.Lbl("rt_st1")));
      asm.Mov(Mem.Word(Reg.BX, asm.Lbl("rt_fmode")), Reg.CX);
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

    this.FHandle = asm.MarkLabel("rt_fhandle");
    {
      var console = asm.DefineLabel();
      var found = asm.DefineLabel();
      asm.Test(Reg.AX, Reg.AX);
      asm.Jz(console);
      asm.Cmp(Reg.AX, 15);
      asm.Jg(ioError);
      asm.Mov(Reg.BX, Reg.AX);
      asm.Shl(Reg.BX, 1);
      asm.Mov(Reg.BX, Mem.Word(Reg.BX, files));
      asm.Test(Reg.BX, Reg.BX);
      asm.Jz(ioError);
      asm.Jmp(found);
      asm.MarkLabel(console);
      asm.Xor(Reg.BX, Reg.BX);
      asm.MarkLabel(found);
      asm.Ret();
    }

    this.FRead = asm.MarkLabel("rt_fread");
    {
      asm.Push(Reg.DS);
      asm.Mov(Reg.DS, Reg.SI);
      asm.Mov(Reg.AH, 0x3F);
      asm.Int(0x21);
      asm.Pop(Reg.DS);
      asm.Jc(ioError);
      asm.Ret();
    }

    this.FWrite = asm.MarkLabel("rt_fwrite");
    {
      asm.Push(Reg.AX);
      asm.Push(Reg.DS);
      asm.Mov(Reg.DS, Reg.SI);
      asm.Mov(Reg.AH, 0x40);
      asm.Int(0x21);
      asm.Pop(Reg.DS);
      asm.Jc(ioError);
      asm.Pop(Reg.AX);
      asm.Ret();
    }

    this.Lof = asm.MarkLabel("rt_lof");
    {
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Push(Reg.SI);
      asm.Push(Reg.DI);
      asm.Call(this.FHandle);
      asm.Mov(Reg.AX, 0x4201);              // remember current position
      asm.Xor(Reg.CX, Reg.CX);
      asm.Xor(Reg.DX, Reg.DX);
      asm.Int(0x21);
      asm.Mov(Reg.SI, Reg.AX);
      asm.Mov(Reg.DI, Reg.DX);
      asm.Mov(Reg.AX, 0x4202);              // end = length
      asm.Xor(Reg.CX, Reg.CX);
      asm.Xor(Reg.DX, Reg.DX);
      asm.Int(0x21);
      asm.Push(Reg.AX);
      asm.Push(Reg.DX);
      asm.Mov(Reg.AX, 0x4200);              // restore position
      asm.Mov(Reg.CX, Reg.DI);
      asm.Mov(Reg.DX, Reg.SI);
      asm.Int(0x21);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.AX);
      asm.Pop(Reg.DI);
      asm.Pop(Reg.SI);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.BX);
      asm.Ret();
    }

    // GET/PUT positions are 1-based (records for RANDOM, bytes for BINARY);
    // positions below 1 clamp to the start (the corpus uses GET f,0 and GET f,1
    // interchangeably for byte 0)
    this.FSetPos = asm.MarkLabel("rt_fsetpos");
    {
      var inRange = asm.DefineLabel();
      asm.Push(Reg.AX);
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      asm.Push(Reg.SI);
      asm.Call(this.FHandle);
      asm.Mov(Reg.SI, Reg.AX);
      asm.Shl(Reg.SI, 1);
      asm.Mov(Reg.SI, Mem.Word(Reg.SI, asm.Lbl("rt_reclen")));
      asm.Sub(Reg.CX, (Imm)1);              // 1-based -> 0-based
      asm.Sbb(Reg.DX, (Imm)0);
      asm.Test(Reg.DX, Reg.DX);
      asm.Jns(inRange);
      asm.Xor(Reg.CX, Reg.CX);
      asm.Xor(Reg.DX, Reg.DX);
      asm.MarkLabel(inRange);
      asm.Jmp(asm.Lbl("rt_fseek_core"));
    }

    // PB's SEEK statement: raw 0-based byte offset for BINARY, 1-based records for RANDOM
    asm.MarkLabel("rt_fseekstmt");
    {
      var core = asm.Lbl("rt_fseek_core");
      asm.Push(Reg.AX);
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      asm.Push(Reg.SI);
      asm.Call(this.FHandle);
      asm.Mov(Reg.SI, Reg.AX);
      asm.Shl(Reg.SI, 1);
      asm.Mov(Reg.SI, Mem.Word(Reg.SI, asm.Lbl("rt_reclen")));
      asm.Cmp(Reg.SI, 2);
      asm.Jb(core);
      asm.Sub(Reg.CX, (Imm)1);
      asm.Sbb(Reg.DX, (Imm)0);
      asm.Jmp(core);
    }

    // common tail: DX:CX = 0-based position (record index when reclen > 1), BX = DOS handle
    asm.MarkLabel("rt_fseek_core");
    {
      var seek = asm.DefineLabel();
      asm.Cmp(Reg.SI, 2);
      asm.Jb(seek);
      asm.Push(Reg.BX);                     // record number * reclen
      asm.Mov(Reg.AX, Reg.CX);
      asm.Mov(Reg.BX, Reg.SI);
      asm.Xor(Reg.CX, Reg.CX);
      asm.Call(this.LongMul);
      asm.Mov(Reg.CX, Reg.DX);
      asm.Mov(Reg.DX, Reg.AX);
      asm.Pop(Reg.BX);
      asm.Jmp(asm.Lbl("rt_fsetpos_go"));
      asm.MarkLabel(seek);
      asm.Xchg(Reg.CX, Reg.DX);             // INT 21h/42h wants CX:DX = high:low
      asm.MarkLabel("rt_fsetpos_go");
      asm.Mov(Reg.AX, 0x4200);
      asm.Int(0x21);
      asm.Jc(ioError);
      asm.Pop(Reg.SI);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.BX);
      asm.Pop(Reg.AX);
      asm.Ret();
    }

    this.FPos = asm.MarkLabel("rt_fpos");
    {
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Call(this.FHandle);
      asm.Mov(Reg.AX, 0x4201);
      asm.Xor(Reg.CX, Reg.CX);
      asm.Xor(Reg.DX, Reg.DX);
      asm.Int(0x21);                        // DX:AX = 0-based position (PB BINARY convention)
      asm.Pop(Reg.CX);
      asm.Pop(Reg.BX);
      asm.Ret();
    }

    this.FGetStr = asm.MarkLabel("rt_fgetstr");
    {
      var done = asm.DefineLabel();
      var empty = asm.DefineLabel();
      var fits = asm.DefineLabel();
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      asm.Push(Reg.SI);
      asm.Push(Reg.DI);
      asm.Call(this.FHandle);
      asm.Jcxz(empty);
      asm.Call(this.StrAlloc);              // CX=length -> AX=handle
      asm.Mov(Reg.DI, Reg.AX);
      asm.Mov(Reg.SI, Reg.AX);
      asm.Shl(Reg.SI, 1);
      asm.Shl(Reg.SI, 1);
      asm.Mov(Reg.DX, this.Descriptor(Reg.SI));   // data offset
      asm.Mov(Reg.SI, Mem.Word(asm.Lbl("rt_strseg")));
      asm.Call(this.FRead);                 // AX = actual count
      asm.Cmp(Reg.AX, Reg.CX);
      asm.Jae(fits);
      asm.Mov(Reg.SI, Reg.DI);              // short read: shrink the descriptor
      asm.Shl(Reg.SI, 1);
      asm.Shl(Reg.SI, 1);
      asm.Mov(this.Descriptor(Reg.SI, 2), Reg.AX);
      asm.MarkLabel(fits);
      asm.Mov(Reg.AX, Reg.DI);
      asm.Jmp(done);
      asm.MarkLabel(empty);
      asm.Xor(Reg.AX, Reg.AX);
      asm.MarkLabel(done);
      asm.Pop(Reg.DI);
      asm.Pop(Reg.SI);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.BX);
      asm.Ret();
    }

    // raw write: AX=PB file number, DX=string handle (kept)
    this.FPutRaw = asm.MarkLabel("rt_fputraw");
    {
      var skip = asm.DefineLabel();
      asm.Push(Reg.AX);
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      asm.Push(Reg.SI);
      asm.Call(this.FHandle);
      asm.Mov(Reg.SI, Reg.DX);
      asm.Shl(Reg.SI, 1);
      asm.Shl(Reg.SI, 1);
      asm.Mov(Reg.CX, this.Descriptor(Reg.SI, 2));
      asm.Mov(Reg.DX, this.Descriptor(Reg.SI));
      asm.Jcxz(skip);
      asm.Mov(Reg.SI, Mem.Word(asm.Lbl("rt_strseg")));
      asm.Call(this.FWrite);
      asm.MarkLabel(skip);
      asm.Pop(Reg.SI);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.BX);
      asm.Pop(Reg.AX);
      asm.Ret();
    }

    this.FPutStr = asm.MarkLabel("rt_fputstr");
    {
      asm.Call(this.FPutRaw);
      asm.Push(Reg.AX);
      asm.Mov(Reg.AX, Reg.DX);
      asm.Call(this.StrFree);
      asm.Pop(Reg.AX);
      asm.Ret();
    }

    // raw read: AX=PB file number, DX=string handle - fills the string's
    // current LEN bytes in place (PB GET semantics on a pre-sized string)
    this.FGetInto = asm.MarkLabel("rt_fgetinto");
    {
      var skip = asm.DefineLabel();
      asm.Push(Reg.AX);
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      asm.Push(Reg.SI);
      asm.Call(this.FHandle);
      asm.Mov(Reg.SI, Reg.DX);
      asm.Shl(Reg.SI, 1);
      asm.Shl(Reg.SI, 1);
      asm.Mov(Reg.CX, this.Descriptor(Reg.SI, 2));
      asm.Mov(Reg.DX, this.Descriptor(Reg.SI));
      asm.Jcxz(skip);
      asm.Mov(Reg.SI, Mem.Word(asm.Lbl("rt_strseg")));
      asm.Call(this.FRead);
      asm.MarkLabel(skip);
      asm.Pop(Reg.SI);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.BX);
      asm.Pop(Reg.AX);
      asm.Ret();
    }

    this.FToken = asm.MarkLabel("rt_ftoken");
    {
      var skipLead = asm.DefineLabel();
      var accumulate = asm.DefineLabel();
      var character = asm.DefineLabel();
      var finish = asm.DefineLabel();
      var stripCr = asm.DefineLabel();
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      asm.Push(Reg.SI);
      asm.Push(Reg.DI);
      asm.Call(this.FHandle);
      asm.Xor(Reg.DI, Reg.DI);
      // skip leading spaces / CR / LF
      asm.MarkLabel(skipLead);
      asm.Mov(Reg.DX, Imm.OffsetOf(asm.Lbl("rt_linebuf")));
      asm.Mov(Reg.CX, 1);
      asm.Mov(Reg.AH, 0x3F);
      asm.Int(0x21);
      asm.Jc(finish);
      asm.Test(Reg.AX, Reg.AX);
      asm.Jz(finish);
      asm.Mov(Reg.AL, Mem.Byte(asm.Lbl("rt_linebuf")));
      asm.Cmp(Reg.AL, (Imm)' ');
      asm.Je(skipLead);
      asm.Cmp(Reg.AL, (Imm)13);
      asm.Je(skipLead);
      asm.Cmp(Reg.AL, (Imm)10);
      asm.Je(skipLead);
      asm.Jmp(character);
      // accumulate until comma / CR / LF / EOF
      asm.MarkLabel(accumulate);
      asm.Mov(Reg.DX, Imm.OffsetOf(asm.Lbl("rt_linebuf")));
      asm.Add(Reg.DX, Reg.DI);
      asm.Mov(Reg.CX, 1);
      asm.Mov(Reg.AH, 0x3F);
      asm.Int(0x21);
      asm.Jc(finish);
      asm.Test(Reg.AX, Reg.AX);
      asm.Jz(finish);
      asm.Mov(Reg.SI, Reg.DI);
      asm.Mov(Reg.AL, Mem.Byte(Reg.SI, asm.Lbl("rt_linebuf")));
      asm.MarkLabel(character);
      asm.Cmp(Reg.AL, (Imm)',');
      asm.Je(finish);
      asm.Cmp(Reg.AL, (Imm)10);
      asm.Je(stripCr);
      asm.Mov(Reg.SI, Reg.DI);
      asm.Mov(Mem.Byte(Reg.SI, asm.Lbl("rt_linebuf")), Reg.AL);
      asm.Inc(Reg.DI);
      asm.Cmp(Reg.DI, 255);
      asm.Jb(accumulate);
      asm.Jmp(finish);
      asm.MarkLabel(stripCr);
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
      if (this.Dialect.IsBascomRuntime()) {
        // the BASCOM lineage (QB 1.0-3.0) ends sequential OUTPUT/APPEND files
        // with a CP/M-style ^Z marker (oracle-verified; QB 4.x dropped the habit)
        var noEof = asm.DefineLabel();
        var writeEof = asm.DefineLabel();
        asm.Cmp(Mem.Word(Reg.BX, asm.Lbl("rt_fmode")), (Imm)1);
        asm.Je(writeEof);
        asm.Cmp(Mem.Word(Reg.BX, asm.Lbl("rt_fmode")), (Imm)2);
        asm.Jne(noEof);
        asm.MarkLabel(writeEof);
        asm.Push(Reg.AX);
        asm.Push(Reg.CX);
        asm.Push(Reg.DX);
        asm.Mov(Reg.BX, Reg.AX);
        asm.Mov(Mem.Byte(asm.Lbl("rt_namebuf")), 0x1A);
        asm.Mov(Reg.CX, 1);
        asm.Mov(Reg.DX, Imm.OffsetOf(asm.Lbl("rt_namebuf")));
        asm.Mov(Reg.AH, 0x40);
        asm.Int(0x21);
        asm.Pop(Reg.DX);
        asm.Pop(Reg.CX);
        asm.Pop(Reg.AX);
        asm.MarkLabel(noEof);
      }
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
      asm.Call(this.FHandle);
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
    this.ZeroBlob(asm, "rt_files", 32);
    this.ZeroBlob(asm, "rt_fmode", 32);
    asm.MarkLabel("rt_reclen");
    asm.Db(new byte[32]);
    this.ZeroBlob(asm, "rt_namebuf", 128);
    this.ZeroBlob(asm, "rt_linebuf", 256);
  }
}
