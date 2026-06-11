using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

/// <summary>
/// 64-bit (QUAD) integer helpers. Values normally ride the x87 stack; for
/// \, MOD and the bitwise operators they are spilled into the 8-byte memory
/// cells rt_q0 (left) / rt_q1 (right) and processed as four words.
/// All routines: rt_q0 = rt_q0 OP rt_q1, registers preserved.
/// </summary>
public sealed partial class DosRuntime {

  public Label QuadDiv { get; private set; } = null!;
  public Label QuadMod { get; private set; } = null!;
  public Label QuadAnd { get; private set; } = null!;
  public Label QuadOr { get; private set; } = null!;
  public Label QuadXor { get; private set; } = null!;
  public Label QuadNot { get; private set; } = null!;
  public Label QuadEqv { get; private set; } = null!;
  public Label QuadImp { get; private set; } = null!;

  private void EmitQuadProcedures(Assembler asm) {
    this.EmitFixHelpers(asm);
    var q0 = asm.Lbl("rt_q0");
    var q1 = asm.Lbl("rt_q1");

    // ---- word-wise bitwise family ------------------------------------------
    void EmitWordwise(string name, Action<Mem, Reg> op, bool notLeftFirst = false, bool notResult = false) {
      asm.MarkLabel(name);
      asm.Push(Reg.AX);
      for (var w = 0; w < 8; w += 2) {
        asm.Mov(Reg.AX, Mem.Word(q1, w));
        if (notLeftFirst) {
          asm.Not(Mem.Word(q0, w));
        }
        op(Mem.Word(q0, w), Reg.AX);
        if (notResult)
          asm.Not(Mem.Word(q0, w));
      }
      asm.Pop(Reg.AX);
      asm.Ret();
    }

    this.QuadAnd = asm.Lbl("rt_qand");
    EmitWordwise("rt_qand", (m, r) => asm.And(m, r));
    this.QuadOr = asm.Lbl("rt_qor");
    EmitWordwise("rt_qor", (m, r) => asm.Or(m, r));
    this.QuadXor = asm.Lbl("rt_qxor");
    EmitWordwise("rt_qxor", (m, r) => asm.Xor(m, r));
    this.QuadEqv = asm.Lbl("rt_qeqv");
    EmitWordwise("rt_qeqv", (m, r) => asm.Xor(m, r), notResult: true);
    this.QuadImp = asm.Lbl("rt_qimp");
    EmitWordwise("rt_qimp", (m, r) => asm.Or(m, r), notLeftFirst: true);

    this.QuadNot = asm.MarkLabel("rt_qnot");
    for (var w = 0; w < 8; w += 2)
      asm.Not(Mem.Word(q0, w));
    asm.Ret();

    // ---- signed 64 / 64 division --------------------------------------------
    // shared core: |q0| / |q1| -> quotient in q0, remainder in q2; SI bit0 set
    // when the quotient must be negated, bit1 when the remainder must be.
    var q2 = asm.Lbl("rt_q2");

    void EmitNegate(Label cell) { // two's complement of a 4-word cell
      asm.Not(Mem.Word(cell, 0));
      asm.Not(Mem.Word(cell, 2));
      asm.Not(Mem.Word(cell, 4));
      asm.Not(Mem.Word(cell, 6));
      asm.Add(Mem.Word(cell, 0), (Imm)1);
      asm.Adc(Mem.Word(cell, 2), (Imm)0);
      asm.Adc(Mem.Word(cell, 4), (Imm)0);
      asm.Adc(Mem.Word(cell, 6), (Imm)0);
    }

    asm.MarkLabel("rt_qdivcore");
    {
      var leftPositive = asm.DefineLabel();
      var rightPositive = asm.DefineLabel();
      var loop = asm.DefineLabel();
      var next = asm.DefineLabel();
      var subtract = asm.DefineLabel();

      asm.Xor(Reg.SI, Reg.SI);
      asm.Test(Mem.Word(q0, 6), (Imm)0x8000);
      asm.Jz(leftPositive);
      asm.Mov(Reg.SI, 3);                 // negative dividend: flip quotient and remainder
      EmitNegate(q0);
      asm.MarkLabel(leftPositive);
      asm.Test(Mem.Word(q1, 6), (Imm)0x8000);
      asm.Jz(rightPositive);
      asm.Xor(Reg.SI, 1);                 // negative divisor flips just the quotient
      EmitNegate(q1);
      asm.MarkLabel(rightPositive);

      // q2 (remainder) = 0; 64 restoring-division iterations, quotient builds in q0
      asm.Mov(Mem.Word(q2, 0), (Imm)0);
      asm.Mov(Mem.Word(q2, 2), (Imm)0);
      asm.Mov(Mem.Word(q2, 4), (Imm)0);
      asm.Mov(Mem.Word(q2, 6), (Imm)0);
      asm.Mov(Reg.CX, 64);
      asm.MarkLabel(loop);
      asm.Shl(Mem.Word(q0, 0), 1);        // shift remainder:dividend left one bit
      asm.Rcl(Mem.Word(q0, 2), 1);
      asm.Rcl(Mem.Word(q0, 4), 1);
      asm.Rcl(Mem.Word(q0, 6), 1);
      asm.Rcl(Mem.Word(q2, 0), 1);
      asm.Rcl(Mem.Word(q2, 2), 1);
      asm.Rcl(Mem.Word(q2, 4), 1);
      asm.Rcl(Mem.Word(q2, 6), 1);
      // compare remainder with divisor, high word first
      asm.Mov(Reg.AX, Mem.Word(q2, 6));
      asm.Cmp(Reg.AX, Mem.Word(q1, 6));
      asm.Jb(next);
      asm.Ja(subtract);
      asm.Mov(Reg.AX, Mem.Word(q2, 4));
      asm.Cmp(Reg.AX, Mem.Word(q1, 4));
      asm.Jb(next);
      asm.Ja(subtract);
      asm.Mov(Reg.AX, Mem.Word(q2, 2));
      asm.Cmp(Reg.AX, Mem.Word(q1, 2));
      asm.Jb(next);
      asm.Ja(subtract);
      asm.Mov(Reg.AX, Mem.Word(q2, 0));
      asm.Cmp(Reg.AX, Mem.Word(q1, 0));
      asm.Jb(next);
      asm.MarkLabel(subtract);
      asm.Mov(Reg.AX, Mem.Word(q1, 0));
      asm.Sub(Mem.Word(q2, 0), Reg.AX);
      asm.Mov(Reg.AX, Mem.Word(q1, 2));
      asm.Sbb(Mem.Word(q2, 2), Reg.AX);
      asm.Mov(Reg.AX, Mem.Word(q1, 4));
      asm.Sbb(Mem.Word(q2, 4), Reg.AX);
      asm.Mov(Reg.AX, Mem.Word(q1, 6));
      asm.Sbb(Mem.Word(q2, 6), Reg.AX);
      asm.Or(Mem.Word(q0, 0), (Imm)1);    // quotient bit (just shifted in as 0)
      asm.MarkLabel(next);
      var coreDone = asm.DefineLabel();   // the body exceeds LOOP's 8-bit range
      asm.Dec(Reg.CX);
      asm.Jz(coreDone);
      asm.Jmp(loop);
      asm.MarkLabel(coreDone);
      asm.Ret();
    }

    this.QuadDiv = asm.MarkLabel("rt_qdiv");
    {
      var done = asm.DefineLabel();
      asm.Push(Reg.AX);
      asm.Push(Reg.CX);
      asm.Push(Reg.SI);
      asm.Call(asm.Lbl("rt_qdivcore"));
      asm.Test(Reg.SI, (Imm)1);
      asm.Jz(done);
      EmitNegate(q0);
      asm.MarkLabel(done);
      asm.Pop(Reg.SI);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.AX);
      asm.Ret();
    }

    this.QuadMod = asm.MarkLabel("rt_qmod");
    {
      var done = asm.DefineLabel();
      asm.Push(Reg.AX);
      asm.Push(Reg.CX);
      asm.Push(Reg.SI);
      asm.Call(asm.Lbl("rt_qdivcore"));
      // remainder (q2) -> q0, sign of the dividend
      asm.Mov(Reg.AX, Mem.Word(q2, 0));
      asm.Mov(Mem.Word(q0, 0), Reg.AX);
      asm.Mov(Reg.AX, Mem.Word(q2, 2));
      asm.Mov(Mem.Word(q0, 2), Reg.AX);
      asm.Mov(Reg.AX, Mem.Word(q2, 4));
      asm.Mov(Mem.Word(q0, 4), Reg.AX);
      asm.Mov(Reg.AX, Mem.Word(q2, 6));
      asm.Mov(Mem.Word(q0, 6), Reg.AX);
      asm.Test(Reg.SI, (Imm)2);
      asm.Jz(done);
      EmitNegate(q0);
      asm.MarkLabel(done);
      asm.Pop(Reg.SI);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.AX);
      asm.Ret();
    }
  }

  public Label FixDown { get; private set; } = null!;
  public Label FixUp { get; private set; } = null!;

  /// <summary>
  /// FIX scaling: rt_fixdn divides ST0 by 10^pbvFixDigits (load path),
  /// rt_fixup multiplies and rounds to the nearest integer (store path).
  /// </summary>
  private void EmitFixHelpers(Assembler asm) {
    void EmitScale(bool divide) {
      var apply = asm.DefineLabel();
      var loop = asm.DefineLabel();
      asm.Push(Reg.CX);
      asm.Mov(Reg.CL, Mem.Byte(asm.Lbl("rt_pbv_fixdigits")));
      asm.Xor(Reg.CH, Reg.CH);
      asm.Fld1();
      asm.Jcxz(apply);
      asm.MarkLabel(loop);
      asm.Fmul(Mem.Qword(asm.Lbl("rt_const_ten_m64")));
      asm.Loop(loop);
      asm.MarkLabel(apply);          // ST0 = 10^digits, ST1 = value
      if (divide)
        asm.Fdivp();
      else {
        asm.Fmulp();
        asm.Frndint();               // round-to-nearest (default control word)
      }
      asm.Pop(Reg.CX);
      asm.Ret();
    }

    this.FixDown = asm.MarkLabel("rt_fixdn");
    EmitScale(divide: true);
    this.FixUp = asm.MarkLabel("rt_fixup");
    EmitScale(divide: false);
  }

  private void EmitQuadData(Assembler asm) {
    asm.Align(2);
    asm.MarkLabel("rt_q0");
    asm.Db(new byte[8]);
    asm.MarkLabel("rt_q1");
    asm.Db(new byte[8]);
    asm.MarkLabel("rt_q2");
    asm.Db(new byte[8]);
  }
}
