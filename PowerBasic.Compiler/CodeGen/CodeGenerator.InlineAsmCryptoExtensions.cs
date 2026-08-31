using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Runtime;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {
  private sealed record AesLookupTables(Label Sbox, Label InverseSbox, Label Mul2, Label Mul3,
    Label Mul9, Label Mul11, Label Mul13, Label Mul14);

  private AesLookupTables? _aesLookupTables;

  private const int CryptoStateScratch = 0;
  private const int CryptoSourceScratch = 16;
  private const int CryptoTransformScratch = 32;
  private const int PclMultiplierScratch = 48;
  private const int PclResultScratch = 56;

  private static RuntimeCpuFeatures RequiredCryptoFeature(InlineInstruction instruction) => instruction.Mnemonic switch {
    "AESIMC" or "AESENC" or "AESENCLAST" or "AESDEC" or "AESDECLAST" or "AESKEYGENASSIST" => RuntimeCpuFeatures.Aes,
    "PCLMULQDQ" => RuntimeCpuFeatures.Pclmulqdq,
    _ => RuntimeCpuFeatures.None,
  };

  private static bool IsAesInstruction(string mnemonic) => mnemonic is
    "AESIMC" or "AESENC" or "AESENCLAST" or "AESDEC" or "AESDECLAST" or "AESKEYGENASSIST";

  private bool TryEmitNativeCryptoInstruction(InlineInstruction instruction, InlineAsmResolver resolver, out string? error) {
    error = null;
    if (!IsAesInstruction(instruction.Mnemonic) && instruction.Mnemonic != "PCLMULQDQ")
      return false;

    this._textAssembler ??= new(this._asm);
    if (!this._textAssembler.TryParseOperands(instruction.Operands, resolver, out var operands, out error))
      return true;

    if (instruction.Mnemonic is "AESKEYGENASSIST" or "PCLMULQDQ")
      return this.EmitNativeCryptoImmediate(instruction.Mnemonic, operands, out error);
    return this.EmitNativeCryptoBinary(instruction.Mnemonic, operands, out error);
  }

  private bool EmitNativeCryptoBinary(string mnemonic, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands,
      out string? error) {
    error = null;
    if (operands.Count != 2 || operands[0] is not TextAssembler.ParsedAsmRegister { Register: var destination }
        || !destination.IsXmm()) {
      error = $"{mnemonic} expects XMM, XMM/m128";
      return true;
    }

    switch (operands[1]) {
      case TextAssembler.ParsedAsmRegister { Register: var source } when source.IsXmm():
        this.EmitNativeCryptoBinary(mnemonic, destination, source);
        return true;
      case TextAssembler.ParsedAsmMemory source:
        this.EmitNativeCryptoBinary(mnemonic, destination, source.Memory);
        return true;
      default:
        error = $"{mnemonic} source must be XMM or m128";
        return true;
    }
  }

  private void EmitNativeCryptoBinary(string mnemonic, Reg destination, Reg source) {
    switch (mnemonic) {
      case "AESIMC": this._asm.Aesimc(destination, source); break;
      case "AESENC": this._asm.Aesenc(destination, source); break;
      case "AESENCLAST": this._asm.Aesenclast(destination, source); break;
      case "AESDEC": this._asm.Aesdec(destination, source); break;
      case "AESDECLAST": this._asm.Aesdeclast(destination, source); break;
    }
  }

  private void EmitNativeCryptoBinary(string mnemonic, Reg destination, Mem source) {
    switch (mnemonic) {
      case "AESIMC": this._asm.Aesimc(destination, source); break;
      case "AESENC": this._asm.Aesenc(destination, source); break;
      case "AESENCLAST": this._asm.Aesenclast(destination, source); break;
      case "AESDEC": this._asm.Aesdec(destination, source); break;
      case "AESDECLAST": this._asm.Aesdeclast(destination, source); break;
    }
  }

  private bool EmitNativeCryptoImmediate(string mnemonic, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands,
      out string? error) {
    error = null;
    if (operands.Count != 3 || operands[0] is not TextAssembler.ParsedAsmRegister { Register: var destination }
        || !destination.IsXmm() || operands[2] is not TextAssembler.ParsedAsmImmediate immediate) {
      error = $"{mnemonic} expects XMM, XMM/m128, imm8";
      return true;
    }

    var control = unchecked((byte)immediate.Value);
    switch (operands[1]) {
      case TextAssembler.ParsedAsmRegister { Register: var source } when source.IsXmm():
        if (mnemonic == "AESKEYGENASSIST") this._asm.Aeskeygenassist(destination, source, control);
        else this._asm.Pclmulqdq(destination, source, control);
        return true;
      case TextAssembler.ParsedAsmMemory source:
        if (mnemonic == "AESKEYGENASSIST") this._asm.Aeskeygenassist(destination, source.Memory, control);
        else this._asm.Pclmulqdq(destination, source.Memory, control);
        return true;
      default:
        error = $"{mnemonic} source must be XMM or m128";
        return true;
    }
  }

  private bool TryEmitVirtualCryptoInstruction(InlineInstruction instruction, InlineAsmResolver resolver,
      RuntimeTarget target, out string? error) {
    error = null;
    if (!IsAesInstruction(instruction.Mnemonic) && instruction.Mnemonic != "PCLMULQDQ")
      return false;

    this._textAssembler ??= new(this._asm);
    if (!this._textAssembler.TryParseOperands(instruction.Operands, resolver, out var operands, out error))
      return true;

    return instruction.Mnemonic switch {
      "PCLMULQDQ" => this.EmitVirtualPclmulqdq(operands, out error),
      "AESKEYGENASSIST" => this.EmitVirtualAesKeygenAssist(operands, out error),
      _ => this.EmitVirtualAesRound(instruction.Mnemonic, operands, out error),
    };
  }

  private bool TryCryptoOperands(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, int expectedCount,
      out Reg destination, out VirtualOperand source, out string? error) {
    destination = default;
    source = default;
    error = null;
    if (operands.Count != expectedCount || operands[0] is not TextAssembler.ParsedAsmRegister { Register: var d }
        || !d.IsXmm()) {
      error = "crypto instruction requires an XMM destination";
      return false;
    }

    switch (operands[1]) {
      case TextAssembler.ParsedAsmRegister { Register: var s } when s.IsXmm(): source = VirtualOperand.Of(s); break;
      case TextAssembler.ParsedAsmMemory memory: source = VirtualOperand.Of(memory.Memory); break;
      default:
        error = "crypto instruction source must be XMM or m128";
        return false;
    }
    destination = d;
    return true;
  }

  private bool EmitVirtualAesRound(string mnemonic, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands,
      out string? error) {
    if (!this.TryCryptoOperands(operands, 2, out var destination, out var source, out error))
      return true;

    var tables = this.EnsureAesLookupTables();
    var state = this.EnsureVirtualIsaState();
    this.CopyToScratch(state, VirtualOperand.Of(destination), CryptoStateScratch, 16);
    this.CopyToScratch(state, source, CryptoSourceScratch, 16);

    this._asm.Pushf();
    this._asm.Push(Reg.AX);
    this._asm.Push(Reg.BX);
    this._asm.Push(Reg.CX);
    this._asm.Push(Reg.DX);

    switch (mnemonic) {
      case "AESIMC":
        this.EmitAesMixColumns(state, tables, CryptoStateScratch, destination, inverse: true, addRoundKey: false);
        break;
      case "AESENC":
        this.EmitAesSubShift(state, tables.Sbox, inverseShift: false);
        this.EmitAesMixColumns(state, tables, CryptoTransformScratch, destination, inverse: false, addRoundKey: true);
        break;
      case "AESENCLAST":
        this.EmitAesSubShift(state, tables.Sbox, inverseShift: false);
        this.EmitAesAddRoundKey(state, destination);
        break;
      case "AESDEC":
        this.EmitAesSubShift(state, tables.InverseSbox, inverseShift: true);
        this.EmitAesMixColumns(state, tables, CryptoTransformScratch, destination, inverse: true, addRoundKey: true);
        break;
      case "AESDECLAST":
        this.EmitAesSubShift(state, tables.InverseSbox, inverseShift: true);
        this.EmitAesAddRoundKey(state, destination);
        break;
    }

    this._asm.Pop(Reg.DX);
    this._asm.Pop(Reg.CX);
    this._asm.Pop(Reg.BX);
    this._asm.Pop(Reg.AX);
    this._asm.Popf();
    return true;
  }

  private bool EmitVirtualAesKeygenAssist(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    if (!this.TryCryptoOperands(operands, 3, out var destination, out var source, out error))
      return true;
    if (operands[2] is not TextAssembler.ParsedAsmImmediate immediate) {
      error = "AESKEYGENASSIST requires an imm8 round constant";
      return true;
    }

    var tables = this.EnsureAesLookupTables();
    var state = this.EnsureVirtualIsaState();
    this.CopyToScratch(state, source, CryptoStateScratch, 16);
    this._asm.Pushf();
    this._asm.Push(Reg.AX);
    this._asm.Push(Reg.BX);

    int[] subWordX1 = [4, 5, 6, 7];
    int[] rotWordX1 = [5, 6, 7, 4];
    int[] subWordX3 = [12, 13, 14, 15];
    int[] rotWordX3 = [13, 14, 15, 12];
    this.EmitAesKeygenWord(state, tables.Sbox, destination, 0, subWordX1, 0);
    this.EmitAesKeygenWord(state, tables.Sbox, destination, 4, rotWordX1, unchecked((byte)immediate.Value));
    this.EmitAesKeygenWord(state, tables.Sbox, destination, 8, subWordX3, 0);
    this.EmitAesKeygenWord(state, tables.Sbox, destination, 12, rotWordX3, unchecked((byte)immediate.Value));

    this._asm.Pop(Reg.BX);
    this._asm.Pop(Reg.AX);
    this._asm.Popf();
    return true;
  }

  private void EmitAesKeygenWord(VirtualIsaState state, Label sbox, Reg destination, int destinationOffset,
      IReadOnlyList<int> sourceOffsets, byte rcon) {
    for (var i = 0; i < 4; ++i) {
      this.EmitAesLookup(state, sbox, CryptoStateScratch + sourceOffsets[i]);
      if (i == 0 && rcon != 0)
        this._asm.Xor(Reg.AL, rcon);
      this._asm.Mov(this.VirtualCell(state, destination, destinationOffset + i, OperandSize.Byte), Reg.AL);
    }
  }

  private void EmitAesSubShift(VirtualIsaState state, Label table, bool inverseShift) {
    for (var column = 0; column < 4; ++column)
      for (var row = 0; row < 4; ++row) {
        var sourceColumn = inverseShift ? (column - row + 4) & 3 : (column + row) & 3;
        var sourceOffset = 4 * sourceColumn + row;
        var destinationOffset = CryptoTransformScratch + 4 * column + row;
        this.EmitAesLookup(state, table, CryptoStateScratch + sourceOffset);
        this._asm.Mov(CryptoScratchByte(state, destinationOffset), Reg.AL);
      }
  }

  private void EmitAesAddRoundKey(VirtualIsaState state, Reg destination) {
    for (var i = 0; i < 16; ++i) {
      this._asm.Mov(Reg.AL, CryptoScratchByte(state, CryptoTransformScratch + i));
      this._asm.Xor(Reg.AL, CryptoScratchByte(state, CryptoSourceScratch + i));
      this._asm.Mov(this.VirtualCell(state, destination, i, OperandSize.Byte), Reg.AL);
    }
  }

  private void EmitAesMixColumns(VirtualIsaState state, AesLookupTables tables, int inputOffset,
      Reg destination, bool inverse, bool addRoundKey) {
    ReadOnlySpan<int> forward = [2, 3, 1, 1, 1, 2, 3, 1, 1, 1, 2, 3, 3, 1, 1, 2];
    ReadOnlySpan<int> backward = [14, 11, 13, 9, 9, 14, 11, 13, 13, 9, 14, 11, 11, 13, 9, 14];
    var matrix = inverse ? backward : forward;

    for (var column = 0; column < 4; ++column)
      for (var row = 0; row < 4; ++row) {
        this._asm.Xor(Reg.DL, Reg.DL);
        for (var k = 0; k < 4; ++k) {
          var factor = matrix[row * 4 + k];
          var sourceOffset = inputOffset + column * 4 + k;
          if (factor == 1)
            this._asm.Mov(Reg.AL, CryptoScratchByte(state, sourceOffset));
          else
            this.EmitAesLookup(state, this.AesFactorTable(tables, factor), sourceOffset);
          this._asm.Xor(Reg.DL, Reg.AL);
        }
        var destinationOffset = column * 4 + row;
        if (addRoundKey)
          this._asm.Xor(Reg.DL, CryptoScratchByte(state, CryptoSourceScratch + destinationOffset));
        this._asm.Mov(this.VirtualCell(state, destination, destinationOffset, OperandSize.Byte), Reg.DL);
      }
  }

  private Label AesFactorTable(AesLookupTables tables, int factor) => factor switch {
    2 => tables.Mul2,
    3 => tables.Mul3,
    9 => tables.Mul9,
    11 => tables.Mul11,
    13 => tables.Mul13,
    14 => tables.Mul14,
    _ => throw new ArgumentOutOfRangeException(nameof(factor)),
  };

  private void EmitAesLookup(VirtualIsaState state, Label table, int scratchOffset) {
    this._asm.Mov(Reg.AL, CryptoScratchByte(state, scratchOffset));
    this._asm.Xor(Reg.AH, Reg.AH);
    this._asm.Mov(Reg.BX, Reg.AX);
    this._asm.Mov(Reg.AL, Mem.Byte(Reg.BX, table).Cs());
  }

  private static Mem CryptoScratchByte(VirtualIsaState state, int offset) => Mem.Byte(state.Scratch, offset).Cs();
  private static Mem CryptoScratchWord(VirtualIsaState state, int offset) => Mem.Word(state.Scratch, offset).Cs();

  private bool EmitVirtualPclmulqdq(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    if (!this.TryCryptoOperands(operands, 3, out var destination, out var source, out error))
      return true;
    if (operands[2] is not TextAssembler.ParsedAsmImmediate immediate) {
      error = "PCLMULQDQ requires an imm8 selector";
      return true;
    }

    var state = this.EnsureVirtualIsaState();
    this.CopyToScratch(state, VirtualOperand.Of(destination), CryptoStateScratch, 16);
    this.CopyToScratch(state, source, CryptoSourceScratch, 16);
    var control = unchecked((byte)immediate.Value);
    var leftOffset = (control & 1) != 0 ? 8 : 0;
    var rightOffset = (control & 0x10) != 0 ? 8 : 0;

    this._asm.Pushf();
    this._asm.Push(Reg.AX);
    this._asm.Push(Reg.CX);

    for (var i = 0; i < 4; ++i) {
      this._asm.Mov(Reg.AX, CryptoScratchWord(state, CryptoStateScratch + leftOffset + i * 2));
      this._asm.Mov(CryptoScratchWord(state, CryptoTransformScratch + i * 2), Reg.AX);
      this._asm.Mov(Reg.AX, CryptoScratchWord(state, CryptoSourceScratch + rightOffset + i * 2));
      this._asm.Mov(CryptoScratchWord(state, PclMultiplierScratch + i * 2), Reg.AX);
    }
    for (var i = 4; i < 8; ++i)
      this._asm.Mov(CryptoScratchWord(state, CryptoTransformScratch + i * 2), 0);
    for (var i = 0; i < 8; ++i)
      this._asm.Mov(CryptoScratchWord(state, PclResultScratch + i * 2), 0);

    this._asm.Mov(Reg.CX, 64);
    var loop = this._asm.DefineLabel();
    var skipXor = this._asm.DefineLabel();
    this._asm.MarkLabel(loop);
    this._asm.Test(CryptoScratchWord(state, PclMultiplierScratch), 1);
    this._asm.J(Condition.Equal, skipXor);
    for (var i = 0; i < 8; ++i) {
      this._asm.Mov(Reg.AX, CryptoScratchWord(state, PclResultScratch + i * 2));
      this._asm.Xor(Reg.AX, CryptoScratchWord(state, CryptoTransformScratch + i * 2));
      this._asm.Mov(CryptoScratchWord(state, PclResultScratch + i * 2), Reg.AX);
    }
    this._asm.MarkLabel(skipXor);

    this._asm.Shr(CryptoScratchWord(state, PclMultiplierScratch + 6), 1);
    this._asm.Rcr(CryptoScratchWord(state, PclMultiplierScratch + 4), 1);
    this._asm.Rcr(CryptoScratchWord(state, PclMultiplierScratch + 2), 1);
    this._asm.Rcr(CryptoScratchWord(state, PclMultiplierScratch), 1);
    this._asm.Shl(CryptoScratchWord(state, CryptoTransformScratch), 1);
    for (var i = 1; i < 8; ++i)
      this._asm.Rcl(CryptoScratchWord(state, CryptoTransformScratch + i * 2), 1);

    this._asm.Dec(Reg.CX);
    this._asm.J(Condition.NotEqual, loop);

    for (var i = 0; i < 8; ++i) {
      this._asm.Mov(Reg.AX, CryptoScratchWord(state, PclResultScratch + i * 2));
      this._asm.Mov(this.VirtualCell(state, destination, i * 2, OperandSize.Word), Reg.AX);
    }
    this._asm.Pop(Reg.CX);
    this._asm.Pop(Reg.AX);
    this._asm.Popf();
    return true;
  }

  private AesLookupTables EnsureAesLookupTables() {
    if (this._aesLookupTables is { } existing)
      return existing;

    var over = this._asm.DefineLabel();
    var sbox = this._asm.DefineLabel();
    var inverseSbox = this._asm.DefineLabel();
    var mul2 = this._asm.DefineLabel();
    var mul3 = this._asm.DefineLabel();
    var mul9 = this._asm.DefineLabel();
    var mul11 = this._asm.DefineLabel();
    var mul13 = this._asm.DefineLabel();
    var mul14 = this._asm.DefineLabel();
    this._asm.Jmp(over);
    this._asm.MarkLabel(sbox); this._asm.Db(BuildAesSbox());
    this._asm.MarkLabel(inverseSbox); this._asm.Db(BuildInverseAesSbox());
    this._asm.MarkLabel(mul2); this._asm.Db(BuildAesMultiplyTable(2));
    this._asm.MarkLabel(mul3); this._asm.Db(BuildAesMultiplyTable(3));
    this._asm.MarkLabel(mul9); this._asm.Db(BuildAesMultiplyTable(9));
    this._asm.MarkLabel(mul11); this._asm.Db(BuildAesMultiplyTable(11));
    this._asm.MarkLabel(mul13); this._asm.Db(BuildAesMultiplyTable(13));
    this._asm.MarkLabel(mul14); this._asm.Db(BuildAesMultiplyTable(14));
    this._asm.MarkLabel(over);
    return this._aesLookupTables = new(sbox, inverseSbox, mul2, mul3, mul9, mul11, mul13, mul14);
  }

  private static byte[] BuildAesSbox() {
    var result = new byte[256];
    for (var i = 0; i < result.Length; ++i) {
      var inverse = i == 0 ? (byte)0 : AesGfPow((byte)i, 254);
      result[i] = (byte)(inverse ^ RotateByteLeft(inverse, 1) ^ RotateByteLeft(inverse, 2)
        ^ RotateByteLeft(inverse, 3) ^ RotateByteLeft(inverse, 4) ^ 0x63);
    }
    return result;
  }

  private static byte[] BuildInverseAesSbox() {
    var forward = BuildAesSbox();
    var inverse = new byte[256];
    for (var i = 0; i < forward.Length; ++i)
      inverse[forward[i]] = (byte)i;
    return inverse;
  }

  private static byte[] BuildAesMultiplyTable(byte factor) {
    var result = new byte[256];
    for (var i = 0; i < result.Length; ++i)
      result[i] = AesGfMultiply((byte)i, factor);
    return result;
  }

  private static byte AesGfPow(byte value, int exponent) {
    byte result = 1;
    while (exponent != 0) {
      if ((exponent & 1) != 0)
        result = AesGfMultiply(result, value);
      value = AesGfMultiply(value, value);
      exponent >>= 1;
    }
    return result;
  }

  private static byte AesGfMultiply(byte left, byte right) {
    byte result = 0;
    for (var i = 0; i < 8; ++i) {
      if ((right & 1) != 0)
        result ^= left;
      var high = (left & 0x80) != 0;
      left <<= 1;
      if (high)
        left ^= 0x1B;
      right >>= 1;
    }
    return result;
  }

  private static byte RotateByteLeft(byte value, int count) =>
    (byte)((value << count) | (value >> (8 - count)));
}
