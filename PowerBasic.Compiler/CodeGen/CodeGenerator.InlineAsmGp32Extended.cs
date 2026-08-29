using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Runtime;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {
  private const int GpShiftCountScratch = 96;

  /// <summary>Pre-386 lowering for 386 operations that are not ordinary two-operand ALU forms.</summary>
  private bool TryEmitVirtualGp32ExtendedInstruction(InlineInstruction instruction, InlineAsmResolver resolver, RuntimeTarget target, out string? error) {
    error = null;
    var mnemonic = instruction.Mnemonic;
    if (mnemonic is not ("CWDE" or "CDQ" or "PUSH" or "POP" or "SHL" or "SAL" or "SHR" or "SAR"))
      return false;

    this._textAssembler ??= new(this._asm);
    if (!this._textAssembler.TryParseOperands(instruction.Operands, resolver, out var operands, out error))
      return true;

    var state = this.EnsureVirtualIsaState();
    return mnemonic switch {
      "CWDE" => this.EmitVirtualCwde(state, operands, target, out error),
      "CDQ" => this.EmitVirtualCdq(state, operands, target, out error),
      "PUSH" => this.EmitVirtualPushDword(state, operands, target, out error),
      "POP" => this.EmitVirtualPopDword(state, operands, target, out error),
      "SHL" or "SAL" or "SHR" or "SAR" => this.EmitVirtualDwordShift(state, mnemonic, operands, target, out error),
      _ => false,
    };
  }

  private bool EmitVirtualCwde(VirtualIsaState state, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, RuntimeTarget target, out string? error) {
    error = null;
    if (operands.Count != 0) { error = "CWDE takes no operands"; return true; }
    this._asm.Pushf();
    this._asm.Push(Reg.CX);
    this._asm.Mov(Reg.CX, Reg.AX);
    this._asm.Sar(Reg.CX, 15);
    this._asm.Mov(this.GpHighCell(state, Reg.EAX), Reg.CX);
    this._asm.Pop(Reg.CX);
    this.BridgeHighToNativeIfAvailable(state, Reg.EAX, target);
    this._asm.Popf();
    return true;
  }

  private bool EmitVirtualCdq(VirtualIsaState state, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, RuntimeTarget target, out string? error) {
    error = null;
    if (operands.Count != 0) { error = "CDQ takes no operands"; return true; }
    this.BridgeHighFromNativeIfAvailable(state, Reg.EAX, target);
    this._asm.Pushf();
    this._asm.Push(Reg.CX);
    this._asm.Mov(Reg.CX, this.GpHighCell(state, Reg.EAX));
    this._asm.Sar(Reg.CX, 15);
    this._asm.Mov(Reg.DX, Reg.CX);
    this._asm.Mov(this.GpHighCell(state, Reg.EDX), Reg.CX);
    this._asm.Pop(Reg.CX);
    this.BridgeHighToNativeIfAvailable(state, Reg.EDX, target);
    this._asm.Popf();
    return true;
  }

  private bool EmitVirtualPushDword(VirtualIsaState state, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, RuntimeTarget target, out string? error) {
    error = null;
    if (operands.Count != 1 || operands[0] is not (TextAssembler.ParsedAsmRegister or TextAssembler.ParsedAsmMemory or TextAssembler.ParsedAsmImmediate))
      return false; // ordinary 16-bit PUSH belongs to the normal assembler
    if (operands[0] is TextAssembler.ParsedAsmRegister r && (!r.Register.IsDword() || r.Register == Reg.ESP))
      return false;
    if (operands[0] is TextAssembler.ParsedAsmMemory m && m.Memory.Size != OperandSize.Dword)
      return false;

    this.StageDword(state, operands[0], GpSourceScratch, target);
    // 32-bit PUSH leaves the low word at the final SP and high word at SP+2.
    this._asm.Push(this.GpScratch(state, GpSourceScratch + 2));
    this._asm.Push(this.GpScratch(state, GpSourceScratch));
    return true;
  }

  private bool EmitVirtualPopDword(VirtualIsaState state, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, RuntimeTarget target, out string? error) {
    error = null;
    if (operands.Count != 1 || !TryDwordPlace(operands[0], out var destination))
      return false;

    // Stage both words before writing a BP-relative destination: POP itself changes SP, not BP.
    this._asm.Pop(this.GpScratch(state, GpDestScratch));
    this._asm.Pop(this.GpScratch(state, GpDestScratch + 2));
    this.WriteDwordPlace(state, destination, GpDestScratch, target);
    return true;
  }

  private bool EmitVirtualDwordShift(VirtualIsaState state, string mnemonic, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, RuntimeTarget target, out string? error) {
    error = null;
    if (operands.Count != 2 || !TryDwordPlace(operands[0], out var destination))
      return false;

    var countCell = this.GpScratch(state, GpShiftCountScratch);
    switch (operands[1]) {
      case TextAssembler.ParsedAsmImmediate immediate:
        this._asm.Mov(countCell, immediate.Value & 31);
        break;
      case TextAssembler.ParsedAsmRegister { Register: Reg.CL }:
        this._asm.Push(Reg.AX);
        this._asm.Xor(Reg.AH, Reg.AH);
        this._asm.Mov(Reg.AL, Reg.CL);
        this._asm.And(Reg.AX, 31);
        this._asm.Mov(countCell, Reg.AX);
        this._asm.Pop(Reg.AX);
        break;
      default:
        error = $"32-bit {mnemonic} emulation expects an immediate count or CL";
        return true;
    }

    this.StageDwordPlace(state, destination, GpDestScratch, target);
    var low = this.GpScratch(state, GpDestScratch);
    var high = this.GpScratch(state, GpDestScratch + 2);
    var original = this.GpScratch(state, GpOrigFlagsScratch);
    var lowFlags = this.GpScratch(state, GpLowFlagsScratch);
    var highFlags = this.GpScratch(state, GpHighFlagsScratch);

    this._asm.Push(Reg.AX);
    this._asm.Push(Reg.CX);
    this._asm.Pushf(); this._asm.Pop(Reg.AX); this._asm.Mov(original, Reg.AX);
    this._asm.Mov(Reg.CX, countCell);
    var unchanged = this._asm.DefineLabel();
    var loop = this._asm.DefineLabel();
    var finish = this._asm.DefineLabel();
    this._asm.Jcxz(unchanged);

    this._asm.MarkLabel(loop);
    if (mnemonic is "SHL" or "SAL") {
      this._asm.Shl(low, 1);
      this._asm.Pushf(); this._asm.Pop(Reg.AX); this._asm.Mov(lowFlags, Reg.AX);
      this._asm.Rcl(high, 1);
      this._asm.Pushf(); this._asm.Pop(Reg.AX); this._asm.Mov(highFlags, Reg.AX);
    } else {
      if (mnemonic == "SAR") this._asm.Sar(high, 1); else this._asm.Shr(high, 1);
      this._asm.Pushf(); this._asm.Pop(Reg.AX); this._asm.Mov(highFlags, Reg.AX);
      this._asm.Rcr(low, 1);
      this._asm.Pushf(); this._asm.Pop(Reg.AX); this._asm.Mov(lowFlags, Reg.AX);
    }
    this._asm.Loop(loop);

    this.MergeDwordShiftFlags(state, Reg.AX, left: mnemonic is "SHL" or "SAL", arithmetic: mnemonic == "SAR");
    this.WriteDwordPlace(state, destination, GpDestScratch, target);
    this._asm.Jmp(finish);

    this._asm.MarkLabel(unchanged);
    this._asm.Push(original); this._asm.Popf();
    this._asm.MarkLabel(finish);
    this._asm.Pop(Reg.CX);
    this._asm.Pop(Reg.AX);
    return true;
  }

  /// <summary>Merges 16-bit half-operation flags into x86's 32-bit shift result flags.</summary>
  private void MergeDwordShiftFlags(VirtualIsaState state, Reg temp, bool left, bool arithmetic) {
    var merged = this.GpScratch(state, GpMergedFlagsScratch);
    var original = this.GpScratch(state, GpOrigFlagsScratch);
    var low = this.GpScratch(state, GpLowFlagsScratch);
    var high = this.GpScratch(state, GpHighFlagsScratch);

    // Preserve all non-status bits and AF (undefined for shifts). SF comes from the high word,
    // PF from the low byte, ZF is the conjunction of both word ZFs, and CF comes from the outer half.
    this._asm.Mov(temp, original); this._asm.And(temp, 0xF73A); this._asm.Mov(merged, temp);
    this._asm.Mov(temp, high); this._asm.And(temp, 0x0080); this._asm.Or(merged, temp);
    this._asm.Mov(temp, low); this._asm.And(temp, 0x0004); this._asm.Or(merged, temp);
    this._asm.Mov(temp, left ? high : low); this._asm.And(temp, 0x0001); this._asm.Or(merged, temp);

    var noZero = this._asm.DefineLabel();
    this._asm.Mov(temp, low); this._asm.Test(temp, 0x0040); this._asm.Jz(noZero);
    this._asm.Mov(temp, high); this._asm.Test(temp, 0x0040); this._asm.Jz(noZero);
    this._asm.Or(merged, 0x0040);
    this._asm.MarkLabel(noZero);

    // For SAR, OF is defined as zero for count=1. For larger counts it is undefined; zero is a legal
    // deterministic choice. For SHL/SHR use the last half-operation OF; count>1 is likewise undefined.
    if (!arithmetic) {
      this._asm.Mov(temp, left ? high : high);
      this._asm.And(temp, 0x0800);
      this._asm.Or(merged, temp);
    }

    this._asm.Push(merged);
    this._asm.Popf();
  }
}
