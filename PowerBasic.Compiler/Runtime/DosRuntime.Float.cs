using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Runtime;

public sealed partial class DosRuntime {
  private void EmitPrintFloat(Assembler asm) {
    var fmt = this.EffectiveDialect;
    var turbo = fmt.IsTurboBasic();
    var microsoft = fmt.Family() == DialectFamily.Microsoft;
    this.PrintSingle = asm.MarkLabel("rt_print_f32");
    asm.Mov(Reg.BX, turbo ? 16 : 7);
    asm.Jmp(asm.Lbl("rt_print_flt"));

    this.PrintDouble = asm.MarkLabel("rt_print_f64");
    asm.Mov(Reg.BX, turbo || microsoft && fmt < Dialect.Pds70 ? 16 : 15);

    asm.MarkLabel("rt_print_flt");
    var zero = asm.DefineLabel();
    var scaleDown = asm.DefineLabel();
    var scaleDownTest = asm.DefineLabel();
    var scaleUp = asm.DefineLabel();
    var scaleUpTest = asm.DefineLabel();
    var emit = asm.DefineLabel();

    asm.Push(Reg.SI);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.DI);

    asm.Ftst();
    asm.FstswAx();
    asm.Sahf();
    asm.Jz(zero);

    asm.Mov(Reg.DI, (Imm)0);
    asm.Jnc(asm.Lbl("rt_print_flt_abs"));
    asm.Mov(Reg.DI, 1);
    asm.MarkLabel("rt_print_flt_abs");
    asm.Fabs();

    if (microsoft)
      asm.Fld(St.St0);

    asm.Xor(Reg.CX, Reg.CX);

    this.EmitLoadPow10(asm, Reg.BX);
    asm.MarkLabel(scaleDownTest);
    asm.Fcom();
    asm.FstswAx();
    asm.Sahf();
    asm.Ja(asm.Lbl("rt_print_flt_belowupper"));
    asm.MarkLabel(scaleDown);
    asm.Fxch();
    asm.Fdiv(Mem.Qword(asm.Lbl("rt_const_ten_m64")));
    asm.Fxch();
    asm.Inc(Reg.CX);
    asm.Jmp(scaleDownTest);
    asm.MarkLabel("rt_print_flt_belowupper");
    asm.Fstp(St.St0);

    asm.Dec(Reg.BX);
    this.EmitLoadPow10(asm, Reg.BX);
    asm.Inc(Reg.BX);
    asm.MarkLabel(scaleUpTest);
    asm.Fcom();
    asm.FstswAx();
    asm.Sahf();
    asm.Jbe(asm.Lbl("rt_print_flt_scaled"));
    asm.MarkLabel(scaleUp);
    asm.Fxch();
    asm.Fmul(Mem.Qword(asm.Lbl("rt_const_ten_m64")));
    asm.Fxch();
    asm.Dec(Reg.CX);
    asm.Jmp(scaleUpTest);
    asm.MarkLabel("rt_print_flt_scaled");
    asm.Fstp(St.St0);

    if (microsoft) {
      var rescaleMul = asm.DefineLabel();
      var rescaleDone = asm.DefineLabel();
      var rescalePositive = asm.DefineLabel();
      asm.Fstp(St.St0);
      asm.Mov(Reg.DX, Reg.CX);
      asm.Test(Reg.CX, Reg.CX);
      asm.Jns(rescalePositive);
      asm.Neg(Reg.DX);
      asm.MarkLabel(rescalePositive);
      this.EmitLoadPow10(asm, Reg.DX);
      asm.Test(Reg.CX, Reg.CX);
      asm.Js(rescaleMul);
      asm.Fdivp();
      asm.Jmp(rescaleDone);
      asm.MarkLabel(rescaleMul);
      asm.Fmulp();
      asm.MarkLabel(rescaleDone);
    }

    asm.Frndint();
    this.EmitLoadPow10(asm, Reg.BX);
    asm.Fcom();
    asm.FstswAx();
    asm.Sahf();
    asm.Ja(asm.Lbl("rt_print_flt_nocarry"));
    asm.Fxch();
    asm.Fdiv(Mem.Qword(asm.Lbl("rt_const_ten_m64")));
    asm.Frndint();
    asm.Fxch();
    asm.Inc(Reg.CX);
    asm.MarkLabel("rt_print_flt_nocarry");
    asm.Fstp(St.St0);
    asm.Fistp(Mem.Qword(asm.Lbl("rt_scratch")));
    asm.Jmp(emit);

    asm.MarkLabel(zero);
    asm.Fstp(St.St0);
    asm.Push(Reg.AX);
    asm.Xor(Reg.AX, Reg.AX);
    asm.Cwd();
    asm.Call(this.PrintInt32);
    asm.Pop(Reg.AX);
    asm.Jmp(asm.Lbl("rt_print_flt_done"));

    asm.MarkLabel(emit);
    this.EmitFloatDigits(asm);

    asm.MarkLabel("rt_print_flt_done");
    asm.Pop(Reg.DI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.SI);
    asm.Ret();
  }

  private void EmitLoadPow10(Assembler asm, Reg countReg) {
    var loop = asm.DefineLabel();
    var done = asm.DefineLabel();
    asm.Push(Reg.CX);
    asm.Mov(Reg.CX, countReg);
    asm.Fld1();
    asm.Jcxz(done);
    asm.MarkLabel(loop);
    asm.Fmul(Mem.Qword(asm.Lbl("rt_const_ten_m64")));
    asm.Loop(loop);
    asm.MarkLabel(done);
    asm.Pop(Reg.CX);
  }

  private void EmitFloatDigits(Assembler asm) {
    var digitLoop = asm.MarkLabel("rt_fd_digits");
    _ = digitLoop;

    asm.Push(Reg.DI);
    asm.Push(Reg.BX);
    asm.Mov(Reg.SI, Imm.OffsetOf(this._numBuffer, 34));
    asm.Push(Reg.BX);

    asm.MarkLabel("rt_fd_next");
    asm.Push(Reg.CX);
    asm.Mov(Reg.CX, 4);
    asm.Mov(Reg.DI, Imm.OffsetOf(this._scratch, 6));
    asm.Xor(Reg.DX, Reg.DX);
    asm.MarkLabel("rt_fd_divword");
    asm.Mov(Reg.AX, Mem.Word(Reg.DI));
    asm.Push(Reg.BX);
    asm.Mov(Reg.BX, 10);
    asm.Div(Reg.BX);
    asm.Pop(Reg.BX);
    asm.Mov(Mem.Word(Reg.DI), Reg.AX);
    asm.Sub(Reg.DI, 2);
    asm.Loop(asm.Lbl("rt_fd_divword"));
    asm.Pop(Reg.CX);
    asm.Add(Reg.DX, '0');
    asm.Dec(Reg.SI);
    asm.Mov(Mem.Byte(Reg.SI), Reg.DL);
    asm.Pop(Reg.BX);
    asm.Dec(Reg.BX);
    asm.Push(Reg.BX);
    asm.Test(Reg.BX, Reg.BX);
    asm.Jnz(asm.Lbl("rt_fd_next"));
    asm.Pop(Reg.BX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.DI);

    this.EmitFloatLayout(asm);
  }

  private void EmitFloatLayout(Assembler asm) {
    asm.Call(asm.Lbl("rt_fd_layout"));
    asm.Jmp(asm.Lbl("rt_print_flt_done"));

    asm.MarkLabel("rt_fd_layout");
    var outBuf = this._numBuffer;
    var write = Reg.DI;
    _ = write;

    var noSign = asm.DefineLabel();
    var fixedNotation = asm.DefineLabel();

    asm.Mov(Reg.DX, Reg.BX);
    asm.Add(Reg.DX, Reg.CX);
    asm.Push(Reg.SI);
    asm.Mov(Reg.SI, Imm.OffsetOf(outBuf));

    asm.Test(Reg.DI, Reg.DI);
    asm.Jz(noSign);
    asm.Mov(Mem.Byte(Reg.SI), '-');
    asm.Jmp(asm.Lbl("rt_fd_signdone"));
    asm.MarkLabel(noSign);
    asm.Mov(Mem.Byte(Reg.SI), ' ');
    asm.MarkLabel("rt_fd_signdone");
    asm.Inc(Reg.SI);
    asm.Pop(Reg.DI);

    asm.Cmp(Reg.DX, 1);
    asm.Jl(asm.Lbl("rt_fd_fracmaybe"));
    asm.Cmp(Reg.DX, Reg.BX);
    asm.Jle(fixedNotation);
    asm.Jmp(asm.Lbl("rt_fd_exp"));

    asm.MarkLabel("rt_fd_fracmaybe");
    if (this.EffectiveDialect.IsTurboBasic()) {
      asm.Cmp(Reg.DX, (Imm)0);
      asm.Jl(asm.Lbl("rt_fd_exp"));
    } else if (this.EffectiveDialect.Family() == DialectFamily.Microsoft) {
      asm.Cmp(Reg.BX, (Imm)7);
      asm.Jne(asm.Lbl("rt_fd_fracdbl"));
      asm.Cmp(Reg.DX, (Imm)(-6));
      asm.Jl(asm.Lbl("rt_fd_exp"));
      asm.Jmp(asm.Lbl("rt_fd_fracok"));
      asm.MarkLabel("rt_fd_fracdbl");
      asm.Mov(Reg.AX, Reg.BX);
      asm.Neg(Reg.AX);
      asm.Cmp(Reg.DX, Reg.AX);
      asm.Jl(asm.Lbl("rt_fd_exp"));
      asm.MarkLabel("rt_fd_fracok");
    } else {
      asm.Cmp(Reg.DX, (Imm)(-6));
      asm.Jl(asm.Lbl("rt_fd_exp"));
    }

    asm.Mov(Mem.Byte(Reg.SI), (byte)'.');
    asm.Inc(Reg.SI);
    asm.Push(Reg.CX);
    asm.Mov(Reg.CX, Reg.DX);
    asm.Neg(Reg.CX);
    asm.Jcxz(asm.Lbl("rt_fd_fraczdone"));
    asm.MarkLabel("rt_fd_fraczero");
    asm.Mov(Mem.Byte(Reg.SI), (byte)'0');
    asm.Inc(Reg.SI);
    asm.Loop(asm.Lbl("rt_fd_fraczero"));
    asm.MarkLabel("rt_fd_fraczdone");
    asm.Mov(Reg.CX, Reg.BX);
    asm.MarkLabel("rt_fd_fraccpy");
    asm.Mov(Reg.AL, Mem.Byte(Reg.DI));
    asm.Mov(Mem.Byte(Reg.SI), Reg.AL);
    asm.Inc(Reg.SI);
    asm.Inc(Reg.DI);
    asm.Loop(asm.Lbl("rt_fd_fraccpy"));
    asm.Pop(Reg.CX);
    asm.Jmp(asm.Lbl("rt_fd_trim"));

    asm.MarkLabel(fixedNotation);
    asm.Push(Reg.CX);
    asm.Mov(Reg.CX, Reg.DX);
    asm.MarkLabel("rt_fd_intcopy");
    asm.Mov(Reg.AL, Mem.Byte(Reg.DI));
    asm.Mov(Mem.Byte(Reg.SI), Reg.AL);
    asm.Inc(Reg.SI);
    asm.Inc(Reg.DI);
    asm.Loop(asm.Lbl("rt_fd_intcopy"));
    asm.Mov(Mem.Byte(Reg.SI), (byte)'.');
    asm.Inc(Reg.SI);
    asm.Mov(Reg.CX, Reg.BX);
    asm.Sub(Reg.CX, Reg.DX);
    asm.Jcxz(asm.Lbl("rt_fd_fraccopied"));
    asm.MarkLabel("rt_fd_fraccopy");
    asm.Mov(Reg.AL, Mem.Byte(Reg.DI));
    asm.Mov(Mem.Byte(Reg.SI), Reg.AL);
    asm.Inc(Reg.SI);
    asm.Inc(Reg.DI);
    asm.Loop(asm.Lbl("rt_fd_fraccopy"));
    asm.MarkLabel("rt_fd_fraccopied");
    asm.Pop(Reg.CX);
    asm.MarkLabel("rt_fd_trim");
    asm.Dec(Reg.SI);
    asm.Cmp(Mem.Byte(Reg.SI), (byte)'0');
    asm.Je(asm.Lbl("rt_fd_trim"));
    asm.Cmp(Mem.Byte(Reg.SI), (byte)'.');
    asm.Je(asm.Lbl("rt_fd_pointtrimmed"));
    asm.Inc(Reg.SI);
    asm.MarkLabel("rt_fd_pointtrimmed");
    asm.Mov(Mem.Byte(Reg.SI), (byte)' ');
    asm.Inc(Reg.SI);
    asm.Jmp(asm.Lbl("rt_fd_flush"));

    asm.MarkLabel("rt_fd_exp");
    asm.Mov(Reg.AL, Mem.Byte(Reg.DI));
    asm.Mov(Mem.Byte(Reg.SI), Reg.AL);
    asm.Inc(Reg.SI);
    asm.Inc(Reg.DI);
    asm.Mov(Mem.Byte(Reg.SI), (byte)'.');
    asm.Inc(Reg.SI);
    asm.Push(Reg.CX);
    asm.Mov(Reg.CX, Reg.BX);
    asm.Dec(Reg.CX);
    asm.Jcxz(asm.Lbl("rt_fd_expdigitsdone"));
    asm.MarkLabel("rt_fd_expdigits");
    asm.Mov(Reg.AL, Mem.Byte(Reg.DI));
    asm.Mov(Mem.Byte(Reg.SI), Reg.AL);
    asm.Inc(Reg.SI);
    asm.Inc(Reg.DI);
    asm.Loop(asm.Lbl("rt_fd_expdigits"));
    asm.MarkLabel("rt_fd_expdigitsdone");
    asm.Pop(Reg.CX);
    asm.MarkLabel("rt_fd_exptrim");
    asm.Dec(Reg.SI);
    asm.Cmp(Mem.Byte(Reg.SI), (byte)'0');
    asm.Je(asm.Lbl("rt_fd_exptrim"));
    asm.Cmp(Mem.Byte(Reg.SI), (byte)'.');
    asm.Je(asm.Lbl("rt_fd_exptrimmed"));
    asm.Inc(Reg.SI);
    asm.MarkLabel("rt_fd_exptrimmed");
    if (this.EffectiveDialect.Family() == DialectFamily.Microsoft) {
      asm.Mov(Reg.AL, 'E');
      asm.Cmp(Reg.BX, (Imm)7);
      asm.Je(asm.Lbl("rt_fd_expmark"));
      asm.Mov(Reg.AL, 'D');
      asm.MarkLabel("rt_fd_expmark");
      asm.Mov(Mem.Byte(Reg.SI), Reg.AL);
    } else {
      asm.Mov(Mem.Byte(Reg.SI), (byte)'E');
    }
    asm.Inc(Reg.SI);
    asm.Dec(Reg.DX);
    asm.Mov(Mem.Byte(Reg.SI), (byte)'+');
    asm.Test(Reg.DX, Reg.DX);
    asm.Jns(asm.Lbl("rt_fd_exppos"));
    asm.Mov(Mem.Byte(Reg.SI), (byte)'-');
    asm.Neg(Reg.DX);
    asm.MarkLabel("rt_fd_exppos");
    asm.Inc(Reg.SI);
    asm.Mov(Reg.AX, Reg.DX);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Mov(Reg.BX, 10);
    asm.Xor(Reg.CX, Reg.CX);
    asm.MarkLabel("rt_fd_expdiv");
    asm.Xor(Reg.DX, Reg.DX);
    asm.Div(Reg.BX);
    asm.Push(Reg.DX);
    asm.Inc(Reg.CX);
    asm.Test(Reg.AX, Reg.AX);
    asm.Jnz(asm.Lbl("rt_fd_expdiv"));
    var padWidth = this.EffectiveDialect.IsTurboBasic() ? 3 : this.EffectiveDialect.Family() == DialectFamily.Microsoft ? 2 : 0;
    if (padWidth > 0) {
      asm.Xor(Reg.DX, Reg.DX);
      asm.MarkLabel("rt_fd_exppad");
      asm.Cmp(Reg.CX, (Imm)padWidth);
      asm.Jae(asm.Lbl("rt_fd_exppop"));
      asm.Push(Reg.DX);
      asm.Inc(Reg.CX);
      asm.Jmp(asm.Lbl("rt_fd_exppad"));
    }
    asm.MarkLabel("rt_fd_exppop");
    asm.Pop(Reg.AX);
    asm.Add(Reg.AL, (byte)'0');
    asm.Mov(Mem.Byte(Reg.SI), Reg.AL);
    asm.Inc(Reg.SI);
    asm.Loop(asm.Lbl("rt_fd_exppop"));
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Mov(Mem.Byte(Reg.SI), (byte)' ');
    asm.Inc(Reg.SI);

    asm.MarkLabel("rt_fd_flush");
    asm.Mov(Reg.CX, Reg.SI);
    asm.Mov(Reg.SI, Imm.OffsetOf(this._numBuffer));
    asm.Sub(Reg.CX, Reg.SI);
    asm.Call(this.PrintStr);
    asm.Ret();
  }
}
