using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Runtime;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {
  /// <summary>
  /// 8086 lowering for MOVSD/CMPSD/STOSD/LODSD/SCASD, including REP/REPE/REPNE. Dword loads and
  /// stores are staged before writes so overlap cannot corrupt the second word, and index adjustment
  /// preserves the architectural flags (especially the comparison result consumed by REP(E/NE)).
  /// </summary>
  private bool TryEmitVirtualGp32StringInstruction(InlineInstruction instruction, RuntimeTarget target, out string? error) {
    error = null;
    if (instruction.Mnemonic is not ("MOVSD" or "CMPSD" or "STOSD" or "LODSD" or "SCASD"))
      return false;

    var state = this.EnsureVirtualIsaState();
    var repeated = instruction.RepPrefix is not null;
    var loop = this._asm.DefineLabel();
    var afterDecrement = this._asm.DefineLabel();
    var done = this._asm.DefineLabel();

    if (repeated)
      this._asm.Jcxz(done);
    this._asm.MarkLabel(loop);

    switch (instruction.Mnemonic) {
      case "MOVSD":
        this.EmitVirtualMovsdIteration(state, target);
        break;
      case "STOSD":
        this.EmitVirtualStosdIteration(state, target);
        break;
      case "LODSD":
        this.EmitVirtualLodsdIteration(state, target);
        break;
      case "CMPSD":
        this.EmitVirtualCmpsdIteration(state, target);
        break;
      case "SCASD":
        this.EmitVirtualScasdIteration(state, target);
        break;
    }

    if (!repeated) {
      this._asm.MarkLabel(done);
      return true;
    }

    if (instruction.Mnemonic is not ("CMPSD" or "SCASD")) {
      this._asm.Loop(loop);
      this._asm.MarkLabel(done);
      return true;
    }

    // CMPS/SCAS decrement CX after each comparison, then apply the ZF continuation condition.
    this._asm.Loop(afterDecrement);
    this._asm.Jmp(done);
    this._asm.MarkLabel(afterDecrement);
    if (instruction.RepPrefix is "REPNE" or "REPNZ")
      this._asm.J(Condition.Equal, done);
    else // REP, REPE and REPZ are the F3/REPE spelling for CMPS/SCAS
      this._asm.J(Condition.NotEqual, done);
    this._asm.Jmp(loop);
    this._asm.MarkLabel(done);
    return true;
  }

  private void EmitVirtualMovsdIteration(VirtualIsaState state, RuntimeTarget target) {
    var source = Mem.At(Reg.SI).Ds().WithSize(OperandSize.Dword);
    var destination = Mem.At(Reg.DI).Es().WithSize(OperandSize.Dword);
    this.StageDword(state, new TextAssembler.ParsedAsmMemory(source), GpSourceScratch, target);
    this.WriteDwordPlace(state, DwordPlace.Of(destination), GpSourceScratch, target);
    this.EmitAdjustDwordStringIndices(adjustSi: true, adjustDi: true);
  }

  private void EmitVirtualStosdIteration(VirtualIsaState state, RuntimeTarget target) {
    var destination = Mem.At(Reg.DI).Es().WithSize(OperandSize.Dword);
    this.StageDword(state, new TextAssembler.ParsedAsmRegister(Reg.EAX), GpSourceScratch, target);
    this.WriteDwordPlace(state, DwordPlace.Of(destination), GpSourceScratch, target);
    this.EmitAdjustDwordStringIndices(adjustSi: false, adjustDi: true);
  }

  private void EmitVirtualLodsdIteration(VirtualIsaState state, RuntimeTarget target) {
    var source = Mem.At(Reg.SI).Ds().WithSize(OperandSize.Dword);
    this.StageDword(state, new TextAssembler.ParsedAsmMemory(source), GpSourceScratch, target);
    this.WriteDwordPlace(state, DwordPlace.Of(Reg.EAX), GpSourceScratch, target);
    this.EmitAdjustDwordStringIndices(adjustSi: true, adjustDi: false);
  }

  private void EmitVirtualCmpsdIteration(VirtualIsaState state, RuntimeTarget target) {
    var left = Mem.At(Reg.SI).Ds().WithSize(OperandSize.Dword);
    var right = Mem.At(Reg.DI).Es().WithSize(OperandSize.Dword);
    IReadOnlyList<TextAssembler.ParsedAsmOperand> operands = [
      new TextAssembler.ParsedAsmMemory(left),
      new TextAssembler.ParsedAsmMemory(right),
    ];
    _ = this.EmitVirtualDwordAlu(state, "CMP", operands, target, out _);
    this.EmitAdjustDwordStringIndices(adjustSi: true, adjustDi: true);
  }

  private void EmitVirtualScasdIteration(VirtualIsaState state, RuntimeTarget target) {
    var right = Mem.At(Reg.DI).Es().WithSize(OperandSize.Dword);
    IReadOnlyList<TextAssembler.ParsedAsmOperand> operands = [
      new TextAssembler.ParsedAsmRegister(Reg.EAX),
      new TextAssembler.ParsedAsmMemory(right),
    ];
    _ = this.EmitVirtualDwordAlu(state, "CMP", operands, target, out _);
    this.EmitAdjustDwordStringIndices(adjustSi: false, adjustDi: true);
  }

  /// <summary>Adjusts SI/DI by four according to DF without changing any flag.</summary>
  private void EmitAdjustDwordStringIndices(bool adjustSi, bool adjustDi) {
    this._asm.Push(Reg.AX);
    this._asm.Pushf();
    this._asm.Pop(Reg.AX);
    var backwards = this._asm.DefineLabel();
    var done = this._asm.DefineLabel();
    this._asm.Test(Reg.AX, 0x0400);
    this._asm.J(Condition.NotEqual, backwards);
    if (adjustSi) this._asm.Add(Reg.SI, 4);
    if (adjustDi) this._asm.Add(Reg.DI, 4);
    this._asm.Jmp(done);
    this._asm.MarkLabel(backwards);
    if (adjustSi) this._asm.Sub(Reg.SI, 4);
    if (adjustDi) this._asm.Sub(Reg.DI, 4);
    this._asm.MarkLabel(done);
    this._asm.Push(Reg.AX);
    this._asm.Popf();
    this._asm.Pop(Reg.AX);
  }
}
