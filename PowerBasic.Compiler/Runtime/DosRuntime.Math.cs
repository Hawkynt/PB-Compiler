using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

public sealed partial class DosRuntime {
  private void EmitPow(Assembler asm) {
    this.Pow = asm.MarkLabel("rt_pow");
    asm.Fxch();
    asm.Fld1();
    asm.Fxch();
    asm.Fyl2x();
    asm.Fmulp();
    asm.MarkLabel("rt_pow2");
    asm.Fld(St.St0);
    asm.Frndint();
    asm.Fxch();
    asm.Fsub(St.St0, St.St1);
    asm.F2xm1();
    asm.Fld1();
    asm.Faddp();
    asm.Fscale();
    asm.Fstp(St.St1);
    asm.Ret();
  }

  private void EmitRounding(Assembler asm) {
    void Emit(string label, int rcBits) {
      asm.MarkLabel(label);
      asm.Fnstcw(Mem.Word(this._scratch, 12));
      asm.Mov(Reg.AX, Mem.Word(this._scratch, 12));
      asm.Or(Reg.AX, 0x0C00);
      if (rcBits != 0x0C00) {
        asm.And(Reg.AX, ~0x0C00 & 0xFFFF);
        asm.Or(Reg.AX, rcBits);
      }
      asm.Mov(Mem.Word(this._scratch, 14), Reg.AX);
      asm.Fldcw(Mem.Word(this._scratch, 14));
      asm.Frndint();
      asm.Fldcw(Mem.Word(this._scratch, 12));
      asm.Ret();
    }

    this.Floor = asm.Lbl("rt_floor");
    Emit("rt_floor", 0x0400);
    this.Trunc = asm.Lbl("rt_trunc");
    Emit("rt_trunc", 0x0C00);
    this.Ceil = asm.Lbl("rt_ceil");
    Emit("rt_ceil", 0x0800);

    this.Round = asm.MarkLabel("rt_round");
    {
      var scaleLoop = asm.DefineLabel();
      var scaled = asm.DefineLabel();
      var wasPositive = asm.DefineLabel();
      asm.Push(Reg.AX);

      asm.Fld1();
      asm.Jcxz(scaled);
      asm.MarkLabel(scaleLoop);
      asm.Fld(Mem.Qword(asm.Lbl("rt_ten")));
      asm.Fmulp(St.St1);
      asm.Loop(scaleLoop);
      asm.MarkLabel(scaled);

      asm.Fxch();
      asm.Fmul(St.St0, St.St1);
      asm.Ftst();
      asm.Fstsw(Mem.Word(this._scratch, 16));
      asm.Mov(Reg.AX, Mem.Word(this._scratch, 16));
      asm.Sahf();
      asm.Pushf();

      asm.Fabs();
      asm.Fld(Mem.Qword(asm.Lbl("rt_half")));
      asm.Faddp(St.St1);
      asm.Call(asm.Lbl("rt_trunc"));

      asm.Popf();
      asm.Jnc(wasPositive);
      asm.Fchs();
      asm.MarkLabel(wasPositive);

      asm.Fdiv(St.St0, St.St1);
      asm.Fxch();
      asm.Fstp(St.St0);
      asm.Pop(Reg.AX);
      asm.Ret();
    }

    asm.MarkLabel("rt_ten");
    asm.Dq(10.0);
    asm.MarkLabel("rt_l32max");
    asm.Dq(2147483647.0);
    asm.MarkLabel("rt_l32min");
    asm.Dq(-2147483648.0);
    asm.MarkLabel("rt_half");
    asm.Dq(0.5);
  }

  private void EmitLongHelpers(Assembler asm) {
    this.LongMul = asm.MarkLabel("rt_lmul");
    asm.Push(Reg.SI);
    asm.Mov(Reg.SI, Reg.AX);
    asm.Mov(Reg.AX, Reg.DX);
    asm.Mul(Reg.BX);
    asm.Xchg(Reg.AX, Reg.CX);
    asm.Mul(Reg.SI);
    asm.Add(Reg.CX, Reg.AX);
    asm.Mov(Reg.AX, Reg.SI);
    asm.Mul(Reg.BX);
    asm.Add(Reg.DX, Reg.CX);
    asm.Pop(Reg.SI);
    asm.Ret();

    this.LongDiv = asm.MarkLabel("rt_ldiv");
    this.EmitLongDivide(asm, wantRemainder: false);

    this.LongMod = asm.MarkLabel("rt_lmod");
    this.EmitLongDivide(asm, wantRemainder: true);

    this.LongDivU = asm.MarkLabel("rt_uldiv");
    asm.Mov(Mem.Word(this._scratch), Reg.BX);
    asm.Or(Mem.Word(this._scratch), Reg.CX);
    asm.Jnz(asm.Lbl("rt_uldiv_ok"));
    asm.Mov(Reg.AX, 11);
    asm.Call(asm.Lbl("rt_raise"));
    asm.MarkLabel("rt_uldiv_ok");
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.BP);
    asm.Xor(Reg.SI, Reg.SI);
    asm.Jmp(asm.Lbl("rt_ld_core"));

    this.LongModU = asm.MarkLabel("rt_ulmod");
    asm.Mov(Mem.Word(this._scratch), Reg.BX);
    asm.Or(Mem.Word(this._scratch), Reg.CX);
    asm.Jnz(asm.Lbl("rt_ulmod_ok"));
    asm.Mov(Reg.AX, 11);
    asm.Call(asm.Lbl("rt_raise"));
    asm.MarkLabel("rt_ulmod_ok");
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.BP);
    asm.Xor(Reg.SI, Reg.SI);
    asm.Jmp(asm.Lbl("rt_lm_core"));
  }

  private void EmitLongDivide(Assembler asm, bool wantRemainder) {
    var suffix = wantRemainder ? "m" : "d";

    var divisorOk = asm.DefineLabel();
    asm.Mov(Mem.Word(this._scratch), Reg.BX);
    asm.Or(Mem.Word(this._scratch), Reg.CX);
    asm.Jnz(divisorOk);
    asm.Mov(Reg.AX, 11);
    asm.Call(asm.Lbl("rt_raise"));
    asm.MarkLabel(divisorOk);

    // Any normalized 386-or-newer target gets the native 32-bit divide, including 586+ targets
    // that the old exact-string Cpu386 test accidentally classified as pre-386.
    if (this.Target.Has32BitGeneralPurpose) {
      var legacy = asm.DefineLabel();
      var fast = asm.DefineLabel();
      asm.Mov(Mem.Word(this._scratch), Reg.BX);
      asm.Mov(Mem.Word(this._scratch, 2), Reg.CX);
      asm.Mov(Reg.EBX, Mem.Dword(this._scratch));
      asm.Mov(Mem.Word(this._scratch), Reg.AX);
      asm.Mov(Mem.Word(this._scratch, 2), Reg.DX);
      asm.Mov(Reg.EAX, Mem.Dword(this._scratch));
      asm.Or(Reg.EBX, Reg.EBX);
      asm.Jz(legacy);
      asm.Cmp(Reg.EBX, (Imm)(-1));
      asm.Jne(fast);
      asm.Cmp(Reg.EAX, Mem.Dword(asm.Lbl("rt_const_min32")));
      asm.Je(legacy);
      asm.MarkLabel(fast);
      asm.Cdq();
      asm.Idiv(Reg.EBX);
      if (wantRemainder)
        asm.Mov(Reg.EAX, Reg.EDX);
      asm.Mov(Mem.Dword(this._scratch), Reg.EAX);
      asm.Mov(Reg.AX, Mem.Word(this._scratch));
      asm.Mov(Reg.DX, Mem.Word(this._scratch, 2));
      if (!wantRemainder) {
        asm.Mov(Mem.Dword(this._scratch), Reg.EDX);
        asm.Mov(Reg.BX, Mem.Word(this._scratch));
        asm.Mov(Reg.CX, Mem.Word(this._scratch, 2));
      }
      asm.Ret();
      asm.MarkLabel(legacy);
      asm.Mov(Reg.AX, Mem.Word(this._scratch));
      asm.Mov(Reg.DX, Mem.Word(this._scratch, 2));
    }

    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.BP);

    asm.Xor(Reg.SI, Reg.SI);
    asm.Test(Reg.DX, Reg.DX);
    asm.Jns(asm.Lbl($"rt_l{suffix}_p1"));
    asm.Mov(Reg.SI, 3);
    asm.Not(Reg.DX);
    asm.Not(Reg.AX);
    asm.Add(Reg.AX, 1);
    asm.Adc(Reg.DX, (Imm)0);
    asm.MarkLabel($"rt_l{suffix}_p1");
    asm.Test(Reg.CX, Reg.CX);
    asm.Jns(asm.Lbl($"rt_l{suffix}_p2"));
    asm.Xor(Reg.SI, 1);
    asm.Not(Reg.CX);
    asm.Not(Reg.BX);
    asm.Add(Reg.BX, 1);
    asm.Adc(Reg.CX, (Imm)0);
    asm.MarkLabel($"rt_l{suffix}_p2");

    asm.MarkLabel($"rt_l{suffix}_core");
    asm.Xor(Reg.DI, Reg.DI);
    asm.Xor(Reg.BP, Reg.BP);
    asm.Push(Reg.CX);
    asm.Push(Reg.BX);
    asm.Mov(Reg.CX, 32);
    asm.MarkLabel($"rt_l{suffix}_loop");
    asm.Shl(Reg.AX, 1);
    asm.Rcl(Reg.DX, 1);
    asm.Rcl(Reg.BP, 1);
    asm.Rcl(Reg.DI, 1);
    asm.Mov(Reg.BX, Reg.SP);
    asm.Cmp(Reg.DI, Mem.Word(Reg.BX, 2));
    asm.Jb(asm.Lbl($"rt_l{suffix}_next"));
    asm.Ja(asm.Lbl($"rt_l{suffix}_sub"));
    asm.Cmp(Reg.BP, Mem.Word(Reg.BX));
    asm.Jb(asm.Lbl($"rt_l{suffix}_next"));
    asm.MarkLabel($"rt_l{suffix}_sub");
    asm.Sub(Reg.BP, Mem.Word(Reg.BX));
    asm.Sbb(Reg.DI, Mem.Word(Reg.BX, 2));
    asm.Or(Reg.AX, 1);
    asm.MarkLabel($"rt_l{suffix}_next");
    asm.Loop(asm.Lbl($"rt_l{suffix}_loop"));
    asm.Pop(Reg.BX);
    asm.Pop(Reg.CX);

    if (wantRemainder) {
      asm.Mov(Reg.AX, Reg.BP);
      asm.Mov(Reg.DX, Reg.DI);
      asm.Test(Reg.SI, 2);
      asm.Jz(asm.Lbl($"rt_l{suffix}_done"));
    } else {
      asm.Test(Reg.SI, 1);
      asm.Jz(asm.Lbl($"rt_l{suffix}_done"));
    }
    asm.Not(Reg.DX);
    asm.Not(Reg.AX);
    asm.Add(Reg.AX, 1);
    asm.Adc(Reg.DX, (Imm)0);
    asm.MarkLabel($"rt_l{suffix}_done");
    asm.Pop(Reg.BP);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Ret();
  }

  public void EmitConstants(Assembler asm) {
    asm.MarkLabel("rt_crlf");
    asm.Db(0x0D, 0x0A);
    asm.MarkLabel("rt_spaces");
    asm.Db(new string(' ', 16));
    this.EmitErrorMessages(asm);
    asm.Align(2);
    asm.MarkLabel("rt_const_ten_m64");
    asm.Dq(10.0);
    asm.MarkLabel("rt_const_half_m64");
    asm.Dq(0.5);
    asm.MarkLabel("rt_const_pi2_m64");
    asm.Dq(Math.PI / 2);
    asm.MarkLabel("rt_const_pi4_m64");
    asm.Dq(Math.PI / 4);
    asm.MarkLabel("rt_const_65536");
    asm.Dq(65536.0);
    asm.MarkLabel("rt_const_2p31");
    asm.Dq(2147483648.0);
    asm.MarkLabel("rt_const_2p32");
    asm.Dq(4294967296.0);
    asm.MarkLabel("rt_const_min32");
    asm.Dd(0x80000000u);
    this.EmitMiscConstants(asm);
  }
}
