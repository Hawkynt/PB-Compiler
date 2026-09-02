using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Runtime;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {
  private const int PackedStringA = 0;
  private const int PackedStringB = 16;
  private const int PackedStringLengthA = 32;
  private const int PackedStringLengthB = 34;
  private const int PackedStringIntRes1 = 36;
  private const int PackedStringIntRes2 = 38;
  private const int PackedStringOriginalFlags = 40;
  private const int PackedStringFinalFlags = 42;
  private const int PackedStringRawLengthA = 44;
  private const int PackedStringRawLengthB = 48;
  private const int PackedStringIndex = 52;

  /// <summary>
  /// Exact 8086 lowering for the SSE4.2 packed-string compare family. The implementation follows the
  /// architectural validity override, aggregation, polarity and implicit-result rules rather than
  /// approximating the instructions with a library string comparison.
  /// </summary>
  private bool TryEmitVirtualPackedStringInstruction(InlineInstruction instruction, InlineAsmResolver resolver,
      RuntimeTarget target, out string? error) {
    error = null;
    if (instruction.Mnemonic is not ("PCMPESTRI" or "PCMPESTRM" or "PCMPISTRI" or "PCMPISTRM"))
      return false;

    this._textAssembler ??= new(this._asm);
    if (!this._textAssembler.TryParseOperands(instruction.Operands, resolver, out var operands, out error))
      return true;
    if (operands.Count != 3 || operands[0] is not TextAssembler.ParsedAsmRegister first || !first.Register.IsXmm()
        || !TryVectorOperand(operands[1], out var second) || operands[2] is not TextAssembler.ParsedAsmImmediate immediate) {
      error = $"{instruction.Mnemonic} expects XMM, XMM/m128, imm8";
      return true;
    }
    if (second.Register is { } secondRegister && !secondRegister.IsXmm()) {
      error = $"{instruction.Mnemonic} second register operand must be XMM";
      return true;
    }

    var explicitLength = instruction.Mnemonic.StartsWith("PCMPE", StringComparison.Ordinal);
    var indexResult = instruction.Mnemonic.EndsWith('I');
    var control = unchecked((byte)immediate.Value);
    var wordElements = (control & 1) != 0;
    var elementBytes = wordElements ? 2 : 1;
    var elementCount = wordElements ? 8 : 16;
    var state = this.EnsureVirtualIsaState();

    this.CapturePackedStringFlags(state);
    if (explicitLength) {
      this.StageDword(state, new TextAssembler.ParsedAsmRegister(Reg.EAX), PackedStringRawLengthA, target);
      this.StageDword(state, new TextAssembler.ParsedAsmRegister(Reg.EDX), PackedStringRawLengthB, target);
    }

    this._asm.Push(Reg.AX);
    this._asm.Push(Reg.BX);
    this._asm.Push(Reg.CX);
    this._asm.Push(Reg.DX);
    try {
      this.CopyToScratch(state, VirtualOperand.Of(first.Register), PackedStringA, 16);
      this.CopyToScratch(state, second, PackedStringB, 16);

      if (explicitLength) {
        this.EmitPackedStringExplicitLength(state, PackedStringRawLengthA, PackedStringLengthA, elementCount);
        this.EmitPackedStringExplicitLength(state, PackedStringRawLengthB, PackedStringLengthB, elementCount);
      } else {
        this.EmitPackedStringImplicitLength(state, PackedStringA, PackedStringLengthA, elementCount, elementBytes);
        this.EmitPackedStringImplicitLength(state, PackedStringB, PackedStringLengthB, elementCount, elementBytes);
      }

      this.EmitPackedStringAggregation(state, control, elementCount, elementBytes);
      this.EmitPackedStringPolarity(state, control, elementCount);
      if (indexResult)
        this.EmitPackedStringIndex(state, control, elementCount);
      else
        this.EmitPackedStringMask(state, control, elementCount, elementBytes);
      this.EmitPackedStringFlags(state, elementCount);
    } finally {
      this._asm.Pop(Reg.DX);
      this._asm.Pop(Reg.CX);
      this._asm.Pop(Reg.BX);
      this._asm.Pop(Reg.AX);
    }

    if (indexResult)
      this.WriteDwordPlace(state, DwordPlace.Of(Reg.ECX), PackedStringIndex, target);
    this.RestorePackedStringFlags(state);
    return true;
  }

  private Mem PackedStringWord(VirtualIsaState state, int offset) => Mem.Word(state.Scratch, offset).Cs();
  private Mem PackedStringByte(VirtualIsaState state, int offset) => Mem.Byte(state.Scratch, offset).Cs();

  private void CapturePackedStringFlags(VirtualIsaState state) {
    this._asm.Push(Reg.BX);
    this._asm.Pushf();
    this._asm.Pop(Reg.BX);
    this._asm.Mov(this.PackedStringWord(state, PackedStringOriginalFlags), Reg.BX);
    this._asm.Pop(Reg.BX);
  }

  private void RestorePackedStringFlags(VirtualIsaState state) {
    this._asm.Push(Reg.AX);
    this._asm.Mov(Reg.AX, this.PackedStringWord(state, PackedStringFinalFlags));
    this._asm.Push(Reg.AX);
    this._asm.Popf();
    this._asm.Pop(Reg.AX);
  }

  /// <summary>
  /// Intel defines explicit length as min(abs(signed dword), elementCount). Only values in
  /// [-elementCount,+elementCount] need an actual absolute value; every other bit pattern saturates.
  /// This also handles INT_MIN without overflowing a software ABS.
  /// </summary>
  private void EmitPackedStringExplicitLength(VirtualIsaState state, int rawOffset, int lengthOffset, int elementCount) {
    var positive = this._asm.DefineLabel();
    var done = this._asm.DefineLabel();
    this._asm.Mov(this.PackedStringWord(state, lengthOffset), elementCount);
    this._asm.Mov(Reg.AX, this.PackedStringWord(state, rawOffset + 2));
    this._asm.Cmp(Reg.AX, 0);
    this._asm.J(Condition.Equal, positive);
    this._asm.Cmp(Reg.AX, -1);
    this._asm.J(Condition.NotEqual, done);

    this._asm.Mov(Reg.AX, this.PackedStringWord(state, rawOffset));
    this._asm.Cmp(Reg.AX, unchecked((ushort)(0x10000 - elementCount)));
    this._asm.J(Condition.Below, done);
    this._asm.Neg(Reg.AX);
    this._asm.Mov(this.PackedStringWord(state, lengthOffset), Reg.AX);
    this._asm.Jmp(done);

    this._asm.MarkLabel(positive);
    this._asm.Mov(Reg.AX, this.PackedStringWord(state, rawOffset));
    this._asm.Cmp(Reg.AX, elementCount);
    this._asm.J(Condition.Above, done);
    this._asm.Mov(this.PackedStringWord(state, lengthOffset), Reg.AX);
    this._asm.MarkLabel(done);
  }

  private void EmitPackedStringImplicitLength(VirtualIsaState state, int dataOffset, int lengthOffset,
      int elementCount, int elementBytes) {
    var done = this._asm.DefineLabel();
    this._asm.Mov(this.PackedStringWord(state, lengthOffset), elementCount);
    for (var i = 0; i < elementCount; ++i) {
      var next = this._asm.DefineLabel();
      if (elementBytes == 1) {
        this._asm.Cmp(this.PackedStringByte(state, dataOffset + i), 0);
      } else {
        this._asm.Cmp(this.PackedStringWord(state, dataOffset + i * 2), 0);
      }
      this._asm.J(Condition.NotEqual, next);
      this._asm.Mov(this.PackedStringWord(state, lengthOffset), i);
      this._asm.Jmp(done);
      this._asm.MarkLabel(next);
    }
    this._asm.MarkLabel(done);
  }

  private void EmitPackedStringAggregation(VirtualIsaState state, byte control, int elementCount, int elementBytes) {
    this._asm.Mov(this.PackedStringWord(state, PackedStringIntRes1), 0);
    switch ((control >> 2) & 3) {
      case 0:
        this.EmitPackedStringEqualAny(state, elementCount, elementBytes);
        break;
      case 1:
        this.EmitPackedStringRanges(state, elementCount, elementBytes, signed: (control & 2) != 0);
        break;
      case 2:
        this.EmitPackedStringEqualEach(state, elementCount, elementBytes);
        break;
      case 3:
        this.EmitPackedStringEqualOrdered(state, elementCount, elementBytes);
        break;
    }
  }

  private void EmitPackedStringEqualAny(VirtualIsaState state, int elementCount, int elementBytes) {
    for (var j = 0; j < elementCount; ++j) {
      var match = this._asm.DefineLabel();
      var done = this._asm.DefineLabel();
      this._asm.Mov(Reg.AX, this.PackedStringWord(state, PackedStringLengthB));
      this._asm.Cmp(Reg.AX, j);
      this._asm.J(Condition.BelowOrEqual, done);
      for (var i = 0; i < elementCount; ++i) {
        this._asm.Mov(Reg.AX, this.PackedStringWord(state, PackedStringLengthA));
        this._asm.Cmp(Reg.AX, i);
        this._asm.J(Condition.BelowOrEqual, done);
        this.EmitPackedStringElementCompare(state, i, j, elementBytes);
        this._asm.J(Condition.Equal, match);
      }
      this._asm.Jmp(done);
      this._asm.MarkLabel(match);
      this.EmitPackedStringSetBit(state, PackedStringIntRes1, j);
      this._asm.MarkLabel(done);
    }
  }

  private void EmitPackedStringRanges(VirtualIsaState state, int elementCount, int elementBytes, bool signed) {
    var below = signed ? Condition.Less : Condition.Below;
    var above = signed ? Condition.Greater : Condition.Above;
    for (var j = 0; j < elementCount; ++j) {
      var match = this._asm.DefineLabel();
      var done = this._asm.DefineLabel();
      this._asm.Mov(Reg.AX, this.PackedStringWord(state, PackedStringLengthB));
      this._asm.Cmp(Reg.AX, j);
      this._asm.J(Condition.BelowOrEqual, done);

      for (var i = 0; i < elementCount; i += 2) {
        var nextRange = this._asm.DefineLabel();
        this._asm.Mov(Reg.AX, this.PackedStringWord(state, PackedStringLengthA));
        this._asm.Cmp(Reg.AX, i + 1);
        this._asm.J(Condition.BelowOrEqual, done);
        if (elementBytes == 1) {
          this._asm.Mov(Reg.DL, this.PackedStringByte(state, PackedStringB + j));
          this._asm.Mov(Reg.AL, this.PackedStringByte(state, PackedStringA + i));
          this._asm.Cmp(Reg.DL, Reg.AL);
          this._asm.J(below, nextRange);
          this._asm.Mov(Reg.AL, this.PackedStringByte(state, PackedStringA + i + 1));
          this._asm.Cmp(Reg.DL, Reg.AL);
        } else {
          this._asm.Mov(Reg.DX, this.PackedStringWord(state, PackedStringB + j * 2));
          this._asm.Mov(Reg.AX, this.PackedStringWord(state, PackedStringA + i * 2));
          this._asm.Cmp(Reg.DX, Reg.AX);
          this._asm.J(below, nextRange);
          this._asm.Mov(Reg.AX, this.PackedStringWord(state, PackedStringA + (i + 1) * 2));
          this._asm.Cmp(Reg.DX, Reg.AX);
        }
        this._asm.J(above, nextRange);
        this._asm.Jmp(match);
        this._asm.MarkLabel(nextRange);
      }

      this._asm.Jmp(done);
      this._asm.MarkLabel(match);
      this.EmitPackedStringSetBit(state, PackedStringIntRes1, j);
      this._asm.MarkLabel(done);
    }
  }

  private void EmitPackedStringEqualEach(VirtualIsaState state, int elementCount, int elementBytes) {
    for (var i = 0; i < elementCount; ++i) {
      var aValid = this._asm.DefineLabel();
      var match = this._asm.DefineLabel();
      var done = this._asm.DefineLabel();
      this._asm.Mov(Reg.AX, this.PackedStringWord(state, PackedStringLengthA));
      this._asm.Cmp(Reg.AX, i);
      this._asm.J(Condition.Above, aValid);
      this._asm.Mov(Reg.AX, this.PackedStringWord(state, PackedStringLengthB));
      this._asm.Cmp(Reg.AX, i);
      this._asm.J(Condition.BelowOrEqual, match); // both invalid: force true
      this._asm.Jmp(done);                        // A invalid, B valid: force false

      this._asm.MarkLabel(aValid);
      this._asm.Mov(Reg.AX, this.PackedStringWord(state, PackedStringLengthB));
      this._asm.Cmp(Reg.AX, i);
      this._asm.J(Condition.BelowOrEqual, done); // A valid, B invalid: force false
      this.EmitPackedStringElementCompare(state, i, i, elementBytes);
      this._asm.J(Condition.NotEqual, done);

      this._asm.MarkLabel(match);
      this.EmitPackedStringSetBit(state, PackedStringIntRes1, i);
      this._asm.MarkLabel(done);
    }
  }

  private void EmitPackedStringEqualOrdered(VirtualIsaState state, int elementCount, int elementBytes) {
    for (var start = 0; start < elementCount; ++start) {
      var match = this._asm.DefineLabel();
      var done = this._asm.DefineLabel();
      for (var i = 0; i < elementCount - start; ++i) {
        var bValid = this._asm.DefineLabel();
        this._asm.Mov(Reg.AX, this.PackedStringWord(state, PackedStringLengthA));
        this._asm.Cmp(Reg.AX, i);
        this._asm.J(Condition.BelowOrEqual, match); // A invalid forces this and every later pair true
        this._asm.Mov(Reg.AX, this.PackedStringWord(state, PackedStringLengthB));
        this._asm.Cmp(Reg.AX, start + i);
        this._asm.J(Condition.Above, bValid);
        this._asm.Jmp(done);                       // A valid, B invalid: force false
        this._asm.MarkLabel(bValid);
        this.EmitPackedStringElementCompare(state, i, start + i, elementBytes);
        this._asm.J(Condition.NotEqual, done);
      }
      this._asm.MarkLabel(match);
      this.EmitPackedStringSetBit(state, PackedStringIntRes1, start);
      this._asm.MarkLabel(done);
    }
  }

  private void EmitPackedStringElementCompare(VirtualIsaState state, int aIndex, int bIndex, int elementBytes) {
    if (elementBytes == 1) {
      this._asm.Mov(Reg.AL, this.PackedStringByte(state, PackedStringA + aIndex));
      this._asm.Mov(Reg.DL, this.PackedStringByte(state, PackedStringB + bIndex));
      this._asm.Cmp(Reg.AL, Reg.DL);
      return;
    }
    this._asm.Mov(Reg.AX, this.PackedStringWord(state, PackedStringA + aIndex * 2));
    this._asm.Mov(Reg.DX, this.PackedStringWord(state, PackedStringB + bIndex * 2));
    this._asm.Cmp(Reg.AX, Reg.DX);
  }

  private void EmitPackedStringSetBit(VirtualIsaState state, int resultOffset, int bit) {
    this._asm.Mov(Reg.AX, this.PackedStringWord(state, resultOffset));
    this._asm.Or(Reg.AX, 1 << bit);
    this._asm.Mov(this.PackedStringWord(state, resultOffset), Reg.AX);
  }

  private void EmitPackedStringPolarity(VirtualIsaState state, byte control, int elementCount) {
    var mask = elementCount == 16 ? 0xFFFF : 0x00FF;
    this._asm.Mov(Reg.AX, this.PackedStringWord(state, PackedStringIntRes1));
    this._asm.Mov(this.PackedStringWord(state, PackedStringIntRes2), Reg.AX);
    if ((control & 0x10) == 0)
      return;

    if ((control & 0x20) == 0) {
      this._asm.Xor(Reg.AX, mask);
      this._asm.Mov(this.PackedStringWord(state, PackedStringIntRes2), Reg.AX);
      return;
    }

    for (var i = 0; i < elementCount; ++i) {
      var next = this._asm.DefineLabel();
      this._asm.Mov(Reg.AX, this.PackedStringWord(state, PackedStringLengthB));
      this._asm.Cmp(Reg.AX, i);
      this._asm.J(Condition.BelowOrEqual, next);
      this._asm.Mov(Reg.AX, this.PackedStringWord(state, PackedStringIntRes2));
      this._asm.Xor(Reg.AX, 1 << i);
      this._asm.Mov(this.PackedStringWord(state, PackedStringIntRes2), Reg.AX);
      this._asm.MarkLabel(next);
    }
  }

  private void EmitPackedStringIndex(VirtualIsaState state, byte control, int elementCount) {
    var done = this._asm.DefineLabel();
    this._asm.Mov(this.PackedStringWord(state, PackedStringIndex), elementCount);
    this._asm.Mov(this.PackedStringWord(state, PackedStringIndex + 2), 0);
    if ((control & 0x40) == 0) {
      for (var i = 0; i < elementCount; ++i) {
        var next = this._asm.DefineLabel();
        this._asm.Test(this.PackedStringWord(state, PackedStringIntRes2), 1 << i);
        this._asm.J(Condition.Equal, next);
        this._asm.Mov(this.PackedStringWord(state, PackedStringIndex), i);
        this._asm.Jmp(done);
        this._asm.MarkLabel(next);
      }
    } else {
      for (var i = elementCount - 1; i >= 0; --i) {
        var next = this._asm.DefineLabel();
        this._asm.Test(this.PackedStringWord(state, PackedStringIntRes2), 1 << i);
        this._asm.J(Condition.Equal, next);
        this._asm.Mov(this.PackedStringWord(state, PackedStringIndex), i);
        this._asm.Jmp(done);
        this._asm.MarkLabel(next);
      }
    }
    this._asm.MarkLabel(done);
  }

  private void EmitPackedStringMask(VirtualIsaState state, byte control, int elementCount, int elementBytes) {
    this.ZeroBytes(state, Reg.XMM0, 0, 16);
    if ((control & 0x40) == 0) {
      this._asm.Mov(Reg.AX, this.PackedStringWord(state, PackedStringIntRes2));
      this._asm.Mov(this.VirtualCell(state, Reg.XMM0, 0, OperandSize.Word), Reg.AX);
      return;
    }

    for (var i = 0; i < elementCount; ++i) {
      var next = this._asm.DefineLabel();
      this._asm.Test(this.PackedStringWord(state, PackedStringIntRes2), 1 << i);
      this._asm.J(Condition.Equal, next);
      this._asm.Mov(this.VirtualCell(state, Reg.XMM0, i * elementBytes,
        elementBytes == 1 ? OperandSize.Byte : OperandSize.Word), -1);
      this._asm.MarkLabel(next);
    }
  }

  private void EmitPackedStringFlags(VirtualIsaState state, int elementCount) {
    var noCarry = this._asm.DefineLabel();
    var noZero = this._asm.DefineLabel();
    var noSign = this._asm.DefineLabel();
    var noOverflow = this._asm.DefineLabel();
    this._asm.Mov(Reg.AX, this.PackedStringWord(state, PackedStringOriginalFlags));
    this._asm.And(Reg.AX, 0xF72A); // clear CF/PF/AF/ZF/SF/OF; retain unrelated architectural flags
    this._asm.Mov(Reg.BX, this.PackedStringWord(state, PackedStringIntRes2));
    this._asm.Test(Reg.BX, Reg.BX);
    this._asm.J(Condition.Equal, noCarry);
    this._asm.Or(Reg.AX, 0x0001);
    this._asm.MarkLabel(noCarry);
    this._asm.Cmp(this.PackedStringWord(state, PackedStringLengthB), elementCount);
    this._asm.J(Condition.AboveOrEqual, noZero);
    this._asm.Or(Reg.AX, 0x0040);
    this._asm.MarkLabel(noZero);
    this._asm.Cmp(this.PackedStringWord(state, PackedStringLengthA), elementCount);
    this._asm.J(Condition.AboveOrEqual, noSign);
    this._asm.Or(Reg.AX, 0x0080);
    this._asm.MarkLabel(noSign);
    this._asm.Test(Reg.BX, 1);
    this._asm.J(Condition.Equal, noOverflow);
    this._asm.Or(Reg.AX, 0x0800);
    this._asm.MarkLabel(noOverflow);
    this._asm.Mov(this.PackedStringWord(state, PackedStringFinalFlags), Reg.AX);
  }
}
