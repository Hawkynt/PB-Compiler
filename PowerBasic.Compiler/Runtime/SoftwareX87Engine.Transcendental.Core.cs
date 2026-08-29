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
      default: this.RestoreIntegerState(); return false;
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

  /// <summary>Rounds one finite value to an integral canonical value with an explicit RC.</summary>
  private void RoundCanonicalToInteger(int source, int destination, int rc) {
    var special = this._asm.DefineLabel();
    var alreadyIntegral = this._asm.DefineLabel();
    var done = this._asm.DefineLabel();
    this.BranchIfNotClass(source, ClassFinite, special);
    this._asm.Cmp(this.Scratch(source + Exponent, OperandSize.Word), 63);
    this._asm.J(Condition.GreaterOrEqual, alreadyIntegral);
    this.SaveControl();
    this.SetTemporaryRounding(rc);
    this.ConvertCanonicalToInteger(source, this.Scratch(IntScratch, OperandSize.Qword), 64);
    this.RestoreControl();
    this.ConvertIntegerToCanonical(this.Scratch(IntScratch, OperandSize.Qword), 64, destination);
    this._asm.Jmp(done);
    this._asm.MarkLabel(alreadyIntegral);
    this.CopyScratch(source, destination);
    this._asm.Jmp(done);
    this._asm.MarkLabel(special);
    this.CopyScratch(source, destination);
    this._asm.MarkLabel(done);
  }

  private void EmitRoundInteger() {
    this.CopySlotToScratch(0, ScratchA);
    var finite = this._asm.DefineLabel();
    var done = this._asm.DefineLabel();
    this.BranchIfClass(ScratchA, ClassFinite, finite);
    this.CopyScratchToSlot(ScratchA, 0);
    this._asm.Jmp(done);
    this._asm.MarkLabel(finite);
    this._asm.Cmp(this.Scratch(ScratchA + Exponent, OperandSize.Word), 63);
    var already = this._asm.DefineLabel();
    this._asm.J(Condition.GreaterOrEqual, already);
    this.ConvertCanonicalToInteger(ScratchA, this.Scratch(IntScratch, OperandSize.Qword), 64);
    this.ConvertIntegerToCanonical(this.Scratch(IntScratch, OperandSize.Qword), 64, ScratchC);
    this.CopyScratchToSlot(ScratchC, 0);
    this._asm.Jmp(done);
    this._asm.MarkLabel(already);
    this.CopyScratchToSlot(ScratchA, 0);
    this._asm.MarkLabel(done);
  }

  private void EmitScale() {
    this.CopySlotToScratch(0, ScratchA);
    this.CopySlotToScratch(1, ScratchB);
    var done = this._asm.DefineLabel();
    var aFinite = this._asm.DefineLabel();
    this.BranchIfClass(ScratchA, ClassFinite, aFinite);
    this._asm.Jmp(done);
    this._asm.MarkLabel(aFinite);

    var bFinite = this._asm.DefineLabel();
    var bZero = this._asm.DefineLabel();
    var bNan = this._asm.DefineLabel();
    this.BranchIfClass(ScratchB, ClassFinite, bFinite);
    this.BranchIfClass(ScratchB, ClassZero, bZero);
    this.BranchIfClass(ScratchB, ClassNaN, bNan);
    // infinite scale
    this._asm.Test(this.Scratch(ScratchB + Meta, OperandSize.Word), SignMask);
    var negativeInfiniteScale = this._asm.DefineLabel();
    this._asm.J(Condition.NotEqual, negativeInfiniteScale);
    this._asm.Mov(this.Scratch(ScratchA + Exponent, OperandSize.Word), 32767);
    this.FinalizeCanonicalRange(ScratchA);
    this._asm.Jmp(bZero);
    this._asm.MarkLabel(negativeInfiniteScale);
    this._asm.Mov(this.Scratch(ScratchA + Exponent, OperandSize.Word), -32768);
    this.FinalizeCanonicalRange(ScratchA);
    this._asm.Jmp(bZero);

    this._asm.MarkLabel(bNan);
    this.PropagateNaN(ScratchB, ScratchA);
    this._asm.Jmp(bZero);

    this._asm.MarkLabel(bFinite);
    this._asm.Cmp(this.Scratch(ScratchB + Exponent, OperandSize.Word), 14);
    var ordinary = this._asm.DefineLabel();
    this._asm.J(Condition.LessOrEqual, ordinary);
    this._asm.Test(this.Scratch(ScratchB + Meta, OperandSize.Word), SignMask);
    var hugeNegative = this._asm.DefineLabel();
    this._asm.J(Condition.NotEqual, hugeNegative);
    this._asm.Mov(this.Scratch(ScratchA + Exponent, OperandSize.Word), 32767);
    this.FinalizeCanonicalRange(ScratchA);
    this._asm.Jmp(bZero);
    this._asm.MarkLabel(hugeNegative);
    this._asm.Mov(this.Scratch(ScratchA + Exponent, OperandSize.Word), -32768);
    this.FinalizeCanonicalRange(ScratchA);
    this._asm.Jmp(bZero);

    this._asm.MarkLabel(ordinary);
    this.RoundCanonicalToInteger(ScratchB, ScratchC, rc: 3); // FSCALE always truncates ST1
    this.ConvertCanonicalToInteger(ScratchC, this.Scratch(IntScratch, OperandSize.Qword), 64);
    this._asm.Mov(Reg.AX, this.Scratch(IntScratch, OperandSize.Word));
    this._asm.Add(this.Scratch(ScratchA + Exponent, OperandSize.Word), Reg.AX);
    this.FinalizeCanonicalRange(ScratchA);

    this._asm.MarkLabel(bZero);
    this.CopyScratchToSlot(ScratchA, 0);
    this._asm.MarkLabel(done);
  }

  private void EmitSquareRoot() {
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

    this._asm.MarkLabel(zero);
    this.CopyScratchToSlot(ScratchA, 0);
    this._asm.Jmp(done);

    this._asm.MarkLabel(infinity);
    this._asm.Test(this.Scratch(ScratchA + Meta, OperandSize.Word), SignMask);
    var positiveInfinity = this._asm.DefineLabel();
    this._asm.J(Condition.Equal, positiveInfinity);
    this.EmitIndefiniteNaN(ScratchC);
    this.RaiseException(StatusInvalid);
    this.CopyScratchToSlot(ScratchC, 0);
    this._asm.Jmp(done);
    this._asm.MarkLabel(positiveInfinity);
    this.CopyScratchToSlot(ScratchA, 0);
    this._asm.Jmp(done);

    this._asm.MarkLabel(finite);
    this._asm.Test(this.Scratch(ScratchA + Meta, OperandSize.Word), SignMask);
    var positive = this._asm.DefineLabel();
    this._asm.J(Condition.Equal, positive);
    this.EmitIndefiniteNaN(ScratchC);
    this.RaiseException(StatusInvalid);
    this.CopyScratchToSlot(ScratchC, 0);
    this._asm.Jmp(done);

    this._asm.MarkLabel(positive);
    this.LoadCanonical(ScratchC, 0, 0, 0x8000000000000000UL);
    this._asm.Mov(Reg.AX, this.Scratch(ScratchA + Exponent, OperandSize.Word));
    this._asm.Mov(Reg.DX, Reg.AX);
    this._asm.Sar(Reg.DX, 1);
    this._asm.Mov(this.Scratch(ScratchC + Exponent, OperandSize.Word), Reg.DX);
    this._asm.Test(Reg.AX, 1);
    var evenExponent = this._asm.DefineLabel();
    this._asm.J(Condition.Equal, evenExponent);
    this._asm.Mov(this.Scratch(ScratchC + Sig0, OperandSize.Word), 0x6484);
    this._asm.Mov(this.Scratch(ScratchC + Sig1, OperandSize.Word), 0xF9DE);
    this._asm.Mov(this.Scratch(ScratchC + Sig2, OperandSize.Word), 0xF333);
    this._asm.Mov(this.Scratch(ScratchC + Sig3, OperandSize.Word), 0xB504);
    this._asm.MarkLabel(evenExponent);
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
    var dividendOkay = this._asm.DefineLabel();
    var invalid = this._asm.DefineLabel();
    var divisorInfinity = this._asm.DefineLabel();
    var done = this._asm.DefineLabel();
    this.BranchIfClass(ScratchA, ClassFinite, dividendOkay);
    this.BranchIfClass(ScratchA, ClassZero, dividendOkay);
    this._asm.Jmp(invalid);
    this._asm.MarkLabel(dividendOkay);
    this.BranchIfClass(ScratchB, ClassNaN, invalid);
    this.BranchIfClass(ScratchB, ClassZero, invalid);
    this.BranchIfClass(ScratchB, ClassInfinity, divisorInfinity);

    this._asm.Mov(Reg.AX, this.Scratch(ScratchA + Exponent, OperandSize.Word));
    this._asm.Sub(Reg.AX, this.Scratch(ScratchB + Exponent, OperandSize.Word));
    this._asm.Cmp(Reg.AX, 31);
    var complete = this._asm.DefineLabel();
    this._asm.J(Condition.LessOrEqual, complete);

    // Partial reduction: scale the divisor so the quotient chunk fits 31 bits and advertise C2=1.
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

    this._asm.MarkLabel(divisorInfinity);
    this.SetRemainderConditionCodes(c2: false, quotientAvailable: true);
    this._asm.Jmp(done);

    this._asm.MarkLabel(invalid);
    this.EmitIndefiniteNaN(ScratchC);
    this.RaiseException(StatusInvalid);
    this.CopyScratchToSlot(ScratchC, 0);
    this._asm.MarkLabel(done);
  }

  private void SetRemainderConditionCodes(bool c2, bool quotientAvailable) {
    this._asm.Mov(Reg.AX, this.Status);
    this._asm.And(Reg.AX, 0xB8FF);
    if (c2) this._asm.Or(Reg.AX, 0x0400);
    if (quotientAvailable) {
      this._asm.Mov(Reg.DX, this.Scratch(IntScratch, OperandSize.Word));
      var q0 = this._asm.DefineLabel();
      this._asm.Test(Reg.DX, 1); this._asm.J(Condition.Equal, q0); this._asm.Or(Reg.AX, 0x0200); this._asm.MarkLabel(q0);
      var q1 = this._asm.DefineLabel();
      this._asm.Test(Reg.DX, 2); this._asm.J(Condition.Equal, q1); this._asm.Or(Reg.AX, 0x4000); this._asm.MarkLabel(q1);
      var q2 = this._asm.DefineLabel();
      this._asm.Test(Reg.DX, 4); this._asm.J(Condition.Equal, q2); this._asm.Or(Reg.AX, 0x0100); this._asm.MarkLabel(q2);
    }
    this._asm.Mov(this.Status, Reg.AX);
  }

  /// <summary>Branches to ge when |left| >= |right|, otherwise to lt.</summary>
  private void EmitScratchMagnitudeCompare(int left, int right, Label ge, Label lt) {
    this._asm.Mov(Reg.AX, this.Scratch(left + Exponent, OperandSize.Word));
    this._asm.Cmp(Reg.AX, this.Scratch(right + Exponent, OperandSize.Word));
    this._asm.J(Condition.Greater, ge);
    this._asm.J(Condition.Less, lt);
    for (var i = 3; i >= 0; --i) {
      var next = this._asm.DefineLabel();
      this._asm.Mov(Reg.AX, this.Scratch(left + i * 2, OperandSize.Word));
      this._asm.Cmp(Reg.AX, this.Scratch(right + i * 2, OperandSize.Word));
      this._asm.J(Condition.Equal, next);
      this._asm.J(Condition.Above, ge);
      this._asm.Jmp(lt);
      this._asm.MarkLabel(next);
    }
    this._asm.Jmp(ge);
  }
}
