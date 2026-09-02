using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {
  private bool TryEmitVirtualSsse3ArithmeticInstruction(InlineInstruction instruction, InlineAsmResolver resolver,
      out string? error) {
    error = null;
    if (instruction.Mnemonic is not ("PHADDSW" or "PHSUBSW" or "PMADDUBSW" or "PMULHRSW"))
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
      return instruction.Mnemonic switch {
        "PHADDSW" => this.EmitVirtualHorizontalSaturatingWords(state, operands, subtract: false, out error),
        "PHSUBSW" => this.EmitVirtualHorizontalSaturatingWords(state, operands, subtract: true, out error),
        "PMADDUBSW" => this.EmitVirtualMultiplyAddUnsignedSignedBytes(state, operands, out error),
        "PMULHRSW" => this.EmitVirtualMultiplyHighRoundedWords(state, operands, out error),
        _ => false,
      };
    } finally {
      this._asm.Pop(Reg.DX);
      this._asm.Pop(Reg.CX);
      this._asm.Pop(Reg.BX);
      this._asm.Pop(Reg.AX);
      this._asm.Popf();
    }
  }

  private bool EmitVirtualHorizontalSaturatingWords(VirtualIsaState state,
      IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, bool subtract, out string? error) {
    error = null;
    if (operands.Count != 2 || operands[0] is not TextAssembler.ParsedAsmRegister destination
        || !(destination.Register.IsMmx() || destination.Register.IsXmm()) || !TryVectorOperand(operands[1], out var source)) {
      error = "saturating horizontal SIMD expects MMX/XMM destination and matching vector/memory source";
      return true;
    }
    var width = VectorWidth(destination.Register);
    if (source.Register is { } sourceRegister && VectorWidth(sourceRegister) != width) {
      error = "saturating horizontal SIMD operand widths differ";
      return true;
    }

    var output = VirtualOperand.Of(destination.Register);
    this.CopyToScratch(state, output, 0, width);
    this.CopyToScratch(state, source, 64, width);
    var outputLane = 0;
    for (var half = 0; half < 2; ++half) {
      var input = VirtualOperand.Of(Mem.At(state.Scratch, half * 64).Cs());
      for (var lane = 0; lane < width / 2; lane += 2, ++outputLane) {
        this.LoadWord(state, Reg.AX, input, lane * 2);
        this.LoadWord(state, Reg.DX, input, lane * 2 + 2);
        this.EmitSignedSaturatingWordAddSub(subtract);
        this.StoreWord(state, output, outputLane * 2, Reg.AX);
      }
    }
    return true;
  }

  private void EmitSignedSaturatingWordAddSub(bool subtract) {
    var normal = this._asm.DefineLabel();
    var positive = this._asm.DefineLabel();
    var done = this._asm.DefineLabel();
    if (!subtract) {
      this._asm.Add(Reg.AX, Reg.DX);
      this._asm.J(Condition.NotOverflow, normal);
      this._asm.Test(Reg.DX, Reg.DX);
      this._asm.J(Condition.NotSign, positive);
    } else {
      this._asm.Mov(Reg.CX, Reg.AX);
      this._asm.Sub(Reg.AX, Reg.DX);
      this._asm.J(Condition.NotOverflow, normal);
      this._asm.Test(Reg.CX, Reg.CX);
      this._asm.J(Condition.NotSign, positive);
    }
    this._asm.Mov(Reg.AX, 0x8000);
    this._asm.Jmp(done);
    this._asm.MarkLabel(positive);
    this._asm.Mov(Reg.AX, 0x7FFF);
    this._asm.MarkLabel(normal);
    this._asm.MarkLabel(done);
  }

  private bool EmitVirtualMultiplyAddUnsignedSignedBytes(VirtualIsaState state,
      IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    error = null;
    if (!TrySsse3BinaryOperands(operands, out var destination, out var source, out error))
      return true;

    var width = VectorWidth(destination.Register);
    var output = VirtualOperand.Of(destination.Register);
    for (var offset = 0; offset < width; offset += 2) {
      this.LoadByte(state, Reg.AL, output, offset);
      this._asm.Xor(Reg.AH, Reg.AH);
      this._asm.Mov(Reg.CX, Reg.AX);
      this.LoadByte(state, Reg.AL, source, offset);
      this._asm.Cbw();
      this._asm.Imul(Reg.CX);
      this._asm.Mov(Reg.BX, Reg.AX);

      this.LoadByte(state, Reg.AL, output, offset + 1);
      this._asm.Xor(Reg.AH, Reg.AH);
      this._asm.Mov(Reg.CX, Reg.AX);
      this.LoadByte(state, Reg.AL, source, offset + 1);
      this._asm.Cbw();
      this._asm.Imul(Reg.CX);
      this._asm.Mov(Reg.DX, Reg.BX);
      this._asm.Add(Reg.AX, Reg.BX);
      var normal = this._asm.DefineLabel();
      var positive = this._asm.DefineLabel();
      var done = this._asm.DefineLabel();
      this._asm.J(Condition.NotOverflow, normal);
      this._asm.Test(Reg.DX, Reg.DX);
      this._asm.J(Condition.NotSign, positive);
      this._asm.Mov(Reg.AX, 0x8000);
      this._asm.Jmp(done);
      this._asm.MarkLabel(positive);
      this._asm.Mov(Reg.AX, 0x7FFF);
      this._asm.MarkLabel(normal);
      this._asm.MarkLabel(done);
      this.StoreWord(state, output, offset, Reg.AX);
    }
    return true;
  }

  private bool EmitVirtualMultiplyHighRoundedWords(VirtualIsaState state,
      IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    error = null;
    if (!TrySsse3BinaryOperands(operands, out var destination, out var source, out error))
      return true;

    var width = VectorWidth(destination.Register);
    var output = VirtualOperand.Of(destination.Register);
    for (var offset = 0; offset < width; offset += 2) {
      this.LoadWord(state, Reg.AX, output, offset);
      this.LoadWord(state, Reg.CX, source, offset);
      this._asm.Imul(Reg.CX);
      this._asm.Add(Reg.AX, 0x4000);
      this._asm.Adc(Reg.DX, 0);
      for (var bit = 0; bit < 15; ++bit) {
        this._asm.Sar(Reg.DX, 1);
        this._asm.Rcr(Reg.AX, 1);
      }
      this.StoreWord(state, output, offset, Reg.AX);
    }
    return true;
  }

  private static bool TrySsse3BinaryOperands(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands,
      out TextAssembler.ParsedAsmRegister destination, out VirtualOperand source, out string? error) {
    destination = null!;
    source = default;
    error = null;
    if (operands.Count != 2 || operands[0] is not TextAssembler.ParsedAsmRegister parsedDestination
        || !(parsedDestination.Register.IsMmx() || parsedDestination.Register.IsXmm()) || !TryVectorOperand(operands[1], out source)) {
      error = "SSSE3 packed operation expects MMX/XMM destination and matching vector/memory source";
      return false;
    }
    if (source.Register is { } sourceRegister && VectorWidth(sourceRegister) != VectorWidth(parsedDestination.Register)) {
      error = "SSSE3 packed operand widths differ";
      return false;
    }
    destination = parsedDestination;
    return true;
  }
}
