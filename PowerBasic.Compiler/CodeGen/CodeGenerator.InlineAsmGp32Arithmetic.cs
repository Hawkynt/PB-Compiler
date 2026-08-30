using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Runtime;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {
  private const int GpArithA = 98;
  private const int GpArithB = 102;
  private const int GpArithWide = 106; // four words, low dword first
  private const int GpArithAux = 114;
  private const int GpArithFlags = 118;
  private const int GpArithCount = 120;
  private const int GpArithSign = 122;

  private bool TryEmitVirtualGp32ArithmeticInstruction(InlineInstruction instruction, InlineAsmResolver resolver,
      RuntimeTarget target, out string? error) {
    error = null;
    if (instruction.Mnemonic is not ("MUL" or "IMUL" or "DIV" or "IDIV" or "ROL" or "ROR" or "RCL" or "RCR"))
      return false;

    this._textAssembler ??= new(this._asm);
    if (!this._textAssembler.TryParseOperands(instruction.Operands, resolver, out var operands, out error))
      return true;

    var state = this.EnsureVirtualIsaState();
    return instruction.Mnemonic switch {
      "MUL" => this.EmitVirtualMul(state, operands, target, signed: false, out error),
      "IMUL" => this.EmitVirtualImul(state, operands, target, out error),
      "DIV" => this.EmitVirtualDiv(state, operands, target, signed: false, out error),
      "IDIV" => this.EmitVirtualDiv(state, operands, target, signed: true, out error),
      _ => this.EmitVirtualDwordRotate(state, instruction.Mnemonic, operands, target, out error),
    };
  }

  private static bool IsDwordSource(TextAssembler.ParsedAsmOperand operand) => operand switch {
    TextAssembler.ParsedAsmRegister r => r.Register.IsDword() && r.Register != Reg.ESP,
    TextAssembler.ParsedAsmMemory m => m.Memory.Size == OperandSize.Dword,
    TextAssembler.ParsedAsmImmediate => true,
    _ => false,
  };

  #region multiply

  private bool EmitVirtualMul(VirtualIsaState state, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands,
      RuntimeTarget target, bool signed, out string? error) {
    error = null;
    if (operands.Count != 1 || !IsDwordSource(operands[0]))
      return false;

    this.StageDword(state, new TextAssembler.ParsedAsmRegister(Reg.EAX), GpArithA, target);
    this.StageDword(state, operands[0], GpArithB, target);
    this.SaveArithmeticFlags(state);
    this._asm.Push(Reg.CX);
    if (signed)
      this.EmitSignedMultiply32(state, GpArithA, GpArithB, GpArithWide);
    else
      this.EmitUnsignedMultiply32(state, GpArithA, GpArithB, GpArithWide);
    this._asm.Pop(Reg.CX);

    this.WriteDwordPlace(state, DwordPlace.Of(Reg.EAX), GpArithWide, target);
    this.WriteDwordPlace(state, DwordPlace.Of(Reg.EDX), GpArithWide + 4, target);
    if (signed)
      this.RestoreSignedMultiplyFlags(state, GpArithWide);
    else
      this.RestoreUnsignedMultiplyFlags(state, GpArithWide + 4);
    return true;
  }

  private bool EmitVirtualImul(VirtualIsaState state, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands,
      RuntimeTarget target, out string? error) {
    error = null;
    if (operands.Count == 1)
      return this.EmitVirtualMul(state, operands, target, signed: true, out error);

    if (operands.Count is not (2 or 3)
        || operands[0] is not TextAssembler.ParsedAsmRegister destination
        || !destination.Register.IsDword() || destination.Register == Reg.ESP) {
      error = "32-bit IMUL expects r/m32, r32,r/m32, or r32,r/m32,imm";
      return true;
    }

    TextAssembler.ParsedAsmOperand left;
    TextAssembler.ParsedAsmOperand right;
    if (operands.Count == 2) {
      if (!IsDwordSource(operands[1])) { error = "two-operand IMUL requires a dword source"; return true; }
      left = operands[0];
      right = operands[1];
    } else {
      if (!IsDwordSource(operands[1]) || operands[2] is not TextAssembler.ParsedAsmImmediate) {
        error = "three-operand IMUL requires r32,r/m32,immediate";
        return true;
      }
      left = operands[1];
      right = operands[2];
    }

    this.StageDword(state, left, GpArithA, target);
    this.StageDword(state, right, GpArithB, target);
    this.SaveArithmeticFlags(state);
    this._asm.Push(Reg.CX);
    this.EmitSignedMultiply32(state, GpArithA, GpArithB, GpArithWide);
    this._asm.Pop(Reg.CX);
    this.WriteDwordPlace(state, DwordPlace.Of(destination.Register), GpArithWide, target);
    this.RestoreSignedMultiplyFlags(state, GpArithWide);
    return true;
  }

  private void EmitUnsignedMultiply32(VirtualIsaState state, int a, int b, int wide) {
    var a0 = this.GpScratch(state, a);
    var a1 = this.GpScratch(state, a + 2);
    var b0 = this.GpScratch(state, b);
    var b1 = this.GpScratch(state, b + 2);
    var w0 = this.GpScratch(state, wide);
    var w1 = this.GpScratch(state, wide + 2);
    var w2 = this.GpScratch(state, wide + 4);
    var w3 = this.GpScratch(state, wide + 6);

    this._asm.Mov(Reg.AX, a0); this._asm.Mov(Reg.CX, b0); this._asm.Mul(Reg.CX);
    this._asm.Mov(w0, Reg.AX); this._asm.Mov(w1, Reg.DX);
    this._asm.Mov(w2, 0); this._asm.Mov(w3, 0);

    this._asm.Mov(Reg.AX, a0); this._asm.Mov(Reg.CX, b1); this._asm.Mul(Reg.CX);
    this._asm.Add(w1, Reg.AX); this._asm.Adc(w2, Reg.DX); this._asm.Adc(w3, 0);

    this._asm.Mov(Reg.AX, a1); this._asm.Mov(Reg.CX, b0); this._asm.Mul(Reg.CX);
    this._asm.Add(w1, Reg.AX); this._asm.Adc(w2, Reg.DX); this._asm.Adc(w3, 0);

    this._asm.Mov(Reg.AX, a1); this._asm.Mov(Reg.CX, b1); this._asm.Mul(Reg.CX);
    this._asm.Add(w2, Reg.AX); this._asm.Adc(w3, Reg.DX);
  }

  private void EmitSignedMultiply32(VirtualIsaState state, int a, int b, int wide) {
    var sign = this.GpScratch(state, GpArithSign);
    this._asm.Mov(sign, 0);
    this.EmitMakeDwordMagnitude(state, a, sign);
    this.EmitMakeDwordMagnitude(state, b, sign);
    this.EmitUnsignedMultiply32(state, a, b, wide);
    var done = this._asm.DefineLabel();
    this._asm.Test(sign, 1);
    this._asm.J(Condition.Equal, done);
    this.EmitNegateWords(state, wide, 4);
    this._asm.MarkLabel(done);
  }

  private void EmitMakeDwordMagnitude(VirtualIsaState state, int offset, Mem sign) {
    var done = this._asm.DefineLabel();
    this._asm.Test(this.GpScratch(state, offset + 2), 0x8000);
    this._asm.J(Condition.Equal, done);
    this._asm.Xor(sign, 1);
    this.EmitNegateWords(state, offset, 2);
    this._asm.MarkLabel(done);
  }

  private void EmitNegateWords(VirtualIsaState state, int offset, int words) {
    for (var i = 0; i < words; ++i)
      this._asm.Not(this.GpScratch(state, offset + i * 2));
    this._asm.Add(this.GpScratch(state, offset), 1);
    for (var i = 1; i < words; ++i)
      this._asm.Adc(this.GpScratch(state, offset + i * 2), 0);
  }

  private void SaveArithmeticFlags(VirtualIsaState state) {
    this._asm.Push(Reg.AX);
    this._asm.Pushf(); this._asm.Pop(Reg.AX);
    this._asm.Mov(this.GpScratch(state, GpArithFlags), Reg.AX);
    this._asm.Pop(Reg.AX);
  }

  private void RestoreUnsignedMultiplyFlags(VirtualIsaState state, int highDwordOffset) {
    var noOverflow = this._asm.DefineLabel();
    var done = this._asm.DefineLabel();
    this._asm.Push(Reg.AX);
    this._asm.Mov(Reg.AX, this.GpScratch(state, highDwordOffset));
    this._asm.Or(Reg.AX, this.GpScratch(state, highDwordOffset + 2));
    this._asm.J(Condition.Equal, noOverflow);
    this.RestoreMulFlagsFromOriginal(state, overflow: true);
    this._asm.Jmp(done);
    this._asm.MarkLabel(noOverflow);
    this.RestoreMulFlagsFromOriginal(state, overflow: false);
    this._asm.MarkLabel(done);
    this._asm.Pop(Reg.AX);
  }

  private void RestoreSignedMultiplyFlags(VirtualIsaState state, int wide) {
    var overflow = this._asm.DefineLabel();
    var noOverflow = this._asm.DefineLabel();
    var done = this._asm.DefineLabel();
    var lowerNegative = this._asm.DefineLabel();
    this._asm.Push(Reg.AX);
    this._asm.Mov(Reg.AX, this.GpScratch(state, wide + 2));
    this._asm.Test(Reg.AX, 0x8000);
    this._asm.J(Condition.NotEqual, lowerNegative);
    this._asm.Cmp(this.GpScratch(state, wide + 4), 0);
    this._asm.J(Condition.NotEqual, overflow);
    this._asm.Cmp(this.GpScratch(state, wide + 6), 0);
    this._asm.J(Condition.NotEqual, overflow);
    this._asm.Jmp(noOverflow);
    this._asm.MarkLabel(lowerNegative);
    this._asm.Cmp(this.GpScratch(state, wide + 4), -1);
    this._asm.J(Condition.NotEqual, overflow);
    this._asm.Cmp(this.GpScratch(state, wide + 6), -1);
    this._asm.J(Condition.NotEqual, overflow);
    this._asm.Jmp(noOverflow);
    this._asm.MarkLabel(overflow);
    this.RestoreMulFlagsFromOriginal(state, overflow: true);
    this._asm.Jmp(done);
    this._asm.MarkLabel(noOverflow);
    this.RestoreMulFlagsFromOriginal(state, overflow: false);
    this._asm.MarkLabel(done);
    this._asm.Pop(Reg.AX);
  }

  private void RestoreMulFlagsFromOriginal(VirtualIsaState state, bool overflow) {
    this._asm.Push(Reg.AX);
    this._asm.Mov(Reg.AX, this.GpScratch(state, GpArithFlags));
    this._asm.And(Reg.AX, 0xF7FE); // only CF/OF are defined for MUL/IMUL
    if (overflow) this._asm.Or(Reg.AX, 0x0801);
    this._asm.Push(Reg.AX); this._asm.Popf();
    this._asm.Pop(Reg.AX);
  }

  #endregion

  #region divide

  private bool EmitVirtualDiv(VirtualIsaState state, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands,
      RuntimeTarget target, bool signed, out string? error) {
    error = null;
    if (operands.Count != 1 || !IsDwordSource(operands[0]))
      return false;

    this.StageDword(state, new TextAssembler.ParsedAsmRegister(Reg.EAX), GpArithWide, target);
    this.StageDword(state, new TextAssembler.ParsedAsmRegister(Reg.EDX), GpArithWide + 4, target);
    this.StageDword(state, operands[0], GpArithB, target);
    this.SaveArithmeticFlags(state);

    this._asm.Push(Reg.BX); this._asm.Push(Reg.CX); this._asm.Push(Reg.DX);
    if (signed)
      this.EmitSignedDivide32(state);
    else
      this.EmitUnsignedDivide32(state, GpArithWide, GpArithB, GpArithA, GpArithAux);
    this._asm.Pop(Reg.DX); this._asm.Pop(Reg.CX); this._asm.Pop(Reg.BX);

    this.WriteDwordPlace(state, DwordPlace.Of(Reg.EAX), GpArithA, target);
    this.WriteDwordPlace(state, DwordPlace.Of(Reg.EDX), GpArithAux, target);
    // DIV/IDIV leave status flags undefined; retaining the incoming image is deterministic.
    this._asm.Push(this.GpScratch(state, GpArithFlags)); this._asm.Popf();
    return true;
  }

  /// <summary>
  /// Unsigned restoring division of a 64-bit four-word dividend by a two-word divisor. The 386
  /// architectural precondition high32 &lt; divisor is checked before the 32 quotient-bit iterations.
  /// </summary>
  private void EmitUnsignedDivide32(VirtualIsaState state, int dividend64, int divisor, int quotient, int remainder) {
    var div0 = this.GpScratch(state, divisor);
    var div1 = this.GpScratch(state, divisor + 2);
    var q0 = this.GpScratch(state, quotient);
    var q1 = this.GpScratch(state, quotient + 2);
    var r0 = this.GpScratch(state, remainder);
    var r1 = this.GpScratch(state, remainder + 2);

    var divisorOk = this._asm.DefineLabel();
    this._asm.Mov(Reg.AX, div0); this._asm.Or(Reg.AX, div1);
    this._asm.J(Condition.NotEqual, divisorOk);
    this.EmitDivideError();
    this._asm.MarkLabel(divisorOk);

    this._asm.Mov(Reg.AX, this.GpScratch(state, dividend64)); this._asm.Mov(q0, Reg.AX);
    this._asm.Mov(Reg.AX, this.GpScratch(state, dividend64 + 2)); this._asm.Mov(q1, Reg.AX);
    this._asm.Mov(Reg.AX, this.GpScratch(state, dividend64 + 4)); this._asm.Mov(r0, Reg.AX);
    this._asm.Mov(Reg.AX, this.GpScratch(state, dividend64 + 6)); this._asm.Mov(r1, Reg.AX);

    var highLess = this._asm.DefineLabel();
    var highEqual = this._asm.DefineLabel();
    this._asm.Mov(Reg.AX, r1); this._asm.Cmp(Reg.AX, div1);
    this._asm.J(Condition.Below, highLess);
    this._asm.J(Condition.Equal, highEqual);
    this.EmitDivideError();
    this._asm.MarkLabel(highEqual);
    this._asm.Mov(Reg.AX, r0); this._asm.Cmp(Reg.AX, div0);
    this._asm.J(Condition.Below, highLess);
    this.EmitDivideError();
    this._asm.MarkLabel(highLess);

    this._asm.Mov(Reg.CX, 32);
    var loop = this._asm.DefineLabel();
    var subtract = this._asm.DefineLabel();
    var next = this._asm.DefineLabel();
    this._asm.MarkLabel(loop);
    this._asm.Shl(q0, 1); this._asm.Rcl(q1, 1);
    this._asm.Rcl(r0, 1); this._asm.Rcl(r1, 1);
    this._asm.J(Condition.Below, subtract); // 33rd remainder bit is set
    this._asm.Mov(Reg.AX, r1); this._asm.Cmp(Reg.AX, div1);
    this._asm.J(Condition.Above, subtract);
    this._asm.J(Condition.Below, next);
    this._asm.Mov(Reg.AX, r0); this._asm.Cmp(Reg.AX, div0);
    this._asm.J(Condition.Below, next);
    this._asm.MarkLabel(subtract);
    this._asm.Mov(Reg.AX, div0); this._asm.Sub(r0, Reg.AX);
    this._asm.Mov(Reg.AX, div1); this._asm.Sbb(r1, Reg.AX);
    this._asm.Inc(q0); // bit0 was just shifted in as zero; cannot carry
    this._asm.MarkLabel(next);
    this._asm.Loop(loop);
  }

  private void EmitSignedDivide32(VirtualIsaState state) {
    var sign = this.GpScratch(state, GpArithSign);
    this._asm.Mov(sign, 0);

    // bit0 = original dividend/remainder sign; bit1 = quotient sign.
    var dividendPositive = this._asm.DefineLabel();
    this._asm.Test(this.GpScratch(state, GpArithWide + 6), 0x8000);
    this._asm.J(Condition.Equal, dividendPositive);
    this._asm.Or(sign, 3);
    this.EmitNegateWords(state, GpArithWide, 4);
    this._asm.MarkLabel(dividendPositive);

    var divisorPositive = this._asm.DefineLabel();
    this._asm.Test(this.GpScratch(state, GpArithB + 2), 0x8000);
    this._asm.J(Condition.Equal, divisorPositive);
    this._asm.Xor(sign, 2);
    this.EmitNegateWords(state, GpArithB, 2);
    this._asm.MarkLabel(divisorPositive);

    this.EmitUnsignedDivide32(state, GpArithWide, GpArithB, GpArithA, GpArithAux);

    var negative = this._asm.DefineLabel();
    var rangeOk = this._asm.DefineLabel();
    var overflow = this._asm.DefineLabel();
    this._asm.Test(sign, 2);
    this._asm.J(Condition.NotEqual, negative);
    // Positive signed quotient must be <= 7fffffffh.
    this._asm.Test(this.GpScratch(state, GpArithA + 2), 0x8000);
    this._asm.J(Condition.Equal, rangeOk);
    this._asm.Jmp(overflow);

    this._asm.MarkLabel(negative);
    // Negative magnitude may be at most 80000000h.
    this._asm.Mov(Reg.AX, this.GpScratch(state, GpArithA + 2));
    this._asm.Cmp(Reg.AX, 0x8000);
    this._asm.J(Condition.Below, rangeOk);
    this._asm.J(Condition.Above, overflow);
    this._asm.Cmp(this.GpScratch(state, GpArithA), 0);
    this._asm.J(Condition.Equal, rangeOk);

    this._asm.MarkLabel(overflow);
    this.EmitDivideError();

    this._asm.MarkLabel(rangeOk);
    var quotientDone = this._asm.DefineLabel();
    this._asm.Test(sign, 2);
    this._asm.J(Condition.Equal, quotientDone);
    this.EmitNegateWords(state, GpArithA, 2);
    this._asm.MarkLabel(quotientDone);

    var remainderDone = this._asm.DefineLabel();
    this._asm.Test(sign, 1);
    this._asm.J(Condition.Equal, remainderDone);
    this.EmitNegateWords(state, GpArithAux, 2);
    this._asm.MarkLabel(remainderDone);
  }

  private void EmitDivideError() {
    this._asm.Xor(Reg.CX, Reg.CX);
    this._asm.Div(Reg.CX); // real 8086 #DE, matching the architectural instruction
  }

  #endregion

  #region rotate

  private bool EmitVirtualDwordRotate(VirtualIsaState state, string mnemonic,
      IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, RuntimeTarget target, out string? error) {
    error = null;
    if (operands.Count != 2 || !TryDwordPlace(operands[0], out var destination)
        || destination.Register == Reg.ESP
        || destination.Memory is { } memory && memory.Size != OperandSize.Dword)
      return false;

    this.StageDwordPlace(state, destination, GpArithA, target);
    this.SaveArithmeticFlags(state);
    var count = this.GpScratch(state, GpArithCount);
    switch (operands[1]) {
      case TextAssembler.ParsedAsmImmediate immediate:
        this._asm.Mov(count, immediate.Value & 31);
        break;
      case TextAssembler.ParsedAsmRegister { Register: Reg.CL }:
        this._asm.Push(Reg.AX);
        this._asm.Xor(Reg.AH, Reg.AH); this._asm.Mov(Reg.AL, Reg.CL);
        this._asm.And(Reg.AX, 31); this._asm.Mov(count, Reg.AX);
        this._asm.Pop(Reg.AX);
        break;
      default:
        error = $"32-bit {mnemonic} expects an immediate count or CL";
        return true;
    }

    var low = this.GpScratch(state, GpArithA);
    var high = this.GpScratch(state, GpArithA + 2);
    var carry = this.GpScratch(state, GpArithSign);
    this._asm.Push(Reg.AX); this._asm.Push(Reg.CX); this._asm.Push(Reg.DX);
    this._asm.Mov(Reg.CX, count);
    var unchanged = this._asm.DefineLabel();
    var loop = this._asm.DefineLabel();
    var finish = this._asm.DefineLabel();
    this._asm.Cmp(Reg.CX, 0);
    this._asm.J(Condition.Equal, unchanged);

    if (mnemonic is "RCL" or "RCR") {
      this._asm.Push(this.GpScratch(state, GpArithFlags));
      this._asm.Popf();
    }

    this._asm.MarkLabel(loop);
    switch (mnemonic) {
      case "ROL": {
        this._asm.Shl(low, 1); this._asm.Rcl(high, 1);
        var noWrap = this._asm.DefineLabel();
        this._asm.J(Condition.AboveOrEqual, noWrap);
        this._asm.Inc(low); // preserves the rotate carry
        this._asm.MarkLabel(noWrap);
        break;
      }
      case "ROR": {
        this._asm.Shr(high, 1); this._asm.Rcr(low, 1);
        this._asm.Pushf(); this._asm.Pop(Reg.AX);
        this._asm.And(Reg.AX, 1); this._asm.Mov(carry, Reg.AX);
        var noWrap = this._asm.DefineLabel();
        this._asm.Test(carry, 1); this._asm.J(Condition.Equal, noWrap);
        this._asm.Or(high, 0x8000);
        this._asm.MarkLabel(noWrap);
        break;
      }
      case "RCL":
        this._asm.Rcl(low, 1); this._asm.Rcl(high, 1);
        break;
      case "RCR":
        this._asm.Rcr(high, 1); this._asm.Rcr(low, 1);
        break;
    }
    this._asm.Loop(loop); // LOOP does not disturb flags

    if (mnemonic != "ROR") {
      this._asm.Pushf(); this._asm.Pop(Reg.AX);
      this._asm.And(Reg.AX, 1); this._asm.Mov(carry, Reg.AX);
    }
    this.WriteDwordPlace(state, destination, GpArithA, target);
    this.RestoreRotateFlags(state, mnemonic, high, count, carry);
    this._asm.Jmp(finish);

    this._asm.MarkLabel(unchanged);
    this._asm.Push(this.GpScratch(state, GpArithFlags)); this._asm.Popf();
    this._asm.MarkLabel(finish);
    this._asm.Pop(Reg.DX); this._asm.Pop(Reg.CX); this._asm.Pop(Reg.AX);
    return true;
  }

  private void RestoreRotateFlags(VirtualIsaState state, string mnemonic, Mem resultHigh, Mem count, Mem carry) {
    var merged = this.GpScratch(state, GpMergedFlagsScratch);
    this._asm.Mov(Reg.AX, this.GpScratch(state, GpArithFlags));
    this._asm.And(Reg.AX, 0xF7FE); // clear OF/CF only
    this._asm.Mov(merged, Reg.AX);
    this._asm.Mov(Reg.AX, carry); this._asm.And(Reg.AX, 1); this._asm.Or(merged, Reg.AX);

    var preserveOldOf = this._asm.DefineLabel();
    var noOf = this._asm.DefineLabel();
    var push = this._asm.DefineLabel();
    this._asm.Cmp(count, 1);
    this._asm.J(Condition.NotEqual, preserveOldOf);

    this._asm.Mov(Reg.DX, resultHigh);
    this._asm.Mov(Reg.AX, Reg.DX);
    this._asm.Shr(Reg.DX, 15); // new bit31
    if (mnemonic is "ROL" or "RCL") {
      this._asm.Mov(Reg.AX, carry); this._asm.And(Reg.AX, 1);
    } else {
      this._asm.Shr(Reg.AX, 14); this._asm.And(Reg.AX, 1); // new bit30
    }
    this._asm.Xor(Reg.DX, Reg.AX);
    this._asm.Test(Reg.DX, 1); this._asm.J(Condition.Equal, noOf);
    this._asm.Or(merged, 0x0800);
    this._asm.MarkLabel(noOf);
    this._asm.Jmp(push);

    this._asm.MarkLabel(preserveOldOf);
    this._asm.Mov(Reg.AX, this.GpScratch(state, GpArithFlags));
    this._asm.And(Reg.AX, 0x0800); this._asm.Or(merged, Reg.AX);
    this._asm.MarkLabel(push);
    this._asm.Push(merged); this._asm.Popf();
  }

  #endregion
}
