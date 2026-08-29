using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

public sealed partial class SoftwareX87Engine {
  private const int IntScratch = ScratchMisc;
  private const int SavedControlScratch = ScratchMisc + 8;
  private const int QuadrantScratch = ScratchMisc + 10;

  private bool EmitInlineTranscendental(string mnemonic, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    if (!RequireNoOperands(operands, out error)) return false;
    return this.EmitUnaryMath(mnemonic);
  }

  private bool EmitUnaryMath(string mnemonic) {
    this.PreserveIntegerState();
    switch (mnemonic) {
      case "FRNDINT": this.EmitRoundInteger(); break;
      case "FSCALE": this.EmitScale(); break;
      case "FSQRT": this.EmitSquareRoot(); break;
      case "FPREM": this.EmitPartialRemainder(nearest: false); break;
      case "FPREM1": this.EmitPartialRemainder(nearest: true); break;
      case "F2XM1": this.EmitExp2MinusOne(); break;
      case "FYL2X": this.EmitYLog2X(plusOne: false); break;
      case "FYL2XP1": this.EmitYLog2X(plusOne: true); break;
      case "FSIN": this.EmitTrig(TrigResult.Sin); break;
      case "FCOS": this.EmitTrig(TrigResult.Cos); break;
      case "FSINCOS": this.EmitTrig(TrigResult.SinCos); break;
      case "FPTAN": this.EmitTrig(TrigResult.Tan); break;
      case "FPATAN": this.EmitAtan2(); break;
      default:
        this.RestoreIntegerState();
        return false;
    }
    this.RestoreIntegerState();
    return true;
  }

  private enum TrigResult { Sin, Cos, SinCos, Tan }

  private void SaveControl() {
    this._asm.Mov(Reg.AX, this.Control);
    this._asm.Mov(this.Scratch(SavedControlScratch, OperandSize.Word), Reg.AX);
  }

  private void SetTemporaryRounding(int rc) {
    this._asm.Mov(Reg.AX, this.Control);
    this._asm.And(Reg.AX, 0xF3FF);
    this._asm.Or(Reg.AX, rc << 10);
    this._asm.Mov(this.Control, Reg.AX);
  }

  private void RestoreControl() {
    this._asm.Mov(Reg.AX, this.Scratch(SavedControlScratch, OperandSize.Word));
    this._asm.Mov(this.Control, Reg.AX);
  }

  private void RoundCanonicalToInteger(int source, int destination, int rc) {
    var already = this._asm.DefineLabel();
    var convert = this._asm.DefineLabel();
    var done = this._asm.DefineLabel();
    this.BranchIfNotClass(source, ClassFinite, convert);
    this._asm.Cmp(this.Scratch(source + Exponent, OperandSize.Word), 63);
    this._asm.J(Condition.GreaterOrEqual, already);
    this._asm.MarkLabel(convert);
    this.SaveControl();
    this.SetTemporaryRounding(rc);
    this.ConvertCanonicalToInteger(source, this.Scratch(IntScratch, OperandSize.Qword), 64);
    this.RestoreControl();
    this.ConvertIntegerToCanonical(this.Scratch(IntScratch, OperandSize.Qword), 64, destination);
    this._asm.Jmp(done);
    this._asm.MarkLabel(already);
    this.CopyScratch(source, destination);
    this._asm.MarkLabel(done);
  }

  private void EmitRoundInteger() {
    this.CopySlotToScratch(0, ScratchA);
    // RC=4 means use the architectural current RC instead of forcing one.
    var finite = this._asm.DefineLabel(); var special = this._asm.DefineLabel(); var done = this._asm.DefineLabel();
    this.BranchIfClass(ScratchA, ClassFinite, finite); this._asm.Jmp(special);
    this._asm.MarkLabel(finite);
    this._asm.Cmp(this.Scratch(ScratchA + Exponent, OperandSize.Word), 63); this._asm.J(Condition.GreaterOrEqual, special);
    this.ConvertCanonicalToInteger(ScratchA, this.Scratch(IntScratch, OperandSize.Qword), 64);
    this.ConvertIntegerToCanonical(this.Scratch(IntScratch, OperandSize.Qword), 64, ScratchC);
    this.CopyScratchToSlot(ScratchC, 0); this._asm.Jmp(done);
    this._asm.MarkLabel(special); this.CopyScratchToSlot(ScratchA, 0);
    this._asm.MarkLabel(done);
  }

  private void EmitScale() {
    this.CopySlotToScratch(0, ScratchA);
    this.CopySlotToScratch(1, ScratchB);
    var finite = this._asm.DefineLabel(); var done = this._asm.DefineLabel();
    this.BranchIfClass(ScratchA, ClassFinite, finite);
    this._asm.Jmp(done); // zero/inf/nan remain unchanged except x87 stack errors, handled elsewhere
    this._asm.MarkLabel(finite);
    var scaleFinite = this._asm.DefineLabel();
    this.BranchIfClass(ScratchB, ClassFinite, scaleFinite);
    var scaleZero = this._asm.DefineLabel();
    this.BranchIfClass(ScratchB, ClassZero, scaleZero);
    var scaleNan = this._asm.DefineLabel();
    this.BranchIfClass(ScratchB, ClassNaN, scaleNan);
    // Infinite scale drives finite ST0 toward infinity or zero according to its sign.
    this._asm.Test(this.Scratch(ScratchB + Meta, OperandSize.Word), SignMask);
    var down = this._asm.DefineLabel(); this._asm.J(Condition.NotEqual, down);
    this._asm.Mov(this.Scratch(ScratchA + Exponent, OperandSize.Word), 0x7FFF); this.FinalizeCanonicalRange(ScratchA); this._asm.Jmp(scaleZero);
    this._asm.MarkLabel(down);
    this._asm.Mov(this.Scratch(ScratchA + Exponent, OperandSize.Word), unchecked((short)-32768)); this.FinalizeCanonicalRange(ScratchA); this._asm.Jmp(scaleZero);
    this._asm.MarkLabel(scaleNan); this.PropagateNaN(ScratchB, ScratchA); this._asm.Jmp(scaleZero);

    this._asm.MarkLabel(scaleFinite);
    // |scale| >= 2^15 is already far beyond the exponent range; avoid integer conversion overflow.
    this._asm.Cmp(this.Scratch(ScratchB + Exponent, OperandSize.Word), 14);
    var ordinary = this._asm.DefineLabel(); this._asm.J(Condition.LessOrEqual, ordinary);
    this._asm.Test(this.Scratch(ScratchB + Meta, OperandSize.Word), SignMask);
    var hugeDown = this._asm.DefineLabel(); this._asm.J(Condition.NotEqual, hugeDown);
    this._asm.Mov(this.Scratch(ScratchA + Exponent, OperandSize.Word), 0x7FFF); this.FinalizeCanonicalRange(ScratchA); this._asm.Jmp(scaleZero);
    this._asm.MarkLabel(hugeDown);
    this._asm.Mov(this.Scratch(ScratchA + Exponent, OperandSize.Word), unchecked((short)-32768)); this.FinalizeCanonicalRange(ScratchA); this._asm.Jmp(scaleZero);

    this._asm.MarkLabel(ordinary);
    this.RoundCanonicalToInteger(ScratchB, ScratchC, rc: 3); // FSCALE truncates ST1 toward zero
    this.ConvertCanonicalToInteger(ScratchC, this.Scratch(IntScratch, OperandSize.Qword), 64);
    this._asm.Mov(Reg.AX, this.Scratch(IntScratch, OperandSize.Word));
    this._asm.Add(this.Scratch(ScratchA + Exponent, OperandSize.Word), Reg.AX);
    this.FinalizeCanonicalRange(ScratchA);
    this._asm.MarkLabel(scaleZero);
    this.CopyScratchToSlot(ScratchA, 0);
    this._asm.MarkLabel(done);
  }

  private void EmitSquareRoot() {
    this.CopySlotToScratch(0, ScratchA);
    var finite = this._asm.DefineLabel(); var zero = this._asm.DefineLabel(); var inf = this._asm.DefineLabel(); var nan = this._asm.DefineLabel(); var done = this._asm.DefineLabel();
    this.BranchByClass(ScratchA, finite, zero, inf, nan);
    this._asm.MarkLabel(nan); this.PropagateNaN(ScratchA, ScratchC); this.CopyScratchToSlot(ScratchC, 0); this._asm.Jmp(done);
    this._asm.MarkLabel(zero); this.CopyScratchToSlot(ScratchA, 0); this._asm.Jmp(done);
    this._asm.MarkLabel(inf);
    this._asm.Test(this.Scratch(ScratchA + Meta, OperandSize.Word), SignMask);
    var positiveInf = this._asm.DefineLabel(); this._asm.J(Condition.Equal, positiveInf);
    this.EmitIndefiniteNaN(ScratchC); this.RaiseException(StatusInvalid); this.CopyScratchToSlot(ScratchC, 0); this._asm.Jmp(done);
    this._asm.MarkLabel(positiveInf); this.CopyScratchToSlot(ScratchA, 0); this._asm.Jmp(done);

    this._asm.MarkLabel(finite);
    this._asm.Test(this.Scratch(ScratchA + Meta, OperandSize.Word), SignMask);
    var positive = this._asm.DefineLabel(); this._asm.J(Condition.Equal, positive);
    this.EmitIndefiniteNaN(ScratchC); this.RaiseException(StatusInvalid); this.CopyScratchToSlot(ScratchC, 0); this._asm.Jmp(done);
    this._asm.MarkLabel(positive);

    // Power-of-two/sqrt(2) seed from the unbiased exponent; seven Newton steps exceed 64 bits.
    this.LoadCanonical(ScratchC, 0, 0, 0x8000000000000000UL);
    this._asm.Mov(Reg.AX, this.Scratch(ScratchA + Exponent, OperandSize.Word));
    this._asm.Mov(Reg.DX, Reg.AX); this._asm.Sar(Reg.DX, 1);
    this._asm.Mov(this.Scratch(ScratchC + Exponent, OperandSize.Word), Reg.DX);
    this._asm.Test(Reg.AX, 1);
    var even = this._asm.DefineLabel(); this._asm.J(Condition.Equal, even);
    this._asm.Mov(this.Scratch(ScratchC + Sig0, OperandSize.Word), 0x6484);
    this._asm.Mov(this.Scratch(ScratchC + Sig1, OperandSize.Word), 0xF9DE);
    this._asm.Mov(this.Scratch(ScratchC + Sig2, OperandSize.Word), 0xF333);
    this._asm.Mov(this.Scratch(ScratchC + Sig3, OperandSize.Word), 0xB504);
    this._asm.MarkLabel(even);
    this.LoadCanonicalRational(ScratchD, 1, 2);
    for (var i = 0; i < 7; ++i) {
      this.MathDiv(ScratchA, ScratchC, Math3);
      this.MathAdd(ScratchC, Math3, Math4);
      this.MathMul(Math4, ScratchD, ScratchC);
    }
    this.CopyScratchToSlot(ScratchC, 0);
    this._asm.MarkLabel(done);
  }

  private void EmitPartialRemainder(bool nearest) {
    this.CopySlotToScratch(0, ScratchA);
    this.CopySlotToScratch(1, ScratchB);
    var aFinite = this._asm.DefineLabel(); var invalid = this._asm.DefineLabel(); var done = this._asm.DefineLabel();
    this.BranchIfClass(ScratchA, ClassFinite, aFinite);
    this.BranchIfClass(ScratchA, ClassZero, aFinite);
    this._asm.Jmp(invalid);
    this._asm.MarkLabel(aFinite);
    this.BranchIfClass(ScratchB, ClassNaN, invalid);
    this.BranchIfClass(ScratchB, ClassZero, invalid);
    this.BranchIfClass(ScratchB, ClassInfinity, done); // finite % infinity = dividend

    // Large quotient: perform one architecturally valid partial reduction by a divisor scaled so
    // the quotient chunk is <= 31 bits, and set C2. The caller may repeat FPREM/FPREM1.
    this._asm.Mov(Reg.AX, this.Scratch(ScratchA + Exponent, OperandSize.Word));
    this._asm.Sub(Reg.AX, this.Scratch(ScratchB + Exponent, OperandSize.Word));
    this._asm.Cmp(Reg.AX, 31);
    var complete = this._asm.DefineLabel(); this._asm.J(Condition.LessOrEqual, complete);
    this.CopyScratch(ScratchB, ScratchC);
    this._asm.Sub(Reg.AX, 31);
    this._asm.Add(this.Scratch(ScratchC + Exponent, OperandSize.Word), Reg.AX);
    this.MathDiv(ScratchA, ScratchC, ScratchD);
    this.RoundCanonicalToInteger(ScratchD, Math3, rc: nearest ? 0 : 3);
    this.MathMul(Math3, ScratchC, Math4);
    this.MathSub(ScratchA, Math4, ScratchA);
    this.CopyScratchToSlot(ScratchA, 0);
    this.SetRemainderConditionCodes(c2: true, quotientAvailable: false);
    this._asm.Jmp(done);

    this._asm.MarkLabel(complete);
    this.MathDiv(ScratchA, ScratchB, ScratchD);
    this.RoundCanonicalToInteger(ScratchD, Math3, rc: nearest ? 0 : 3);
    this.ConvertCanonicalToInteger(Math3, this.Scratch(IntScratch, OperandSize.Qword), 64);
    this.MathMul(Math3, ScratchB, Math4);
    this.MathSub(ScratchA, Math4, ScratchA);
    this.CopyScratchToSlot(ScratchA, 0);
    this.SetRemainderConditionCodes(c2: false, quotientAvailable: true);
    this._asm.Jmp(done);

    this._asm.MarkLabel(invalid);
    this.EmitIndefiniteNaN(ScratchC); this.RaiseException(StatusInvalid); this.CopyScratchToSlot(ScratchC, 0);
    this._asm.MarkLabel(done);
  }

  private void SetRemainderConditionCodes(bool c2, bool quotientAvailable) {
    this._asm.Mov(Reg.AX, this.Status);
    this._asm.And(Reg.AX, 0xB8FF); // clear C0/C1/C2/C3
    if (c2) this._asm.Or(Reg.AX, 0x0400);
    if (quotientAvailable) {
      this._asm.Mov(Reg.DX, this.Scratch(IntScratch, OperandSize.Word));
      var noQ0 = this._asm.DefineLabel(); this._asm.Test(Reg.DX, 1); this._asm.J(Condition.Equal, noQ0); this._asm.Or(Reg.AX, 0x0200); this._asm.MarkLabel(noQ0);
      var noQ1 = this._asm.DefineLabel(); this._asm.Test(Reg.DX, 2); this._asm.J(Condition.Equal, noQ1); this._asm.Or(Reg.AX, 0x4000); this._asm.MarkLabel(noQ1);
      var noQ2 = this._asm.DefineLabel(); this._asm.Test(Reg.DX, 4); this._asm.J(Condition.Equal, noQ2); this._asm.Or(Reg.AX, 0x0100); this._asm.MarkLabel(noQ2);
    }
    this._asm.Mov(this.Status, Reg.AX);
  }

  private void EmitExp2MinusOne() {
    this.CopySlotToScratch(0, ScratchA);
    var finite = this._asm.DefineLabel(); var done = this._asm.DefineLabel();
    this.BranchIfClass(ScratchA, ClassFinite, finite);
    this.BranchIfClass(ScratchA, ClassZero, done);
    this.PropagateNaN(ScratchA, ScratchC); this.CopyScratchToSlot(ScratchC, 0); this._asm.Jmp(done);
    this._asm.MarkLabel(finite);
    // Architectural domain is -1 <= x <= +1. Outside it C2=1 and ST0 is left unchanged.
    this.LoadCanonicalInteger(ScratchB, 1);
    this.CopyScratch(ScratchA, ScratchC); this.AbsCanonical(ScratchC);
    var inRange = this._asm.DefineLabel();
    this.EmitScratchMagnitudeCompare(ScratchC, ScratchB, inRange, outOfRange: done);
    this._asm.MarkLabel(inRange);
    this.LoadLn2(ScratchB);
    this.MathMul(ScratchA, ScratchB, ScratchC); // t=x ln2
    this.CopyScratch(ScratchC, ScratchD);       // term=t
    this.CopyScratch(ScratchC, Math3);          // sum=t
    for (var n = 2; n <= 20; ++n) {
      this.MathMul(ScratchD, ScratchC, Math4);
      this.LoadCanonicalInteger(Math5, n);
      this.MathDiv(Math4, Math5, ScratchD);
      this.MathAdd(Math3, ScratchD, Math3);
    }
    this.CopyScratchToSlot(Math3, 0);
    this._asm.MarkLabel(done);
  }

  private void EmitYLog2X(bool plusOne) {
    this.CopySlotToScratch(0, ScratchA);
    this.CopySlotToScratch(1, ScratchB);
    if (plusOne) {
      this.LoadCanonicalInteger(ScratchC, 1);
      this.MathAdd(ScratchA, ScratchC, ScratchA);
    }
    this.EmitLog2(ScratchA, ScratchC);
    this.MathMul(ScratchB, ScratchC, ScratchD);
    this.CopyScratchToSlot(ScratchD, 1);
    this.EmitPop();
  }

  private void EmitLog2(int source, int result) {
    var finite = this._asm.DefineLabel(); var zero = this._asm.DefineLabel(); var nan = this._asm.DefineLabel(); var negative = this._asm.DefineLabel(); var done = this._asm.DefineLabel();
    this.BranchIfClass(source, ClassNaN, nan);
    this.BranchIfClass(source, ClassZero, zero);
    this.BranchIfClass(source, ClassInfinity, finite);
    this._asm.Test(this.Scratch(source + Meta, OperandSize.Word), SignMask); this._asm.J(Condition.NotEqual, negative);
    this._asm.Jmp(finite);
    this._asm.MarkLabel(nan); this.PropagateNaN(source, result); this._asm.Jmp(done);
    this._asm.MarkLabel(zero);
    this.LoadCanonical(result, 0, (ushort)(SignMask | ClassInfinity), 0x8000000000000000UL); this.RaiseException(StatusZeroDivide); this._asm.Jmp(done);
    this._asm.MarkLabel(negative); this.EmitIndefiniteNaN(result); this.RaiseException(StatusInvalid); this._asm.Jmp(done);

    this._asm.MarkLabel(finite);
    this.CopyScratch(source, ScratchC);
    var infinity = this._asm.DefineLabel();
    this.BranchIfClass(source, ClassInfinity, infinity);
    this._asm.Mov(Reg.AX, this.Scratch(source + Exponent, OperandSize.Word));
    this._asm.Mov(this.Scratch(QuadrantScratch, OperandSize.Word), Reg.AX);
    this._asm.Mov(this.Scratch(ScratchC + Exponent, OperandSize.Word), 0); // mantissa m in [1,2)
    this.LoadCanonicalInteger(ScratchD, 1);
    this.MathSub(ScratchC, ScratchD, Math3);
    this.MathAdd(ScratchC, ScratchD, Math4);
    this.MathDiv(Math3, Math4, Math5); // z=(m-1)/(m+1)
    this.MathMul(Math5, Math5, ScratchD); // z2
    this.CopyScratch(Math5, Math3);       // term
    this.CopyScratch(Math5, Math4);       // sum
    for (var n = 3; n <= 39; n += 2) {
      this.MathMul(Math3, ScratchD, Math3);
      this.LoadCanonicalInteger(ScratchC, n);
      this.MathDiv(Math3, ScratchC, Math5);
      this.MathAdd(Math4, Math5, Math4);
    }
    this.LoadCanonicalInteger(ScratchC, 2);
    this.MathMul(Math4, ScratchC, Math4); // ln(m)
    this.LoadLn2(ScratchC);
    this.MathDiv(Math4, ScratchC, Math4); // log2(m)
    // Reconstitute the integral exponent exactly as a canonical number.
    this._asm.Mov(Reg.AX, this.Scratch(QuadrantScratch, OperandSize.Word));
    this._asm.Mov(this.Scratch(IntScratch, OperandSize.Word), Reg.AX);
    this._asm.Mov(Reg.DX, Reg.AX); this._asm.Sar(Reg.DX, 15);
    this._asm.Mov(this.Scratch(IntScratch + 2, OperandSize.Word), Reg.DX);
    this._asm.Mov(this.Scratch(IntScratch + 4, OperandSize.Word), Reg.DX);
    this._asm.Mov(this.Scratch(IntScratch + 6, OperandSize.Word), Reg.DX);
    this.ConvertIntegerToCanonical(this.Scratch(IntScratch, OperandSize.Qword), 64, ScratchC);
    this.MathAdd(Math4, ScratchC, result);
    this._asm.Jmp(done);
    this._asm.MarkLabel(infinity); this.CopyScratch(source, result);
    this._asm.MarkLabel(done);
  }

  private void EmitTrig(TrigResult resultKind) {
    this.CopySlotToScratch(0, ScratchA);
    var finite = this._asm.DefineLabel(); var zero = this._asm.DefineLabel(); var nan = this._asm.DefineLabel(); var invalid = this._asm.DefineLabel(); var done = this._asm.DefineLabel();
    this.BranchByClass(ScratchA, finite, zero, invalid, nan);
    this._asm.MarkLabel(nan); this.PropagateNaN(ScratchA, ScratchC); this.CopyScratchToSlot(ScratchC, 0); this._asm.Jmp(done);
    this._asm.MarkLabel(invalid); this.EmitIndefiniteNaN(ScratchC); this.RaiseException(StatusInvalid); this.CopyScratchToSlot(ScratchC, 0); this._asm.Jmp(done);
    this._asm.MarkLabel(zero);
    if (resultKind is TrigResult.Sin or TrigResult.Tan) { this.CopyScratchToSlot(ScratchA, 0); this._asm.Jmp(done); }
    if (resultKind == TrigResult.Cos) { this.LoadCanonicalInteger(ScratchC, 1); this.CopyScratchToSlot(ScratchC, 0); this._asm.Jmp(done); }
    // FSINCOS(±0): ST0=+1, ST1=±0 after the push.
    this.CopyScratch(ScratchA, ScratchC); this.EmitPushEmpty(); this.LoadCanonicalInteger(ScratchD, 1); this.CopyScratchToSlot(ScratchC, 1); this.CopyScratchToSlot(ScratchD, 0); this._asm.Jmp(done);

    this._asm.MarkLabel(finite);
    // Intel documents incomplete range reduction for |x| >= 2^63: set C2 and leave ST0 untouched.
    this._asm.Cmp(this.Scratch(ScratchA + Exponent, OperandSize.Word), 63);
    var reduced = this._asm.DefineLabel(); this._asm.J(Condition.Less, reduced);
    this._asm.Or(this.Status, 0x0400); this._asm.Jmp(done);
    this._asm.MarkLabel(reduced);
    this.LoadPiOver2(ScratchB);
    this.MathDiv(ScratchA, ScratchB, ScratchC);
    this.RoundCanonicalToInteger(ScratchC, ScratchD, rc: 0);
    this.ConvertCanonicalToInteger(ScratchD, this.Scratch(IntScratch, OperandSize.Qword), 64);
    this._asm.Mov(Reg.AX, this.Scratch(IntScratch, OperandSize.Word)); this._asm.And(Reg.AX, 3); this._asm.Mov(this.Scratch(QuadrantScratch, OperandSize.Word), Reg.AX);
    this.MathMul(ScratchD, ScratchB, ScratchC);
    this.MathSub(ScratchA, ScratchC, ScratchA); // r in approximately [-pi/4,pi/4]
    this.EmitSinCosSeries(ScratchA, ScratchC, ScratchD);
    this.MapSinCosQuadrant(ScratchC, ScratchD);

    switch (resultKind) {
      case TrigResult.Sin: this.CopyScratchToSlot(ScratchC, 0); break;
      case TrigResult.Cos: this.CopyScratchToSlot(ScratchD, 0); break;
      case TrigResult.SinCos:
        // Before push ScratchC=sin, ScratchD=cos. Push makes room: ST0=cos, ST1=sin.
        this.EmitPushEmpty(); this.CopyScratchToSlot(ScratchC, 1); this.CopyScratchToSlot(ScratchD, 0); break;
      case TrigResult.Tan:
        this.MathDiv(ScratchC, ScratchD, ScratchC);
        this.CopyScratchToSlot(ScratchC, 0);
        this.EmitPushEmpty(); this.LoadCanonicalInteger(ScratchD, 1); this.CopyScratchToSlot(ScratchD, 0); break;
    }
    this._asm.And(this.Status, 0xFBFF); // successful range reduction clears C2
    this._asm.MarkLabel(done);
  }

  private void EmitSinCosSeries(int x, int sinResult, int cosResult) {
    this.MathMul(x, x, Math3); // x2
    this.CopyScratch(x, sinResult);
    this.CopyScratch(x, Math4); // sin term
    this.LoadCanonicalInteger(cosResult, 1);
    this.LoadCanonicalInteger(Math5, 1); // cos term
    for (var k = 1; k <= 13; ++k) {
      this.MathMul(Math4, Math3, Math4); this.NegateCanonical(Math4);
      this.LoadCanonicalInteger(ScratchB, (2L * k) * (2L * k + 1));
      this.MathDiv(Math4, ScratchB, Math4);
      this.MathAdd(sinResult, Math4, sinResult);

      this.MathMul(Math5, Math3, Math5); this.NegateCanonical(Math5);
      this.LoadCanonicalInteger(ScratchB, (2L * k - 1) * (2L * k));
      this.MathDiv(Math5, ScratchB, Math5);
      this.MathAdd(cosResult, Math5, cosResult);
    }
  }

  private void MapSinCosQuadrant(int sinValue, int cosValue) {
    var q0 = this._asm.DefineLabel(); var q1 = this._asm.DefineLabel(); var q2 = this._asm.DefineLabel(); var done = this._asm.DefineLabel();
    this._asm.Mov(Reg.AX, this.Scratch(QuadrantScratch, OperandSize.Word));
    this._asm.Cmp(Reg.AX, 0); this._asm.J(Condition.Equal, q0);
    this._asm.Cmp(Reg.AX, 1); this._asm.J(Condition.Equal, q1);
    this._asm.Cmp(Reg.AX, 2); this._asm.J(Condition.Equal, q2);
    // q=3: sin=-cos(r), cos=sin(r)
    this.CopyScratch(sinValue, Math3); this.CopyScratch(cosValue, sinValue); this.NegateCanonical(sinValue); this.CopyScratch(Math3, cosValue); this._asm.Jmp(done);
    this._asm.MarkLabel(q1); // sin=cos(r), cos=-sin(r)
    this.CopyScratch(sinValue, Math3); this.CopyScratch(cosValue, sinValue); this.CopyScratch(Math3, cosValue); this.NegateCanonical(cosValue); this._asm.Jmp(done);
    this._asm.MarkLabel(q2); this.NegateCanonical(sinValue); this.NegateCanonical(cosValue); this._asm.Jmp(done);
    this._asm.MarkLabel(q0);
    this._asm.MarkLabel(done);
  }

  private void EmitAtan2() {
    this.CopySlotToScratch(1, ScratchA); // y
    this.CopySlotToScratch(0, ScratchB); // x
    var nan = this._asm.DefineLabel(); var finite = this._asm.DefineLabel(); var done = this._asm.DefineLabel();
    this.BranchIfClass(ScratchA, ClassNaN, nan); this.BranchIfClass(ScratchB, ClassNaN, nan); this._asm.Jmp(finite);
    this._asm.MarkLabel(nan);
    this.BranchIfClass(ScratchA, ClassNaN, done);
    this.PropagateNaN(ScratchB, ScratchC); this.CopyScratchToSlot(ScratchC, 1); this.EmitPop(); this._asm.Jmp(done);
    this._asm.MarkLabel(finite);
    this.CopyScratch(ScratchA, ScratchC); this.AbsCanonical(ScratchC);
    this.CopyScratch(ScratchB, ScratchD); this.AbsCanonical(ScratchD);
    var xZero = this._asm.DefineLabel(); var yZero = this._asm.DefineLabel(); var ordinary = this._asm.DefineLabel();
    this.BranchIfClass(ScratchB, ClassZero, xZero); this.BranchIfClass(ScratchA, ClassZero, yZero); this._asm.Jmp(ordinary);
    this._asm.MarkLabel(xZero); this.LoadPiOver2(Math3); this.CopyScratch(Math3, Math4); this._asm.Jmp(this._asm.DefineLabel());
    var haveAngle = this._asm.DefineLabel();
    // Bind the forward target from xZero explicitly.
    var xZeroAngle = haveAngle;
    // ordinary path
    this._asm.MarkLabel(ordinary);
    this.MathDiv(ScratchC, ScratchD, Math3); // |y/x|
    this.EmitAtanPositive(Math3, Math4);
    this._asm.Jmp(haveAngle);
    this._asm.MarkLabel(yZero);
    this.LoadCanonicalInteger(Math4, 0);
    this._asm.MarkLabel(haveAngle);
    // x<0 -> pi-angle; sign follows y (including signed zero).
    var xPositive = this._asm.DefineLabel();
    this._asm.Test(this.Scratch(ScratchB + Meta, OperandSize.Word), SignMask); this._asm.J(Condition.Equal, xPositive);
    this.LoadPi(Math3); this.MathSub(Math3, Math4, Math4);
    this._asm.MarkLabel(xPositive);
    this._asm.Test(this.Scratch(ScratchA + Meta, OperandSize.Word), SignMask);
    var positiveY = this._asm.DefineLabel(); this._asm.J(Condition.Equal, positiveY); this.NegateCanonical(Math4); this._asm.MarkLabel(positiveY);
    this.CopyScratchToSlot(Math4, 1); this.EmitPop();
    this._asm.MarkLabel(done);
  }

  private void EmitAtanPositive(int z, int result) {
    // Reduce z to <= tan(pi/8)=sqrt(2)-1. First reciprocal for z>1, then the pi/4 transform.
    this.LoadCanonicalInteger(ScratchC, 1);
    var reciprocal = this._asm.DefineLabel(); var afterReciprocal = this._asm.DefineLabel();
    this.EmitScratchMagnitudeCompare(z, ScratchC, reciprocal, afterReciprocal);
    this._asm.MarkLabel(reciprocal); this.MathDiv(ScratchC, z, z); this._asm.Mov(this.Scratch(QuadrantScratch, OperandSize.Word), 1); this._asm.Jmp(afterReciprocal);
    this._asm.MarkLabel(afterReciprocal);
    this.LoadCanonical(ScratchC, -2, 0, 0xD413CCCFE7799211UL); // sqrt(2)-1
    var transform = this._asm.DefineLabel(); var series = this._asm.DefineLabel();
    this.EmitScratchMagnitudeCompare(z, ScratchC, transform, series);
    this._asm.MarkLabel(transform);
    this.LoadCanonicalInteger(ScratchC, 1);
    this.MathSub(z, ScratchC, ScratchD);
    this.MathAdd(z, ScratchC, Math3);
    this.MathDiv(ScratchD, Math3, z);
    this._asm.Or(this.Scratch(QuadrantScratch, OperandSize.Word), 2);
    this._asm.MarkLabel(series);
    this.MathMul(z, z, ScratchD);
    this.CopyScratch(z, Math3); this.CopyScratch(z, result);
    for (var n = 3; n <= 49; n += 2) {
      this.MathMul(Math3, ScratchD, Math3); this.NegateCanonical(Math3);
      this.LoadCanonicalInteger(ScratchC, n);
      this.MathDiv(Math3, ScratchC, Math4);
      this.MathAdd(result, Math4, result);
    }
    var noPi4 = this._asm.DefineLabel();
    this._asm.Test(this.Scratch(QuadrantScratch, OperandSize.Word), 2); this._asm.J(Condition.Equal, noPi4);
    this.LoadPiOver4(ScratchC); this.MathAdd(result, ScratchC, result); this._asm.MarkLabel(noPi4);
    var noReciprocal = this._asm.DefineLabel();
    this._asm.Test(this.Scratch(QuadrantScratch, OperandSize.Word), 1); this._asm.J(Condition.Equal, noReciprocal);
    this.LoadPiOver2(ScratchC); this.MathSub(ScratchC, result, result); this._asm.MarkLabel(noReciprocal);
  }

  /// <summary>Branches to greater/equal when |left| >= |right|, otherwise to less.</summary>
  private void EmitScratchMagnitudeCompare(int left, int right, Label greaterOrEqual, Label outOfRange) {
    this._asm.Mov(Reg.AX, this.Scratch(left + Exponent, OperandSize.Word));
    this._asm.Cmp(Reg.AX, this.Scratch(right + Exponent, OperandSize.Word));
    this._asm.J(Condition.Greater, greaterOrEqual); this._asm.J(Condition.Less, outOfRange);
    for (var i = 3; i >= 0; --i) {
      var next = this._asm.DefineLabel();
      this._asm.Mov(Reg.AX, this.Scratch(left + i * 2, OperandSize.Word));
      this._asm.Cmp(Reg.AX, this.Scratch(right + i * 2, OperandSize.Word));
      this._asm.J(Condition.Equal, next); this._asm.J(Condition.Above, greaterOrEqual); this._asm.Jmp(outOfRange); this._asm.MarkLabel(next);
    }
    this._asm.Jmp(greaterOrEqual);
  }
}
