using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

public sealed partial class SoftwareX87Engine {
  private void BranchByClass(int value, Label finite, Label zero, Label infinity, Label nan) {
    this._asm.Mov(Reg.AX, this.Scratch(value + Meta, OperandSize.Word));
    this._asm.And(Reg.AX, ClassMask);
    this._asm.Cmp(Reg.AX, ClassZero); this._asm.J(Condition.Equal, zero);
    this._asm.Cmp(Reg.AX, ClassInfinity); this._asm.J(Condition.Equal, infinity);
    this._asm.Cmp(Reg.AX, ClassNaN); this._asm.J(Condition.Equal, nan);
    this._asm.Jmp(finite);
  }

  private void BranchIfClass(int value, ushort @class, Label target) {
    this._asm.Mov(Reg.AX, this.Scratch(value + Meta, OperandSize.Word));
    this._asm.And(Reg.AX, ClassMask);
    this._asm.Cmp(Reg.AX, @class);
    this._asm.J(Condition.Equal, target);
  }

  private void BranchIfNotClass(int value, ushort @class, Label target) {
    this._asm.Mov(Reg.AX, this.Scratch(value + Meta, OperandSize.Word));
    this._asm.And(Reg.AX, ClassMask);
    this._asm.Cmp(Reg.AX, @class);
    this._asm.J(Condition.NotEqual, target);
  }

  private void CopyCanonicalToGuard(int canonical, int guard) {
    this._asm.Mov(this.Scratch(guard, OperandSize.Word), 0);
    for (var i = 0; i < 4; ++i) {
      this._asm.Mov(Reg.AX, this.Scratch(canonical + i * 2, OperandSize.Word));
      this._asm.Mov(this.Scratch(guard + (i + 1) * 2, OperandSize.Word), Reg.AX);
    }
  }

  private void CopyGuard(int source, int destination) {
    for (var i = 0; i < 5; ++i) {
      this._asm.Mov(Reg.AX, this.Scratch(source + i * 2, OperandSize.Word));
      this._asm.Mov(this.Scratch(destination + i * 2, OperandSize.Word), Reg.AX);
    }
  }

  private void SwapCanonicalScratch(int a, int b) {
    for (var i = 0; i < SlotBytes; i += 2) {
      this._asm.Mov(Reg.AX, this.Scratch(a + i, OperandSize.Word));
      this._asm.Mov(Reg.DX, this.Scratch(b + i, OperandSize.Word));
      this._asm.Mov(this.Scratch(a + i, OperandSize.Word), Reg.DX);
      this._asm.Mov(this.Scratch(b + i, OperandSize.Word), Reg.AX);
    }
  }

  /// <summary>Shifts an 80-bit guard+significand right, merging every discarded one into sticky bit 0.</summary>
  private void ShiftGuardRight(int guard, Reg count) {
    this._asm.Mov(Reg.CX, count);
    var done = this._asm.DefineLabel();
    var huge = this._asm.DefineLabel();
    var loop = this._asm.DefineLabel();
    var noSticky = this._asm.DefineLabel();
    this._asm.Jcxz(done);
    this._asm.Cmp(Reg.CX, 80); this._asm.J(Condition.AboveOrEqual, huge);
    this._asm.Mov(this.Scratch(Tmp4, OperandSize.Word), 0);
    this._asm.MarkLabel(loop);
    this._asm.Clc();
    for (var i = 4; i >= 0; --i)
      this._asm.Rcr(this.Scratch(guard + i * 2, OperandSize.Word), 1);
    this._asm.Pushf(); this._asm.Pop(Reg.AX); this._asm.And(Reg.AX, 1);
    this._asm.Or(this.Scratch(Tmp4, OperandSize.Word), Reg.AX);
    this._asm.Loop(loop);
    this._asm.Cmp(this.Scratch(Tmp4, OperandSize.Word), 0); this._asm.J(Condition.Equal, noSticky);
    this._asm.Or(this.Scratch(guard, OperandSize.Word), 1);
    this._asm.MarkLabel(noSticky);
    this._asm.Jmp(done);

    this._asm.MarkLabel(huge);
    this._asm.Mov(Reg.AX, this.Scratch(guard, OperandSize.Word));
    for (var i = 1; i < 5; ++i)
      this._asm.Or(Reg.AX, this.Scratch(guard + i * 2, OperandSize.Word));
    for (var i = 0; i < 5; ++i)
      this._asm.Mov(this.Scratch(guard + i * 2, OperandSize.Word), 0);
    var allZero = this._asm.DefineLabel();
    this._asm.Cmp(Reg.AX, 0); this._asm.J(Condition.Equal, allZero);
    this._asm.Mov(this.Scratch(guard, OperandSize.Word), 1);
    this._asm.MarkLabel(allZero);
    this._asm.MarkLabel(done);
  }

  private void ShiftGuardLeftOne(int guard) {
    this._asm.Clc();
    for (var i = 0; i < 5; ++i)
      this._asm.Rcl(this.Scratch(guard + i * 2, OperandSize.Word), 1);
  }

  private void CompareGuards(int a, int b, Label aGreater, Label bGreater, Label equal) {
    for (var i = 4; i >= 0; --i) {
      var next = this._asm.DefineLabel();
      this._asm.Mov(Reg.AX, this.Scratch(a + i * 2, OperandSize.Word));
      this._asm.Cmp(Reg.AX, this.Scratch(b + i * 2, OperandSize.Word));
      this._asm.J(Condition.Equal, next);
      this._asm.J(Condition.Above, aGreater);
      this._asm.Jmp(bGreater);
      this._asm.MarkLabel(next);
    }
    this._asm.Jmp(equal);
  }

  private void SubtractGuards(int destination, int source) {
    this._asm.Mov(Reg.AX, this.Scratch(source, OperandSize.Word));
    this._asm.Sub(this.Scratch(destination, OperandSize.Word), Reg.AX);
    for (var i = 1; i < 5; ++i) {
      this._asm.Mov(Reg.AX, this.Scratch(source + i * 2, OperandSize.Word));
      this._asm.Sbb(this.Scratch(destination + i * 2, OperandSize.Word), Reg.AX);
    }
  }

  private void ShiftWideLeftOne(int wide, int words) {
    this._asm.Clc();
    for (var i = 0; i < words; ++i)
      this._asm.Rcl(this.Scratch(wide + i * 2, OperandSize.Word), 1);
  }

  private void ShiftWideRightOne(int wide, int words) {
    this._asm.Clc();
    for (var i = words - 1; i >= 0; --i)
      this._asm.Rcr(this.Scratch(wide + i * 2, OperandSize.Word), 1);
  }

  private void CompareWords(int a, int b, int words, Label greater, Label less, Label equal) {
    for (var i = words - 1; i >= 0; --i) {
      var next = this._asm.DefineLabel();
      this._asm.Mov(Reg.AX, this.Scratch(a + i * 2, OperandSize.Word));
      this._asm.Cmp(Reg.AX, this.Scratch(b + i * 2, OperandSize.Word));
      this._asm.J(Condition.Equal, next);
      this._asm.J(Condition.Above, greater);
      this._asm.Jmp(less);
      this._asm.MarkLabel(next);
    }
    this._asm.Jmp(equal);
  }

  private void SubtractFourWords(int destination, int source) {
    this._asm.Mov(Reg.AX, this.Scratch(source, OperandSize.Word));
    this._asm.Sub(this.Scratch(destination, OperandSize.Word), Reg.AX);
    for (var i = 1; i < 4; ++i) {
      this._asm.Mov(Reg.AX, this.Scratch(source + i * 2, OperandSize.Word));
      this._asm.Sbb(this.Scratch(destination + i * 2, OperandSize.Word), Reg.AX);
    }
    this._asm.Mov(Reg.AX, 0);
    this._asm.Sbb(this.Scratch(destination + 8, OperandSize.Word), Reg.AX);
  }

  /// <summary>Rounds five guard/significand words to the x87 PC setting and canonicalizes range.</summary>
  private void RoundGuardToCanonical(int guard, Mem exponent, Mem sign, int result) {
    var pc24 = this._asm.DefineLabel();
    var pc53 = this._asm.DefineLabel();
    var pc64 = this._asm.DefineLabel();
    var afterRound = this._asm.DefineLabel();
    this._asm.Mov(Reg.AX, this.Control);
    for (var i = 0; i < 8; ++i) this._asm.Shr(Reg.AX, 1);
    this._asm.And(Reg.AX, 3);
    this._asm.Cmp(Reg.AX, 0); this._asm.J(Condition.Equal, pc24);
    this._asm.Cmp(Reg.AX, 2); this._asm.J(Condition.Equal, pc53);
    this._asm.Jmp(pc64); // reserved PC=01 follows extended precision like real x87 implementations

    this._asm.MarkLabel(pc24); this.RoundGuardAtPrecision(guard, exponent, sign, 24); this._asm.Jmp(afterRound);
    this._asm.MarkLabel(pc53); this.RoundGuardAtPrecision(guard, exponent, sign, 53); this._asm.Jmp(afterRound);
    this._asm.MarkLabel(pc64); this.RoundGuardAtPrecision(guard, exponent, sign, 64);
    this._asm.MarkLabel(afterRound);

    for (var i = 0; i < 4; ++i) {
      this._asm.Mov(Reg.AX, this.Scratch(guard + (i + 1) * 2, OperandSize.Word));
      this._asm.Mov(this.Scratch(result + i * 2, OperandSize.Word), Reg.AX);
    }
    this._asm.Mov(Reg.AX, exponent); this._asm.Mov(this.Scratch(result + Exponent, OperandSize.Word), Reg.AX);
    this._asm.Mov(Reg.AX, sign); this._asm.And(Reg.AX, SignMask); this._asm.Mov(this.Scratch(result + Meta, OperandSize.Word), Reg.AX);
    this.FinalizeCanonicalRange(result);
  }

  private void RoundGuardAtPrecision(int guard, Mem exponent, Mem sign, int precision) {
    var guardFlag = this.Scratch(Tmp2, OperandSize.Word);
    var sticky = this.Scratch(Tmp3, OperandSize.Word);
    this._asm.Mov(guardFlag, 0); this._asm.Mov(sticky, 0);

    if (precision == 64) {
      this._asm.Mov(Reg.AX, this.Scratch(guard, OperandSize.Word));
      this._asm.Mov(Reg.DX, Reg.AX); this._asm.And(Reg.DX, 0x8000);
      for (var i = 0; i < 15; ++i) this._asm.Shr(Reg.DX, 1);
      this._asm.Mov(guardFlag, Reg.DX);
      this._asm.And(Reg.AX, 0x7FFF);
      var noSticky = this._asm.DefineLabel();
      this._asm.Cmp(Reg.AX, 0); this._asm.J(Condition.Equal, noSticky);
      this._asm.Mov(sticky, 1); this._asm.MarkLabel(noSticky);
      this._asm.Mov(this.Scratch(guard, OperandSize.Word), 0);
    } else if (precision == 53) {
      this._asm.Mov(Reg.AX, this.Scratch(guard + 2, OperandSize.Word));
      this._asm.Mov(Reg.DX, Reg.AX); this._asm.And(Reg.DX, 0x0400);
      for (var i = 0; i < 10; ++i) this._asm.Shr(Reg.DX, 1);
      this._asm.Mov(guardFlag, Reg.DX);
      this._asm.And(Reg.AX, 0x03FF); this._asm.Or(Reg.AX, this.Scratch(guard, OperandSize.Word));
      var noSticky = this._asm.DefineLabel();
      this._asm.Cmp(Reg.AX, 0); this._asm.J(Condition.Equal, noSticky);
      this._asm.Mov(sticky, 1); this._asm.MarkLabel(noSticky);
      this._asm.And(this.Scratch(guard + 2, OperandSize.Word), 0xF800);
      this._asm.Mov(this.Scratch(guard, OperandSize.Word), 0);
    } else {
      this._asm.Mov(Reg.AX, this.Scratch(guard + 6, OperandSize.Word));
      this._asm.Mov(Reg.DX, Reg.AX); this._asm.And(Reg.DX, 0x0080);
      for (var i = 0; i < 7; ++i) this._asm.Shr(Reg.DX, 1);
      this._asm.Mov(guardFlag, Reg.DX);
      this._asm.And(Reg.AX, 0x007F);
      this._asm.Or(Reg.AX, this.Scratch(guard, OperandSize.Word));
      this._asm.Or(Reg.AX, this.Scratch(guard + 2, OperandSize.Word));
      this._asm.Or(Reg.AX, this.Scratch(guard + 4, OperandSize.Word));
      var noSticky = this._asm.DefineLabel();
      this._asm.Cmp(Reg.AX, 0); this._asm.J(Condition.Equal, noSticky);
      this._asm.Mov(sticky, 1); this._asm.MarkLabel(noSticky);
      this._asm.Mov(this.Scratch(guard, OperandSize.Word), 0);
      this._asm.Mov(this.Scratch(guard + 2, OperandSize.Word), 0);
      this._asm.Mov(this.Scratch(guard + 4, OperandSize.Word), 0);
      this._asm.And(this.Scratch(guard + 6, OperandSize.Word), 0xFF00);
    }

    this.ApplyGuardRounding(guard, exponent, sign, precision, guardFlag, sticky);
  }

  private void ApplyGuardRounding(int guard, Mem exponent, Mem sign, int precision, Mem guardFlag, Mem sticky) {
    var discarded = this._asm.DefineLabel();
    var increment = this._asm.DefineLabel();
    var done = this._asm.DefineLabel();
    this._asm.Mov(Reg.AX, guardFlag); this._asm.Or(Reg.AX, sticky);
    this._asm.J(Condition.NotEqual, discarded); this._asm.Jmp(done);

    this._asm.MarkLabel(discarded);
    this.RaiseException(StatusPrecision);
    this._asm.Mov(Reg.AX, this.Control); this._asm.Mov(Reg.DX, Reg.AX);
    for (var i = 0; i < 10; ++i) this._asm.Shr(Reg.DX, 1);
    this._asm.And(Reg.DX, 3);
    var nearest = this._asm.DefineLabel(); var down = this._asm.DefineLabel(); var up = this._asm.DefineLabel();
    this._asm.Cmp(Reg.DX, 0); this._asm.J(Condition.Equal, nearest);
    this._asm.Cmp(Reg.DX, 1); this._asm.J(Condition.Equal, down);
    this._asm.Cmp(Reg.DX, 2); this._asm.J(Condition.Equal, up);
    this._asm.Jmp(done); // chop

    this._asm.MarkLabel(nearest);
    this._asm.Cmp(guardFlag, 0); this._asm.J(Condition.Equal, done);
    this._asm.Cmp(sticky, 0); this._asm.J(Condition.NotEqual, increment);
    var lsbMask = precision == 64 ? 0x0001 : precision == 53 ? 0x0800 : 0x0100;
    var lsbWord = precision == 24 ? guard + 6 : guard + 2;
    this._asm.Test(this.Scratch(lsbWord, OperandSize.Word), lsbMask);
    this._asm.J(Condition.NotEqual, increment); this._asm.Jmp(done);

    this._asm.MarkLabel(down);
    this._asm.Test(sign, SignMask); this._asm.J(Condition.NotEqual, increment); this._asm.Jmp(done);
    this._asm.MarkLabel(up);
    this._asm.Test(sign, SignMask); this._asm.J(Condition.Equal, increment); this._asm.Jmp(done);

    this._asm.MarkLabel(increment);
    if (precision == 64) {
      this._asm.Add(this.Scratch(guard + 2, OperandSize.Word), 1);
      this._asm.Adc(this.Scratch(guard + 4, OperandSize.Word), 0);
      this._asm.Adc(this.Scratch(guard + 6, OperandSize.Word), 0);
      this._asm.Adc(this.Scratch(guard + 8, OperandSize.Word), 0);
    } else if (precision == 53) {
      this._asm.Add(this.Scratch(guard + 2, OperandSize.Word), 0x0800);
      this._asm.Adc(this.Scratch(guard + 4, OperandSize.Word), 0);
      this._asm.Adc(this.Scratch(guard + 6, OperandSize.Word), 0);
      this._asm.Adc(this.Scratch(guard + 8, OperandSize.Word), 0);
    } else {
      this._asm.Add(this.Scratch(guard + 6, OperandSize.Word), 0x0100);
      this._asm.Adc(this.Scratch(guard + 8, OperandSize.Word), 0);
    }
    var noOverflow = this._asm.DefineLabel();
    this._asm.J(Condition.AboveOrEqual, noOverflow);
    for (var i = 0; i < 4; ++i)
      this._asm.Mov(this.Scratch(guard + 2 + i * 2, OperandSize.Word), 0);
    this._asm.Mov(this.Scratch(guard + 8, OperandSize.Word), 0x8000);
    this._asm.Inc(exponent);
    this._asm.MarkLabel(noOverflow);
    this._asm.MarkLabel(done);
  }

  private void FinalizeCanonicalRange(int result) {
    var okay = this._asm.DefineLabel();
    var overflow = this._asm.DefineLabel();
    var underflow = this._asm.DefineLabel();
    var done = this._asm.DefineLabel();
    this._asm.Mov(Reg.AX, this.Scratch(result + Exponent, OperandSize.Word));
    this._asm.Cmp(Reg.AX, 16383); this._asm.J(Condition.Greater, overflow);
    this._asm.Cmp(Reg.AX, -16445); this._asm.J(Condition.Less, underflow);
    this._asm.Jmp(okay);

    this._asm.MarkLabel(overflow);
    this.RaiseException(StatusOverflow);
    this.RaiseException(StatusPrecision);
    this.EmitOverflowResult(result);
    this._asm.Jmp(done);

    this._asm.MarkLabel(underflow);
    this.RaiseException(StatusUnderflow);
    this.RaiseException(StatusPrecision);
    this.EmitUnderflowResult(result);
    this._asm.Jmp(done);

    this._asm.MarkLabel(okay);
    this._asm.MarkLabel(done);
  }

  private void EmitOverflowResult(int result) {
    var infinity = this._asm.DefineLabel();
    var maxFinite = this._asm.DefineLabel();
    var finish = this._asm.DefineLabel();
    this._asm.Mov(Reg.AX, this.Control); this._asm.Mov(Reg.DX, Reg.AX);
    for (var i = 0; i < 10; ++i) this._asm.Shr(Reg.DX, 1);
    this._asm.And(Reg.DX, 3);
    this._asm.Cmp(Reg.DX, 0); this._asm.J(Condition.Equal, infinity);
    this._asm.Cmp(Reg.DX, 3); this._asm.J(Condition.Equal, maxFinite);
    var negative = this._asm.DefineLabel();
    this._asm.Test(this.Scratch(result + Meta, OperandSize.Word), SignMask); this._asm.J(Condition.NotEqual, negative);
    this._asm.Cmp(Reg.DX, 2); this._asm.J(Condition.Equal, infinity); this._asm.Jmp(maxFinite);
    this._asm.MarkLabel(negative);
    this._asm.Cmp(Reg.DX, 1); this._asm.J(Condition.Equal, infinity); this._asm.Jmp(maxFinite);

    this._asm.MarkLabel(infinity);
    for (var i = 0; i < 3; ++i) this._asm.Mov(this.Scratch(result + i * 2, OperandSize.Word), 0);
    this._asm.Mov(this.Scratch(result + Sig3, OperandSize.Word), 0x8000);
    this._asm.Mov(Reg.AX, this.Scratch(result + Meta, OperandSize.Word)); this._asm.And(Reg.AX, SignMask); this._asm.Or(Reg.AX, ClassInfinity);
    this._asm.Mov(this.Scratch(result + Meta, OperandSize.Word), Reg.AX); this._asm.Jmp(finish);

    this._asm.MarkLabel(maxFinite);
    for (var i = 0; i < 4; ++i) this._asm.Mov(this.Scratch(result + i * 2, OperandSize.Word), 0xFFFF);
    this._asm.Mov(this.Scratch(result + Exponent, OperandSize.Word), 16383);
    this._asm.And(this.Scratch(result + Meta, OperandSize.Word), SignMask);
    this._asm.MarkLabel(finish);
  }

  private void EmitUnderflowResult(int result) {
    var toMinSubnormal = this._asm.DefineLabel();
    var toZero = this._asm.DefineLabel();
    var finish = this._asm.DefineLabel();
    this._asm.Mov(Reg.AX, this.Control); this._asm.Mov(Reg.DX, Reg.AX);
    for (var i = 0; i < 10; ++i) this._asm.Shr(Reg.DX, 1);
    this._asm.And(Reg.DX, 3);

    this._asm.Cmp(Reg.DX, 1);
    var notDown = this._asm.DefineLabel(); this._asm.J(Condition.NotEqual, notDown);
    this._asm.Test(this.Scratch(result + Meta, OperandSize.Word), SignMask); this._asm.J(Condition.NotEqual, toMinSubnormal); this._asm.Jmp(toZero);
    this._asm.MarkLabel(notDown);
    this._asm.Cmp(Reg.DX, 2);
    var notUp = this._asm.DefineLabel(); this._asm.J(Condition.NotEqual, notUp);
    this._asm.Test(this.Scratch(result + Meta, OperandSize.Word), SignMask); this._asm.J(Condition.Equal, toMinSubnormal); this._asm.Jmp(toZero);
    this._asm.MarkLabel(notUp);
    this._asm.Cmp(Reg.DX, 3); this._asm.J(Condition.Equal, toZero);

    // nearest/even: only exponent -16446 can reach a minimum subnormal, and exact half ties to zero.
    this._asm.Cmp(this.Scratch(result + Exponent, OperandSize.Word), -16446); this._asm.J(Condition.NotEqual, toZero);
    this._asm.Cmp(this.Scratch(result + Sig3, OperandSize.Word), 0x8000);
    var greaterHalf = this._asm.DefineLabel(); this._asm.J(Condition.Above, greaterHalf); this._asm.J(Condition.Below, toZero);
    this._asm.Mov(Reg.AX, this.Scratch(result + Sig0, OperandSize.Word));
    this._asm.Or(Reg.AX, this.Scratch(result + Sig1, OperandSize.Word)); this._asm.Or(Reg.AX, this.Scratch(result + Sig2, OperandSize.Word));
    this._asm.Cmp(Reg.AX, 0); this._asm.J(Condition.Equal, toZero);
    this._asm.MarkLabel(greaterHalf);

    this._asm.MarkLabel(toMinSubnormal);
    for (var i = 0; i < 3; ++i) this._asm.Mov(this.Scratch(result + i * 2, OperandSize.Word), 0);
    this._asm.Mov(this.Scratch(result + Sig3, OperandSize.Word), 0x8000);
    this._asm.Mov(this.Scratch(result + Exponent, OperandSize.Word), -16445);
    this._asm.And(this.Scratch(result + Meta, OperandSize.Word), SignMask);
    this._asm.Jmp(finish);

    this._asm.MarkLabel(toZero);
    for (var i = 0; i < 4; ++i) this._asm.Mov(this.Scratch(result + i * 2, OperandSize.Word), 0);
    this._asm.Mov(this.Scratch(result + Exponent, OperandSize.Word), 0);
    this._asm.Mov(Reg.AX, this.Scratch(result + Meta, OperandSize.Word)); this._asm.And(Reg.AX, SignMask); this._asm.Or(Reg.AX, ClassZero);
    this._asm.Mov(this.Scratch(result + Meta, OperandSize.Word), Reg.AX);
    this._asm.MarkLabel(finish);
  }
}
