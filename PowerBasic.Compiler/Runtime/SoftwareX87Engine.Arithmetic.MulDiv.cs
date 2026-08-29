using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

public sealed partial class SoftwareX87Engine {
  private void EmitCanonicalMultiply(int left, int right, int result) {
    this.CopyScratch(left, ScratchA);
    this.CopyScratch(right, ScratchB);
    var aFinite = this._asm.DefineLabel();
    var aZero = this._asm.DefineLabel();
    var aInf = this._asm.DefineLabel();
    var aNan = this._asm.DefineLabel();
    var bNan = this._asm.DefineLabel();
    var invalid = this._asm.DefineLabel();
    var done = this._asm.DefineLabel();
    this.BranchByClass(ScratchA, aFinite, aZero, aInf, aNan);

    this._asm.MarkLabel(aNan); this.PropagateNaN(ScratchA, result); this._asm.Jmp(done);
    this._asm.MarkLabel(aZero);
    this.BranchIfClass(ScratchB, ClassNaN, bNan);
    this.BranchIfClass(ScratchB, ClassInfinity, invalid);
    this.EmitSignedClassResult(ScratchA, ScratchB, result, ClassZero); this._asm.Jmp(done);

    this._asm.MarkLabel(aInf);
    this.BranchIfClass(ScratchB, ClassNaN, bNan);
    this.BranchIfClass(ScratchB, ClassZero, invalid);
    this.EmitSignedClassResult(ScratchA, ScratchB, result, ClassInfinity); this._asm.Jmp(done);

    this._asm.MarkLabel(bNan); this.PropagateNaN(ScratchB, result); this._asm.Jmp(done);
    this._asm.MarkLabel(invalid); this.EmitIndefiniteNaN(result); this.RaiseException(StatusInvalid); this._asm.Jmp(done);

    this._asm.MarkLabel(aFinite);
    this.BranchIfClass(ScratchB, ClassNaN, bNan);
    var bZero = this._asm.DefineLabel();
    var bInf = this._asm.DefineLabel();
    var bothFinite = this._asm.DefineLabel();
    this.BranchIfClass(ScratchB, ClassZero, bZero);
    this.BranchIfClass(ScratchB, ClassInfinity, bInf);
    this._asm.Jmp(bothFinite);
    this._asm.MarkLabel(bZero); this.EmitSignedClassResult(ScratchA, ScratchB, result, ClassZero); this._asm.Jmp(done);
    this._asm.MarkLabel(bInf); this.EmitSignedClassResult(ScratchA, ScratchB, result, ClassInfinity); this._asm.Jmp(done);
    this._asm.MarkLabel(bothFinite); this.EmitFiniteMultiply(ScratchA, ScratchB, result);
    this._asm.MarkLabel(done);
  }

  /// <summary>Exact 64x64 -> 128-bit limb product followed by x87 PC/RC rounding.</summary>
  private void EmitFiniteMultiply(int a, int b, int result) {
    for (var i = 0; i < 8; ++i)
      this._asm.Mov(this.Scratch(ScratchWide + i * 2, OperandSize.Word), 0);

    for (var i = 0; i < 4; ++i)
      for (var j = 0; j < 4; ++j) {
        this._asm.Mov(Reg.AX, this.Scratch(a + i * 2, OperandSize.Word));
        this._asm.Mov(Reg.BX, this.Scratch(b + j * 2, OperandSize.Word));
        this._asm.Mul(Reg.BX);
        var k = i + j;
        this._asm.Add(this.Scratch(ScratchWide + k * 2, OperandSize.Word), Reg.AX);
        this._asm.Adc(this.Scratch(ScratchWide + (k + 1) * 2, OperandSize.Word), Reg.DX);
        for (var p = k + 2; p < 8; ++p)
          this._asm.Adc(this.Scratch(ScratchWide + p * 2, OperandSize.Word), 0);
      }

    this._asm.Mov(Reg.AX, this.Scratch(a + Exponent, OperandSize.Word));
    this._asm.Add(Reg.AX, this.Scratch(b + Exponent, OperandSize.Word));
    this._asm.Mov(this.Scratch(Tmp0, OperandSize.Word), Reg.AX);

    // Product significands are in [1,4). bit127 selects [2,4); otherwise bit126 is the integer bit
    // and the entire 128-bit product is shifted left once before selecting the top 80 bits.
    var belowTwo = this._asm.DefineLabel();
    var normalized = this._asm.DefineLabel();
    this._asm.Test(this.Scratch(ScratchWide + 14, OperandSize.Word), 0x8000);
    this._asm.J(Condition.Equal, belowTwo);
    this._asm.Inc(this.Scratch(Tmp0, OperandSize.Word));
    this._asm.Jmp(normalized);
    this._asm.MarkLabel(belowTwo);
    this.ShiftWideLeftOne(ScratchWide, 8);
    this._asm.MarkLabel(normalized);

    // GuardA = bits 48..127. The lower 48 product bits collapse into sticky bit zero.
    for (var i = 0; i < 5; ++i) {
      this._asm.Mov(Reg.AX, this.Scratch(ScratchWide + (i + 3) * 2, OperandSize.Word));
      this._asm.Mov(this.Scratch(ScratchGuardA + i * 2, OperandSize.Word), Reg.AX);
    }
    this._asm.Mov(Reg.AX, this.Scratch(ScratchWide, OperandSize.Word));
    this._asm.Or(Reg.AX, this.Scratch(ScratchWide + 2, OperandSize.Word));
    this._asm.Or(Reg.AX, this.Scratch(ScratchWide + 4, OperandSize.Word));
    var exact = this._asm.DefineLabel();
    this._asm.Cmp(Reg.AX, 0); this._asm.J(Condition.Equal, exact);
    this._asm.Or(this.Scratch(ScratchGuardA, OperandSize.Word), 1);
    this._asm.MarkLabel(exact);

    this._asm.Mov(Reg.AX, this.Scratch(a + Meta, OperandSize.Word));
    this._asm.Xor(Reg.AX, this.Scratch(b + Meta, OperandSize.Word));
    this._asm.And(Reg.AX, SignMask);
    this._asm.Mov(this.Scratch(Tmp1, OperandSize.Word), Reg.AX);
    this.RoundGuardToCanonical(ScratchGuardA, this.Scratch(Tmp0, OperandSize.Word), this.Scratch(Tmp1, OperandSize.Word), result);
  }

  private void EmitCanonicalDivide(int left, int right, int result) {
    this.CopyScratch(left, ScratchA);
    this.CopyScratch(right, ScratchB);
    var aFinite = this._asm.DefineLabel();
    var aZero = this._asm.DefineLabel();
    var aInf = this._asm.DefineLabel();
    var aNan = this._asm.DefineLabel();
    var bNan = this._asm.DefineLabel();
    var invalid = this._asm.DefineLabel();
    var done = this._asm.DefineLabel();
    this.BranchByClass(ScratchA, aFinite, aZero, aInf, aNan);

    this._asm.MarkLabel(aNan); this.PropagateNaN(ScratchA, result); this._asm.Jmp(done);
    this._asm.MarkLabel(aZero);
    this.BranchIfClass(ScratchB, ClassNaN, bNan);
    this.BranchIfClass(ScratchB, ClassZero, invalid);
    this.EmitSignedClassResult(ScratchA, ScratchB, result, ClassZero); this._asm.Jmp(done);

    this._asm.MarkLabel(aInf);
    this.BranchIfClass(ScratchB, ClassNaN, bNan);
    this.BranchIfClass(ScratchB, ClassInfinity, invalid);
    this.EmitSignedClassResult(ScratchA, ScratchB, result, ClassInfinity); this._asm.Jmp(done);

    this._asm.MarkLabel(bNan); this.PropagateNaN(ScratchB, result); this._asm.Jmp(done);
    this._asm.MarkLabel(invalid); this.EmitIndefiniteNaN(result); this.RaiseException(StatusInvalid); this._asm.Jmp(done);

    this._asm.MarkLabel(aFinite);
    this.BranchIfClass(ScratchB, ClassNaN, bNan);
    var bZero = this._asm.DefineLabel();
    var bInf = this._asm.DefineLabel();
    var bothFinite = this._asm.DefineLabel();
    this.BranchIfClass(ScratchB, ClassZero, bZero);
    this.BranchIfClass(ScratchB, ClassInfinity, bInf);
    this._asm.Jmp(bothFinite);

    this._asm.MarkLabel(bZero);
    this.EmitSignedClassResult(ScratchA, ScratchB, result, ClassInfinity);
    this.RaiseException(StatusZeroDivide);
    this._asm.Jmp(done);
    this._asm.MarkLabel(bInf); this.EmitSignedClassResult(ScratchA, ScratchB, result, ClassZero); this._asm.Jmp(done);
    this._asm.MarkLabel(bothFinite); this.EmitFiniteDivide(ScratchA, ScratchB, result);
    this._asm.MarkLabel(done);
  }

  /// <summary>
  /// Restoring 64/64 division producing 80 explicit quotient bits. A fifth remainder limb carries
  /// the transient 65th bit, so quotient rounding never depends on host integer width.
  /// </summary>
  private void EmitFiniteDivide(int a, int b, int result) {
    for (var i = 0; i < 5; ++i)
      this._asm.Mov(this.Scratch(ScratchWide + i * 2, OperandSize.Word), 0);
    for (var i = 0; i < 4; ++i) {
      this._asm.Mov(Reg.AX, this.Scratch(a + i * 2, OperandSize.Word));
      this._asm.Mov(this.Scratch(ScratchWide + i * 2, OperandSize.Word), Reg.AX);
    }
    for (var i = 0; i < 5; ++i)
      this._asm.Mov(this.Scratch(ScratchGuardA + i * 2, OperandSize.Word), 0);

    this._asm.Mov(Reg.AX, this.Scratch(a + Exponent, OperandSize.Word));
    this._asm.Sub(Reg.AX, this.Scratch(b + Exponent, OperandSize.Word));
    this._asm.Mov(this.Scratch(Tmp0, OperandSize.Word), Reg.AX);

    var numeratorGreater = this._asm.DefineLabel();
    var numeratorLess = this._asm.DefineLabel();
    var numeratorEqual = this._asm.DefineLabel();
    var normalized = this._asm.DefineLabel();
    this.CompareWords(ScratchWide, b, 4, numeratorGreater, numeratorLess, numeratorEqual);
    this._asm.MarkLabel(numeratorLess);
    this.ShiftWideLeftOne(ScratchWide, 5);
    this._asm.Dec(this.Scratch(Tmp0, OperandSize.Word));
    this._asm.Jmp(normalized);
    this._asm.MarkLabel(numeratorGreater); this._asm.Jmp(normalized);
    this._asm.MarkLabel(numeratorEqual);
    this._asm.MarkLabel(normalized);

    this._asm.Mov(Reg.CX, 80);
    var loop = this._asm.DefineLabel();
    var subtract = this._asm.DefineLabel();
    var less = this._asm.DefineLabel();
    var equal = this._asm.DefineLabel();
    var appendDone = this._asm.DefineLabel();
    this._asm.MarkLabel(loop);
    this.ShiftGuardLeftOne(ScratchGuardA);

    // A nonzero high remainder limb means remainder >= 2^64 > the normalized divisor.
    this._asm.Cmp(this.Scratch(ScratchWide + 8, OperandSize.Word), 0);
    this._asm.J(Condition.NotEqual, subtract);
    var greater = this._asm.DefineLabel();
    this.CompareWords(ScratchWide, b, 4, greater, less, equal);
    this._asm.MarkLabel(greater); this._asm.Jmp(subtract);
    this._asm.MarkLabel(equal);
    this._asm.MarkLabel(subtract);
    this.SubtractFourWords(ScratchWide, b);
    this._asm.Inc(this.Scratch(ScratchGuardA, OperandSize.Word)); // low bit was zero after shift
    this._asm.Jmp(appendDone);
    this._asm.MarkLabel(less);
    this._asm.MarkLabel(appendDone);
    this.ShiftWideLeftOne(ScratchWide, 5);
    this._asm.Loop(loop);

    // Any remainder after the 80th quotient bit is sticky information for final rounding.
    this._asm.Mov(Reg.AX, this.Scratch(ScratchWide, OperandSize.Word));
    for (var i = 1; i < 5; ++i)
      this._asm.Or(Reg.AX, this.Scratch(ScratchWide + i * 2, OperandSize.Word));
    var exact = this._asm.DefineLabel();
    this._asm.Cmp(Reg.AX, 0); this._asm.J(Condition.Equal, exact);
    this._asm.Or(this.Scratch(ScratchGuardA, OperandSize.Word), 1);
    this._asm.MarkLabel(exact);

    this._asm.Mov(Reg.AX, this.Scratch(a + Meta, OperandSize.Word));
    this._asm.Xor(Reg.AX, this.Scratch(b + Meta, OperandSize.Word));
    this._asm.And(Reg.AX, SignMask);
    this._asm.Mov(this.Scratch(Tmp1, OperandSize.Word), Reg.AX);
    this.RoundGuardToCanonical(ScratchGuardA, this.Scratch(Tmp0, OperandSize.Word), this.Scratch(Tmp1, OperandSize.Word), result);
  }
}
