using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

public sealed partial class SoftwareX87Engine {
  private void EmitCanonicalSubtract(int left, int right, int result) {
    this.CopyScratch(right, ScratchD);
    this._asm.Xor(this.Scratch(ScratchD + Meta, OperandSize.Word), SignMask);
    this.EmitCanonicalAdd(left, ScratchD, result);
  }

  private void EmitCanonicalAdd(int left, int right, int result) {
    this.CopyScratch(left, ScratchA);
    this.CopyScratch(right, ScratchB);
    var aFinite = this._asm.DefineLabel();
    var aZero = this._asm.DefineLabel();
    var aInf = this._asm.DefineLabel();
    var aNan = this._asm.DefineLabel();
    var bNan = this._asm.DefineLabel();
    var done = this._asm.DefineLabel();
    this.BranchByClass(ScratchA, aFinite, aZero, aInf, aNan);

    this._asm.MarkLabel(aNan);
    this.PropagateNaN(ScratchA, result);
    this._asm.Jmp(done);

    this._asm.MarkLabel(aInf);
    this.BranchIfClass(ScratchB, ClassNaN, bNan);
    var copyAInf = this._asm.DefineLabel();
    this.BranchIfNotClass(ScratchB, ClassInfinity, copyAInf);
    this._asm.Mov(Reg.AX, this.Scratch(ScratchA + Meta, OperandSize.Word));
    this._asm.Xor(Reg.AX, this.Scratch(ScratchB + Meta, OperandSize.Word));
    this._asm.Test(Reg.AX, SignMask); this._asm.J(Condition.Equal, copyAInf);
    this.EmitIndefiniteNaN(result); this.RaiseException(StatusInvalid); this._asm.Jmp(done);
    this._asm.MarkLabel(copyAInf); this.CopyScratch(ScratchA, result); this._asm.Jmp(done);

    this._asm.MarkLabel(aZero);
    this.BranchIfClass(ScratchB, ClassNaN, bNan);
    var bothZero = this._asm.DefineLabel();
    this.BranchIfClass(ScratchB, ClassZero, bothZero);
    this.CopyScratch(ScratchB, result); this._asm.Jmp(done);
    this._asm.MarkLabel(bothZero);
    this.EmitZeroSum(ScratchA, ScratchB, result); this._asm.Jmp(done);

    this._asm.MarkLabel(bNan);
    this.PropagateNaN(ScratchB, result); this._asm.Jmp(done);

    this._asm.MarkLabel(aFinite);
    this.BranchIfClass(ScratchB, ClassNaN, bNan);
    var bInf = this._asm.DefineLabel();
    var bZero = this._asm.DefineLabel();
    this.BranchIfClass(ScratchB, ClassInfinity, bInf);
    this.BranchIfClass(ScratchB, ClassZero, bZero);
    this.EmitFiniteAdd(ScratchA, ScratchB, result); this._asm.Jmp(done);
    this._asm.MarkLabel(bInf); this.CopyScratch(ScratchB, result); this._asm.Jmp(done);
    this._asm.MarkLabel(bZero); this.CopyScratch(ScratchA, result);
    this._asm.MarkLabel(done);
  }

  private void EmitZeroSum(int a, int b, int result) {
    this.ZeroCanonical(result);
    this._asm.Mov(Reg.AX, this.Scratch(a + Meta, OperandSize.Word));
    this._asm.Xor(Reg.AX, this.Scratch(b + Meta, OperandSize.Word));
    var opposite = this._asm.DefineLabel();
    var finish = this._asm.DefineLabel();
    this._asm.Test(Reg.AX, SignMask); this._asm.J(Condition.NotEqual, opposite);
    this._asm.Mov(Reg.AX, this.Scratch(a + Meta, OperandSize.Word)); this._asm.And(Reg.AX, SignMask);
    this._asm.Or(Reg.AX, ClassZero); this._asm.Mov(this.Scratch(result + Meta, OperandSize.Word), Reg.AX);
    this._asm.Jmp(finish);
    this._asm.MarkLabel(opposite);
    this.EmitExactZero(result);
    this._asm.MarkLabel(finish);
  }

  private void EmitExactZero(int result) {
    this.ZeroCanonical(result);
    this._asm.Mov(this.Scratch(result + Meta, OperandSize.Word), ClassZero);
    // Exact cancellation is -0 only under round-toward-minus-infinity.
    this._asm.Mov(Reg.AX, this.Control);
    for (var i = 0; i < 10; ++i) this._asm.Shr(Reg.AX, 1);
    this._asm.And(Reg.AX, 3);
    var done = this._asm.DefineLabel();
    this._asm.Cmp(Reg.AX, 1); this._asm.J(Condition.NotEqual, done);
    this._asm.Or(this.Scratch(result + Meta, OperandSize.Word), SignMask);
    this._asm.MarkLabel(done);
  }

  private void EmitFiniteAdd(int a, int b, int result) {
    // Addition is commutative after subtraction has toggled the RHS sign. Keep A at the larger exponent.
    var ordered = this._asm.DefineLabel();
    this._asm.Mov(Reg.AX, this.Scratch(a + Exponent, OperandSize.Word));
    this._asm.Cmp(Reg.AX, this.Scratch(b + Exponent, OperandSize.Word));
    this._asm.J(Condition.GreaterOrEqual, ordered);
    this.SwapCanonicalScratch(a, b);
    this._asm.MarkLabel(ordered);

    this.CopyCanonicalToGuard(a, ScratchGuardA);
    this.CopyCanonicalToGuard(b, ScratchGuardB);
    this._asm.Mov(Reg.CX, this.Scratch(a + Exponent, OperandSize.Word));
    this._asm.Sub(Reg.CX, this.Scratch(b + Exponent, OperandSize.Word));
    this.ShiftGuardRight(ScratchGuardB, Reg.CX);
    this._asm.Mov(Reg.AX, this.Scratch(a + Exponent, OperandSize.Word));
    this._asm.Mov(this.Scratch(Tmp0, OperandSize.Word), Reg.AX);
    this._asm.Mov(Reg.AX, this.Scratch(a + Meta, OperandSize.Word));
    this._asm.And(Reg.AX, SignMask);
    this._asm.Mov(this.Scratch(Tmp1, OperandSize.Word), Reg.AX);
    this._asm.Mov(Reg.DX, this.Scratch(b + Meta, OperandSize.Word)); this._asm.And(Reg.DX, SignMask);

    var subtract = this._asm.DefineLabel();
    var round = this._asm.DefineLabel();
    this._asm.Cmp(Reg.AX, Reg.DX); this._asm.J(Condition.NotEqual, subtract);

    // Equal signs: exact 80-bit guard addition. A carry becomes the new explicit integer bit.
    this._asm.Mov(Reg.AX, this.Scratch(ScratchGuardB, OperandSize.Word));
    this._asm.Add(this.Scratch(ScratchGuardA, OperandSize.Word), Reg.AX);
    for (var i = 1; i < 5; ++i) {
      this._asm.Mov(Reg.AX, this.Scratch(ScratchGuardB + i * 2, OperandSize.Word));
      this._asm.Adc(this.Scratch(ScratchGuardA + i * 2, OperandSize.Word), Reg.AX);
    }
    var noCarry = this._asm.DefineLabel();
    this._asm.J(Condition.AboveOrEqual, noCarry);
    this._asm.Stc();
    for (var i = 4; i >= 0; --i)
      this._asm.Rcr(this.Scratch(ScratchGuardA + i * 2, OperandSize.Word), 1);
    // CF is the bit discarded below the guard field; merge it into sticky instead of inventing one.
    this._asm.Pushf(); this._asm.Pop(Reg.AX); this._asm.And(Reg.AX, 1);
    this._asm.Or(this.Scratch(ScratchGuardA, OperandSize.Word), Reg.AX);
    this._asm.Inc(this.Scratch(Tmp0, OperandSize.Word));
    this._asm.MarkLabel(noCarry);
    this._asm.Jmp(round);

    this._asm.MarkLabel(subtract);
    var aGreater = this._asm.DefineLabel();
    var bGreater = this._asm.DefineLabel();
    var exactZero = this._asm.DefineLabel();
    this.CompareGuards(ScratchGuardA, ScratchGuardB, aGreater, bGreater, exactZero);
    this._asm.MarkLabel(bGreater);
    this.SubtractGuards(ScratchGuardB, ScratchGuardA);
    this.CopyGuard(ScratchGuardB, ScratchGuardA);
    this._asm.Mov(Reg.AX, Reg.DX); this._asm.Mov(this.Scratch(Tmp1, OperandSize.Word), Reg.AX);
    this._asm.Jmp(aGreater);

    this._asm.MarkLabel(exactZero);
    this.EmitExactZero(result);
    var finish = this._asm.DefineLabel(); this._asm.Jmp(finish);

    this._asm.MarkLabel(aGreater);
    // Cancellation may expose leading zeros. The guard retains sticky in bit0 while normalization proceeds.
    var normalized = this._asm.DefineLabel();
    var normalize = this._asm.DefineLabel();
    this._asm.MarkLabel(normalize);
    this._asm.Test(this.Scratch(ScratchGuardA + 8, OperandSize.Word), 0x8000);
    this._asm.J(Condition.NotEqual, normalized);
    this.ShiftGuardLeftOne(ScratchGuardA);
    this._asm.Dec(this.Scratch(Tmp0, OperandSize.Word));
    this._asm.Jmp(normalize);
    this._asm.MarkLabel(normalized);

    this._asm.MarkLabel(round);
    this.RoundGuardToCanonical(ScratchGuardA, this.Scratch(Tmp0, OperandSize.Word), this.Scratch(Tmp1, OperandSize.Word), result);
    this._asm.MarkLabel(finish);
  }
}
