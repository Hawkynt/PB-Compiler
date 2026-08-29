using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

public sealed partial class SoftwareX87Engine {
  private const int Tmp0 = ScratchMisc;
  private const int Tmp1 = ScratchMisc + 2;
  private const int Tmp2 = ScratchMisc + 4;
  private const int Tmp3 = ScratchMisc + 6;
  private const int Tmp4 = ScratchMisc + 8;

  internal void PreserveIntegerState() {
    this._asm.Pushf();
    this._asm.Push(Reg.AX); this._asm.Push(Reg.BX); this._asm.Push(Reg.CX); this._asm.Push(Reg.DX);
    this._asm.Push(Reg.SI); this._asm.Push(Reg.DI); this._asm.Push(Reg.BP);
  }

  internal void RestoreIntegerState() {
    this._asm.Pop(Reg.BP); this._asm.Pop(Reg.DI); this._asm.Pop(Reg.SI);
    this._asm.Pop(Reg.DX); this._asm.Pop(Reg.CX); this._asm.Pop(Reg.BX); this._asm.Pop(Reg.AX);
    this._asm.Popf();
  }

  private bool EmitLoadReal(Mem source, int bits) {
    this.PreserveIntegerState();
    switch (bits) {
      case 32: this.ConvertFloat32ToCanonical(source, ScratchA); break;
      case 64: this.ConvertFloat64ToCanonical(source, ScratchA); break;
      case 80: this.ConvertFloat80ToCanonical(source, ScratchA); break;
      default: throw new ArgumentOutOfRangeException(nameof(bits));
    }
    this.EmitPushEmpty();
    this.CopyScratchToSlot(ScratchA, 0);
    this.RestoreIntegerState();
    return true;
  }

  private bool EmitStoreReal(Mem destination, int bits, bool pop) {
    this.PreserveIntegerState();
    this.CopySlotToScratch(0, ScratchA);
    switch (bits) {
      case 32: this.ConvertCanonicalToFloat32(ScratchA, destination); break;
      case 64: this.ConvertCanonicalToFloat64(ScratchA, destination); break;
      case 80: this.ConvertCanonicalToFloat80(ScratchA, destination); break;
      default: throw new ArgumentOutOfRangeException(nameof(bits));
    }
    if (pop) this.EmitPop();
    this.RestoreIntegerState();
    return true;
  }

  private bool EmitLoadInteger(Mem source, int bits) {
    this.PreserveIntegerState();
    this.ConvertIntegerToCanonical(source, bits, ScratchA);
    this.EmitPushEmpty();
    this.CopyScratchToSlot(ScratchA, 0);
    this.RestoreIntegerState();
    return true;
  }

  private bool EmitStoreInteger(Mem destination, int bits, bool pop) {
    this.PreserveIntegerState();
    this.CopySlotToScratch(0, ScratchA);
    this.ConvertCanonicalToInteger(ScratchA, destination, bits);
    if (pop) this.EmitPop();
    this.RestoreIntegerState();
    return true;
  }

  #region input conversions

  private void ConvertFloat80ToCanonical(Mem source, int destination) {
    for (var i = 0; i < 4; ++i) {
      this._asm.Mov(Reg.AX, source.Offset(i * 2).WithSize(OperandSize.Word));
      this._asm.Mov(this.Scratch(destination + i * 2, OperandSize.Word), Reg.AX);
    }
    this._asm.Mov(Reg.AX, source.Offset(8).WithSize(OperandSize.Word));
    this._asm.Mov(Reg.DX, Reg.AX); this._asm.And(Reg.DX, 0x8000); this._asm.Shr(Reg.DX, 15);
    this._asm.And(Reg.AX, 0x7FFF);
    this._asm.Mov(this.Scratch(Tmp0, OperandSize.Word), Reg.AX); // raw exponent
    this._asm.Mov(this.Scratch(destination + Meta, OperandSize.Word), Reg.DX);

    var nonZeroExp = this._asm.DefineLabel();
    var zero = this._asm.DefineLabel();
    var special = this._asm.DefineLabel();
    var done = this._asm.DefineLabel();
    this._asm.Cmp(Reg.AX, 0); this._asm.J(Condition.NotEqual, nonZeroExp);

    this._asm.Mov(Reg.AX, this.Scratch(destination + Sig0, OperandSize.Word));
    this._asm.Or(Reg.AX, this.Scratch(destination + Sig1, OperandSize.Word));
    this._asm.Or(Reg.AX, this.Scratch(destination + Sig2, OperandSize.Word));
    this._asm.Or(Reg.AX, this.Scratch(destination + Sig3, OperandSize.Word));
    this._asm.J(Condition.Equal, zero);
    this._asm.Mov(this.Scratch(destination + Exponent, OperandSize.Word), -16382);
    this.NormalizeCanonicalSignificand(destination);
    this._asm.Jmp(done);

    this._asm.MarkLabel(zero);
    this._asm.Or(this.Scratch(destination + Meta, OperandSize.Word), ClassZero);
    this._asm.Mov(this.Scratch(destination + Exponent, OperandSize.Word), 0);
    this._asm.Jmp(done);

    this._asm.MarkLabel(nonZeroExp);
    this._asm.Cmp(this.Scratch(Tmp0, OperandSize.Word), 0x7FFF);
    this._asm.J(Condition.Equal, special);
    this._asm.Mov(Reg.AX, this.Scratch(Tmp0, OperandSize.Word)); this._asm.Sub(Reg.AX, 16383);
    this._asm.Mov(this.Scratch(destination + Exponent, OperandSize.Word), Reg.AX);
    this._asm.Jmp(done);

    this._asm.MarkLabel(special);
    var nan = this._asm.DefineLabel();
    this._asm.Cmp(this.Scratch(destination + Sig3, OperandSize.Word), 0x8000); this._asm.J(Condition.NotEqual, nan);
    this._asm.Mov(Reg.AX, this.Scratch(destination + Sig0, OperandSize.Word));
    this._asm.Or(Reg.AX, this.Scratch(destination + Sig1, OperandSize.Word));
    this._asm.Or(Reg.AX, this.Scratch(destination + Sig2, OperandSize.Word));
    this._asm.J(Condition.NotEqual, nan);
    this._asm.Or(this.Scratch(destination + Meta, OperandSize.Word), ClassInfinity);
    this._asm.Jmp(done);
    this._asm.MarkLabel(nan);
    this._asm.Or(this.Scratch(destination + Meta, OperandSize.Word), ClassNaN);
    this._asm.Or(this.Scratch(destination + Sig3, OperandSize.Word), 0xC000); // canonical quiet NaN/int bit
    this._asm.MarkLabel(done);
  }

  private void ConvertFloat32ToCanonical(Mem source, int destination) {
    this.ZeroCanonical(destination);
    this._asm.Mov(Reg.AX, source.WithSize(OperandSize.Word)); this._asm.Mov(this.Scratch(Tmp0, OperandSize.Word), Reg.AX);
    this._asm.Mov(Reg.DX, source.Offset(2).WithSize(OperandSize.Word)); this._asm.Mov(this.Scratch(Tmp1, OperandSize.Word), Reg.DX);

    this._asm.Mov(Reg.AX, Reg.DX); this._asm.Shr(Reg.AX, 15);
    this._asm.Mov(this.Scratch(destination + Meta, OperandSize.Word), Reg.AX);
    this._asm.Mov(Reg.AX, Reg.DX);
    for (var i = 0; i < 7; ++i) this._asm.Shr(Reg.AX, 1);
    this._asm.And(Reg.AX, 0x00FF); this._asm.Mov(this.Scratch(Tmp2, OperandSize.Word), Reg.AX);
    this._asm.And(Reg.DX, 0x007F); // mantissa high seven bits

    var expZero = this._asm.DefineLabel();
    var special = this._asm.DefineLabel();
    var build = this._asm.DefineLabel();
    var zero = this._asm.DefineLabel();
    var done = this._asm.DefineLabel();
    this._asm.Cmp(this.Scratch(Tmp2, OperandSize.Word), 0); this._asm.J(Condition.Equal, expZero);
    this._asm.Cmp(this.Scratch(Tmp2, OperandSize.Word), 255); this._asm.J(Condition.Equal, special);
    this._asm.Or(Reg.DX, 0x0080);
    this._asm.Mov(Reg.AX, this.Scratch(Tmp2, OperandSize.Word)); this._asm.Sub(Reg.AX, 127);
    this._asm.Mov(this.Scratch(destination + Exponent, OperandSize.Word), Reg.AX);
    this._asm.Jmp(build);

    this._asm.MarkLabel(expZero);
    this._asm.Mov(Reg.AX, this.Scratch(Tmp0, OperandSize.Word)); this._asm.Or(Reg.AX, Reg.DX);
    this._asm.J(Condition.Equal, zero);
    this._asm.Mov(this.Scratch(destination + Exponent, OperandSize.Word), -126);
    var normalize = this._asm.DefineLabel();
    this._asm.MarkLabel(normalize);
    this._asm.Test(Reg.DX, 0x0080); this._asm.J(Condition.NotEqual, build);
    this._asm.Shl(this.Scratch(Tmp0, OperandSize.Word), 1); this._asm.Rcl(Reg.DX, 1);
    this._asm.Dec(this.Scratch(destination + Exponent, OperandSize.Word));
    this._asm.Jmp(normalize);

    this._asm.MarkLabel(zero);
    this._asm.Or(this.Scratch(destination + Meta, OperandSize.Word), ClassZero);
    this._asm.Jmp(done);

    this._asm.MarkLabel(special);
    this._asm.Mov(Reg.AX, this.Scratch(Tmp0, OperandSize.Word)); this._asm.Or(Reg.AX, Reg.DX);
    var nan = this._asm.DefineLabel();
    this._asm.J(Condition.NotEqual, nan);
    this._asm.Or(this.Scratch(destination + Meta, OperandSize.Word), ClassInfinity);
    this._asm.Mov(this.Scratch(destination + Sig3, OperandSize.Word), 0x8000);
    this._asm.Jmp(done);
    this._asm.MarkLabel(nan);
    this._asm.Or(Reg.DX, 0x00C0);
    this._asm.Or(this.Scratch(destination + Meta, OperandSize.Word), ClassNaN);

    this._asm.MarkLabel(build);
    this._asm.Mov(Reg.AX, this.Scratch(Tmp0, OperandSize.Word));
    this._asm.Mov(Reg.BX, Reg.AX); this._asm.And(Reg.BX, 0x00FF);
    for (var i = 0; i < 8; ++i) this._asm.Shl(Reg.BX, 1);
    this._asm.Mov(this.Scratch(destination + Sig2, OperandSize.Word), Reg.BX);
    for (var i = 0; i < 8; ++i) this._asm.Shr(Reg.AX, 1);
    for (var i = 0; i < 8; ++i) this._asm.Shl(Reg.DX, 1);
    this._asm.Or(Reg.AX, Reg.DX); this._asm.Mov(this.Scratch(destination + Sig3, OperandSize.Word), Reg.AX);
    this._asm.MarkLabel(done);
  }

  private void ConvertFloat64ToCanonical(Mem source, int destination) {
    this.ZeroCanonical(destination);
    for (var i = 0; i < 3; ++i) {
      this._asm.Mov(Reg.AX, source.Offset(i * 2).WithSize(OperandSize.Word));
      this._asm.Mov(this.Scratch(destination + i * 2, OperandSize.Word), Reg.AX);
    }
    this._asm.Mov(Reg.DX, source.Offset(6).WithSize(OperandSize.Word));
    this._asm.Mov(Reg.AX, Reg.DX); this._asm.Shr(Reg.AX, 15); this._asm.Mov(this.Scratch(destination + Meta, OperandSize.Word), Reg.AX);
    this._asm.Mov(Reg.AX, Reg.DX);
    for (var i = 0; i < 4; ++i) this._asm.Shr(Reg.AX, 1);
    this._asm.And(Reg.AX, 0x07FF); this._asm.Mov(this.Scratch(Tmp2, OperandSize.Word), Reg.AX);
    this._asm.And(Reg.DX, 0x000F); this._asm.Mov(this.Scratch(destination + Sig3, OperandSize.Word), Reg.DX);

    var expZero = this._asm.DefineLabel();
    var special = this._asm.DefineLabel();
    var normalize = this._asm.DefineLabel();
    var shiftToCanonical = this._asm.DefineLabel();
    var zero = this._asm.DefineLabel();
    var done = this._asm.DefineLabel();
    this._asm.Cmp(this.Scratch(Tmp2, OperandSize.Word), 0); this._asm.J(Condition.Equal, expZero);
    this._asm.Cmp(this.Scratch(Tmp2, OperandSize.Word), 0x07FF); this._asm.J(Condition.Equal, special);
    this._asm.Or(this.Scratch(destination + Sig3, OperandSize.Word), 0x0010);
    this._asm.Mov(Reg.AX, this.Scratch(Tmp2, OperandSize.Word)); this._asm.Sub(Reg.AX, 1023);
    this._asm.Mov(this.Scratch(destination + Exponent, OperandSize.Word), Reg.AX);
    this._asm.Jmp(shiftToCanonical);

    this._asm.MarkLabel(expZero);
    this._asm.Mov(Reg.AX, this.Scratch(destination + Sig0, OperandSize.Word));
    this._asm.Or(Reg.AX, this.Scratch(destination + Sig1, OperandSize.Word));
    this._asm.Or(Reg.AX, this.Scratch(destination + Sig2, OperandSize.Word));
    this._asm.Or(Reg.AX, this.Scratch(destination + Sig3, OperandSize.Word));
    this._asm.J(Condition.Equal, zero);
    this._asm.Mov(this.Scratch(destination + Exponent, OperandSize.Word), -1022);
    this._asm.MarkLabel(normalize);
    this._asm.Test(this.Scratch(destination + Sig3, OperandSize.Word), 0x0010);
    this._asm.J(Condition.NotEqual, shiftToCanonical);
    this.ShiftCanonicalLeftOne(destination);
    this._asm.Dec(this.Scratch(destination + Exponent, OperandSize.Word));
    this._asm.Jmp(normalize);

    this._asm.MarkLabel(zero);
    this._asm.Or(this.Scratch(destination + Meta, OperandSize.Word), ClassZero);
    this._asm.Jmp(done);

    this._asm.MarkLabel(special);
    this._asm.Mov(Reg.AX, this.Scratch(destination + Sig0, OperandSize.Word));
    this._asm.Or(Reg.AX, this.Scratch(destination + Sig1, OperandSize.Word));
    this._asm.Or(Reg.AX, this.Scratch(destination + Sig2, OperandSize.Word));
    this._asm.Or(Reg.AX, this.Scratch(destination + Sig3, OperandSize.Word));
    var nan = this._asm.DefineLabel();
    this._asm.J(Condition.NotEqual, nan);
    this._asm.Or(this.Scratch(destination + Meta, OperandSize.Word), ClassInfinity);
    this._asm.Mov(this.Scratch(destination + Sig3, OperandSize.Word), 0x8000);
    this._asm.Jmp(done);
    this._asm.MarkLabel(nan);
    this._asm.Or(this.Scratch(destination + Meta, OperandSize.Word), ClassNaN);
    this._asm.Or(this.Scratch(destination + Sig3, OperandSize.Word), 0x0018); // hidden + quiet payload before shift

    this._asm.MarkLabel(shiftToCanonical);
    for (var i = 0; i < 11; ++i) this.ShiftCanonicalLeftOne(destination);
    this._asm.MarkLabel(done);
  }

  private void ConvertIntegerToCanonical(Mem source, int bits, int destination) {
    this.ZeroCanonical(destination);
    var words = bits / 16;
    for (var i = 0; i < words; ++i) {
      this._asm.Mov(Reg.AX, source.Offset(i * 2).WithSize(OperandSize.Word));
      this._asm.Mov(this.Scratch(destination + i * 2, OperandSize.Word), Reg.AX);
    }
    var positive = this._asm.DefineLabel();
    var zero = this._asm.DefineLabel();
    var normalize = this._asm.DefineLabel();
    var done = this._asm.DefineLabel();
    this._asm.Test(this.Scratch(destination + (words - 1) * 2, OperandSize.Word), 0x8000);
    this._asm.J(Condition.Equal, positive);
    this._asm.Or(this.Scratch(destination + Meta, OperandSize.Word), SignMask);
    this.NegateCanonicalWords(destination, words);
    this._asm.MarkLabel(positive);
    this._asm.Mov(Reg.AX, this.Scratch(destination + Sig0, OperandSize.Word));
    for (var i = 1; i < 4; ++i) this._asm.Or(Reg.AX, this.Scratch(destination + i * 2, OperandSize.Word));
    this._asm.J(Condition.Equal, zero);
    this._asm.Mov(this.Scratch(destination + Exponent, OperandSize.Word), 63);
    this._asm.MarkLabel(normalize);
    this._asm.Test(this.Scratch(destination + Sig3, OperandSize.Word), 0x8000);
    this._asm.J(Condition.NotEqual, done);
    this.ShiftCanonicalLeftOne(destination);
    this._asm.Dec(this.Scratch(destination + Exponent, OperandSize.Word));
    this._asm.Jmp(normalize);
    this._asm.MarkLabel(zero);
    this._asm.Or(this.Scratch(destination + Meta, OperandSize.Word), ClassZero);
    this._asm.Mov(this.Scratch(destination + Exponent, OperandSize.Word), 0);
    this._asm.MarkLabel(done);
  }

  #endregion

  #region output conversions

  private void ConvertCanonicalToFloat80(int source, Mem destination) {
    var zero = this._asm.DefineLabel();
    var infinity = this._asm.DefineLabel();
    var nan = this._asm.DefineLabel();
    var finite = this._asm.DefineLabel();
    var emit = this._asm.DefineLabel();
    this.ClassifyCanonical(source, finite, zero, infinity, nan);

    this._asm.MarkLabel(zero);
    for (var i = 0; i < 4; ++i) this._asm.Mov(this.Scratch(ScratchC + i * 2, OperandSize.Word), 0);
    this._asm.Mov(Reg.AX, this.Scratch(source + Meta, OperandSize.Word)); this._asm.And(Reg.AX, SignMask);
    for (var i = 0; i < 15; ++i) this._asm.Shl(Reg.AX, 1);
    this._asm.Mov(this.Scratch(ScratchC + 8, OperandSize.Word), Reg.AX); this._asm.Jmp(emit);

    this._asm.MarkLabel(infinity);
    for (var i = 0; i < 3; ++i) this._asm.Mov(this.Scratch(ScratchC + i * 2, OperandSize.Word), 0);
    this._asm.Mov(this.Scratch(ScratchC + 6, OperandSize.Word), 0x8000);
    this.BuildExtendedSignExponent(source, 0x7FFF, ScratchC + 8); this._asm.Jmp(emit);

    this._asm.MarkLabel(nan);
    for (var i = 0; i < 4; ++i) {
      this._asm.Mov(Reg.AX, this.Scratch(source + i * 2, OperandSize.Word));
      this._asm.Mov(this.Scratch(ScratchC + i * 2, OperandSize.Word), Reg.AX);
    }
    this._asm.Or(this.Scratch(ScratchC + 6, OperandSize.Word), 0xC000);
    this.BuildExtendedSignExponent(source, 0x7FFF, ScratchC + 8); this._asm.Jmp(emit);

    this._asm.MarkLabel(finite);
    this.CopyScratch(source, ScratchC);
    var normal = this._asm.DefineLabel();
    var underflow = this._asm.DefineLabel();
    var overflow = this._asm.DefineLabel();
    this._asm.Mov(Reg.AX, this.Scratch(ScratchC + Exponent, OperandSize.Word));
    this._asm.Cmp(Reg.AX, 16383); this._asm.J(Condition.Greater, overflow);
    this._asm.Cmp(Reg.AX, -16382); this._asm.J(Condition.Less, underflow);
    this._asm.Add(Reg.AX, 16383); this.BuildExtendedSignExponentFromAx(source, ScratchC + 8); this._asm.Jmp(emit);
    this._asm.MarkLabel(underflow);
    this._asm.Mov(Reg.CX, -16382); this._asm.Sub(Reg.CX, this.Scratch(ScratchC + Exponent, OperandSize.Word));
    this.ShiftCanonicalRightSticky(ScratchC, Reg.CX, round: true);
    this.BuildExtendedSignExponent(source, 0, ScratchC + 8); this.SetStatusBits(0x0030); this._asm.Jmp(emit);
    this._asm.MarkLabel(overflow);
    for (var i = 0; i < 3; ++i) this._asm.Mov(this.Scratch(ScratchC + i * 2, OperandSize.Word), 0);
    this._asm.Mov(this.Scratch(ScratchC + 6, OperandSize.Word), 0x8000);
    this.BuildExtendedSignExponent(source, 0x7FFF, ScratchC + 8); this.SetStatusBits(0x0028);

    this._asm.MarkLabel(emit);
    for (var i = 0; i < 5; ++i) {
      this._asm.Mov(Reg.AX, this.Scratch(ScratchC + i * 2, OperandSize.Word));
      this._asm.Mov(destination.Offset(i * 2).WithSize(OperandSize.Word), Reg.AX);
    }
  }

  private void ConvertCanonicalToFloat32(int source, Mem destination) => this.ConvertCanonicalToIeee(source, destination, 32);
  private void ConvertCanonicalToFloat64(int source, Mem destination) => this.ConvertCanonicalToIeee(source, destination, 64);

  private void ConvertCanonicalToIeee(int source, Mem destination, int bits) {
    var precision = bits == 32 ? 24 : 53;
    var minExp = bits == 32 ? -126 : -1022;
    var maxExp = bits == 32 ? 127 : 1023;
    var bias = bits == 32 ? 127 : 1023;
    var baseDiscard = 64 - precision;

    var zero = this._asm.DefineLabel(); var infinity = this._asm.DefineLabel(); var nan = this._asm.DefineLabel(); var finite = this._asm.DefineLabel();
    var pack = this._asm.DefineLabel(); var emitInf = this._asm.DefineLabel(); var done = this._asm.DefineLabel();
    this.ClassifyCanonical(source, finite, zero, infinity, nan);

    this._asm.MarkLabel(zero);
    this.ZeroIeeeScratch(bits);
    this.ApplyIeeeSign(source, bits); this._asm.Jmp(done);

    this._asm.MarkLabel(infinity);
    this.ZeroIeeeScratch(bits); this.SetIeeeExponent(bits, bits == 32 ? 255 : 2047); this.ApplyIeeeSign(source, bits); this._asm.Jmp(done);

    this._asm.MarkLabel(nan);
    this.ZeroIeeeScratch(bits);
    if (bits == 32) this._asm.Mov(this.Scratch(ScratchC, OperandSize.Word), 1);
    else this._asm.Mov(this.Scratch(ScratchC, OperandSize.Word), 1);
    this.SetIeeeExponent(bits, bits == 32 ? 255 : 2047); this.ApplyIeeeSign(source, bits); this._asm.Jmp(done);

    this._asm.MarkLabel(finite);
    this.CopyScratch(source, ScratchC);
    this._asm.Mov(Reg.AX, this.Scratch(source + Exponent, OperandSize.Word));
    this._asm.Cmp(Reg.AX, maxExp); this._asm.J(Condition.Greater, emitInf);
    this._asm.Mov(Reg.DX, Reg.AX); // original exponent
    this._asm.Mov(Reg.CX, baseDiscard);
    var normal = this._asm.DefineLabel();
    this._asm.Cmp(Reg.AX, minExp); this._asm.J(Condition.GreaterOrEqual, normal);
    this._asm.Mov(Reg.CX, minExp); this._asm.Sub(Reg.CX, Reg.AX); this._asm.Add(Reg.CX, baseDiscard);
    this._asm.MarkLabel(normal);
    this.ShiftCanonicalRightSticky(ScratchC, Reg.CX, round: true);

    // Rounding can carry a p-bit significand into p+1 bits.
    var noCarry = this._asm.DefineLabel();
    if (bits == 32) this._asm.Test(this.Scratch(ScratchC + 2, OperandSize.Word), 0x0100);
    else this._asm.Test(this.Scratch(ScratchC + 6, OperandSize.Word), 0x0020);
    this._asm.J(Condition.Equal, noCarry);
    this.ShiftCanonicalRightOne(ScratchC);
    this._asm.Inc(Reg.DX);
    this._asm.MarkLabel(noCarry);
    this._asm.Cmp(Reg.DX, maxExp); this._asm.J(Condition.Greater, emitInf);

    this.ZeroIeeeScratch(bits, preserveMantissa: true);
    var subnormal = this._asm.DefineLabel();
    this._asm.Cmp(Reg.DX, minExp); this._asm.J(Condition.Less, subnormal);
    this._asm.Mov(Reg.AX, Reg.DX); this._asm.Add(Reg.AX, bias); this.SetIeeeExponentFromAx(bits);
    this._asm.Jmp(pack);
    this._asm.MarkLabel(subnormal);
    this.SetIeeeExponent(bits, 0); this.SetStatusBits(0x0030);

    this._asm.MarkLabel(pack);
    this.PackRetainedMantissaToIeee(bits);
    this.ApplyIeeeSign(source, bits); this._asm.Jmp(done);

    this._asm.MarkLabel(emitInf);
    this.ZeroIeeeScratch(bits); this.SetIeeeExponent(bits, bits == 32 ? 255 : 2047); this.ApplyIeeeSign(source, bits); this.SetStatusBits(0x0028);

    this._asm.MarkLabel(done);
    var words = bits / 16;
    for (var i = 0; i < words; ++i) {
      this._asm.Mov(Reg.AX, this.Scratch(ScratchD + i * 2, OperandSize.Word));
      this._asm.Mov(destination.Offset(i * 2).WithSize(OperandSize.Word), Reg.AX);
    }
  }

  private void ConvertCanonicalToInteger(int source, Mem destination, int bits) {
    var invalid = this._asm.DefineLabel(); var finite = this._asm.DefineLabel(); var zero = this._asm.DefineLabel(); var range = this._asm.DefineLabel(); var done = this._asm.DefineLabel();
    this.ClassifyCanonical(source, finite, zero, invalid, invalid);
    this._asm.MarkLabel(zero);
    for (var i = 0; i < bits / 16; ++i) this._asm.Mov(destination.Offset(i * 2).WithSize(OperandSize.Word), 0);
    this._asm.Jmp(done);

    this._asm.MarkLabel(finite);
    this.CopyScratch(source, ScratchC);
    this._asm.Mov(Reg.AX, this.Scratch(source + Exponent, OperandSize.Word));
    this._asm.Cmp(Reg.AX, 63); this._asm.J(Condition.Greater, invalid);
    this._asm.Mov(Reg.CX, 63); this._asm.Sub(Reg.CX, Reg.AX);
    var alreadyInteger = this._asm.DefineLabel();
    this._asm.Cmp(Reg.CX, 0); this._asm.J(Condition.LessOrEqual, alreadyInteger);
    this.ShiftCanonicalRightSticky(ScratchC, Reg.CX, round: true);
    this._asm.MarkLabel(alreadyInteger);

    // ScratchC now holds magnitude right-aligned. Check signed destination range.
    var words = bits / 16;
    for (var i = words; i < 4; ++i) {
      this._asm.Cmp(this.Scratch(ScratchC + i * 2, OperandSize.Word), 0);
      this._asm.J(Condition.NotEqual, invalid);
    }
    this._asm.Mov(Reg.AX, this.Scratch(ScratchC + (words - 1) * 2, OperandSize.Word));
    var negative = this._asm.DefineLabel();
    this._asm.Test(this.Scratch(source + Meta, OperandSize.Word), SignMask); this._asm.J(Condition.NotEqual, negative);
    this._asm.Test(Reg.AX, 0x8000); this._asm.J(Condition.NotEqual, invalid); this._asm.Jmp(range);
    this._asm.MarkLabel(negative);
    this._asm.Cmp(Reg.AX, 0x8000); this._asm.J(Condition.Above, invalid);
    var belowMin = this._asm.DefineLabel();
    this._asm.J(Condition.Below, belowMin);
    for (var i = 0; i < words - 1; ++i) {
      this._asm.Cmp(this.Scratch(ScratchC + i * 2, OperandSize.Word), 0);
      this._asm.J(Condition.NotEqual, invalid);
    }
    this._asm.MarkLabel(belowMin);
    this.NegateCanonicalWords(ScratchC, words);

    this._asm.MarkLabel(range);
    for (var i = 0; i < words; ++i) {
      this._asm.Mov(Reg.AX, this.Scratch(ScratchC + i * 2, OperandSize.Word));
      this._asm.Mov(destination.Offset(i * 2).WithSize(OperandSize.Word), Reg.AX);
    }
    this._asm.Jmp(done);

    this._asm.MarkLabel(invalid);
    for (var i = 0; i < words; ++i) this._asm.Mov(destination.Offset(i * 2).WithSize(OperandSize.Word), 0);
    this._asm.Mov(destination.Offset((words - 1) * 2).WithSize(OperandSize.Word), 0x8000);
    this.SetStatusBits(0x0001);
    this._asm.MarkLabel(done);
  }

  #endregion

  #region conversion helpers

  private void ZeroCanonical(int destination) {
    for (var i = 0; i < SlotBytes; i += 2) this._asm.Mov(this.Scratch(destination + i, OperandSize.Word), 0);
  }

  private void NormalizeCanonicalSignificand(int value) {
    var done = this._asm.DefineLabel(); var loop = this._asm.DefineLabel();
    this._asm.MarkLabel(loop);
    this._asm.Test(this.Scratch(value + Sig3, OperandSize.Word), 0x8000); this._asm.J(Condition.NotEqual, done);
    this.ShiftCanonicalLeftOne(value); this._asm.Dec(this.Scratch(value + Exponent, OperandSize.Word)); this._asm.Jmp(loop);
    this._asm.MarkLabel(done);
  }

  internal void ShiftCanonicalLeftOne(int value) {
    this._asm.Shl(this.Scratch(value + Sig0, OperandSize.Word), 1);
    this._asm.Rcl(this.Scratch(value + Sig1, OperandSize.Word), 1);
    this._asm.Rcl(this.Scratch(value + Sig2, OperandSize.Word), 1);
    this._asm.Rcl(this.Scratch(value + Sig3, OperandSize.Word), 1);
  }

  internal void ShiftCanonicalRightOne(int value) {
    this._asm.Shr(this.Scratch(value + Sig3, OperandSize.Word), 1);
    this._asm.Rcr(this.Scratch(value + Sig2, OperandSize.Word), 1);
    this._asm.Rcr(this.Scratch(value + Sig1, OperandSize.Word), 1);
    this._asm.Rcr(this.Scratch(value + Sig0, OperandSize.Word), 1);
  }

  /// <summary>Right shifts a 64-bit magnitude and applies the x87 RC field to the discarded bits.</summary>
  internal void ShiftCanonicalRightSticky(int value, Reg countRegister, bool round) {
    var sticky = this.Scratch(Tmp3, OperandSize.Word);
    var guard = this.Scratch(Tmp4, OperandSize.Word);
    this._asm.Mov(sticky, 0); this._asm.Mov(guard, 0);
    this._asm.Mov(Reg.CX, countRegister);
    var noShift = this._asm.DefineLabel(); var loop = this._asm.DefineLabel(); var last = this._asm.DefineLabel(); var shifted = this._asm.DefineLabel();
    this._asm.Jcxz(noShift);
    this._asm.MarkLabel(loop);
    this.ShiftCanonicalRightOne(value);
    this._asm.Pushf(); this._asm.Pop(Reg.AX); this._asm.And(Reg.AX, 1);
    this._asm.Cmp(Reg.CX, 1); this._asm.J(Condition.Equal, last);
    this._asm.Or(sticky, Reg.AX); this._asm.Loop(loop); this._asm.Jmp(shifted);
    this._asm.MarkLabel(last); this._asm.Mov(guard, Reg.AX); this._asm.Dec(Reg.CX);
    this._asm.MarkLabel(shifted);
    if (round) this.RoundShiftedMagnitude(value, guard, sticky);
    this._asm.MarkLabel(noShift);
  }

  private void RoundShiftedMagnitude(int value, Mem guard, Mem sticky) {
    var noDiscard = this._asm.DefineLabel(); var increment = this._asm.DefineLabel(); var done = this._asm.DefineLabel();
    this._asm.Mov(Reg.AX, guard); this._asm.Or(Reg.AX, sticky); this._asm.J(Condition.Equal, noDiscard);
    this.SetStatusBits(0x0020); // precision/inexact
    this._asm.Mov(Reg.AX, this.Control); this._asm.Mov(Reg.DX, Reg.AX);
    for (var i = 0; i < 10; ++i) this._asm.Shr(Reg.DX, 1);
    this._asm.And(Reg.DX, 3);
    var nearest = this._asm.DefineLabel(); var down = this._asm.DefineLabel(); var up = this._asm.DefineLabel();
    this._asm.Cmp(Reg.DX, 0); this._asm.J(Condition.Equal, nearest);
    this._asm.Cmp(Reg.DX, 1); this._asm.J(Condition.Equal, down);
    this._asm.Cmp(Reg.DX, 2); this._asm.J(Condition.Equal, up);
    this._asm.Jmp(done); // chop

    this._asm.MarkLabel(nearest);
    this._asm.Cmp(guard, 0); this._asm.J(Condition.Equal, done);
    this._asm.Cmp(sticky, 0); this._asm.J(Condition.NotEqual, increment);
    this._asm.Test(this.Scratch(value + Sig0, OperandSize.Word), 1); this._asm.J(Condition.NotEqual, increment);
    this._asm.Jmp(done);
    this._asm.MarkLabel(down);
    this._asm.Test(this.Scratch(value + Meta, OperandSize.Word), SignMask); this._asm.J(Condition.NotEqual, increment); this._asm.Jmp(done);
    this._asm.MarkLabel(up);
    this._asm.Test(this.Scratch(value + Meta, OperandSize.Word), SignMask); this._asm.J(Condition.Equal, increment); this._asm.Jmp(done);
    this._asm.MarkLabel(increment);
    this._asm.Add(this.Scratch(value + Sig0, OperandSize.Word), 1);
    this._asm.Adc(this.Scratch(value + Sig1, OperandSize.Word), 0);
    this._asm.Adc(this.Scratch(value + Sig2, OperandSize.Word), 0);
    this._asm.Adc(this.Scratch(value + Sig3, OperandSize.Word), 0);
    this._asm.MarkLabel(done);
    this._asm.MarkLabel(noDiscard);
  }

  private void NegateCanonicalWords(int value, int words) {
    for (var i = 0; i < words; ++i) this._asm.Not(this.Scratch(value + i * 2, OperandSize.Word));
    this._asm.Add(this.Scratch(value, OperandSize.Word), 1);
    for (var i = 1; i < words; ++i) this._asm.Adc(this.Scratch(value + i * 2, OperandSize.Word), 0);
  }

  private void ClassifyCanonical(int source, Label finite, Label zero, Label infinity, Label nan) {
    this._asm.Mov(Reg.AX, this.Scratch(source + Meta, OperandSize.Word)); this._asm.And(Reg.AX, ClassMask);
    this._asm.Cmp(Reg.AX, ClassZero); this._asm.J(Condition.Equal, zero);
    this._asm.Cmp(Reg.AX, ClassInfinity); this._asm.J(Condition.Equal, infinity);
    this._asm.Cmp(Reg.AX, ClassNaN); this._asm.J(Condition.Equal, nan);
    this._asm.Jmp(finite);
  }

  private void BuildExtendedSignExponent(int source, int exponent, int destinationWord) {
    this._asm.Mov(Reg.AX, exponent); this.BuildExtendedSignExponentFromAx(source, destinationWord);
  }

  private void BuildExtendedSignExponentFromAx(int source, int destinationWord) {
    this._asm.Mov(Reg.DX, this.Scratch(source + Meta, OperandSize.Word)); this._asm.And(Reg.DX, SignMask);
    for (var i = 0; i < 15; ++i) this._asm.Shl(Reg.DX, 1);
    this._asm.Or(Reg.AX, Reg.DX); this._asm.Mov(this.Scratch(destinationWord, OperandSize.Word), Reg.AX);
  }

  private void ZeroIeeeScratch(int bits, bool preserveMantissa = false) {
    var words = bits / 16;
    if (!preserveMantissa)
      for (var i = 0; i < words; ++i) this._asm.Mov(this.Scratch(ScratchD + i * 2, OperandSize.Word), 0);
    else
      for (var i = 0; i < words; ++i) this._asm.Mov(this.Scratch(ScratchD + i * 2, OperandSize.Word), 0);
  }

  private void PackRetainedMantissaToIeee(int bits) {
    if (bits == 32) {
      this._asm.Mov(Reg.AX, this.Scratch(ScratchC + Sig0, OperandSize.Word)); this._asm.Mov(this.Scratch(ScratchD, OperandSize.Word), Reg.AX);
      this._asm.Mov(Reg.AX, this.Scratch(ScratchC + Sig1, OperandSize.Word)); this._asm.And(Reg.AX, 0x007F);
      this._asm.Or(this.Scratch(ScratchD + 2, OperandSize.Word), Reg.AX);
    } else {
      for (var i = 0; i < 3; ++i) {
        this._asm.Mov(Reg.AX, this.Scratch(ScratchC + i * 2, OperandSize.Word)); this._asm.Mov(this.Scratch(ScratchD + i * 2, OperandSize.Word), Reg.AX);
      }
      this._asm.Mov(Reg.AX, this.Scratch(ScratchC + 6, OperandSize.Word)); this._asm.And(Reg.AX, 0x000F);
      this._asm.Or(this.Scratch(ScratchD + 6, OperandSize.Word), Reg.AX);
    }
  }

  private void SetIeeeExponent(int bits, int exponent) {
    this._asm.Mov(Reg.AX, exponent); this.SetIeeeExponentFromAx(bits);
  }

  private void SetIeeeExponentFromAx(int bits) {
    if (bits == 32) {
      for (var i = 0; i < 7; ++i) this._asm.Shl(Reg.AX, 1);
      this._asm.Or(this.Scratch(ScratchD + 2, OperandSize.Word), Reg.AX);
    } else {
      for (var i = 0; i < 4; ++i) this._asm.Shl(Reg.AX, 1);
      this._asm.Or(this.Scratch(ScratchD + 6, OperandSize.Word), Reg.AX);
    }
  }

  private void ApplyIeeeSign(int source, int bits) {
    this._asm.Mov(Reg.AX, this.Scratch(source + Meta, OperandSize.Word)); this._asm.And(Reg.AX, SignMask);
    for (var i = 0; i < 15; ++i) this._asm.Shl(Reg.AX, 1);
    this._asm.Or(this.Scratch(ScratchD + (bits / 8) - 2, OperandSize.Word), Reg.AX);
  }

  #endregion
}
