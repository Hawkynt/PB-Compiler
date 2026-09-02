using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

public sealed partial class SoftwareX87Engine {
  private void EmitExp2MinusOne() {
    this.CopySlotToScratch(0, ScratchA);
    var finite = this._asm.DefineLabel();
    var zero = this._asm.DefineLabel();
    var nan = this._asm.DefineLabel();
    var done = this._asm.DefineLabel();
    this.BranchIfClass(ScratchA, ClassNaN, nan);
    this.BranchIfClass(ScratchA, ClassZero, zero);
    this.BranchIfClass(ScratchA, ClassFinite, finite);
    // Intel defines F2XM1 only for [-1,+1]. Leave out-of-domain infinities unchanged rather than
    // inventing host-library behavior for an architecturally undefined input.
    this._asm.Jmp(done);

    this._asm.MarkLabel(nan);
    this.PropagateNaN(ScratchA, ScratchC);
    this.CopyScratchToSlot(ScratchC, 0);
    this._asm.Jmp(done);
    this._asm.MarkLabel(zero);
    this._asm.Jmp(done);

    this._asm.MarkLabel(finite);
    this.CopyScratch(ScratchA, ScratchC);
    this.AbsCanonical(ScratchC);
    this.LoadCanonicalInteger(ScratchB, 1);
    var inRange = this._asm.DefineLabel();
    var outOfRange = this._asm.DefineLabel();
    // 1 >= |x| means the architectural F2XM1 domain.
    this.EmitScratchMagnitudeCompare(ScratchB, ScratchC, inRange, outOfRange);
    this._asm.MarkLabel(outOfRange);
    this._asm.Jmp(done);

    this._asm.MarkLabel(inRange);
    this.LoadLn2(ScratchB);
    this.MathMul(ScratchA, ScratchB, ScratchC); // t = x ln 2
    this.CopyScratch(ScratchC, ScratchD);       // term = t
    this.CopyScratch(ScratchC, Math3);          // sum = t
    for (var n = 2; n <= 22; ++n) {
      this.MathMul(ScratchD, ScratchC, Math4);
      this.LoadCanonicalInteger(Math5, n);
      this.MathDiv(Math4, Math5, ScratchD);
      this.MathAdd(Math3, ScratchD, Math3);
    }
    this.CopyScratchToSlot(Math3, 0);
    this._asm.MarkLabel(done);
  }

  private void EmitYLog2X(bool plusOne) {
    this.CopySlotToScratch(0, ScratchA); // x
    this.CopySlotToScratch(1, ScratchB); // y
    if (plusOne) {
      this.LoadCanonicalInteger(ScratchC, 1);
      this.MathAdd(ScratchA, ScratchC, ScratchA);
    }
    this.EmitLog2(ScratchA, ScratchC);
    this.MathMul(ScratchB, ScratchC, ScratchD);
    this.CopyScratchToSlot(ScratchD, 1);
    this.EmitPop();
  }

  /// <summary>
  /// Computes log2(x) without host floating point. The mantissa uses
  /// ln(m)=2*(z+z^3/3+...), z=(m-1)/(m+1); m in [1,2) makes |z| <= 1/3.
  /// </summary>
  private void EmitLog2(int source, int result) {
    var finite = this._asm.DefineLabel();
    var zero = this._asm.DefineLabel();
    var infinity = this._asm.DefineLabel();
    var nan = this._asm.DefineLabel();
    var invalid = this._asm.DefineLabel();
    var done = this._asm.DefineLabel();
    this.BranchByClass(source, finite, zero, infinity, nan);

    this._asm.MarkLabel(nan);
    this.PropagateNaN(source, result);
    this._asm.Jmp(done);

    this._asm.MarkLabel(zero);
    this.LoadCanonical(result, 0, (ushort)(SignMask | ClassInfinity), 0x8000000000000000UL);
    this.RaiseException(StatusZeroDivide);
    this._asm.Jmp(done);

    this._asm.MarkLabel(infinity);
    this._asm.Test(this.Scratch(source + Meta, OperandSize.Word), SignMask);
    this._asm.J(Condition.NotEqual, invalid);
    this.CopyScratch(source, result);
    this._asm.Jmp(done);

    this._asm.MarkLabel(finite);
    this._asm.Test(this.Scratch(source + Meta, OperandSize.Word), SignMask);
    this._asm.J(Condition.NotEqual, invalid);
    this.CopyScratch(source, ScratchC);
    this._asm.Mov(Reg.AX, this.Scratch(source + Exponent, OperandSize.Word));
    this._asm.Mov(this.Scratch(QuadrantScratch, OperandSize.Word), Reg.AX);
    this._asm.Mov(this.Scratch(ScratchC + Exponent, OperandSize.Word), 0); // m in [1,2)

    this.LoadCanonicalInteger(ScratchD, 1);
    this.MathSub(ScratchC, ScratchD, Math3);
    this.MathAdd(ScratchC, ScratchD, Math4);
    this.MathDiv(Math3, Math4, Math5);       // z
    this.MathMul(Math5, Math5, ScratchD);    // z^2
    this.CopyScratch(Math5, Math3);          // current odd power
    this.CopyScratch(Math5, Math4);          // series sum
    for (var n = 3; n <= 43; n += 2) {
      this.MathMul(Math3, ScratchD, Math3);
      this.LoadCanonicalInteger(ScratchC, n);
      this.MathDiv(Math3, ScratchC, Math5);
      this.MathAdd(Math4, Math5, Math4);
    }
    this.LoadCanonicalInteger(ScratchC, 2);
    this.MathMul(Math4, ScratchC, Math4);    // ln(m)
    this.LoadLn2(ScratchC);
    this.MathDiv(Math4, ScratchC, Math4);    // log2(m)

    // Add the exact integral exponent e.
    this._asm.Mov(Reg.AX, this.Scratch(QuadrantScratch, OperandSize.Word));
    this._asm.Mov(this.Scratch(IntScratch, OperandSize.Word), Reg.AX);
    this._asm.Mov(Reg.DX, Reg.AX);
    this._asm.Sar(Reg.DX, 15);
    this._asm.Mov(this.Scratch(IntScratch + 2, OperandSize.Word), Reg.DX);
    this._asm.Mov(this.Scratch(IntScratch + 4, OperandSize.Word), Reg.DX);
    this._asm.Mov(this.Scratch(IntScratch + 6, OperandSize.Word), Reg.DX);
    this.ConvertIntegerToCanonical(this.Scratch(IntScratch, OperandSize.Qword), 64, ScratchC);
    this.MathAdd(Math4, ScratchC, result);
    this._asm.Jmp(done);

    this._asm.MarkLabel(invalid);
    this.EmitIndefiniteNaN(result);
    this.RaiseException(StatusInvalid);
    this._asm.MarkLabel(done);
  }
}
