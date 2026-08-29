using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {
  private bool TryEmitVirtualSupplementalInstruction(InlineInstruction instruction, InlineAsmResolver resolver, out string? error) {
    error = null;
    if (instruction.Mnemonic is not ("PSIGNB" or "PSIGNW" or "PSIGND" or "PCMPGTQ" or "PHMINPOSUW"))
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
        "PSIGNB" => this.EmitVirtualSign(state, operands, 1, out error),
        "PSIGNW" => this.EmitVirtualSign(state, operands, 2, out error),
        "PSIGND" => this.EmitVirtualSign(state, operands, 4, out error),
        "PCMPGTQ" => this.EmitVirtualCompareQwordGreater(state, operands, out error),
        "PHMINPOSUW" => this.EmitVirtualMinPositionUnsignedWord(state, operands, out error),
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

  private bool EmitVirtualSign(VirtualIsaState state, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands,
      int laneBytes, out string? error) {
    error = null;
    if (operands.Count != 2 || operands[0] is not TextAssembler.ParsedAsmRegister destination
        || !(destination.Register.IsMmx() || destination.Register.IsXmm()) || !TryVectorOperand(operands[1], out var signs)) {
      error = "PSIGN expects MMX/XMM destination and matching vector/memory source";
      return true;
    }
    var width = VectorWidth(destination.Register);
    if (signs.Register is { } signRegister && VectorWidth(signRegister) != width) {
      error = "PSIGN operand widths differ";
      return true;
    }

    var output = VirtualOperand.Of(destination.Register);
    for (var offset = 0; offset < width; offset += laneBytes) {
      var zero = this._asm.DefineLabel();
      var positive = this._asm.DefineLabel();
      var store = this._asm.DefineLabel();
      if (laneBytes == 1) {
        this.LoadByte(state, Reg.AL, output, offset);
        this.LoadByte(state, Reg.DL, signs, offset);
        this._asm.Test(Reg.DL, Reg.DL);
        this._asm.J(Condition.Equal, zero);
        this._asm.J(Condition.NotSign, positive);
        this._asm.Neg(Reg.AL);
        this._asm.Jmp(store);
        this._asm.MarkLabel(zero);
        this._asm.Xor(Reg.AL, Reg.AL);
        this._asm.MarkLabel(positive);
        this._asm.MarkLabel(store);
        this.StoreByte(state, output, offset, Reg.AL);
        continue;
      }
      if (laneBytes == 2) {
        this.LoadWord(state, Reg.AX, output, offset);
        this.LoadWord(state, Reg.DX, signs, offset);
        this._asm.Test(Reg.DX, Reg.DX);
        this._asm.J(Condition.Equal, zero);
        this._asm.J(Condition.NotSign, positive);
        this._asm.Neg(Reg.AX);
        this._asm.Jmp(store);
        this._asm.MarkLabel(zero);
        this._asm.Xor(Reg.AX, Reg.AX);
        this._asm.MarkLabel(positive);
        this._asm.MarkLabel(store);
        this.StoreWord(state, output, offset, Reg.AX);
        continue;
      }

      this.LoadWord(state, Reg.AX, output, offset);
      this.LoadWord(state, Reg.BX, output, offset + 2);
      this.LoadWord(state, Reg.DX, signs, offset + 2);
      this.LoadWord(state, Reg.CX, signs, offset);
      this._asm.Or(Reg.CX, Reg.DX);
      this._asm.J(Condition.Equal, zero);
      this._asm.Test(Reg.DX, Reg.DX);
      this._asm.J(Condition.NotSign, positive);
      this._asm.Neg(Reg.AX);
      this._asm.Adc(Reg.BX, 0);
      this._asm.Neg(Reg.BX);
      this._asm.Jmp(store);
      this._asm.MarkLabel(zero);
      this._asm.Xor(Reg.AX, Reg.AX);
      this._asm.Xor(Reg.BX, Reg.BX);
      this._asm.MarkLabel(positive);
      this._asm.MarkLabel(store);
      this.StoreWord(state, output, offset, Reg.AX);
      this.StoreWord(state, output, offset + 2, Reg.BX);
    }
    return true;
  }

  private bool EmitVirtualCompareQwordGreater(VirtualIsaState state,
      IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    error = null;
    if (operands.Count != 2 || operands[0] is not TextAssembler.ParsedAsmRegister destination || !destination.Register.IsXmm()
        || !TryVectorOperand(operands[1], out var source) || source.Register is { } sourceRegister && !sourceRegister.IsXmm()) {
      error = "PCMPGTQ expects XMM destination and XMM/m128 source";
      return true;
    }

    var output = VirtualOperand.Of(destination.Register);
    for (var offset = 0; offset < 16; offset += 8) {
      var yes = this._asm.DefineLabel();
      var no = this._asm.DefineLabel();
      var done = this._asm.DefineLabel();
      for (var word = 6; word >= 0; word -= 2) {
        this.LoadWord(state, Reg.AX, output, offset + word);
        this.LoadWord(state, Reg.DX, source, offset + word);
        this._asm.Cmp(Reg.AX, Reg.DX);
        if (word == 6) {
          this._asm.J(Condition.Greater, yes);
          this._asm.J(Condition.Less, no);
        } else {
          this._asm.J(Condition.Above, yes);
          this._asm.J(Condition.Below, no);
        }
      }
      this._asm.MarkLabel(no);
      for (var word = 0; word < 8; word += 2)
        this._asm.Mov(OperandCell(output, offset + word, OperandSize.Word,
          (r, p, s) => this.VirtualCell(state, r, p, s)), 0);
      this._asm.Jmp(done);
      this._asm.MarkLabel(yes);
      for (var word = 0; word < 8; word += 2)
        this._asm.Mov(OperandCell(output, offset + word, OperandSize.Word,
          (r, p, s) => this.VirtualCell(state, r, p, s)), -1);
      this._asm.MarkLabel(done);
    }
    return true;
  }

  private bool EmitVirtualMinPositionUnsignedWord(VirtualIsaState state,
      IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    error = null;
    if (operands.Count != 2 || operands[0] is not TextAssembler.ParsedAsmRegister destination || !destination.Register.IsXmm()
        || !TryVectorOperand(operands[1], out var source) || source.Register is { } sourceRegister && !sourceRegister.IsXmm()) {
      error = "PHMINPOSUW expects XMM destination and XMM/m128 source";
      return true;
    }

    this.LoadWord(state, Reg.BX, source, 0);
    this._asm.Xor(Reg.CX, Reg.CX);
    for (var lane = 1; lane < 8; ++lane) {
      var keep = this._asm.DefineLabel();
      this.LoadWord(state, Reg.AX, source, lane * 2);
      this._asm.Cmp(Reg.AX, Reg.BX);
      this._asm.J(Condition.AboveOrEqual, keep);
      this._asm.Mov(Reg.BX, Reg.AX);
      this._asm.Mov(Reg.CX, lane);
      this._asm.MarkLabel(keep);
    }

    var output = VirtualOperand.Of(destination.Register);
    this.StoreWord(state, output, 0, Reg.BX);
    this.StoreWord(state, output, 2, Reg.CX);
    for (var offset = 4; offset < 16; offset += 2)
      this._asm.Mov(OperandCell(output, offset, OperandSize.Word,
        (r, p, s) => this.VirtualCell(state, r, p, s)), 0);
    return true;
  }
}
