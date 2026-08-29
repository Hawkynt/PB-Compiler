using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {
  /// <summary>
  /// Scalar 8086 lowerings for extended packed-integer instructions whose semantics can be reproduced
  /// exactly with the virtual vector bank. This runs before the legacy packed-SIMD scalarizer.
  /// </summary>
  private bool TryEmitVirtualExtendedInstruction(InlineInstruction instruction, InlineAsmResolver resolver, out string? error) {
    error = null;
    if (!IsVirtualExtendedSupported(instruction.Mnemonic))
      return false;

    this._textAssembler ??= new(this._asm);
    if (!this._textAssembler.TryParseOperands(instruction.Operands, resolver, out var operands, out error))
      return true;

    var state = this.EnsureVirtualIsaState();
    this._asm.Pushf();
    this._asm.Push(Reg.AX);
    this._asm.Push(Reg.BX);
    this._asm.Push(Reg.CX);
    this._asm.Push(Reg.DX);
    try {
      return this.EmitVirtualExtendedCore(state, instruction.Mnemonic, operands, out error);
    } finally {
      this._asm.Pop(Reg.DX);
      this._asm.Pop(Reg.CX);
      this._asm.Pop(Reg.BX);
      this._asm.Pop(Reg.AX);
      this._asm.Popf();
    }
  }

  private static bool IsVirtualExtendedSupported(string mnemonic) => mnemonic is
    "PABSB" or "PABSW" or "PABSD" or "PBLENDW" or "PMULLD" or
    "PMINSB" or "PMAXSB" or "PMINUW" or "PMAXUW" or "PMINUD" or "PMAXUD" or "PCMPEQQ";

  private bool EmitVirtualExtendedCore(VirtualIsaState state, string mnemonic,
      IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    error = null;
    if (mnemonic == "PBLENDW")
      return this.EmitVirtualBlendWords(state, operands, out error);

    if (operands.Count != 2 || operands[0] is not TextAssembler.ParsedAsmRegister destination
        || !IsVirtualVector(destination.Register) || !TryVectorOperand(operands[1], out var source)) {
      error = $"{mnemonic} expects vector destination and vector/memory source";
      return true;
    }

    var width = VectorWidth(destination.Register);
    if (source.Register is { } sourceRegister && VectorWidth(sourceRegister) != width) {
      error = $"{mnemonic} operand widths differ";
      return true;
    }

    if (mnemonic.StartsWith("PABS", StringComparison.Ordinal)) {
      if (!(destination.Register.IsMmx() || destination.Register.IsXmm())) {
        error = $"{mnemonic} expects MMX/XMM destination";
        return true;
      }
      this.EmitVirtualAbs(state, VirtualOperand.Of(destination.Register), source, width, mnemonic[^1] switch {
        'B' => 1,
        'W' => 2,
        _ => 4,
      });
      return true;
    }

    if (!destination.Register.IsXmm()) {
      error = $"{mnemonic} expects XMM destination";
      return true;
    }

    var output = VirtualOperand.Of(destination.Register);
    switch (mnemonic) {
      case "PMULLD":
        this.EmitVirtualMultiplyDwords(state, output, source, width);
        return true;
      case "PMINSB": this.EmitVirtualSignedByteMinMax(state, output, source, width, wantMax: false); return true;
      case "PMAXSB": this.EmitVirtualSignedByteMinMax(state, output, source, width, wantMax: true); return true;
      case "PMINUW": this.EmitVirtualUnsignedWordMinMax(state, output, source, width, wantMax: false); return true;
      case "PMAXUW": this.EmitVirtualUnsignedWordMinMax(state, output, source, width, wantMax: true); return true;
      case "PMINUD": this.EmitVirtualUnsignedDwordMinMax(state, output, source, width, wantMax: false); return true;
      case "PMAXUD": this.EmitVirtualUnsignedDwordMinMax(state, output, source, width, wantMax: true); return true;
      case "PCMPEQQ": this.EmitVirtualCompareQwordEqual(state, output, source, width); return true;
      default:
        error = $"extended packed-SIMD emulator has no {mnemonic} lowering";
        return true;
    }
  }

  private void EmitVirtualAbs(VirtualIsaState state, VirtualOperand destination, VirtualOperand source, int width, int laneBytes) {
    for (var offset = 0; offset < width; offset += laneBytes) {
      var nonnegative = this._asm.DefineLabel();
      if (laneBytes == 1) {
        this.LoadByte(state, Reg.AL, source, offset);
        this._asm.Test(Reg.AL, Reg.AL);
        this._asm.J(Condition.NotSign, nonnegative);
        this._asm.Neg(Reg.AL);
        this._asm.MarkLabel(nonnegative);
        this.StoreByte(state, destination, offset, Reg.AL);
        continue;
      }
      if (laneBytes == 2) {
        this.LoadWord(state, Reg.AX, source, offset);
        this._asm.Test(Reg.AX, Reg.AX);
        this._asm.J(Condition.NotSign, nonnegative);
        this._asm.Neg(Reg.AX);
        this._asm.MarkLabel(nonnegative);
        this.StoreWord(state, destination, offset, Reg.AX);
        continue;
      }

      this.LoadWord(state, Reg.AX, source, offset);
      this.LoadWord(state, Reg.DX, source, offset + 2);
      this._asm.Test(Reg.DX, Reg.DX);
      this._asm.J(Condition.NotSign, nonnegative);
      this._asm.Neg(Reg.AX);
      this._asm.Adc(Reg.DX, 0);
      this._asm.Neg(Reg.DX);
      this._asm.MarkLabel(nonnegative);
      this.StoreWord(state, destination, offset, Reg.AX);
      this.StoreWord(state, destination, offset + 2, Reg.DX);
    }
  }

  private bool EmitVirtualBlendWords(VirtualIsaState state, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    error = null;
    if (operands.Count != 3 || operands[0] is not TextAssembler.ParsedAsmRegister destination || !destination.Register.IsXmm()
        || !TryVectorOperand(operands[1], out var source) || operands[2] is not TextAssembler.ParsedAsmImmediate immediate) {
      error = "PBLENDW expects XMM destination, XMM/m128 source, imm8";
      return true;
    }
    if (source.Register is { } sourceRegister && !sourceRegister.IsXmm()) {
      error = "PBLENDW source register must be XMM";
      return true;
    }

    var control = unchecked((byte)immediate.Value);
    var output = VirtualOperand.Of(destination.Register);
    for (var lane = 0; lane < 8; ++lane) {
      if ((control & (1 << lane)) == 0)
        continue;
      this.LoadWord(state, Reg.AX, source, lane * 2);
      this.StoreWord(state, output, lane * 2, Reg.AX);
    }
    return true;
  }

  /// <summary>Low 32 bits of each signed/unsigned product are identical, so plain 16-bit MUL is sufficient.</summary>
  private void EmitVirtualMultiplyDwords(VirtualIsaState state, VirtualOperand destination, VirtualOperand source, int width) {
    this.CopyToScratch(state, destination, 0, width);
    this.CopyToScratch(state, source, 64, width);
    var a = VirtualOperand.Of(Mem.At(state.Scratch, 0).Cs());
    var b = VirtualOperand.Of(Mem.At(state.Scratch, 64).Cs());

    for (var offset = 0; offset < width; offset += 4) {
      this.LoadWord(state, Reg.AX, a, offset);
      this.LoadWord(state, Reg.CX, b, offset);
      this._asm.Mul(Reg.CX);
      this.StoreWord(state, destination, offset, Reg.AX);
      this._asm.Mov(Reg.BX, Reg.DX);

      this.LoadWord(state, Reg.AX, a, offset);
      this.LoadWord(state, Reg.CX, b, offset + 2);
      this._asm.Mul(Reg.CX);
      this._asm.Add(Reg.BX, Reg.AX);

      this.LoadWord(state, Reg.AX, a, offset + 2);
      this.LoadWord(state, Reg.CX, b, offset);
      this._asm.Mul(Reg.CX);
      this._asm.Add(Reg.BX, Reg.AX);
      this.StoreWord(state, destination, offset + 2, Reg.BX);
    }
  }

  private void EmitVirtualSignedByteMinMax(VirtualIsaState state, VirtualOperand destination, VirtualOperand source,
      int width, bool wantMax) {
    for (var offset = 0; offset < width; ++offset) {
      var keep = this._asm.DefineLabel();
      this.LoadByte(state, Reg.AL, destination, offset);
      this.LoadByte(state, Reg.DL, source, offset);
      this._asm.Cmp(Reg.AL, Reg.DL);
      this._asm.J(wantMax ? Condition.GreaterOrEqual : Condition.LessOrEqual, keep);
      this.StoreByte(state, destination, offset, Reg.DL);
      this._asm.MarkLabel(keep);
    }
  }

  private void EmitVirtualUnsignedWordMinMax(VirtualIsaState state, VirtualOperand destination, VirtualOperand source,
      int width, bool wantMax) {
    for (var offset = 0; offset < width; offset += 2) {
      var keep = this._asm.DefineLabel();
      this.LoadWord(state, Reg.AX, destination, offset);
      this.LoadWord(state, Reg.DX, source, offset);
      this._asm.Cmp(Reg.AX, Reg.DX);
      this._asm.J(wantMax ? Condition.AboveOrEqual : Condition.BelowOrEqual, keep);
      this.StoreWord(state, destination, offset, Reg.DX);
      this._asm.MarkLabel(keep);
    }
  }

  private void EmitVirtualUnsignedDwordMinMax(VirtualIsaState state, VirtualOperand destination, VirtualOperand source,
      int width, bool wantMax) {
    for (var offset = 0; offset < width; offset += 4) {
      var keep = this._asm.DefineLabel();
      var take = this._asm.DefineLabel();
      this.LoadWord(state, Reg.AX, destination, offset + 2);
      this.LoadWord(state, Reg.DX, source, offset + 2);
      this._asm.Cmp(Reg.AX, Reg.DX);
      this._asm.J(wantMax ? Condition.Above : Condition.Below, keep);
      this._asm.J(wantMax ? Condition.Below : Condition.Above, take);
      this.LoadWord(state, Reg.AX, destination, offset);
      this.LoadWord(state, Reg.DX, source, offset);
      this._asm.Cmp(Reg.AX, Reg.DX);
      this._asm.J(wantMax ? Condition.AboveOrEqual : Condition.BelowOrEqual, keep);
      this._asm.MarkLabel(take);
      this.LoadWord(state, Reg.AX, source, offset);
      this.StoreWord(state, destination, offset, Reg.AX);
      this.LoadWord(state, Reg.AX, source, offset + 2);
      this.StoreWord(state, destination, offset + 2, Reg.AX);
      this._asm.MarkLabel(keep);
    }
  }

  private void EmitVirtualCompareQwordEqual(VirtualIsaState state, VirtualOperand destination, VirtualOperand source, int width) {
    for (var offset = 0; offset < width; offset += 8) {
      var different = this._asm.DefineLabel();
      var done = this._asm.DefineLabel();
      for (var word = 0; word < 8; word += 2) {
        this.LoadWord(state, Reg.AX, destination, offset + word);
        this.LoadWord(state, Reg.DX, source, offset + word);
        this._asm.Cmp(Reg.AX, Reg.DX);
        this._asm.J(Condition.NotEqual, different);
      }
      for (var word = 0; word < 8; word += 2)
        this._asm.Mov(OperandCell(destination, offset + word, OperandSize.Word,
          (r, p, s) => this.VirtualCell(state, r, p, s)), -1);
      this._asm.Jmp(done);
      this._asm.MarkLabel(different);
      for (var word = 0; word < 8; word += 2)
        this._asm.Mov(OperandCell(destination, offset + word, OperandSize.Word,
          (r, p, s) => this.VirtualCell(state, r, p, s)), 0);
      this._asm.MarkLabel(done);
    }
  }
}
