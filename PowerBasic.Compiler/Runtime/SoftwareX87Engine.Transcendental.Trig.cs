using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

public sealed partial class SoftwareX87Engine {
  private void EmitTrig(TrigResult resultKind) {
    this.CopySlotToScratch(0, ScratchA);
    var finite = this._asm.DefineLabel();
    var zero = this._asm.DefineLabel();
    var infinity = this._asm.DefineLabel();
    var nan = this._asm.DefineLabel();
    var done = this._asm.DefineLabel();
    this.BranchByClass(ScratchA, finite, zero, infinity, nan);

    this._asm.MarkLabel(nan);
    this.PropagateNaN(ScratchA, ScratchC);
    this.CopyScratchToSlot(ScratchC, 0);
    this._asm.Jmp(done);

    this._asm.MarkLabel(infinity);
    this.EmitIndefiniteNaN(ScratchC);
    this.RaiseException(StatusInvalid);
    this.CopyScratchToSlot(ScratchC, 0);
    this._asm.Jmp(done);

    this._asm.MarkLabel(zero);
    switch (resultKind) {
      case TrigResult.Sin:
      case TrigResult.Tan:
        this.CopyScratchToSlot(ScratchA, 0);
        break;
      case TrigResult.Cos:
        this.LoadCanonicalInteger(ScratchC, 1);
        this.CopyScratchToSlot(ScratchC, 0);
        break;
      case TrigResult.SinCos:
        this.CopyScratch(ScratchA, ScratchC);
        this.LoadCanonicalInteger(ScratchD, 1);
        this.EmitPushEmpty();
        this.CopyScratchToSlot(ScratchC, 1); // sine
        this.CopyScratchToSlot(ScratchD, 0); // cosine
        break;
    }
    this._asm.Jmp(done);

    this._asm.MarkLabel(finite);
    // FSIN/FCOS/FPTAN/FSINCOS decline range reduction for |x| >= 2^63 and set C2.
    this._asm.Cmp(this.Scratch(ScratchA + Exponent, OperandSize.Word), 63);
    var reducible = this._asm.DefineLabel();
    this._asm.J(Condition.Less, reducible);
    this._asm.Or(this.Status, 0x0400);
    this._asm.Jmp(done);

    this._asm.MarkLabel(reducible);
    this.LoadPiOver2(ScratchB);
    this.MathDiv(ScratchA, ScratchB, ScratchC);
    this.RoundCanonicalToInteger(ScratchC, ScratchD, rc: 0); // nearest quadrant
    this.ConvertCanonicalToInteger(ScratchD, this.Scratch(IntScratch, OperandSize.Qword), 64);
    this._asm.Mov(Reg.AX, this.Scratch(IntScratch, OperandSize.Word));
    this._asm.And(Reg.AX, 3);
    this._asm.Mov(this.Scratch(QuadrantScratch, OperandSize.Word), Reg.AX);
    this.MathMul(ScratchD, ScratchB, ScratchC);
    this.MathSub(ScratchA, ScratchC, ScratchA); // reduced r in approximately [-pi/4,+pi/4]

    this.EmitSinCosSeries(ScratchA, ScratchC, ScratchD);
    this.MapSinCosQuadrant(ScratchC, ScratchD);
    switch (resultKind) {
      case TrigResult.Sin:
        this.CopyScratchToSlot(ScratchC, 0);
        break;
      case TrigResult.Cos:
        this.CopyScratchToSlot(ScratchD, 0);
        break;
      case TrigResult.SinCos:
        this.EmitPushEmpty();
        this.CopyScratchToSlot(ScratchC, 1);
        this.CopyScratchToSlot(ScratchD, 0);
        break;
      case TrigResult.Tan:
        this.MathDiv(ScratchC, ScratchD, ScratchC);
        this.CopyScratchToSlot(ScratchC, 0);
        this.EmitPushEmpty();
        this.LoadCanonicalInteger(ScratchD, 1);
        this.CopyScratchToSlot(ScratchD, 0);
        break;
    }
    this._asm.And(this.Status, 0xFBFF);
    this._asm.MarkLabel(done);
  }

  private void EmitSinCosSeries(int x, int sinResult, int cosResult) {
    this.MathMul(x, x, Math3); // x^2
    this.CopyScratch(x, sinResult);
    this.CopyScratch(x, Math4); // current sine term
    this.LoadCanonicalInteger(cosResult, 1);
    this.LoadCanonicalInteger(Math5, 1); // current cosine term
    for (var k = 1; k <= 14; ++k) {
      this.MathMul(Math4, Math3, Math4);
      this.NegateCanonical(Math4);
      this.LoadCanonicalInteger(ScratchB, (2L * k) * (2L * k + 1));
      this.MathDiv(Math4, ScratchB, Math4);
      this.MathAdd(sinResult, Math4, sinResult);

      this.MathMul(Math5, Math3, Math5);
      this.NegateCanonical(Math5);
      this.LoadCanonicalInteger(ScratchB, (2L * k - 1) * (2L * k));
      this.MathDiv(Math5, ScratchB, Math5);
      this.MathAdd(cosResult, Math5, cosResult);
    }
  }

  private void MapSinCosQuadrant(int sinValue, int cosValue) {
    var q0 = this._asm.DefineLabel();
    var q1 = this._asm.DefineLabel();
    var q2 = this._asm.DefineLabel();
    var done = this._asm.DefineLabel();
    this._asm.Mov(Reg.AX, this.Scratch(QuadrantScratch, OperandSize.Word));
    this._asm.Cmp(Reg.AX, 0); this._asm.J(Condition.Equal, q0);
    this._asm.Cmp(Reg.AX, 1); this._asm.J(Condition.Equal, q1);
    this._asm.Cmp(Reg.AX, 2); this._asm.J(Condition.Equal, q2);

    // q=3: sin=-cos(r), cos=sin(r)
    this.CopyScratch(sinValue, Math3);
    this.CopyScratch(cosValue, sinValue);
    this.NegateCanonical(sinValue);
    this.CopyScratch(Math3, cosValue);
    this._asm.Jmp(done);

    this._asm.MarkLabel(q1); // sin=cos(r), cos=-sin(r)
    this.CopyScratch(sinValue, Math3);
    this.CopyScratch(cosValue, sinValue);
    this.CopyScratch(Math3, cosValue);
    this.NegateCanonical(cosValue);
    this._asm.Jmp(done);

    this._asm.MarkLabel(q2);
    this.NegateCanonical(sinValue);
    this.NegateCanonical(cosValue);
    this._asm.Jmp(done);

    this._asm.MarkLabel(q0);
    this._asm.MarkLabel(done);
  }

  /// <summary>FPATAN implements atan2(ST1,ST0), writes ST1, then pops once.</summary>
  private void EmitAtan2() {
    this.CopySlotToScratch(1, ScratchA); // y
    this.CopySlotToScratch(0, ScratchB); // x
    var aNan = this._asm.DefineLabel();
    var bNan = this._asm.DefineLabel();
    var yZero = this._asm.DefineLabel();
    var xZero = this._asm.DefineLabel();
    var yInfinity = this._asm.DefineLabel();
    var xInfinity = this._asm.DefineLabel();
    var ordinary = this._asm.DefineLabel();
    var havePositiveAngle = this._asm.DefineLabel();
    var applyQuadrant = this._asm.DefineLabel();
    var done = this._asm.DefineLabel();

    this.BranchIfClass(ScratchA, ClassNaN, aNan);
    this.BranchIfClass(ScratchB, ClassNaN, bNan);
    this.BranchIfClass(ScratchA, ClassZero, yZero);
    this.BranchIfClass(ScratchB, ClassZero, xZero);
    this.BranchIfClass(ScratchA, ClassInfinity, yInfinity);
    this.BranchIfClass(ScratchB, ClassInfinity, xInfinity);
    this._asm.Jmp(ordinary);

    this._asm.MarkLabel(aNan);
    this.PropagateNaN(ScratchA, Math4);
    this.CopyScratchToSlot(Math4, 1);
    this.EmitPop();
    this._asm.Jmp(done);
    this._asm.MarkLabel(bNan);
    this.PropagateNaN(ScratchB, Math4);
    this.CopyScratchToSlot(Math4, 1);
    this.EmitPop();
    this._asm.Jmp(done);

    this._asm.MarkLabel(yZero);
    this.LoadCanonicalInteger(Math4, 0);
    // Preserve y's signed zero; x's sign below selects zero versus pi.
    this._asm.Mov(Reg.AX, this.Scratch(ScratchA + Meta, OperandSize.Word));
    this._asm.And(Reg.AX, SignMask);
    this._asm.Or(this.Scratch(Math4 + Meta, OperandSize.Word), Reg.AX);
    this._asm.Jmp(applyQuadrant);

    this._asm.MarkLabel(xZero);
    this.LoadPiOver2(Math4);
    this._asm.Jmp(applyQuadrant);

    this._asm.MarkLabel(yInfinity);
    this.BranchIfClass(ScratchB, ClassInfinity, havePositiveAngle);
    this.LoadPiOver2(Math4);
    this._asm.Jmp(applyQuadrant);

    this._asm.MarkLabel(havePositiveAngle); // both magnitudes infinite => pi/4
    this.LoadPiOver4(Math4);
    this._asm.Jmp(applyQuadrant);

    this._asm.MarkLabel(xInfinity);
    this.LoadCanonicalInteger(Math4, 0);
    this._asm.Jmp(applyQuadrant);

    this._asm.MarkLabel(ordinary);
    this.CopyScratch(ScratchA, ScratchC); this.AbsCanonical(ScratchC);
    this.CopyScratch(ScratchB, ScratchD); this.AbsCanonical(ScratchD);
    this.MathDiv(ScratchC, ScratchD, Math3);
    this.EmitAtanPositive(Math3, Math4);

    this._asm.MarkLabel(applyQuadrant);
    // Negative x reflects the positive-magnitude angle through pi.
    this._asm.Test(this.Scratch(ScratchB + Meta, OperandSize.Word), SignMask);
    var xPositive = this._asm.DefineLabel();
    this._asm.J(Condition.Equal, xPositive);
    this.LoadPi(Math3);
    this.MathSub(Math3, Math4, Math4);
    this._asm.MarkLabel(xPositive);
    // y supplies the result sign, including signed-zero quadrants.
    this._asm.Test(this.Scratch(ScratchA + Meta, OperandSize.Word), SignMask);
    var yPositive = this._asm.DefineLabel();
    this._asm.J(Condition.Equal, yPositive);
    this._asm.Or(this.Scratch(Math4 + Meta, OperandSize.Word), SignMask);
    this._asm.MarkLabel(yPositive);
    this.CopyScratchToSlot(Math4, 1);
    this.EmitPop();
    this._asm.MarkLabel(done);
  }

  private void EmitAtanPositive(int z, int result) {
    this._asm.Mov(this.Scratch(QuadrantScratch, OperandSize.Word), 0);
    this.LoadCanonicalInteger(ScratchC, 1);
    var reciprocal = this._asm.DefineLabel();
    var afterReciprocal = this._asm.DefineLabel();
    this.EmitScratchMagnitudeCompare(z, ScratchC, reciprocal, afterReciprocal);
    this._asm.MarkLabel(reciprocal);
    this.MathDiv(ScratchC, z, z);
    this._asm.Or(this.Scratch(QuadrantScratch, OperandSize.Word), 1);
    this._asm.MarkLabel(afterReciprocal);

    this.LoadCanonical(ScratchC, -2, 0, 0xD413CCCFE7799211UL); // sqrt(2)-1
    var transform = this._asm.DefineLabel();
    var series = this._asm.DefineLabel();
    this.EmitScratchMagnitudeCompare(z, ScratchC, transform, series);
    this._asm.MarkLabel(transform);
    this.LoadCanonicalInteger(ScratchC, 1);
    this.MathSub(z, ScratchC, ScratchD);
    this.MathAdd(z, ScratchC, Math3);
    this.MathDiv(ScratchD, Math3, z);
    this._asm.Or(this.Scratch(QuadrantScratch, OperandSize.Word), 2);

    this._asm.MarkLabel(series);
    this.MathMul(z, z, ScratchD);
    this.CopyScratch(z, Math3);
    this.CopyScratch(z, result);
    for (var n = 3; n <= 53; n += 2) {
      this.MathMul(Math3, ScratchD, Math3);
      this.NegateCanonical(Math3);
      this.LoadCanonicalInteger(ScratchC, n);
      this.MathDiv(Math3, ScratchC, Math4);
      this.MathAdd(result, Math4, result);
    }

    var noPi4 = this._asm.DefineLabel();
    this._asm.Test(this.Scratch(QuadrantScratch, OperandSize.Word), 2);
    this._asm.J(Condition.Equal, noPi4);
    this.LoadPiOver4(ScratchC);
    this.MathAdd(result, ScratchC, result);
    this._asm.MarkLabel(noPi4);

    var noReciprocal = this._asm.DefineLabel();
    this._asm.Test(this.Scratch(QuadrantScratch, OperandSize.Word), 1);
    this._asm.J(Condition.Equal, noReciprocal);
    this.LoadPiOver2(ScratchC);
    this.MathSub(ScratchC, result, result);
    this._asm.MarkLabel(noReciprocal);
  }
}
