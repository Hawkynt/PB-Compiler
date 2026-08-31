using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Runtime;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {
  /// <summary>
  /// GP32 rotate lowering with architectural writeback after temporary-register restoration. The
  /// older arithmetic backend saves AX/CX/DX while rotating; on pre-386 targets those word registers
  /// are also the low halves of virtual EAX/ECX/EDX, so restoring them after writeback resurrects the
  /// pre-rotate low word. Keep the value in scratch until the implementation registers are restored.
  /// </summary>
  private bool TryEmitVirtualGp32RotateFixedInstruction(InlineInstruction instruction, InlineAsmResolver resolver,
      RuntimeTarget target, out string? error) {
    error = null;
    if (instruction.Mnemonic is not ("ROL" or "ROR" or "RCL" or "RCR"))
      return false;

    this._textAssembler ??= new(this._asm);
    if (!this._textAssembler.TryParseOperands(instruction.Operands, resolver, out var operands, out error))
      return true;
    if (operands.Count != 2 || !TryDwordPlace(operands[0], out var destination)
        || destination.Register == Reg.ESP
        || destination.Memory is { } memory && memory.Size != OperandSize.Dword)
      return false;

    var state = this.EnsureVirtualIsaState();
    this.StageDwordPlace(state, destination, GpArithA, target);
    this.SaveArithmeticFlags(state);
    var count = this.GpScratch(state, GpArithCount);
    switch (operands[1]) {
      case TextAssembler.ParsedAsmImmediate immediate:
        this._asm.Mov(count, immediate.Value & 31);
        break;
      case TextAssembler.ParsedAsmRegister { Register: Reg.CL }:
        this._asm.Push(Reg.AX);
        this._asm.Xor(Reg.AH, Reg.AH);
        this._asm.Mov(Reg.AL, Reg.CL);
        this._asm.And(Reg.AX, 31);
        this._asm.Mov(count, Reg.AX);
        this._asm.Pop(Reg.AX);
        break;
      default:
        error = $"32-bit {instruction.Mnemonic} expects an immediate count or CL";
        return true;
    }

    var low = this.GpScratch(state, GpArithA);
    var high = this.GpScratch(state, GpArithA + 2);
    var carry = this.GpScratch(state, GpArithSign);
    this._asm.Push(Reg.AX);
    this._asm.Push(Reg.CX);
    this._asm.Push(Reg.DX);
    this._asm.Mov(Reg.CX, count);

    var unchanged = this._asm.DefineLabel();
    var loop = this._asm.DefineLabel();
    var done = this._asm.DefineLabel();
    this._asm.Cmp(Reg.CX, 0);
    this._asm.J(Condition.Equal, unchanged);

    if (instruction.Mnemonic is "RCL" or "RCR") {
      this._asm.Push(this.GpScratch(state, GpArithFlags));
      this._asm.Popf();
    }

    this._asm.MarkLabel(loop);
    switch (instruction.Mnemonic) {
      case "ROL": {
        this._asm.Shl(low, 1);
        this._asm.Rcl(high, 1);
        var noWrap = this._asm.DefineLabel();
        this._asm.J(Condition.AboveOrEqual, noWrap);
        this._asm.Inc(low);
        this._asm.MarkLabel(noWrap);
        break;
      }
      case "ROR": {
        this._asm.Shr(high, 1);
        this._asm.Rcr(low, 1);
        this._asm.Pushf();
        this._asm.Pop(Reg.AX);
        this._asm.And(Reg.AX, 1);
        this._asm.Mov(carry, Reg.AX);
        var noWrap = this._asm.DefineLabel();
        this._asm.Test(carry, 1);
        this._asm.J(Condition.Equal, noWrap);
        this._asm.Or(high, 0x8000);
        this._asm.MarkLabel(noWrap);
        break;
      }
      case "RCL":
        this._asm.Rcl(low, 1);
        this._asm.Rcl(high, 1);
        break;
      case "RCR":
        this._asm.Rcr(high, 1);
        this._asm.Rcr(low, 1);
        break;
    }
    this._asm.Loop(loop);

    if (instruction.Mnemonic != "ROR") {
      this._asm.Pushf();
      this._asm.Pop(Reg.AX);
      this._asm.And(Reg.AX, 1);
      this._asm.Mov(carry, Reg.AX);
    }

    this.RestoreRotateFlags(state, instruction.Mnemonic, high, count, carry);
    this._asm.Pop(Reg.DX);
    this._asm.Pop(Reg.CX);
    this._asm.Pop(Reg.AX);
    this.WriteDwordPlace(state, destination, GpArithA, target);
    this._asm.Jmp(done);

    this._asm.MarkLabel(unchanged);
    this._asm.Push(this.GpScratch(state, GpArithFlags));
    this._asm.Popf();
    this._asm.Pop(Reg.DX);
    this._asm.Pop(Reg.CX);
    this._asm.Pop(Reg.AX);
    this._asm.MarkLabel(done);
    return true;
  }
}
