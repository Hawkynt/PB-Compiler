using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

public sealed partial class DosRuntime {
  private void EmitPrintStr(Assembler asm) {
    this.PrintStr = asm.MarkLabel("rt_print_str");
    var done = asm.DefineLabel("rt_print_str_done");
    var capture = asm.DefineLabel("rt_print_str_cap");
    asm.Or(Reg.CX, Reg.CX);
    asm.Jz(done);
    asm.Cmp(Mem.Byte(asm.Lbl("rt_capmode")), (Imm)0);
    asm.Jne(capture);
    if (this.EnableFastVideo) {
      var dos = asm.DefineLabel();
      var scanFail = asm.DefineLabel();
      var scan = asm.DefineLabel();
      var blit = asm.DefineLabel();
      asm.Cmp(Mem.Word(asm.Lbl("rt_curout")), 1);
      asm.Jne(dos);
      asm.Cmp(Reg.CX, 80);
      asm.Ja(dos);
      asm.Push(Reg.SI);
      asm.Push(Reg.CX);
      asm.MarkLabel(scan);
      asm.Lodsb();
      asm.Cmp(Reg.AL, 0x20);
      asm.Jb(scanFail);
      asm.Loop(scan);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.SI);

      asm.Push(Reg.AX);
      asm.Push(Reg.BX);
      asm.Push(Reg.DX);
      asm.Push(Reg.DI);
      asm.Push(Reg.ES);
      asm.Mov(Reg.AX, 0x0040);
      asm.Mov(Reg.ES, Reg.AX);
      asm.Mov(Reg.DX, Mem.Word(0x50).Seg(Reg.ES));
      asm.Mov(Reg.AL, Reg.DL);
      asm.Add(Reg.AL, Reg.CL);
      asm.Cmp(Reg.AL, 80);
      asm.Ja(asm.Lbl("rt_fv_unfit"));
      asm.Mov(Reg.AL, Reg.DH);
      asm.Mov(Reg.AH, 80);
      asm.Mul(Reg.AH);
      asm.Mov(Reg.BL, Reg.DL);
      asm.Xor(Reg.BH, Reg.BH);
      asm.Add(Reg.AX, Reg.BX);
      asm.Shl(Reg.AX, 1);
      asm.Mov(Reg.DI, Reg.AX);
      asm.Mov(Reg.AX, 0xB800);
      asm.Mov(Reg.ES, Reg.AX);
      asm.Push(Reg.CX);
      asm.Push(Reg.SI);
      asm.MarkLabel(blit);
      asm.Lodsb();
      asm.Mov(Reg.AH, 0x07);
      asm.Stosw();
      asm.Loop(blit);
      asm.Pop(Reg.SI);
      asm.Pop(Reg.CX);
      asm.Add(Reg.DL, Reg.CL);
      asm.Mov(Reg.AH, 0x02);
      asm.Mov(Reg.BH, (Imm)0);
      asm.Int(0x10);
      asm.Pop(Reg.ES);
      asm.Pop(Reg.DI);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.BX);
      asm.Pop(Reg.AX);
      asm.Jmp(asm.Lbl("rt_fv_advance"));

      asm.MarkLabel("rt_fv_unfit");
      asm.Pop(Reg.ES);
      asm.Pop(Reg.DI);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.BX);
      asm.Pop(Reg.AX);
      asm.Jmp(dos);

      asm.MarkLabel(scanFail);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.SI);
      asm.MarkLabel(dos);
    }
    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Push(Reg.DX);
    asm.Mov(Reg.DX, Reg.SI);
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_curout")));
    asm.Mov(Reg.AH, 0x40);
    asm.Int(0x21);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.MarkLabel("rt_fv_advance");
    asm.Push(Reg.BX);
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_colptr")));
    asm.Add(Mem.Word(Reg.BX), Reg.CX);
    asm.Pop(Reg.BX);
    asm.MarkLabel(done);
    asm.Ret();

    // Capture mode (STR$): rt_capbuf lives in DS. Temporarily mirror DS into ES so the same
    // target-aware copy kernel used by strings/memory can handle the run, then restore caller ES.
    asm.MarkLabel(capture);
    asm.Push(Reg.AX);
    asm.Push(Reg.CX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);
    asm.Mov(Reg.DI, Mem.Word(asm.Lbl("rt_caplen")));
    asm.Add(Mem.Word(asm.Lbl("rt_caplen")), Reg.CX);
    asm.Lea(Reg.DI, Mem.At(Reg.DI, asm.Lbl("rt_capbuf")));
    asm.Push(Reg.DS);
    asm.Pop(Reg.ES);
    this.EmitRepMovsbWidened(asm);
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.AX);
    asm.Ret();
  }

  private void EmitPrintNewLine(Assembler asm) {
    this.PrintNewLine = asm.MarkLabel("rt_print_nl");
    asm.Push(Reg.SI);
    asm.Push(Reg.CX);
    asm.Push(Reg.AX);
    asm.Mov(Reg.SI, Imm.OffsetOf(asm.Lbl("rt_crlf")));
    asm.Mov(Reg.CX, 2);
    asm.Call(this.PrintStr);
    asm.Push(Reg.BX);
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_colptr")));
    asm.Mov(Mem.Word(Reg.BX), (Imm)0);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.SI);
    asm.Ret();
  }

  private void EmitPrintZone(Assembler asm) {
    this.PrintZone = asm.MarkLabel("rt_print_zone");
    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_colptr")));
    asm.Mov(Reg.AX, Mem.Word(Reg.BX));
    asm.Xor(Reg.DX, Reg.DX);
    asm.Mov(Reg.BX, 14);
    asm.Div(Reg.BX);
    asm.Mov(Reg.CX, 14);
    asm.Sub(Reg.CX, Reg.DX);
    asm.Mov(Reg.SI, Imm.OffsetOf(asm.Lbl("rt_spaces")));
    asm.Call(this.PrintStr);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Ret();
  }

  private void EmitPrintInt16(Assembler asm) {
    this.PrintInt16 = asm.MarkLabel("rt_print_i16");
    asm.Push(Reg.DX);
    asm.Cwd();
    asm.Call(asm.Lbl("rt_print_i32"));
    asm.Pop(Reg.DX);
    asm.Ret();
  }

  private void EmitPrintInt32(Assembler asm) {
    this.PrintInt32 = asm.MarkLabel("rt_print_i32");
    var convert = asm.DefineLabel();
    var digitLoop = asm.DefineLabel();
    var positive = asm.DefineLabel();

    asm.Push(Reg.SI);
    asm.Push(Reg.CX);
    asm.Push(Reg.BX);
    asm.Push(Reg.DI);
    asm.Push(Reg.AX);
    asm.Push(Reg.DX);

    asm.Mov(Reg.SI, Imm.OffsetOf(this._numBuffer, 31));
    asm.Mov(Mem.Byte(Reg.SI), ' ');
    asm.Xor(Reg.DI, Reg.DI);
    asm.Test(Reg.DX, Reg.DX);
    asm.Jns(positive);
    asm.Mov(Reg.DI, 1);
    asm.Not(Reg.DX);
    asm.Not(Reg.AX);
    asm.Add(Reg.AX, 1);
    asm.Adc(Reg.DX, (Imm)0);
    asm.MarkLabel(positive);

    asm.MarkLabel(convert);
    asm.Mov(Reg.CX, 10);
    asm.MarkLabel(digitLoop);
    asm.Mov(Reg.BX, Reg.AX);
    asm.Mov(Reg.AX, Reg.DX);
    asm.Xor(Reg.DX, Reg.DX);
    asm.Div(Reg.CX);
    asm.Xchg(Reg.AX, Reg.BX);
    asm.Div(Reg.CX);
    asm.Add(Reg.DX, '0');
    asm.Dec(Reg.SI);
    asm.Mov(Mem.Byte(Reg.SI), Reg.DL);
    asm.Mov(Reg.DX, Reg.BX);
    asm.Mov(Reg.BX, Reg.AX);
    asm.Or(Reg.BX, Reg.DX);
    asm.Jnz(digitLoop);
    var noSign = asm.DefineLabel();
    asm.Dec(Reg.SI);
    asm.Test(Reg.DI, Reg.DI);
    asm.Jz(noSign);
    asm.Mov(Mem.Byte(Reg.SI), '-');
    asm.Jmp(asm.Lbl("rt_print_i32_out"));
    asm.MarkLabel(noSign);
    asm.Mov(Mem.Byte(Reg.SI), ' ');

    asm.MarkLabel("rt_print_i32_out");
    asm.Mov(Reg.CX, Imm.OffsetOf(this._numBuffer, 32));
    asm.Sub(Reg.CX, Reg.SI);
    asm.Call(this.PrintStr);

    asm.Pop(Reg.DX);
    asm.Pop(Reg.AX);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.SI);
    asm.Ret();
  }
}
