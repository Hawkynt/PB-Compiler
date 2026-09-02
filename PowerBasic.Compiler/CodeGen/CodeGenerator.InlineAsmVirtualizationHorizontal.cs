using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {
  private bool TryEmitVirtualHorizontalInstruction(InlineInstruction instruction, InlineAsmResolver resolver, out string? error) {
    error = null;
    if (instruction.Mnemonic is not ("PHADDW" or "PHADDD" or "PHSUBW" or "PHSUBD"))
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
        "PHADDW" => this.EmitVirtualHorizontal(state, operands, 2, subtract: false, out error),
        "PHADDD" => this.EmitVirtualHorizontal(state, operands, 4, subtract: false, out error),
        "PHSUBW" => this.EmitVirtualHorizontal(state, operands, 2, subtract: true, out error),
        "PHSUBD" => this.EmitVirtualHorizontal(state, operands, 4, subtract: true, out error),
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

  private bool EmitVirtualHorizontal(VirtualIsaState state, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands,
      int laneBytes, bool subtract, out string? error) {
    error = null;
    if (operands.Count != 2 || operands[0] is not TextAssembler.ParsedAsmRegister destination
        || !(destination.Register.IsMmx() || destination.Register.IsXmm()) || !TryVectorOperand(operands[1], out var source)) {
      error = "horizontal SIMD operation expects MMX/XMM destination and matching vector/memory source";
      return true;
    }
    var width = VectorWidth(destination.Register);
    if (source.Register is { } sourceRegister && VectorWidth(sourceRegister) != width) {
      error = "horizontal SIMD operand widths differ";
      return true;
    }

    var originalDestination = VirtualOperand.Of(destination.Register);
    this.CopyToScratch(state, originalDestination, 0, width);
    this.CopyToScratch(state, source, 64, width);
    var left = VirtualOperand.Of(Mem.At(state.Scratch, 0).Cs());
    var right = VirtualOperand.Of(Mem.At(state.Scratch, 64).Cs());
    var output = VirtualOperand.Of(destination.Register);
    var lanesPerSource = width / laneBytes;
    var outputLane = 0;

    for (var half = 0; half < 2; ++half) {
      var input = half == 0 ? left : right;
      for (var lane = 0; lane < lanesPerSource; lane += 2, ++outputLane) {
        var inputOffset = lane * laneBytes;
        var outputOffset = outputLane * laneBytes;
        if (laneBytes == 2) {
          this.LoadWord(state, Reg.AX, input, inputOffset);
          this.LoadWord(state, Reg.DX, input, inputOffset + 2);
          if (subtract) this._asm.Sub(Reg.AX, Reg.DX); else this._asm.Add(Reg.AX, Reg.DX);
          this.StoreWord(state, output, outputOffset, Reg.AX);
          continue;
        }

        this.LoadWord(state, Reg.AX, input, inputOffset);
        this.LoadWord(state, Reg.BX, input, inputOffset + 2);
        this.LoadWord(state, Reg.CX, input, inputOffset + 4);
        this.LoadWord(state, Reg.DX, input, inputOffset + 6);
        if (subtract) {
          this._asm.Sub(Reg.AX, Reg.CX);
          this._asm.Sbb(Reg.BX, Reg.DX);
        } else {
          this._asm.Add(Reg.AX, Reg.CX);
          this._asm.Adc(Reg.BX, Reg.DX);
        }
        this.StoreWord(state, output, outputOffset, Reg.AX);
        this.StoreWord(state, output, outputOffset + 2, Reg.BX);
      }
    }
    return true;
  }
}
