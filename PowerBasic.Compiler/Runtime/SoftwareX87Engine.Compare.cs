using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

public sealed partial class SoftwareX87Engine {
  private bool EmitInlineCompare(string mnemonic, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    error = null;
    var pop = mnemonic.EndsWith("COMP", StringComparison.Ordinal) ? 1 : 0;
    var unordered = mnemonic.StartsWith("FU", StringComparison.Ordinal);
    var integer = mnemonic.StartsWith("FI", StringComparison.Ordinal);
    if (integer) {
      if (operands.Count != 1 || operands[0] is not TextAssembler.ParsedAsmMemory memory
          || memory.Memory.Size is not (OperandSize.Word or OperandSize.Dword))
        return Fail($"{mnemonic} expects word/dword integer memory", out error);
      return this.EmitCompareMemory(memory.Memory, memory.Memory.Size == OperandSize.Word ? 16 : 32,
        integer: true, pop, unordered: false);
    }

    if (operands.Count == 0)
      return this.EmitCompareStack(1, pop, unordered);
    if (operands.Count == 1 && operands[0] is TextAssembler.ParsedAsmSt st)
      return this.EmitCompareStack(st.Register.Index, pop, unordered);
    if (operands.Count == 1 && operands[0] is TextAssembler.ParsedAsmMemory m
        && m.Memory.Size is OperandSize.Dword or OperandSize.Qword)
      return this.EmitCompareMemory(m.Memory, m.Memory.Size == OperandSize.Dword ? 32 : 64,
        integer: false, pop, unordered);
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
    this.CopySlotToScratch(0, ScratchA);
    this.CopySlotToScratch(index, ScratchB);
    this.EmitCanonicalCompare(ScratchA, ScratchB, unordered);
    for (var i = 0; i < popCount; ++i) this.EmitPop();
    this.RestoreIntegerState();
    return true;
  }

  private bool EmitTestZero() {
    this.PreserveIntegerState();
    this.CopySlotToScratch(0, ScratchA);
    this.ZeroCanonical(ScratchB);
    this._asm.Mov(this.Scratch(ScratchB + Meta, OperandSize.Word), ClassZero);
    this.EmitCanonicalCompare(ScratchA, ScratchB, unorderedQuiet: false);
    this.RestoreIntegerState();
    return true;
  }

  private void EmitCanonicalCompare(int a, int b, bool unorderedQuiet) {
    var unordered = this._asm.DefineLabel();
    var equal = this._asm.DefineLabel();
    var less = this._asm.DefineLabel();
    var greater = this._asm.DefineLabel();
    var done = this._asm.DefineLabel();
    var aNan = this._asm.DefineLabel();
    var bNan = this._asm.DefineLabel();
    this.BranchIfClass(a, ClassNaN, aNan);
    this.BranchIfClass(b, ClassNaN, bNan);

    var aZero = this._asm.DefineLabel();
    var bZero = this._asm.DefineLabel();
    var nonZero = this._asm.DefineLabel();
    this.BranchIfClass(a, ClassZero, aZero);
    this.BranchIfClass(b, ClassZero, bZero);
    this._asm.Jmp(nonZero);

    this._asm.MarkLabel(aZero);
    this.BranchIfClass(b, ClassZero, equal);
    this._asm.Test(this.Scratch(b + Meta, OperandSize.Word), SignMask);
    this._asm.J(Condition.NotEqual, greater); // 0 > negative
    this._asm.Jmp(less);

    this._asm.MarkLabel(bZero);
    this._asm.Test(this.Scratch(a + Meta, OperandSize.Word), SignMask);
    this._asm.J(Condition.NotEqual, less);
    this._asm.Jmp(greater);

    this._asm.MarkLabel(nonZero);
    // Different signs settle every nonzero ordering immediately.
    this._asm.Mov(Reg.AX, this.Scratch(a + Meta, OperandSize.Word));
    this._asm.Xor(Reg.AX, this.Scratch(b + Meta, OperandSize.Word));
    var sameSign = this._asm.DefineLabel();
    this._asm.Test(Reg.AX, SignMask); this._asm.J(Condition.Equal, sameSign);
    this._asm.Test(this.Scratch(a + Meta, OperandSize.Word), SignMask);
    this._asm.J(Condition.NotEqual, less); this._asm.Jmp(greater);
    this._asm.MarkLabel(sameSign);

    var aInf = this._asm.DefineLabel();
    var bInf = this._asm.DefineLabel();
    var finite = this._asm.DefineLabel();
    this.BranchIfClass(a, ClassInfinity, aInf);
    this.BranchIfClass(b, ClassInfinity, bInf);
    this._asm.Jmp(finite);
    this._asm.MarkLabel(aInf);
    this.BranchIfClass(b, ClassInfinity, equal);
    this._asm.Test(this.Scratch(a + Meta, OperandSize.Word), SignMask);
    this._asm.J(Condition.NotEqual, less); this._asm.Jmp(greater);
    this._asm.MarkLabel(bInf);
    this._asm.Test(this.Scratch(b + Meta, OperandSize.Word), SignMask);
    this._asm.J(Condition.NotEqual, greater); this._asm.Jmp(less);

    this._asm.MarkLabel(finite);
    // Compare magnitudes, reversing the result when both operands are negative.
    var magnitudeGreater = this._asm.DefineLabel();
    var magnitudeLess = this._asm.DefineLabel();
    var magnitudeEqual = this._asm.DefineLabel();
    this._asm.Mov(Reg.AX, this.Scratch(a + Exponent, OperandSize.Word));
    this._asm.Cmp(Reg.AX, this.Scratch(b + Exponent, OperandSize.Word));
    this._asm.J(Condition.Greater, magnitudeGreater);
    this._asm.J(Condition.Less, magnitudeLess);
    for (var i = 3; i >= 0; --i) {
      var next = this._asm.DefineLabel();
      this._asm.Mov(Reg.AX, this.Scratch(a + i * 2, OperandSize.Word));
      this._asm.Cmp(Reg.AX, this.Scratch(b + i * 2, OperandSize.Word));
      this._asm.J(Condition.Equal, next);
      this._asm.J(Condition.Above, magnitudeGreater);
      this._asm.Jmp(magnitudeLess);
      this._asm.MarkLabel(next);
    }
    this._asm.Jmp(magnitudeEqual);

    this._asm.MarkLabel(magnitudeGreater);
    this._asm.Test(this.Scratch(a + Meta, OperandSize.Word), SignMask);
    this._asm.J(Condition.NotEqual, less); this._asm.Jmp(greater);
    this._asm.MarkLabel(magnitudeLess);
    this._asm.Test(this.Scratch(a + Meta, OperandSize.Word), SignMask);
    this._asm.J(Condition.NotEqual, greater); this._asm.Jmp(less);
    this._asm.MarkLabel(magnitudeEqual); this._asm.Jmp(equal);

    // Ordered FCOM treats every NaN as invalid. FUCOM is quiet for qNaN but still invalid for sNaN.
    this._asm.MarkLabel(aNan);
    if (!unorderedQuiet) {
      this.RaiseException(StatusInvalid);
      this._asm.Jmp(unordered);
    }
    var aQuiet = this._asm.DefineLabel();
    this._asm.Test(this.Scratch(a + Meta, OperandSize.Word), SignalingNaNMask);
    this._asm.J(Condition.Equal, aQuiet);
    this.RaiseException(StatusInvalid);
    this._asm.MarkLabel(aQuiet);
    this._asm.Jmp(unordered);

    this._asm.MarkLabel(bNan);
    if (!unorderedQuiet) {
      this.RaiseException(StatusInvalid);
      this._asm.Jmp(unordered);
    }
    var bQuiet = this._asm.DefineLabel();
    this._asm.Test(this.Scratch(b + Meta, OperandSize.Word), SignalingNaNMask);
    this._asm.J(Condition.Equal, bQuiet);
    this.RaiseException(StatusInvalid);
    this._asm.MarkLabel(bQuiet);

    this._asm.MarkLabel(unordered); this.SetCompareConditionCodes(true, true, true); this._asm.Jmp(done);
    this._asm.MarkLabel(equal); this.SetCompareConditionCodes(false, false, true); this._asm.Jmp(done);
    this._asm.MarkLabel(less); this.SetCompareConditionCodes(true, false, false); this._asm.Jmp(done);
    this._asm.MarkLabel(greater); this.SetCompareConditionCodes(false, false, false);
    this._asm.MarkLabel(done);
  }
}
