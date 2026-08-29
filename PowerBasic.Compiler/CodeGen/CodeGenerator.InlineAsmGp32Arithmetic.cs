using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Runtime;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {
  private const int GpArithA = 98;
  private const int GpArithB = 102;
  private const int GpArithWide = 106; // 8 bytes
  private const int GpArithAux = 114;  // 4 bytes
  private const int GpArithFlags = 118;
  private const int GpArithCount = 120;
  private const int GpArithSign = 122;

  /// <summary>
  /// Pre-386 emulation for the dword operations whose architectural result is wider than one dword,
  /// plus the four 32-bit rotates. All arithmetic is performed with 8086 word operations; the low
  /// halves of EAX..EDI remain the real 16-bit registers and the high halves live in the virtual GP
  /// bank used by the rest of the compatibility layer.
  /// </summary>
  private bool TryEmitVirtualGp32ArithmeticInstruction(InlineInstruction instruction, InlineAsmResolver resolver, RuntimeTarget target, out string? error) {
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
      "ROL" or "ROR" or "RCL" or "RCR" => this.EmitVirtualDwordRotate(state, instruction.Mnemonic, operands, target, out error),
      _ => false,
    };
  }

  private bool EmitVirtualMul(VirtualIsaState state, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, RuntimeTarget target, bool signed, out string? error) {
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

  private bool EmitVirtualImul(VirtualIsaState state, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, RuntimeTarget target, out string? error) {
    error = null;
    if (operands.Count == 1)
      return this.EmitVirtualMul(state, operands, target, signed: true, out error);

    if (operands.Count is not (2 or 3)
        || operands[0] is not TextAssembler.ParsedAsmRegister destination
        || !destination.Register.IsDword() || destination.Register == Reg.ESP) {
      error = "32-bit IMUL emulation expects IMUL r/m32, IMUL r32,r/m32, or IMUL r32,r/m32,imm";
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

  private static bool IsDwordSource(TextAssembler.ParsedAsmOperand operand) => operand switch {
    TextAssembler.ParsedAsmRegister r => r.Register.IsDword() && r.Register != Reg.ESP,
    TextAssembler.ParsedAsmMemory m => m.Memory.Size == OperandSize.Dword,
    TextAssembler.ParsedAsmImmediate => true,
    _ => false,
  };

  private void SaveArithmeticFlags(VirtualIsaState state) {
    this._asm.Push(Reg.AX);
    this._asm.Pushf();
    this._asm.Pop(Reg.AX);
    this._asm.Mov(this.GpScratch(state, GpArithFlags), Reg.AX);
    this._asm.Pop(Reg.AX);
  }

  /// <summary>Computes the complete unsigned 32x32 product as four little-endian words.</summary>
  private void EmitUnsignedMultiply32(VirtualIsaState state, int a, int b, int wide) {
    var a0 = this.GpScratch(state, a);
    var a1 = this.GpScratch(state, a + 2);
    var b0 = this.GpScratch(state, b);
    var b1 = this.GpScratch(state, b + 2);
    var w0 = this.GpScratch(state, wide);
    var w1 = this.GpScratch(state, wide + 2);
    var w2 = this.GpScratch(state, wide + 4);
    var w3 = this.GpScratch(state, wide + 6);

    this._asm.Mov(Reg.AX, a0);
    this._asm.Mov(Reg.CX, b0);
    this._asm.Mul(Reg.CX);
    this._asm.Mov(w0, Reg.AX);
    this._asm.Mov(w1, Reg.DX);
    this._asm.Mov(w2, 0);
    this._asm.Mov(w3, 0);

    this._asm.Mov(Reg.AX, a0);
    this._asm.Mov(Reg.CX, b1);
    this._asm.Mul(Reg.CX);
    this._asm.Add(w1, Reg.AX);
    this._asm.Adc(w2, Reg.DX);
    this._asm.Adc(w3, 0);

    this._asm.Mov(Reg.AX, a1);
    this._asm.Mov(Reg.CX, b0);
    this._asm.Mul(Reg.CX);
    this._asm.Add(w1, Reg.AX);
    this._asm.Adc(w2, Reg.DX);
    this._asm.Adc(w3, 0);

    this._asm.Mov(Reg.AX, a1);
    this._asm.Mov(Reg.CX, b1);
    this._asm.Mul(Reg.CX);
    this._asm.Add(w2, Reg.AX);
    this._asm.Adc(w3, Reg.DX);
  }

  private void EmitSignedMultiply32(VirtualIsaState state, int a, int b, int wide) {
    var sign = this.GpScratch(state, GpArithSign);
    this._asm.Mov(sign, 0);
    this.EmitMakeDwordMagnitude(state, a, sign);
    this.EmitMakeDwordMagnitude(state, b, sign);
    this.EmitUnsignedMultiply32(state, a, b, wide);

    var positive = this._asm.DefineLabel();
    this._asm.Test(sign, 1);
    this._asm.J(Condition.Equal, positive);
    this.EmitNegateWords(state, wide, 4);
    this._asm.MarkLabel(positive);
  }

  /// <summary>Converts one signed dword scratch cell to magnitude and toggles sign bit 0 when negative.</summary>
  private void EmitMakeDwordMagnitude(VirtualIsaState state, int offset, Mem sign) {
    var nonnegative = this._asm.DefineLabel();
    this._asm.Test(this.GpScratch(state, offset + 2), 0x8000);
    this._asm.J(Condition.Equal, nonnegative);
    this._asm.Xor(sign, 1);
    this.EmitNegateWords(state, offset, 2);
    this._asm.MarkLabel(nonnegative);
  }

  private void EmitNegateWords(VirtualIsaState state, int offset, int words) {
    for (var i = 0; i < words; ++i)
      this._asm.Not(this.GpScratch(state, offset + i * 2));
    this._asm.Add(this.GpScratch(state, offset), 1);
    for (var i = 1; i < words; ++i)
      this._asm.Adc(this.GpScratch(state, offset + i * 2), 0);
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
    this._asm.Push(Reg.AX);
    this._asm.Mov(Reg.AX, this.GpScratch(state, wide + 2));
    this._asm.Test(Reg.AX, 0x8000);
    var negative = this._asm.DefineLabel();
    this._asm.J(Condition.NotEqual, negative);
    this._asm.Cmp(this.GpScratch(state, wide + 4), 0);
    this._asm.J(Condition.NotEqual, overflow);
    this._asm.Cmp(this.GpScratch(state, wide + 6), 0);
    this._asm.J(Condition.NotEqual, overflow);
    this._asm.Jmp(noOverflow);
    this._asm.MarkLabel(negative);
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
    this._asm.And(Reg.AX, 0xF7FE); // clear OF and CF; all other MUL flags are architecturally undefined
    if (overflow)
      this._asm.Or(Reg.AX, 0x0801);
    this._asm.Push(Reg.AX);
    this._asm.Popf();
    this._asm.Pop(Reg.AX);
  }

  private bool EmitVirtualDiv(VirtualIsaState state, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, RuntimeTarget target, bool signed, out string? error) {
    error = null;
    if (operands.Count != 1 || !IsDwordSource(operands[0]))
      return false;

    // wide = EDX:EAX in little-endian word order; B = divisor.
    this.StageDword(state, new TextAssembler.ParsedAsmRegister(Reg.EAX), GpArithWide, target);
    this.StageDword(state, new TextAssembler.ParsedAsmRegister(Reg.EDX), GpArithWide + 4, target);
    this.StageDword(state, operands[0], GpArithB, target);
    this.SaveArithmeticFlags(state);

    this._asm.Push(Reg.BX);
    this._asm.Push(Reg.CX);
    this._asm.Push(Reg.DX);
    if (signed)
      this.EmitSignedDivide32(state);
    else
      this.EmitUnsignedDivide32(state, GpArithWide, GpArithB, GpArithA, GpArithAux);
    this._asm.Pop(Reg.DX);
    this._asm.Pop(Reg.CX);
    this._asm.Pop(Reg.BX);

    this.WriteDwordPlace(state, DwordPlace.Of(Reg.EAX), GpArithA, target);
    this.WriteDwordPlace(state, DwordPlace.Of(Reg.EDX), GpArithAux, target);
    // DIV/IDIV leave status flags undefined. Preserve the incoming image deterministically.
    this._asm.Push(this.GpScratch(state, GpArithFlags));
    this._asm.Popf();
    return true;
  }

  /// <summary>Unsigned restoring division: quotient and remainder are each 32-bit cells.</summary>
  private void EmitUnsignedDivide32(VirtualIsaState state, int dividend64, int divisor, int quotient, int remainder) {
    var div0 = this.GpScratch(state, divisor);
    var div1 = this.GpScratch(state, divisor + 2);
    var q0 = this.GpScratch(state, quotient);
    var q1 = this.GpScratch(state, quotient + 2);
    var r0 = this.GpScratch(state, remainder);
    var r1 = this.GpScratch(state, remainder + 2);

    var divisorOk = this._asm.DefineLabel();
    this._asm.Mov(Reg.AX, div0);
    this._asm.Or(Reg.AX, div1);
    this._asm.J(Condition.NotEqual, divisorOk);
    this.EmitDivideError();
    this._asm.MarkLabel(divisorOk);

    this._asm.Mov(Reg.AX, this.GpScratch(state, dividend64));
    this._asm.Mov(q0, Reg.AX);
    this._asm.Mov(Reg.AX, this.GpScratch(state, dividend64 + 2));
    this._asm.Mov(q1, Reg.AX);
    this._asm.Mov(Reg.AX, this.GpScratch(state, dividend64 + 4));
    this._asm.Mov(r0, Reg.AX);
    this._asm.Mov(Reg.AX, this.GpScratch(state, dividend64 + 6));
    this._asm.Mov(r1, Reg.AX);

    // A high dividend half >= divisor would produce a quotient that cannot fit 32 bits.
    var highLess = this._asm.DefineLabel();
    var highEqual = this._asm.DefineLabel();
    this._asm.Mov(Reg.AX, r1);
    this._asm.Cmp(Reg.AX, div1);
    this._asm.J(Condition.Below, highLess);
    this._asm.J(Condition.Equal, highEqual);
    this.EmitDivideError();
    this._asm.MarkLabel(highEqual);
    this._asm.Mov(Reg.AX, r0);
    this._asm.Cmp(Reg.AX, div0);
    this._asm.J(Condition.Below, highLess);
    this.EmitDivideError();
    this._asm.MarkLabel(highLess);

    this._asm.Mov(Reg.CX, 32);
    var loop = this._asm.DefineLabel();
    var subtract = this._asm.DefineLabel();
    var next = this._asm.DefineLabel();
    this._asm.MarkLabel(loop);
    this._asm.Shl(q0, 1);
    this._asm.Rcl(q1, 1);
    this._asm.Rcl(r0, 1);
    this._asm.Rcl(r1, 1);
    this._asm.J(Condition.Below, subtract); // 33rd remainder bit set => definitely >= divisor

    this._asm.Mov(Reg.AX, r1);
    this._asm.Cmp(Reg.AX, div1);
    this._asm.J(Condition.Above, subtract);
    this._asm.J(Condition.Below, next);
    this._asm.Mov(Reg.AX, r0);
    this._asm.Cmp(Reg.AX, div0);
    this._asm.J(Condition.Below, next);

    this._asm.MarkLabel(subtract);
    this._asm.Mov(Reg.AX, div0);
    this._asm.Sub(r0, Reg.AX);
    this._asm.Mov(Reg.AX, div1);
    this._asm.Sbb(r1, Reg.AX);
    this._asm.Inc(q0); // shifted quotient has bit 0 clear, so INC cannot carry into q1
    this._asm.MarkLabel(next);
    this._asm.Loop(loop);
  }

  private void EmitSignedDivide32(VirtualIsaState state) {
    var sign = this.GpScratch(state, GpArithSign);
    this._asm.Mov(sign, 0);

    // Dividend sign is bit 63. bit0 remembers the remainder sign, bit1 the quotient sign.
    var dividendPositive = this._asm.DefineLabel();
    this._asm.Test(this.GpScratch(state, GpArithWide + 6), 0x8000);
    this._asm.J(Condition.Equal, dividendPositive);
    this._asm.Or(sign, 0x0003);
    this.EmitNegateWords(state, GpArithWide, 4);
    this._asm.MarkLabel(dividendPositive);

    var divisorPositive = this._asm.DefineLabel();
    this._asm.Test(this.GpScratch(state, GpArithB + 2), 0x8000);
    this._asm.J(Condition.Equal, divisorPositive);
    this._asm.Xor(sign, 0x0002);
    this.EmitNegateWords(state, GpArithB, 2);
    this._asm.MarkLabel(divisorPositive);

    this.EmitUnsignedDivide32(state, GpArithWide, GpArithB, GpArithA, GpArithAux);

    // Signed quotient range is [-80000000h, 7fffffffh].
    var negativeQuotient = this._asm.DefineLabel();
    var quotientRangeOk = this._asm.DefineLabel();
    this._asm.Test(sign, 0x0002);
    this._asm.J(Condition.NotEqual, negativeQuotient);
    this._asm.Test(this.GpScratch(state, GpArithA + 2), 0x8000);
    this._asm.J(Condition.Equal, quotientRangeOk);
    this.EmitDivideError();

    this._asm.MarkLabel(negativeQuotient);
    this._asm.Mov(Reg.AX, this.GpScratch(state, GpArithA + 2));
    this._asm.Cmp(Reg.AX, 0x8000);
    this._asm.J(Condition.Below, quotientRangeOk);
    this._asm.J(Condition.Above, this._asm.DefineLabel());
    var highExactlyMin = this._asm.DefineLabel();
    this._asm.Jmp(highExactlyMin);
    // The preceding JA needs a bound target; place the fault immediately here.
    var tooLarge = this._asm.DefineLabel();
    this._asm.MarkLabel(tooLarge);
    this.EmitDivideError();
    this._asm.MarkLabel(highExactlyMin);
    this._asm.Cmp(this.GpScratch(state, GpArithA), 0);
    this._asm.J(Condition.Equal, quotientRangeOk);
    this.EmitDivideError();

    this._asm.MarkLabel(quotientRangeOk);
    var quotientDone = this._asm.DefineLabel();
    this._asm.Test(sign, 0x0002);
    this._asm.J(Condition.Equal, quotientDone);
    this.EmitNegateWords(state, GpArithA, 2);
    this._asm.MarkLabel(quotientDone);

    var remainderDone = this._asm.DefineLabel();
    this._asm.Test(sign, 0x0001);
    this._asm.J(Condition.Equal, remainderDone);
    this.EmitNegateWords(state, GpArithAux, 2);
    this._asm.MarkLabel(remainderDone);
  }

  /// <summary>Deliberately raises x86 divide error (#DE), matching DIV/IDIV fault behaviour.</summary>
  private void EmitDivideError() {
    this._asm.Xor(Reg.CX, Reg.CX);
    this._asm.Div(Reg.CX);
  }

  private bool EmitVirtualDwordRotate(VirtualIsaState state, string mnemonic, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, RuntimeTarget target, out string? error) {
    error = null;
    if (operands.Count != 2 || !TryDwordPlace(operands[0], out var destination)
        || destination.Register == Reg.ESP
        || destination.Memory is { } dm && dm.Size != OperandSize.Dword)
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
        this._asm.Xor(Reg.AH, Reg.AH);
        this._asm.Mov(Reg.AL, Reg.CL);
        this._asm.And(Reg.AX, 31);
        this._asm.Mov(count, Reg.AX);
        this._asm.Pop(Reg.AX);
        break;
      default:
        error = $"32-bit {mnemonic} emulation expects an immediate count or CL";
        return true;
    }

    var low = this.GpScratch(state, GpArithA);
    var high = this.GpScratch(state, GpArithA + 2);
    var carry = this.GpScratch(state, GpArithSign);
    this._asm.Push(Reg.AX);
    this._asm.Push(Reg.CX);
    this._asm.Push(Reg.DX);
    this._asm.Mov(Reg.CX, count);
    var unchanged = this._asm.DefineLabel();
    var loop = this._asm.DefineLabel();
    var finish = this._asm.DefineLabel();
    this._asm.Jcxz(unchanged);

    if (mnemonic is "RCL" or "RCR") {
      this._asm.Push(this.GpScratch(state, GpArithFlags));
      this._asm.Popf(); // seed carry from the architectural incoming flags
    }

    this._asm.MarkLabel(loop);
    switch (mnemonic) {
      case "ROL": {
        this._asm.Shl(low, 1);
        this._asm.Rcl(high, 1);
        var noWrap = this._asm.DefineLabel();
        this._asm.J(Condition.AboveOrEqual, noWrap);
        this._asm.Inc(low); // INC preserves CF
        this._asm.MarkLabel(noWrap);
        break;
      }
      case "ROR": {
        this._asm.Shr(high, 1);
        this._asm.Rcr(low, 1);
        this._asm.Pushf(); this._asm.Pop(Reg.AX); this._asm.And(Reg.AX, 1); this._asm.Mov(carry, Reg.AX);
        var noWrap = this._asm.DefineLabel();
        this._asm.Test(carry, 1);
        this._asm.J(Condition.Equal, noWrap);
        this._asm.Or(high, 0x8000);
        this._asm.MarkLabel(noWrap);
        break;
      }
      case "RCL":
        this._asm.Rcl(low, 1);
        this._asm.Rcl(high, 1);
        break;
      case "RCR":
        this._asm.Rcr(high, 1);
        this._asm.Rcr(low, 1);
        break;
    }
    this._asm.Loop(loop);

    if (mnemonic != "ROR") {
      this._asm.Pushf(); this._asm.Pop(Reg.AX); this._asm.And(Reg.AX, 1); this._asm.Mov(carry, Reg.AX);
    }
    this.WriteDwordPlace(state, destination, GpArithA, target);
    this.RestoreRotateFlags(state, mnemonic, high, count, carry);
    this._asm.Jmp(finish);

    this._asm.MarkLabel(unchanged);
    this._asm.Push(this.GpScratch(state, GpArithFlags));
    this._asm.Popf();
    this._asm.MarkLabel(finish);
    this._asm.Pop(Reg.DX);
    this._asm.Pop(Reg.CX);
    this._asm.Pop(Reg.AX);
    return true;
  }

  private void RestoreRotateFlags(VirtualIsaState state, string mnemonic, Mem resultHigh, Mem count, Mem carry) {
    var merged = this.GpScratch(state, GpMergedFlagsScratch);
    this._asm.Mov(Reg.AX, this.GpScratch(state, GpArithFlags));
    this._asm.And(Reg.AX, 0xF7FE); // rotates affect only CF and, for count 1, OF
    this._asm.Mov(merged, Reg.AX);
    this._asm.Mov(Reg.AX, carry);
    this._asm.And(Reg.AX, 1);
    this._asm.Or(merged, Reg.AX);

    var preserveOldOf = this._asm.DefineLabel();
    var noOf = this._asm.DefineLabel();
    var pushFlags = this._asm.DefineLabel();
    this._asm.Cmp(count, 1);
    this._asm.J(Condition.NotEqual, preserveOldOf);

    this._asm.Mov(Reg.DX, resultHigh);
    this._asm.Mov(Reg.AX, Reg.DX);
    this._asm.Shr(Reg.DX, 15); // result bit31
    if (mnemonic is "ROL" or "RCL") {
      this._asm.Mov(Reg.AX, carry);
      this._asm.And(Reg.AX, 1);
      this._asm.Xor(Reg.DX, Reg.AX);
    } else {
      this._asm.Shr(Reg.AX, 14); // result bit30 -> bit0
      this._asm.And(Reg.AX, 1);
      this._asm.Xor(Reg.DX, Reg.AX);
    }
    this._asm.Test(Reg.DX, 1);
    this._asm.J(Condition.Equal, noOf);
    this._asm.Or(merged, 0x0800);
    this._asm.MarkLabel(noOf);
    this._asm.Jmp(pushFlags);

    this._asm.MarkLabel(preserveOldOf);
    this._asm.Mov(Reg.AX, this.GpScratch(state, GpArithFlags));
    this._asm.And(Reg.AX, 0x0800); // OF is undefined for counts > 1; preserving is deterministic
    this._asm.Or(merged, Reg.AX);
    this._asm.MarkLabel(pushFlags);
    this._asm.Push(merged);
    this._asm.Popf();
  }
}
