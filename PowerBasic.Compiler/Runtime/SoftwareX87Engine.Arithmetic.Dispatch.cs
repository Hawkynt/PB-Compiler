using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

public sealed partial class SoftwareX87Engine {
  private const int OpAdd = 0;
  private const int OpMul = 1;
  private const int OpCompare = 2;
  private const int OpComparePop = 3;
  private const int OpSub = 4;
  private const int OpSubR = 5;
  private const int OpDiv = 6;
  private const int OpDivR = 7;

  private bool EmitInlineArithmetic(string mnemonic, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands,
      bool pop, bool integer, out string? error) {
    error = null;
    var op = mnemonic switch {
      "FADD" or "ADD" => OpAdd,
      "FMUL" or "MUL" => OpMul,
      "FSUB" or "SUB" => OpSub,
      "FSUBR" or "SUBR" => OpSubR,
      "FDIV" or "DIV" => OpDiv,
      "FDIVR" or "DIVR" => OpDivR,
      _ => -1,
    };
    if (op < 0)
      return Fail($"unknown software x87 arithmetic mnemonic {mnemonic}", out error);

    if (pop) {
      var destination = operands.Count switch {
        0 => 1,
        1 when operands[0] is TextAssembler.ParsedAsmSt st => st.Register.Index,
        _ => -1,
      };
      if (destination is < 0 or > 7)
        return Fail($"{mnemonic}P expects optional ST(i)", out error);
      return this.EmitBinaryStack(destination, 0, op, pop: true);
    }

    if (integer) {
      if (operands.Count != 1 || operands[0] is not TextAssembler.ParsedAsmMemory im
          || im.Memory.Size is not (OperandSize.Word or OperandSize.Dword))
        return Fail($"FI{mnemonic} expects word/dword integer memory", out error);
      return this.EmitArithmeticMemory(op, im.Memory, integer: true,
        bits: im.Memory.Size == OperandSize.Word ? 16 : 32);
    }

    if (operands.Count == 1 && operands[0] is TextAssembler.ParsedAsmMemory memory
        && memory.Memory.Size is OperandSize.Dword or OperandSize.Qword)
      return this.EmitArithmeticMemory(op, memory.Memory, integer: false,
        bits: memory.Memory.Size == OperandSize.Dword ? 32 : 64);

    if (operands.Count is 1 or 2) {
      var destination = 0;
      int source;
      if (operands.Count == 1 && operands[0] is TextAssembler.ParsedAsmSt one)
        source = one.Register.Index;
      else if (operands.Count == 2
               && operands[0] is TextAssembler.ParsedAsmSt d
               && operands[1] is TextAssembler.ParsedAsmSt s) {
        destination = d.Register.Index;
        source = s.Register.Index;
      } else {
        return Fail($"{mnemonic} register form expects ST(i) or ST(i),ST(j)", out error);
      }
      if (destination != 0 && source != 0)
        return Fail("x87 binary register form requires ST(0) as one operand", out error);
      return this.EmitBinaryStack(destination, source, op, pop: false);
    }

    return Fail($"invalid {mnemonic} operands", out error);
  }

  private bool EmitArithmeticMemory(int operation, Mem source, bool integer, int bits) {
    if (operation is OpCompare or OpComparePop)
      return this.EmitCompareMemory(source, bits, integer, operation == OpComparePop ? 1 : 0, unordered: false);

    this.PreserveIntegerState();
    this.CopySlotToScratch(0, ScratchA);
    if (integer)
      this.ConvertIntegerToCanonical(source, bits, ScratchB);
    else if (bits == 32)
      this.ConvertFloat32ToCanonical(source, ScratchB);
    else
      this.ConvertFloat64ToCanonical(source, ScratchB);
    this.EmitCanonicalBinary(operation, ScratchA, ScratchB, ScratchC);
    this.CopyScratchToSlot(ScratchC, 0);
    this.RestoreIntegerState();
    return true;
  }

  private bool EmitArithmeticStack(byte opcode, byte modRmBase, int index) {
    var rawOperation = (modRmBase - 0xC0) >> 3;
    if (rawOperation is OpCompare or OpComparePop)
      return this.EmitCompareStack(index, rawOperation == OpComparePop ? 1 : 0, unordered: false);

    if (opcode == 0xD8)
      return this.EmitBinaryStack(0, index, rawOperation, pop: false);

    // DC register forms name ST(i) as destination. The encoding's SUB/SUBR and DIV/DIVR slots are
    // opposite to the D8 ST(0)-destination forms; Assembler.FpuArithmetic performs this same swap.
    var logical = rawOperation switch {
      OpSub => OpSubR,
      OpSubR => OpSub,
      OpDiv => OpDivR,
      OpDivR => OpDiv,
      _ => rawOperation,
    };
    return this.EmitBinaryStack(index, 0, logical, pop: false);
  }

  private bool EmitArithmeticPop(byte modRmBase, int index) {
    var operation = modRmBase switch {
      0xC0 => OpAdd,
      0xC8 => OpMul,
      0xE0 => OpSubR,
      0xE8 => OpSub,
      0xF0 => OpDivR,
      0xF8 => OpDiv,
      _ => -1,
    };
    return operation >= 0 && this.EmitBinaryStack(index, 0, operation, pop: true);
  }

  private bool EmitBinaryStack(int destination, int source, int operation, bool pop) {
    this.PreserveIntegerState();
    this.CopySlotToScratch(destination, ScratchA);
    this.CopySlotToScratch(source, ScratchB);
    this.EmitCanonicalBinary(operation, ScratchA, ScratchB, ScratchC);
    this.CopyScratchToSlot(ScratchC, destination);
    if (pop)
      this.EmitPop();
    this.RestoreIntegerState();
    return true;
  }

  /// <summary>Dispatches canonical binary arithmetic. Result may alias neither input scratch cell.</summary>
  private void EmitCanonicalBinary(int operation, int left, int right, int result) {
    switch (operation) {
      case OpAdd: this.EmitCanonicalAdd(left, right, result); break;
      case OpSub: this.EmitCanonicalSubtract(left, right, result); break;
      case OpSubR: this.EmitCanonicalSubtract(right, left, result); break;
      case OpMul: this.EmitCanonicalMultiply(left, right, result); break;
      case OpDiv: this.EmitCanonicalDivide(left, right, result); break;
      case OpDivR: this.EmitCanonicalDivide(right, left, result); break;
      default: throw new ArgumentOutOfRangeException(nameof(operation));
    }
  }
}
