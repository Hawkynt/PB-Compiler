using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {
  private bool TryEmitVirtualShuffleInstruction(InlineInstruction instruction, InlineAsmResolver resolver, out string? error) {
    error = null;
    if (instruction.Mnemonic != "PSHUFB")
      return false;

    this._textAssembler ??= new(this._asm);
    if (!this._textAssembler.TryParseOperands(instruction.Operands, resolver, out var operands, out error))
      return true;
    if (operands.Count != 2 || operands[0] is not TextAssembler.ParsedAsmRegister destination
        || !(destination.Register.IsMmx() || destination.Register.IsXmm()) || !TryVectorOperand(operands[1], out var control)) {
      error = "PSHUFB expects MMX/XMM destination and matching vector/memory control";
      return true;
    }

    var width = VectorWidth(destination.Register);
    if (control.Register is { } controlRegister && VectorWidth(controlRegister) != width) {
      error = "PSHUFB operand widths differ";
      return true;
    }

    var state = this.EnsureVirtualIsaState();
    var output = VirtualOperand.Of(destination.Register);
    this._asm.Pushf();
    this._asm.Push(Reg.AX);
    this._asm.Push(Reg.BX);
    try {
      this.CopyToScratch(state, output, 0, width);
      this.CopyToScratch(state, control, 64, width);
      var controlCopy = VirtualOperand.Of(Mem.At(state.Scratch, 64).Cs());
      var mask = width - 1;
      for (var offset = 0; offset < width; ++offset) {
        var zero = this._asm.DefineLabel();
        var store = this._asm.DefineLabel();
        this.LoadByte(state, Reg.AL, controlCopy, offset);
        this._asm.Test(Reg.AL, 0x80);
        this._asm.J(Condition.NotEqual, zero);
        this._asm.Xor(Reg.AH, Reg.AH);
        this._asm.And(Reg.AX, mask);
        this._asm.Mov(Reg.BX, Reg.AX);
        this._asm.Mov(Reg.AL, Mem.Byte(Reg.BX, state.Scratch).Cs());
        this._asm.Jmp(store);
        this._asm.MarkLabel(zero);
        this._asm.Xor(Reg.AL, Reg.AL);
        this._asm.MarkLabel(store);
        this.StoreByte(state, output, offset, Reg.AL);
      }
      return true;
    } finally {
      this._asm.Pop(Reg.BX);
      this._asm.Pop(Reg.AX);
      this._asm.Popf();
    }
  }
}
