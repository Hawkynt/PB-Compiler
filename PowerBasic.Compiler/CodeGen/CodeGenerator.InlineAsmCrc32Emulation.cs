using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Runtime;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {
  private bool TryEmitVirtualCrc32Instruction(InlineInstruction instruction, InlineAsmResolver resolver,
      RuntimeTarget target, out string? error) {
    error = null;
    if (instruction.Mnemonic != "CRC32")
      return false;

    this._textAssembler ??= new(this._asm);
    if (!this._textAssembler.TryParseOperands(instruction.Operands, resolver, out var operands, out error))
      return true;
    if (operands.Count != 2 || operands[0] is not TextAssembler.ParsedAsmRegister destination
        || !destination.Register.IsDword() || destination.Register == Reg.ESP) {
      error = "CRC32 emulation expects a 32-bit GP destination other than ESP";
      return true;
    }

    var sourceBytes = operands[1] switch {
      TextAssembler.ParsedAsmRegister r when r.Register.IsByte() => 1,
      TextAssembler.ParsedAsmRegister r when r.Register.IsWord() => 2,
      TextAssembler.ParsedAsmRegister r when r.Register.IsDword() && r.Register != Reg.ESP => 4,
      TextAssembler.ParsedAsmMemory m when m.Memory.Size == OperandSize.Byte => 1,
      TextAssembler.ParsedAsmMemory m when m.Memory.Size == OperandSize.Word => 2,
      TextAssembler.ParsedAsmMemory m when m.Memory.Size == OperandSize.Dword => 4,
      _ => 0,
    };
    if (sourceBytes == 0) {
      error = "CRC32 source must be byte/word/dword register or explicitly sized memory";
      return true;
    }

    var state = this.EnsureVirtualIsaState();
    this.StageDword(state, operands[0], GpDestScratch, target);
    this.StageCrcSource(state, operands[1], sourceBytes, target);

    this._asm.Pushf();
    this._asm.Push(Reg.AX);
    this._asm.Push(Reg.BX);
    this._asm.Push(Reg.DX);
    this._asm.Mov(Reg.AX, this.GpScratch(state, GpDestScratch));
    this._asm.Mov(Reg.DX, this.GpScratch(state, GpDestScratch + 2));
    for (var byteIndex = 0; byteIndex < sourceBytes; ++byteIndex) {
      this._asm.Mov(Reg.BL, Mem.Byte(state.Scratch, GpSourceScratch + byteIndex).Cs());
      this._asm.Xor(Reg.AL, Reg.BL);
      for (var bit = 0; bit < 8; ++bit) {
        var noPolynomial = this._asm.DefineLabel();
        this._asm.Test(Reg.AL, 1);
        this._asm.J(Condition.Equal, noPolynomial);
        this._asm.Shr(Reg.DX, 1);
        this._asm.Rcr(Reg.AX, 1);
        this._asm.Xor(Reg.AX, 0x3B78);
        this._asm.Xor(Reg.DX, 0x82F6);
        var done = this._asm.DefineLabel();
        this._asm.Jmp(done);
        this._asm.MarkLabel(noPolynomial);
        this._asm.Shr(Reg.DX, 1);
        this._asm.Rcr(Reg.AX, 1);
        this._asm.MarkLabel(done);
      }
    }
    this._asm.Mov(this.GpScratch(state, GpDestScratch), Reg.AX);
    this._asm.Mov(this.GpScratch(state, GpDestScratch + 2), Reg.DX);
    this._asm.Pop(Reg.DX);
    this._asm.Pop(Reg.BX);
    this._asm.Pop(Reg.AX);
    this._asm.Popf();
    this.WriteDwordPlace(state, DwordPlace.Of(destination.Register), GpDestScratch, target);
    return true;
  }

  private void StageCrcSource(VirtualIsaState state, TextAssembler.ParsedAsmOperand source, int sourceBytes,
      RuntimeTarget target) {
    if (sourceBytes == 4) {
      this.StageDword(state, source, GpSourceScratch, target);
      return;
    }

    switch (source) {
      case TextAssembler.ParsedAsmRegister register:
        if (sourceBytes == 1)
          this._asm.Mov(Mem.Byte(state.Scratch, GpSourceScratch).Cs(), register.Register);
        else
          this._asm.Mov(Mem.Word(state.Scratch, GpSourceScratch).Cs(), register.Register);
        return;
      case TextAssembler.ParsedAsmMemory memory:
        this._asm.Push(Reg.AX);
        if (sourceBytes == 1) {
          this._asm.Mov(Reg.AL, memory.Memory.WithSize(OperandSize.Byte));
          this._asm.Mov(Mem.Byte(state.Scratch, GpSourceScratch).Cs(), Reg.AL);
        } else {
          this._asm.Mov(Reg.AX, memory.Memory.WithSize(OperandSize.Word));
          this._asm.Mov(Mem.Word(state.Scratch, GpSourceScratch).Cs(), Reg.AX);
        }
        this._asm.Pop(Reg.AX);
        return;
    }
  }
}
