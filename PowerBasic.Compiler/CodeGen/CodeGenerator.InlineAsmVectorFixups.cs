using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Runtime;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {
  /// <summary>Specialized vector lowerings whose edge semantics are easier to express explicitly.</summary>
  private bool TryEmitVirtualVectorFixup(InlineInstruction instruction, InlineAsmResolver resolver, RuntimeTarget target, out string? error) {
    error = null;
    if (instruction.Mnemonic != "PACKSSDW")
      return false;

    this._textAssembler ??= new(this._asm);
    if (!this._textAssembler.TryParseOperands(instruction.Operands, resolver, out var operands, out error))
      return true;

    var state = this.EnsureVirtualIsaState();
    this._asm.Pushf();
    this._asm.Push(Reg.AX);
    this._asm.Push(Reg.CX);
    this._asm.Push(Reg.DX);
    try {
      return this.EmitVirtualPackSsDwExact(state, operands, out error);
    } finally {
      this._asm.Pop(Reg.DX);
      this._asm.Pop(Reg.CX);
      this._asm.Pop(Reg.AX);
      this._asm.Popf();
    }
  }

  private bool EmitVirtualPackSsDwExact(VirtualIsaState state, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    error = null;
    if (operands.Count != 2 || operands[0] is not TextAssembler.ParsedAsmRegister destination || !IsVirtualVector(destination.Register)
        || !TryVectorOperand(operands[1], out var source)) {
      error = "PACKSSDW expects vector destination and vector/memory source";
      return true;
    }

    var width = VectorWidth(destination.Register);
    if (source.Register is { } sr && VectorWidth(sr) != width) {
      error = "PACKSSDW operand widths differ";
      return true;
    }

    // Both inputs may alias the destination, therefore stage both before producing a single output.
    this.CopyToScratch(state, VirtualOperand.Of(destination.Register), 0, width);
    this.CopyToScratch(state, source, 64, width);
    var elementsPerSource = width / 4;
    var output = VirtualOperand.Of(destination.Register);
    var outIndex = 0;
    for (var half = 0; half < 2; ++half)
      for (var i = 0; i < elementsPerSource; ++i, ++outIndex) {
        var input = VirtualOperand.Of(Mem.At(state.Scratch, half * 64 + i * 4).Cs());
        this.EmitPackSignedDwordLane(state, input, output, outIndex * 2);
      }
    return true;
  }

  private void EmitPackSignedDwordLane(VirtualIsaState state, VirtualOperand input, VirtualOperand output, int outputOffset) {
    this.LoadWord(state, Reg.AX, input, 0);
    this.LoadWord(state, Reg.DX, input, 2);
    var nonnegative = this._asm.DefineLabel();
    var minimum = this._asm.DefineLabel();
    var maximum = this._asm.DefineLabel();
    var store = this._asm.DefineLabel();

    this._asm.Cmp(Reg.DX, 0);
    this._asm.J(Condition.Greater, maximum);
    this._asm.J(Condition.Equal, nonnegative);

    // Negative 32-bit values fit signed-word range only when high == FFFFh and low >= 8000h.
    this._asm.Cmp(Reg.DX, -1);
    this._asm.J(Condition.Less, minimum);
    this._asm.Cmp(Reg.AX, 0x8000);
    this._asm.J(Condition.Below, minimum);
    this._asm.Jmp(store);

    this._asm.MarkLabel(nonnegative);
    this._asm.Cmp(Reg.AX, 0x7FFF);
    this._asm.J(Condition.Above, maximum);
    this._asm.Jmp(store);

    this._asm.MarkLabel(minimum);
    this._asm.Mov(Reg.AX, 0x8000);
    this._asm.Jmp(store);
    this._asm.MarkLabel(maximum);
    this._asm.Mov(Reg.AX, 0x7FFF);
    this._asm.MarkLabel(store);
    this.StoreWord(state, output, outputOffset, Reg.AX);
  }
}
