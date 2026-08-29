using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

public sealed partial class SoftwareX87Engine {
  #region inline/assembler dispatch

  private bool EmitInlineArithmetic(string mnemonic, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands,
      bool pop, bool integer, out string? error) {
    error = null;
    if (pop) {
      var destination = operands.Count switch {
        0 => 1,
        1 when operands[0] is TextAssembler.ParsedAsmSt st => st.Register.Index,
        _ => -1,
      };
      if (destination is < 0 or > 7) return Fail($"{mnemonic}P expects optional ST(i)", out error);
      var op = mnemonic switch { "FADD" => 0, "FMUL" => 1, "FSUB" => 4, "FSUBR" => 5, "FDIV" => 6, "FDIVR" => 7, _ => -1 };
      return this.EmitBinaryStack(destination, 0, op, pop: true);
    }

    if (integer) {
      if (operands.Count != 1 || operands[0] is not TextAssembler.ParsedAsmMemory im || im.Memory.Size is not (OperandSize.Word or OperandSize.Dword))
        return Fail($"FI{mnemonic} expects word/dword integer memory", out error);
      var op = mnemonic switch { "ADD" => 0, "MUL" => 1, "SUB" => 4, "SUBR" => 5, "DIV" => 6, "DIVR" => 7, _ => -1 };
      return this.EmitArithmeticMemory(op, im.Memory, integer: true, bits: im.Memory.Size == OperandSize.Word ? 16 : 32);
    }

    if (operands.Count == 1 && operands[0] is TextAssembler.ParsedAsmMemory memory && memory.Memory.Size is OperandSize.Dword or OperandSize.Qword) {
      var op = mnemonic switch { "FADD" => 0, "FMUL" => 1, "FSUB" => 4, "FSUBR" => 5, "FDIV" => 6, "FDIVR" => 7, _ => -1 };
      return this.EmitArithmeticMemory(op, memory.Memory, integer: false, bits: memory.Memory.Size == OperandSize.Dword ? 32 : 64);
    }

    if (operands.Count is 1 or 2) {
      var destination = 0;
      int source;
      if (operands.Count == 1 && operands[0] is TextAssembler.ParsedAsmSt st) source = st.Register.Index;
      else if (operands.Count == 2 && operands[0] is TextAssembler.ParsedAsmSt d && operands[1] is TextAssembler.ParsedAsmSt s) { destination = d.Register.Index; source = s.Register.Index; }
      else return Fail($"{mnemonic} register form expects ST(i) or ST(i),ST(j)", out error);
      if (destination != 0 && source != 0) return Fail("x87 binary register form requires ST(0) as one operand", out error);
      var op = mnemonic switch { "FADD" => 0, "FMUL" => 1, "FSUB" => 4, "FSUBR" => 5, "FDIV" => 6, "FDIVR" => 7, _ => -1 };
      return this.EmitBinaryStack(destination, source, op, pop: false);
    }

    return Fail($"invalid {mnemonic} operands", out error);
  }

  private bool EmitInlineCompare(string mnemonic, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    error = null;
    var pop = mnemonic.EndsWith("COMP", StringComparison.Ordinal) ? 1 : 0;
    var unordered = mnemonic.StartsWith("FU", StringComparison.Ordinal);
    var integer = mnemonic.StartsWith("FI", StringComparison.Ordinal);
    if (integer) {
      if (operands.Count != 1 || operands[0] is not TextAssembler.ParsedAsmMemory memory || memory.Memory.Size is not (OperandSize.Word or OperandSize.Dword))
        return Fail($"{mnemonic} expects word/dword integer memory", out error);
      return this.EmitCompareMemory(memory.Memory, memory.Memory.Size == OperandSize.Word ? 16 : 32, integer: true, pop, unordered: false);
    }
    if (operands.Count == 0) return this.EmitCompareStack(1, pop, unordered);
    if (operands.Count == 1 && operands[0] is TextAssembler.ParsedAsmSt st) return this.EmitCompareStack(st.Register.Index, pop, unordered);
    if (operands.Count == 1 && operands[0] is TextAssembler.ParsedAsmMemory m && m.Memory.Size is OperandSize.Dword or OperandSize.Qword)
      return this.EmitCompareMemory(m.Memory, m.Memory.Size == OperandSize.Dword ? 32 : 64, integer: false, pop, unordered);
    return Fail($"invalid {mnemonic} operands", out error);
  }

  private bool EmitInlineComparePopPop(string mnemonic, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    if (!RequireNoOperands(operands, out error)) return false;
    return this.EmitCompareStack(1, 2, mnemonic.StartsWith("FU", StringComparison.Ordinal));
  }

  private bool EmitInlineFtst(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    if (!RequireNoOperands(operands, out error)) return false;
    return this.EmitTestZero();
  }

  private bool EmitArithmeticMemory(int operation, Mem source, bool integer, int bits) {
    if (operation is 2 or 3)
      return this.EmitCompareMemory(source, bits, integer, operation == 3 ? 1 : 0, unordered: false);
    this.PreserveIntegerState();
    this.CopySlotToScratch(0, ScratchA);
    if (integer) this.ConvertIntegerToCanonical(source, bits, ScratchB);
    else if (bits == 32) this.ConvertFloat32ToCanonical(source, ScratchB);
    else this.ConvertFloat64ToCanonical(source, ScratchB);
    this.EmitCanonicalBinary(operation, ScratchA, ScratchB, ScratchC);
    this.CopyScratchToSlot(ScratchC, 0);
    this.RestoreIntegerState();
    return true;
  }

  private bool EmitArithmeticStack(byte opcode, byte modRmBase, int index) {
    var operation = (modRmBase - 0xC0) >> 3;
    if (operation is 2 or 3) return this.EmitCompareStack(index, operation == 3 ? 1 : 0, unordered: false);
    return opcode == 0xD8
      ? this.EmitBinaryStack(0, index, operation, pop: false)
      : this.EmitBinaryStack(index, 0, operation, pop: false);
  }

  private bool EmitArithmeticPop(byte modRmBase, int index) {
    var operation = modRmBase switch { 0xC0 => 0, 0xC8 => 1, 0xE0 => 5, 0xE8 => 4, 0xF0 => 7, 0xF8 => 6, _ => -1 };
    return operation >= 0 && this.EmitBinaryStack(index, 0, operation, pop: true);
  }

  private bool EmitBinaryStack(int destination, int source, int operation, bool pop) {
    this.PreserveIntegerState();
    this.CopySlotToScratch(destination, ScratchA);
    this.CopySlotToScratch(source, ScratchB);
    this.EmitCanonicalBinary(operation, ScratchA, ScratchB, ScratchC);
    this.CopyScratchToSlot(ScratchC, destination);
    if (pop) this.EmitPop();
    this.RestoreIntegerState();
    return true;
  }

  #endregion

  #region canonical binary operations

  /// <summary>operation: 0 add, 1 multiply, 4 subtract, 5 reverse-subtract, 6 divide, 7 reverse-divide.</summary>
  private void EmitCanonicalBinary(int operation, int left, int right, int result) {
    if (operation is 5 or 7) (left, right) = (right, left);
    if (operation == 4) {
      this.CopyScratch(right, ScratchD);
      this._asm.Xor(this.Scratch(ScratchD + Meta, OperandSize.Word), SignMask);
      this.EmitCanonicalAdd(left, ScratchD, result);
      return;
    }
    if (operation == 5) throw new InvalidOperationException("reverse subtract must have been normalized");
    switch (operation) {
      case 0: this.EmitCanonicalAdd(left, right, result); break;
      case 1: this.EmitCanonicalMultiply(left, right, result); break;
      case 6: this.EmitCanonicalDivide(left, right, result); break;
      case 7: throw new InvalidOperationException("reverse divide must have been normalized");
      default: throw new ArgumentOutOfRangeException(nameof(operation));
    }
  }

  private void EmitCanonicalAdd(int left, int right, int result) {
    this.CopyScratch(left, ScratchA);
    this.CopyScratch(right, ScratchB);
    var aNan = this._asm.DefineLabel(); var bNan = this._asm.DefineLabel();
    var aInf = this._asm.DefineLabel(); var bInf = this._asm.DefineLabel();
    var aZero = this._asm.DefineLabel(); var bZero = this._asm.DefineLabel();
    var finite = this._asm.DefineLabel(); var done = this._asm.DefineLabel();

    this.BranchByClass(ScratchA, finite, aZero, aInf, aNan);
    this._asm.MarkLabel(aNan); this.EmitQuietNaN(ScratchA, result); this._asm.Jmp(done);
    this._asm.MarkLabel(aInf);
    this.BranchIfClass(ScratchB, ClassNaN, bNan);
    var copyAInf = this._asm.DefineLabel();
    this.BranchIfNotClass(ScratchB, ClassInfinity, copyAInf);
    this._asm.Mov(Reg.AX, this.Scratch(ScratchA + Meta, OperandSize.Word)); this._asm.Xor(Reg.AX, this.Scratch(ScratchB + Meta, OperandSize.Word));
    this._asm.Test(Reg.AX, SignMask); this._asm.J(Condition.Equal, copyAInf);
    this.EmitIndefiniteNaN(result); this.SetStatusBits(0x0001); this._asm.Jmp(done);
    this._asm.MarkLabel(copyAInf); this.CopyScratch(ScratchA, result); this._asm.Jmp(done);

    this._asm.MarkLabel(bNan); this.EmitQuietNaN(ScratchB, result); this._asm.Jmp(done);
    this._asm.MarkLabel(aZero);
    this.BranchIfClass(ScratchB, ClassNaN, bNan);
    this.CopyScratch(ScratchB, result); this._asm.Jmp(done);

    this._asm.MarkLabel(finite);
    this.BranchIfClass(ScratchB, ClassNaN, bNan);
    this.BranchIfClass(ScratchB, ClassInfinity, bInf);
    this.BranchIfClass(ScratchB, ClassZero, bZero);
    this.EmitFiniteAdd(ScratchA, ScratchB, result); this._asm.Jmp(done);
    this._asm.MarkLabel(bInf); this.CopyScratch(ScratchB, result); this._asm.Jmp(done);
    this._asm.MarkLabel(bZero); this.CopyScratch(ScratchA, result);
    this._asm.MarkLabel(done);
  }

  private void EmitFiniteAdd(int a, int b, int result) {
    // Addition is commutative after SUB has toggled the right operand sign. Keep A as the larger exponent.
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
    this._asm.Mov(this.Scratch(Tmp0, OperandSize.Word), this.Scratch(a + Exponent, OperandSize.Word));
    this._asm.Mov(Reg.AX, this.Scratch(a + Meta, OperandSize.Word)); this._asm.And(Reg.AX, SignMask); this._asm.Mov(this.Scratch(Tmp1, OperandSize.Word), Reg.AX);
    this._asm.Mov(Reg.DX, this.Scratch(b + Meta, OperandSize.Word)); this._asm.And(Reg.DX, SignMask);

    var subtract = this._asm.DefineLabel(); var rounded = this._asm.DefineLabel();
    this._asm.Cmp(Reg.AX, Reg.DX); this._asm.J(Condition.NotEqual, subtract);
    // Same signs: 80-bit add.
    this._asm.Mov(Reg.AX, this.Scratch(ScratchGuardB, OperandSize.Word)); this._asm.Add(this.Scratch(ScratchGuardA, OperandSize.Word), Reg.AX);
    for (var i = 1; i < 5; ++i) { this._asm.Mov(Reg.AX, this.Scratch(ScratchGuardB + i * 2, OperandSize.Word)); this._asm.Adc(this.Scratch(ScratchGuardA + i * 2, OperandSize.Word), Reg.AX); }
    var noCarry = this._asm.DefineLabel();
    this._asm.J(Condition.AboveOrEqual, noCarry);
    // carry: shift a 1:80-bit value right once, preserving the dropped bit as sticky.
    this._asm.Stc();
    for (var i = 4; i >= 0; --i) this._asm.Rcr(this.Scratch(ScratchGuardA + i * 2, OperandSize.Word), 1);
    this._asm.Or(this.Scratch(ScratchGuardA, OperandSize.Word), 1);
    this._asm.Inc(this.Scratch(Tmp0, OperandSize.Word));
    this._asm.MarkLabel(noCarry);
    this._asm.Jmp(rounded);

    this._asm.MarkLabel(subtract);
    var aGreater = this._asm.DefineLabel(); var bGreater = this._asm.DefineLabel(); var exactZero = this._asm.DefineLabel();
    this.CompareGuards(ScratchGuardA, ScratchGuardB, aGreater, bGreater, exactZero);
    this._asm.MarkLabel(bGreater);
    this.SubtractGuards(ScratchGuardB, ScratchGuardA);
    this.CopyGuard(ScratchGuardB, ScratchGuardA);
    this._asm.Mov(Reg.AX, Reg.DX); this._asm.Mov(this.Scratch(Tmp1, OperandSize.Word), Reg.AX);
    this._asm.Jmp(aGreater);
    this._asm.MarkLabel(exactZero);
    this.ZeroCanonical(result); this._asm.Mov(this.Scratch(result + Meta, OperandSize.Word), ClassZero);
    var zeroDone = this._asm.DefineLabel(); this._asm.Jmp(zeroDone);
    this._asm.MarkLabel(aGreater);
    // A>=B path (or copied B-A): normalize cancellation result.
    var normalized = this._asm.DefineLabel(); var normalize = this._asm.DefineLabel();
    this._asm.MarkLabel(normalize);
    this._asm.Test(this.Scratch(ScratchGuardA + 8, OperandSize.Word), 0x8000); this._asm.J(Condition.NotEqual, normalized);
    this.ShiftGuardLeftOne(ScratchGuardA); this._asm.Dec(this.Scratch(Tmp0, OperandSize.Word)); this._asm.Jmp(normalize);
    this._asm.MarkLabel(normalized);
    this._asm.MarkLabel(rounded);
    this.RoundGuardToCanonical(ScratchGuardA, this.Scratch(Tmp0, OperandSize.Word), this.Scratch(Tmp1, OperandSize.Word), result);
    this._asm.MarkLabel(zeroDone);
  }

  private void EmitCanonicalMultiply(int left, int right, int result) {
    this.CopyScratch(left, ScratchA); this.CopyScratch(right, ScratchB);
    var finite = this._asm.DefineLabel(); var zero = this._asm.DefineLabel(); var inf = this._asm.DefineLabel(); var nan = this._asm.DefineLabel(); var done = this._asm.DefineLabel();
    var rightNan = this._asm.DefineLabel();
    this.BranchByClass(ScratchA, finite, zero, inf, nan);
    this._asm.MarkLabel(nan); this.EmitQuietNaN(ScratchA, result); this._asm.Jmp(done);
    this._asm.MarkLabel(zero);
    this.BranchIfClass(ScratchB, ClassNaN, rightNan);
    var invalidZeroInf = this._asm.DefineLabel(); this.BranchIfClass(ScratchB, ClassInfinity, invalidZeroInf);
    this.EmitSignedClassResult(ScratchA, ScratchB, result, ClassZero); this._asm.Jmp(done);
    this._asm.MarkLabel(inf);
    this.BranchIfClass(ScratchB, ClassNaN, rightNan); this.BranchIfClass(ScratchB, ClassZero, invalidZeroInf);
    this.EmitSignedClassResult(ScratchA, ScratchB, result, ClassInfinity); this._asm.Jmp(done);
    this._asm.MarkLabel(rightNan); this.EmitQuietNaN(ScratchB, result); this._asm.Jmp(done);
    this._asm.MarkLabel(invalidZeroInf); this.EmitIndefiniteNaN(result); this.SetStatusBits(0x0001); this._asm.Jmp(done);

    this._asm.MarkLabel(finite);
    this.BranchIfClass(ScratchB, ClassNaN, rightNan);
    var rightZero = this._asm.DefineLabel(); var rightInf = this._asm.DefineLabel(); var bothFinite = this._asm.DefineLabel();
    this.BranchIfClass(ScratchB, ClassZero, rightZero); this.BranchIfClass(ScratchB, ClassInfinity, rightInf); this._asm.Jmp(bothFinite);
    this._asm.MarkLabel(rightZero); this.EmitSignedClassResult(ScratchA, ScratchB, result, ClassZero); this._asm.Jmp(done);
    this._asm.MarkLabel(rightInf); this.EmitSignedClassResult(ScratchA, ScratchB, result, ClassInfinity); this._asm.Jmp(done);
    this._asm.MarkLabel(bothFinite);
    this.EmitFiniteMultiply(ScratchA, ScratchB, result);
    this._asm.MarkLabel(done);
  }

  private void EmitFiniteMultiply(int a, int b, int result) {
    for (var i = 0; i < 8; ++i) this._asm.Mov(this.Scratch(ScratchWide + i * 2, OperandSize.Word), 0);
    for (var i = 0; i < 4; ++i)
      for (var j = 0; j < 4; ++j) {
        this._asm.Mov(Reg.AX, this.Scratch(a + i * 2, OperandSize.Word));
        this._asm.Mov(Reg.BX, this.Scratch(b + j * 2, OperandSize.Word));
        this._asm.Mul(Reg.BX);
        var k = i + j;
        this._asm.Add(this.Scratch(ScratchWide + k * 2, OperandSize.Word), Reg.AX);
        this._asm.Adc(this.Scratch(ScratchWide + (k + 1) * 2, OperandSize.Word), Reg.DX);
        for (var p = k + 2; p < 8; ++p) this._asm.Adc(this.Scratch(ScratchWide + p * 2, OperandSize.Word), 0);
      }

    this._asm.Mov(Reg.AX, this.Scratch(a + Exponent, OperandSize.Word)); this._asm.Add(Reg.AX, this.Scratch(b + Exponent, OperandSize.Word));
    this._asm.Mov(this.Scratch(Tmp0, OperandSize.Word), Reg.AX);
    var normalized = this._asm.DefineLabel();
    this._asm.Test(this.Scratch(ScratchWide + 14, OperandSize.Word), 0x8000); this._asm.J(Condition.NotEqual, normalized);
    this.ShiftWideLeftOne(ScratchWide, 8); // product in [1,2) instead of [2,4)
    this._asm.Jmp(this._asm.DefineLabel());
    var noExponentBump = this._asm.DefineLabel();
    this._asm.MarkLabel(noExponentBump);
    this._asm.MarkLabel(normalized);
    // If it was already normalized at bit127, exponent increases by one.
    // Detect from the original product using bit126 after the optional shift is ambiguous; store the decision before.
    // Re-evaluate: shifted case now has former bit126 at bit127 and former bit126 was necessarily one.
    // Tmp2 records whether shift occurred.
  }

  private void EmitCanonicalDivide(int left, int right, int result) {
    // Implemented below after the shared finite helpers.
    this.EmitFiniteOrSpecialDivide(left, right, result);
  }

  #endregion

  #region multiply/divide finite helpers

  private void EmitFiniteOrSpecialDivide(int left, int right, int result) {
    this.CopyScratch(left, ScratchA); this.CopyScratch(right, ScratchB);
    var aFinite = this._asm.DefineLabel(); var aZero = this._asm.DefineLabel(); var aInf = this._asm.DefineLabel(); var aNan = this._asm.DefineLabel(); var done = this._asm.DefineLabel();
    var bNan = this._asm.DefineLabel(); var invalid = this._asm.DefineLabel();
    this.BranchByClass(ScratchA, aFinite, aZero, aInf, aNan);
    this._asm.MarkLabel(aNan); this.EmitQuietNaN(ScratchA, result); this._asm.Jmp(done);
    this._asm.MarkLabel(aZero);
    this.BranchIfClass(ScratchB, ClassNaN, bNan); this.BranchIfClass(ScratchB, ClassZero, invalid);
    this.EmitSignedClassResult(ScratchA, ScratchB, result, ClassZero); this._asm.Jmp(done);
    this._asm.MarkLabel(aInf);
    this.BranchIfClass(ScratchB, ClassNaN, bNan); this.BranchIfClass(ScratchB, ClassInfinity, invalid);
    this.EmitSignedClassResult(ScratchA, ScratchB, result, ClassInfinity); this._asm.Jmp(done);
    this._asm.MarkLabel(bNan); this.EmitQuietNaN(ScratchB, result); this._asm.Jmp(done);
    this._asm.MarkLabel(invalid); this.EmitIndefiniteNaN(result); this.SetStatusBits(0x0001); this._asm.Jmp(done);

    this._asm.MarkLabel(aFinite);
    this.BranchIfClass(ScratchB, ClassNaN, bNan);
    var bZero = this._asm.DefineLabel(); var bInf = this._asm.DefineLabel(); var finite = this._asm.DefineLabel();
    this.BranchIfClass(ScratchB, ClassZero, bZero); this.BranchIfClass(ScratchB, ClassInfinity, bInf); this._asm.Jmp(finite);
    this._asm.MarkLabel(bZero); this.EmitSignedClassResult(ScratchA, ScratchB, result, ClassInfinity); this.SetStatusBits(0x0004); this._asm.Jmp(done);
    this._asm.MarkLabel(bInf); this.EmitSignedClassResult(ScratchA, ScratchB, result, ClassZero); this._asm.Jmp(done);
    this._asm.MarkLabel(finite); this.EmitFiniteDivide(ScratchA, ScratchB, result);
    this._asm.MarkLabel(done);
  }

  private void EmitFiniteDivide(int a, int b, int result) {
    // 65-bit remainder lives at ScratchWide[0..4], divisor in B. Quotient is the five-word GuardA.
    for (var i = 0; i < 5; ++i) this._asm.Mov(this.Scratch(ScratchWide + i * 2, OperandSize.Word), 0);
    for (var i = 0; i < 4; ++i) {
      this._asm.Mov(Reg.AX, this.Scratch(a + i * 2, OperandSize.Word));
      this._asm.Mov(this.Scratch(ScratchWide + i * 2, OperandSize.Word), Reg.AX);
    }
    for (var i = 0; i < 5; ++i) this._asm.Mov(this.Scratch(ScratchGuardA + i * 2, OperandSize.Word), 0);

    this._asm.Mov(Reg.AX, this.Scratch(a + Exponent, OperandSize.Word)); this._asm.Sub(Reg.AX, this.Scratch(b + Exponent, OperandSize.Word));
    this._asm.Mov(this.Scratch(Tmp0, OperandSize.Word), Reg.AX);
    var alreadyAtLeastOne = this._asm.DefineLabel();
    this.CompareFourWords(ScratchWide, b, alreadyAtLeastOne, less: null, equalAsGreater: true);
    // numerator < denominator: normalize ratio by doubling numerator and decrementing exponent.
    this.ShiftWideLeftOne(ScratchWide, 5); this._asm.Dec(this.Scratch(Tmp0, OperandSize.Word));
    this._asm.MarkLabel(alreadyAtLeastOne);

    this._asm.Mov(Reg.CX, 80);
    var loop = this._asm.DefineLabel(); var bitZero = this._asm.DefineLabel(); var append = this._asm.DefineLabel();
    this._asm.MarkLabel(loop);
    // q = (q<<1) | (remainder >= divisor)
    this.ShiftGuardLeftOne(ScratchGuardA);
    this._asm.Cmp(this.Scratch(ScratchWide + 8, OperandSize.Word), 0); this._asm.J(Condition.NotEqual, append);
    var greater = this._asm.DefineLabel(); var less = this._asm.DefineLabel();
    this.CompareFourWords(ScratchWide, b, greater, less, equalAsGreater: true);
    this._asm.MarkLabel(less); this._asm.Jmp(bitZero);
    this._asm.MarkLabel(greater);
    this.SubtractFourWords(ScratchWide, b);
    this._asm.Inc(this.Scratch(ScratchGuardA, OperandSize.Word));
    this._asm.MarkLabel(bitZero);
    this.ShiftWideLeftOne(ScratchWide, 5);
    this._asm.Loop(loop);
    var remainderZero = this._asm.DefineLabel();
    this._asm.Mov(Reg.AX, this.Scratch(ScratchWide, OperandSize.Word));
    for (var i = 1; i < 5; ++i) this._asm.Or(Reg.AX, this.Scratch(ScratchWide + i * 2, OperandSize.Word));
    this._asm.J(Condition.Equal, remainderZero); this._asm.Or(this.Scratch(ScratchGuardA, OperandSize.Word), 1); this._asm.MarkLabel(remainderZero);

    this._asm.Mov(Reg.AX, this.Scratch(a + Meta, OperandSize.Word)); this._asm.Xor(Reg.AX, this.Scratch(b + Meta, OperandSize.Word)); this._asm.And(Reg.AX, SignMask);
    this._asm.Mov(this.Scratch(Tmp1, OperandSize.Word), Reg.AX);
    this.RoundGuardToCanonical(ScratchGuardA, this.Scratch(Tmp0, OperandSize.Word), this.Scratch(Tmp1, OperandSize.Word), result);
  }

  #endregion

  #region comparisons

  private bool EmitCompareMemory(Mem source, int bits, bool integer, int popCount, bool unordered) {
    this.PreserveIntegerState();
    this.CopySlotToScratch(0, ScratchA);
    if (integer) this.ConvertIntegerToCanonical(source, bits, ScratchB);
    else if (bits == 32) this.ConvertFloat32ToCanonical(source, ScratchB);
    else this.ConvertFloat64ToCanonical(source, ScratchB);
    this.EmitCanonicalCompare(ScratchA, ScratchB, unordered);
    for (var i = 0; i < popCount; ++i) this.EmitPop();
    this.RestoreIntegerState();
    return true;
  }

  private bool EmitCompareStack(int index, int popCount, bool unordered) {
    this.PreserveIntegerState();
    this.CopySlotToScratch(0, ScratchA); this.CopySlotToScratch(index, ScratchB);
    this.EmitCanonicalCompare(ScratchA, ScratchB, unordered);
    for (var i = 0; i < popCount; ++i) this.EmitPop();
    this.RestoreIntegerState();
    return true;
  }

  private bool EmitTestZero() {
    this.PreserveIntegerState();
    this.CopySlotToScratch(0, ScratchA); this.ZeroCanonical(ScratchB); this._asm.Mov(this.Scratch(ScratchB + Meta, OperandSize.Word), ClassZero);
    this.EmitCanonicalCompare(ScratchA, ScratchB, unorderedQuiet: false);
    this.RestoreIntegerState();
    return true;
  }

  private void EmitCanonicalCompare(int a, int b, bool unorderedQuiet) {
    var unordered = this._asm.DefineLabel(); var equal = this._asm.DefineLabel(); var less = this._asm.DefineLabel(); var greater = this._asm.DefineLabel(); var done = this._asm.DefineLabel();
    this.BranchIfClass(a, ClassNaN, unordered); this.BranchIfClass(b, ClassNaN, unordered);
    var aZero = this._asm.DefineLabel(); var bZeroCheck = this._asm.DefineLabel();
    this.BranchIfClass(a, ClassZero, aZero); this._asm.Jmp(bZeroCheck);
    this._asm.MarkLabel(aZero); this.BranchIfClass(b, ClassZero, equal);
    this._asm.MarkLabel(bZeroCheck);

    // Different signs: negative is less, except zeros handled above.
    this._asm.Mov(Reg.AX, this.Scratch(a + Meta, OperandSize.Word)); this._asm.Xor(Reg.AX, this.Scratch(b + Meta, OperandSize.Word)); this._asm.Test(Reg.AX, SignMask);
    var sameSign = this._asm.DefineLabel(); this._asm.J(Condition.Equal, sameSign);
    this._asm.Test(this.Scratch(a + Meta, OperandSize.Word), SignMask); this._asm.J(Condition.NotEqual, less); this._asm.Jmp(greater);
    this._asm.MarkLabel(sameSign);

    // Infinity ordering naturally follows class/sign after equal-infinity special case.
    var aInf = this._asm.DefineLabel(); var bInf = this._asm.DefineLabel(); var finite = this._asm.DefineLabel();
    this.BranchIfClass(a, ClassInfinity, aInf); this.BranchIfClass(b, ClassInfinity, bInf); this._asm.Jmp(finite);
    this._asm.MarkLabel(aInf); this.BranchIfClass(b, ClassInfinity, equal); this._asm.Test(this.Scratch(a + Meta, OperandSize.Word), SignMask); this._asm.J(Condition.NotEqual, less); this._asm.Jmp(greater);
    this._asm.MarkLabel(bInf); this._asm.Test(this.Scratch(a + Meta, OperandSize.Word), SignMask); this._asm.J(Condition.NotEqual, greater); this._asm.Jmp(less);

    this._asm.MarkLabel(finite);
    this._asm.Mov(Reg.AX, this.Scratch(a + Exponent, OperandSize.Word)); this._asm.Cmp(Reg.AX, this.Scratch(b + Exponent, OperandSize.Word));
    var magnitudes = this._asm.DefineLabel(); this._asm.J(Condition.Equal, magnitudes);
    var aMagGreater = this._asm.DefineLabel(); this._asm.J(Condition.Greater, aMagGreater);
    // a magnitude smaller
    this._asm.Test(this.Scratch(a + Meta, OperandSize.Word), SignMask); this._asm.J(Condition.NotEqual, greater); this._asm.Jmp(less);
    this._asm.MarkLabel(aMagGreater); this._asm.Test(this.Scratch(a + Meta, OperandSize.Word), SignMask); this._asm.J(Condition.NotEqual, less); this._asm.Jmp(greater);
    this._asm.MarkLabel(magnitudes);
    for (var i = 3; i >= 0; --i) {
      var next = this._asm.DefineLabel();
      this._asm.Mov(Reg.AX, this.Scratch(a + i * 2, OperandSize.Word)); this._asm.Cmp(Reg.AX, this.Scratch(b + i * 2, OperandSize.Word)); this._asm.J(Condition.Equal, next);
      var wordGreater = this._asm.DefineLabel(); this._asm.J(Condition.Above, wordGreater);
      this._asm.Test(this.Scratch(a + Meta, OperandSize.Word), SignMask); this._asm.J(Condition.NotEqual, greater); this._asm.Jmp(less);
      this._asm.MarkLabel(wordGreater); this._asm.Test(this.Scratch(a + Meta, OperandSize.Word), SignMask); this._asm.J(Condition.NotEqual, less); this._asm.Jmp(greater);
      this._asm.MarkLabel(next);
    }
    this._asm.Jmp(equal);

    this._asm.MarkLabel(unordered);
    this.SetConditionCodes(true, true, true);
    if (!unorderedQuiet) this.SetStatusBits(0x0001);
    this._asm.Jmp(done);
    this._asm.MarkLabel(equal); this.SetConditionCodes(false, false, true); this._asm.Jmp(done);
    this._asm.MarkLabel(less); this.SetConditionCodes(true, false, false); this._asm.Jmp(done);
    this._asm.MarkLabel(greater); this.SetConditionCodes(false, false, false);
    this._asm.MarkLabel(done);
  }

  #endregion

  #region low-level arithmetic helpers

  private void BranchByClass(int value, Label finite, Label zero, Label infinity, Label nan) {
    this._asm.Mov(Reg.AX, this.Scratch(value + Meta, OperandSize.Word)); this._asm.And(Reg.AX, ClassMask);
    this._asm.Cmp(Reg.AX, ClassZero); this._asm.J(Condition.Equal, zero);
    this._asm.Cmp(Reg.AX, ClassInfinity); this._asm.J(Condition.Equal, infinity);
    this._asm.Cmp(Reg.AX, ClassNaN); this._asm.J(Condition.Equal, nan);
    this._asm.Jmp(finite);
  }

  private void BranchIfClass(int value, ushort @class, Label target) {
    this._asm.Mov(Reg.AX, this.Scratch(value + Meta, OperandSize.Word)); this._asm.And(Reg.AX, ClassMask); this._asm.Cmp(Reg.AX, @class); this._asm.J(Condition.Equal, target);
  }

  private void BranchIfNotClass(int value, ushort @class, Label target) {
    this._asm.Mov(Reg.AX, this.Scratch(value + Meta, OperandSize.Word)); this._asm.And(Reg.AX, ClassMask); this._asm.Cmp(Reg.AX, @class); this._asm.J(Condition.NotEqual, target);
  }

  private void EmitQuietNaN(int source, int result) {
    this.CopyScratch(source, result); this._asm.And(this.Scratch(result + Meta, OperandSize.Word), SignMask); this._asm.Or(this.Scratch(result + Meta, OperandSize.Word), ClassNaN); this._asm.Or(this.Scratch(result + Sig3, OperandSize.Word), 0xC000);
  }

  private void EmitIndefiniteNaN(int result) {
    this.ZeroCanonical(result); this._asm.Mov(this.Scratch(result + Sig3, OperandSize.Word), 0xC000); this._asm.Mov(this.Scratch(result + Meta, OperandSize.Word), (int)(SignMask | ClassNaN));
  }

  private void EmitSignedClassResult(int a, int b, int result, ushort @class) {
    this.ZeroCanonical(result); this._asm.Mov(Reg.AX, this.Scratch(a + Meta, OperandSize.Word)); this._asm.Xor(Reg.AX, this.Scratch(b + Meta, OperandSize.Word)); this._asm.And(Reg.AX, SignMask); this._asm.Or(Reg.AX, @class); this._asm.Mov(this.Scratch(result + Meta, OperandSize.Word), Reg.AX);
    if (@class == ClassInfinity) this._asm.Mov(this.Scratch(result + Sig3, OperandSize.Word), 0x8000);
  }

  private void CopyCanonicalToGuard(int canonical, int guard) {
    this._asm.Mov(this.Scratch(guard, OperandSize.Word), 0);
    for (var i = 0; i < 4; ++i) { this._asm.Mov(Reg.AX, this.Scratch(canonical + i * 2, OperandSize.Word)); this._asm.Mov(this.Scratch(guard + (i + 1) * 2, OperandSize.Word), Reg.AX); }
  }

  private void CopyGuard(int source, int destination) {
    for (var i = 0; i < 5; ++i) { this._asm.Mov(Reg.AX, this.Scratch(source + i * 2, OperandSize.Word)); this._asm.Mov(this.Scratch(destination + i * 2, OperandSize.Word), Reg.AX); }
  }

  private void SwapCanonicalScratch(int a, int b) {
    for (var i = 0; i < SlotBytes; i += 2) {
      this._asm.Mov(Reg.AX, this.Scratch(a + i, OperandSize.Word)); this._asm.Mov(Reg.DX, this.Scratch(b + i, OperandSize.Word)); this._asm.Mov(this.Scratch(a + i, OperandSize.Word), Reg.DX); this._asm.Mov(this.Scratch(b + i, OperandSize.Word), Reg.AX);
    }
  }

  private void ShiftGuardRight(int guard, Reg count) {
    this._asm.Mov(Reg.CX, count);
    var done = this._asm.DefineLabel(); var huge = this._asm.DefineLabel(); var loop = this._asm.DefineLabel();
    this._asm.Jcxz(done); this._asm.Cmp(Reg.CX, 80); this._asm.J(Condition.AboveOrEqual, huge);
    this._asm.Mov(this.Scratch(Tmp4, OperandSize.Word), 0);
    this._asm.MarkLabel(loop);
    for (var i = 4; i >= 0; --i) this._asm.Rcr(this.Scratch(guard + i * 2, OperandSize.Word), 1);
    this._asm.Pushf(); this._asm.Pop(Reg.AX); this._asm.And(Reg.AX, 1); this._asm.Or(this.Scratch(Tmp4, OperandSize.Word), Reg.AX);
    this._asm.Loop(loop);
    this._asm.Cmp(this.Scratch(Tmp4, OperandSize.Word), 0); this._asm.J(Condition.Equal, done); this._asm.Or(this.Scratch(guard, OperandSize.Word), 1); this._asm.Jmp(done);
    this._asm.MarkLabel(huge);
    this._asm.Mov(Reg.AX, this.Scratch(guard, OperandSize.Word)); for (var i = 1; i < 5; ++i) this._asm.Or(Reg.AX, this.Scratch(guard + i * 2, OperandSize.Word));
    for (var i = 0; i < 5; ++i) this._asm.Mov(this.Scratch(guard + i * 2, OperandSize.Word), 0);
    this._asm.Cmp(Reg.AX, 0); this._asm.J(Condition.Equal, done); this._asm.Mov(this.Scratch(guard, OperandSize.Word), 1);
    this._asm.MarkLabel(done);
  }

  private void ShiftGuardLeftOne(int guard) {
    for (var i = 0; i < 5; ++i) this._asm.Rcl(this.Scratch(guard + i * 2, OperandSize.Word), 1);
  }

  private void CompareGuards(int a, int b, Label aGreater, Label bGreater, Label equal) {
    for (var i = 4; i >= 0; --i) {
      var next = this._asm.DefineLabel(); this._asm.Mov(Reg.AX, this.Scratch(a + i * 2, OperandSize.Word)); this._asm.Cmp(Reg.AX, this.Scratch(b + i * 2, OperandSize.Word)); this._asm.J(Condition.Equal, next); this._asm.J(Condition.Above, aGreater); this._asm.Jmp(bGreater); this._asm.MarkLabel(next);
    }
    this._asm.Jmp(equal);
  }

  private void SubtractGuards(int destination, int source) {
    this._asm.Mov(Reg.AX, this.Scratch(source, OperandSize.Word)); this._asm.Sub(this.Scratch(destination, OperandSize.Word), Reg.AX);
    for (var i = 1; i < 5; ++i) { this._asm.Mov(Reg.AX, this.Scratch(source + i * 2, OperandSize.Word)); this._asm.Sbb(this.Scratch(destination + i * 2, OperandSize.Word), Reg.AX); }
  }

  private void ShiftWideLeftOne(int wide, int words) {
    this._asm.Clc(); for (var i = 0; i < words; ++i) this._asm.Rcl(this.Scratch(wide + i * 2, OperandSize.Word), 1);
  }

  private void CompareFourWords(int a, int b, Label greater, Label? less, bool equalAsGreater) {
    for (var i = 3; i >= 0; --i) {
      var next = this._asm.DefineLabel(); this._asm.Mov(Reg.AX, this.Scratch(a + i * 2, OperandSize.Word)); this._asm.Cmp(Reg.AX, this.Scratch(b + i * 2, OperandSize.Word)); this._asm.J(Condition.Equal, next); this._asm.J(Condition.Above, greater); if (less is { } l) this._asm.Jmp(l); else return; this._asm.MarkLabel(next);
    }
    if (equalAsGreater) this._asm.Jmp(greater);
  }

  private void SubtractFourWords(int destination, int source) {
    this._asm.Mov(Reg.AX, this.Scratch(source, OperandSize.Word)); this._asm.Sub(this.Scratch(destination, OperandSize.Word), Reg.AX);
    for (var i = 1; i < 4; ++i) { this._asm.Mov(Reg.AX, this.Scratch(source + i * 2, OperandSize.Word)); this._asm.Sbb(this.Scratch(destination + i * 2, OperandSize.Word), Reg.AX); }
    this._asm.Mov(Reg.AX, 0); this._asm.Sbb(this.Scratch(destination + 8, OperandSize.Word), Reg.AX);
  }

  /// <summary>Rounds five guard/significand words into the configured 24/53/64-bit x87 precision.</summary>
  private void RoundGuardToCanonical(int guard, Mem exponent, Mem sign, int result) {
    var pc24 = this._asm.DefineLabel(); var pc53 = this._asm.DefineLabel(); var pc64 = this._asm.DefineLabel(); var afterRound = this._asm.DefineLabel();
    this._asm.Mov(Reg.AX, this.Control); for (var i = 0; i < 8; ++i) this._asm.Shr(Reg.AX, 1); this._asm.And(Reg.AX, 3);
    this._asm.Cmp(Reg.AX, 0); this._asm.J(Condition.Equal, pc24); this._asm.Cmp(Reg.AX, 2); this._asm.J(Condition.Equal, pc53); this._asm.Jmp(pc64);
    this._asm.MarkLabel(pc24); this.RoundGuardAtPrecision(guard, exponent, sign, 24); this._asm.Jmp(afterRound);
    this._asm.MarkLabel(pc53); this.RoundGuardAtPrecision(guard, exponent, sign, 53); this._asm.Jmp(afterRound);
    this._asm.MarkLabel(pc64); this.RoundGuardAtPrecision(guard, exponent, sign, 64);
    this._asm.MarkLabel(afterRound);
    for (var i = 0; i < 4; ++i) { this._asm.Mov(Reg.AX, this.Scratch(guard + (i + 1) * 2, OperandSize.Word)); this._asm.Mov(this.Scratch(result + i * 2, OperandSize.Word), Reg.AX); }
    this._asm.Mov(Reg.AX, exponent); this._asm.Mov(this.Scratch(result + Exponent, OperandSize.Word), Reg.AX);
    this._asm.Mov(Reg.AX, sign); this._asm.And(Reg.AX, SignMask); this._asm.Mov(this.Scratch(result + Meta, OperandSize.Word), Reg.AX);
    this.FinalizeCanonicalRange(result);
  }

  private void RoundGuardAtPrecision(int guard, Mem exponent, Mem sign, int precision) {
    var guardFlag = this.Scratch(Tmp2, OperandSize.Word); var sticky = this.Scratch(Tmp3, OperandSize.Word);
    this._asm.Mov(guardFlag, 0); this._asm.Mov(sticky, 0);
    if (precision == 64) {
      this._asm.Mov(Reg.AX, this.Scratch(guard, OperandSize.Word)); this._asm.Mov(Reg.DX, Reg.AX); this._asm.And(Reg.DX, 0x8000); for (var i = 0; i < 15; ++i) this._asm.Shr(Reg.DX, 1); this._asm.Mov(guardFlag, Reg.DX); this._asm.And(Reg.AX, 0x7FFF); this._asm.Cmp(Reg.AX, 0); var no = this._asm.DefineLabel(); this._asm.J(Condition.Equal, no); this._asm.Mov(sticky, 1); this._asm.MarkLabel(no);
    } else if (precision == 53) {
      this._asm.Mov(Reg.AX, this.Scratch(guard + 2, OperandSize.Word)); this._asm.Mov(Reg.DX, Reg.AX); this._asm.And(Reg.DX, 0x0400); for (var i = 0; i < 10; ++i) this._asm.Shr(Reg.DX, 1); this._asm.Mov(guardFlag, Reg.DX); this._asm.And(Reg.AX, 0x03FF); this._asm.Or(Reg.AX, this.Scratch(guard, OperandSize.Word)); var no = this._asm.DefineLabel(); this._asm.Cmp(Reg.AX, 0); this._asm.J(Condition.Equal, no); this._asm.Mov(sticky, 1); this._asm.MarkLabel(no); this._asm.And(this.Scratch(guard + 2, OperandSize.Word), 0xF800); this._asm.Mov(this.Scratch(guard, OperandSize.Word), 0);
    } else {
      this._asm.Mov(Reg.AX, this.Scratch(guard + 6, OperandSize.Word)); this._asm.Mov(Reg.DX, Reg.AX); this._asm.And(Reg.DX, 0x0080); for (var i = 0; i < 7; ++i) this._asm.Shr(Reg.DX, 1); this._asm.Mov(guardFlag, Reg.DX); this._asm.And(Reg.AX, 0x007F); this._asm.Or(Reg.AX, this.Scratch(guard, OperandSize.Word)); this._asm.Or(Reg.AX, this.Scratch(guard + 2, OperandSize.Word)); this._asm.Or(Reg.AX, this.Scratch(guard + 4, OperandSize.Word)); var no = this._asm.DefineLabel(); this._asm.Cmp(Reg.AX, 0); this._asm.J(Condition.Equal, no); this._asm.Mov(sticky, 1); this._asm.MarkLabel(no); this._asm.Mov(this.Scratch(guard, OperandSize.Word), 0); this._asm.Mov(this.Scratch(guard + 2, OperandSize.Word), 0); this._asm.Mov(this.Scratch(guard + 4, OperandSize.Word), 0); this._asm.And(this.Scratch(guard + 6, OperandSize.Word), 0xFF00);
    }
    this.ApplyGuardRounding(guard, exponent, sign, precision, guardFlag, sticky);
  }

  private void ApplyGuardRounding(int guard, Mem exponent, Mem sign, int precision, Mem guardFlag, Mem sticky) {
    var discarded = this._asm.DefineLabel(); var done = this._asm.DefineLabel(); var increment = this._asm.DefineLabel();
    this._asm.Mov(Reg.AX, guardFlag); this._asm.Or(Reg.AX, sticky); this._asm.J(Condition.NotEqual, discarded); this._asm.Jmp(done);
    this._asm.MarkLabel(discarded); this.SetStatusBits(0x0020);
    this._asm.Mov(Reg.AX, this.Control); this._asm.Mov(Reg.DX, Reg.AX); for (var i = 0; i < 10; ++i) this._asm.Shr(Reg.DX, 1); this._asm.And(Reg.DX, 3);
    var nearest = this._asm.DefineLabel(); var down = this._asm.DefineLabel(); var up = this._asm.DefineLabel();
    this._asm.Cmp(Reg.DX, 0); this._asm.J(Condition.Equal, nearest); this._asm.Cmp(Reg.DX, 1); this._asm.J(Condition.Equal, down); this._asm.Cmp(Reg.DX, 2); this._asm.J(Condition.Equal, up); this._asm.Jmp(done);
    this._asm.MarkLabel(nearest); this._asm.Cmp(guardFlag, 0); this._asm.J(Condition.Equal, done); this._asm.Cmp(sticky, 0); this._asm.J(Condition.NotEqual, increment);
    var lsbMask = precision == 64 ? 0x0001 : precision == 53 ? 0x0800 : 0x0100; var lsbWord = precision == 24 ? guard + 6 : guard + 2;
    this._asm.Test(this.Scratch(lsbWord, OperandSize.Word), lsbMask); this._asm.J(Condition.NotEqual, increment); this._asm.Jmp(done);
    this._asm.MarkLabel(down); this._asm.Test(sign, SignMask); this._asm.J(Condition.NotEqual, increment); this._asm.Jmp(done);
    this._asm.MarkLabel(up); this._asm.Test(sign, SignMask); this._asm.J(Condition.Equal, increment); this._asm.Jmp(done);
    this._asm.MarkLabel(increment);
    if (precision == 64) this._asm.Add(this.Scratch(guard + 2, OperandSize.Word), 1);
    else if (precision == 53) this._asm.Add(this.Scratch(guard + 2, OperandSize.Word), 0x0800);
    else this._asm.Add(this.Scratch(guard + 6, OperandSize.Word), 0x0100);
    var startWord = precision == 24 ? 4 : 2;
    for (var offset = startWord; offset <= 8; offset += 2) { if (offset == startWord) continue; this._asm.Adc(this.Scratch(guard + offset, OperandSize.Word), 0); }
    var noOverflow = this._asm.DefineLabel(); this._asm.J(Condition.AboveOrEqual, noOverflow);
    for (var i = 0; i < 4; ++i) this._asm.Mov(this.Scratch(guard + 2 + i * 2, OperandSize.Word), 0);
    this._asm.Mov(this.Scratch(guard + 8, OperandSize.Word), 0x8000); this._asm.Inc(exponent);
    this._asm.MarkLabel(noOverflow); this._asm.MarkLabel(done);
  }

  private void FinalizeCanonicalRange(int result) {
    var okay = this._asm.DefineLabel(); var overflow = this._asm.DefineLabel(); var tiny = this._asm.DefineLabel(); var done = this._asm.DefineLabel();
    this._asm.Mov(Reg.AX, this.Scratch(result + Exponent, OperandSize.Word)); this._asm.Cmp(Reg.AX, 16383); this._asm.J(Condition.Greater, overflow); this._asm.Cmp(Reg.AX, -16445); this._asm.J(Condition.Less, tiny); this._asm.Jmp(okay);
    this._asm.MarkLabel(overflow); this._asm.Mov(Reg.AX, this.Scratch(result + Meta, OperandSize.Word)); this._asm.And(Reg.AX, SignMask); this._asm.Or(Reg.AX, ClassInfinity); this._asm.Mov(this.Scratch(result + Meta, OperandSize.Word), Reg.AX); for (var i = 0; i < 3; ++i) this._asm.Mov(this.Scratch(result + i * 2, OperandSize.Word), 0); this._asm.Mov(this.Scratch(result + Sig3, OperandSize.Word), 0x8000); this.SetStatusBits(0x0028); this._asm.Jmp(done);
    this._asm.MarkLabel(tiny); this._asm.Mov(Reg.AX, this.Scratch(result + Meta, OperandSize.Word)); this._asm.And(Reg.AX, SignMask); this._asm.Or(Reg.AX, ClassZero); this._asm.Mov(this.Scratch(result + Meta, OperandSize.Word), Reg.AX); for (var i = 0; i < 4; ++i) this._asm.Mov(this.Scratch(result + i * 2, OperandSize.Word), 0); this.SetStatusBits(0x0030); this._asm.Jmp(done);
    this._asm.MarkLabel(okay); this._asm.MarkLabel(done);
  }

  #endregion
}
