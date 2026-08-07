using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

public sealed partial class DosRuntime {

  /// <summary>8087 sine/cosine: angle on ST(0), BL selects (0 sine, 1 cosine); result on ST(0).</summary>
  public Label Trig { get; private set; } = null!;

  /// <summary>8087 sine: angle on ST(0), result on ST(0). A one-instruction door onto <see cref="Trig"/>.</summary>
  public Label Sin { get; private set; } = null!;

  /// <summary>8087 cosine: angle on ST(0), result on ST(0).</summary>
  public Label Cos { get; private set; } = null!;

  /// <summary>
  /// <c>rt_trig</c> - sine and cosine using only instructions an 8087 has. Entry: the angle on
  /// ST(0), BL = 0 for sine and 1 for cosine. Exit: the result on ST(0).
  ///
  /// FSIN and FCOS are 80387 instructions. Emitting them for an image whose declared target is an
  /// 8086 is what this replaces, and it is also what the oracle does NOT do: genuine PBC 3.5
  /// compiling SIN, COS and TAN in one program emits zero FSIN, zero FCOS and exactly ONE FPTAN -
  /// one shared routine that reduces the argument and derives both from the tangent.
  ///
  /// The 8087's FPTAN is defined only for 0 &lt;= x &lt;= pi/4 and leaves the tangent as the RATIO of
  /// two stack entries rather than a single value, so the reduction is not optional and the divide
  /// is part of reading the answer:
  ///
  ///   |x| is reduced modulo pi/2 by FPREM, whose condition codes carry the low bits of the
  ///   quotient - C3 and C1 are the quadrant, which is exactly what they are there for. FPREM is a
  ///   PARTIAL remainder and may need repeating; C2 says so, hence the loop.
  ///
  ///   A remainder above pi/4 is folded to pi/2 - r, and sine and cosine swap roles - the identity
  ///   that keeps FPTAN inside its domain.
  ///
  ///   FPTAN then gives Y and X with tan = Y/X. Their hypotenuse yields BOTH functions at once:
  ///   sin = Y/h and cos = X/h. That is why one FPTAN serves all three entry points.
  ///
  ///   The quadrant picks the signs, and sine alone carries the sign of the argument, being odd
  ///   where cosine is even.
  /// </summary>
  private void EmitTrig(Assembler asm) {
    // Two named doors onto one routine, so a caller needs no register protocol: the IR back end's
    // math-sequence table names a helper and cannot set BL itself, and the direct emitter reads
    // better without the convention either.
    var body = asm.DefineLabel();
    this.Sin = asm.MarkLabel("rt_sin");
    asm.Xor(Reg.BL, Reg.BL);
    asm.Jmp(body);
    this.Cos = asm.MarkLabel("rt_cos");
    asm.Mov(Reg.BL, 1);
    this.Trig = asm.MarkLabel("rt_trig");
    asm.MarkLabel(body);
    var abs = asm.DefineLabel();
    var prem = asm.DefineLabel();
    var noFold = asm.DefineLabel();
    var wantCos = asm.DefineLabel();
    var divide = asm.DefineLabel();
    var quadDone = asm.DefineLabel();
    var noNegate = asm.DefineLabel();

    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Xor(Reg.BH, Reg.BH);                       // BH = negate-the-result flag
    asm.Mov(Reg.DH, Reg.BL);                       // DH = the function ASKED for; BL gets swapped below

    // sine is odd and cosine is even: work on |x| and remember whether sine owes a sign
    asm.Ftst();
    asm.FstswAx();
    asm.Sahf();
    asm.Jnc(abs);                                  // C0 clear -> x >= 0
    asm.Fabs();
    asm.Or(Reg.BL, Reg.BL);
    asm.Jnz(abs);                                  // cosine: no sign to carry
    asm.Mov(Reg.BH, 1);
    asm.MarkLabel(abs);

    // r = |x| mod pi/2, with the quadrant left in the status word
    asm.Fld(Mem.Qword(asm.Lbl("rt_const_pi2_m64")));
    asm.Fxch();                                    // ST0 = |x|, ST1 = pi/2
    asm.MarkLabel(prem);
    asm.Fprem();
    asm.FstswAx();
    asm.Test(Reg.AH, (Imm)0x04);                   // C2: the reduction is incomplete
    asm.Jnz(prem);

    // quadrant = (C3 << 1) | C1, from AH bits 6 and 1
    asm.Mov(Reg.CL, Reg.AH);
    asm.Shr(Reg.CL, 1);
    asm.And(Reg.CL, (Imm)1);                       // C1 -> bit 0
    asm.Mov(Reg.DL, Reg.AH);
    asm.Shr(Reg.DL, 6);
    asm.And(Reg.DL, (Imm)1);                       // C3
    asm.Shl(Reg.DL, 1);
    asm.Or(Reg.CL, Reg.DL);                        // CL = quadrant 0..3
    asm.Fstp(St.St1);                              // drop pi/2, keep r

    // fold r > pi/4 to pi/2 - r and swap the two functions
    asm.Fld(Mem.Qword(asm.Lbl("rt_const_pi4_m64")));
    asm.Fcomp();                                   // compare pi/4 with r, pop
    asm.FstswAx();
    asm.Sahf();
    asm.Jnc(noFold);                               // pi/4 >= r: already in range
    asm.Fld(Mem.Qword(asm.Lbl("rt_const_pi2_m64")));
    asm.Fsubrp();                                  // ST0 = pi/2 - r
    asm.Xor(Reg.BL, (Imm)1);
    asm.MarkLabel(noFold);

    // the quadrant decides which function the folded angle actually answers, and the sign.
    // sin: q0 -> +sin, q1 -> +cos, q2 -> -sin, q3 -> -cos ; cos is the same table rotated
    asm.Test(Reg.CL, (Imm)1);
    asm.Jz(quadDone);
    asm.Xor(Reg.BL, (Imm)1);                       // odd quadrant: the roles swap again
    asm.MarkLabel(quadDone);
    // The sign belongs to the function that was ASKED for, not the one the folding left us
    // computing: sine is negative in quadrants 2 and 3, cosine in 1 and 2. Both are (q >> 1) & 1
    // once cosine's quadrant is shifted by one, which is why DH is consulted and not BL.
    var sinSign = asm.DefineLabel();
    asm.Mov(Reg.DL, Reg.CL);
    asm.Or(Reg.DH, Reg.DH);
    asm.Jz(sinSign);
    asm.Inc(Reg.DL);                               // cosine: negative for q+1 in {2, 3}
    asm.MarkLabel(sinSign);
    asm.Shr(Reg.DL, 1);
    asm.And(Reg.DL, (Imm)1);
    asm.Xor(Reg.BH, Reg.DL);

    asm.Fptan();                                   // ST0 = X, ST1 = Y ; tan = Y/X
    asm.Fld(St.St0);                               // X
    asm.Fmul(St.St0, St.St0);                      // X*X
    asm.Fld(St.St2);                               // Y
    asm.Fmul(St.St0, St.St0);                      // Y*Y
    asm.Faddp();                                   // X*X + Y*Y
    asm.Fsqrt();                                   // h        [h, X, Y]

    asm.Or(Reg.BL, Reg.BL);
    asm.Jnz(wantCos);
    asm.Fld(St.St2);                               // sine wants Y
    asm.Jmp(divide);
    asm.MarkLabel(wantCos);
    asm.Fld(St.St1);                               // cosine wants X
    asm.MarkLabel(divide);
    asm.Fdiv(St.St0, St.St1);                      // ST0 = numerator / h
    asm.Fstp(St.St1);                              // drop h, X and Y from under the result
    asm.Fstp(St.St1);
    asm.Fstp(St.St1);

    asm.Or(Reg.BH, Reg.BH);
    asm.Jz(noNegate);
    asm.Fchs();
    asm.MarkLabel(noNegate);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Ret();
  }
}
