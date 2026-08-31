using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Runtime;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {
  private const int BmiA = 96;
  private const int BmiB = 100;
  private const int BmiC = 104;
  private const int BmiD = 108;
  private const int BmiFlags = 124;

  private static bool IsBmi1(string mnemonic) => mnemonic is "ANDN" or "BEXTR" or "BLSI" or "BLSMSK" or "BLSR" or "TZCNT";
  private static bool IsBmi2(string mnemonic) => mnemonic is "BZHI" or "MULX" or "PDEP" or "PEXT" or "RORX" or "SARX" or "SHLX" or "SHRX";

  private static RuntimeCpuFeatures RequiredBmiFeature(InlineInstruction instruction) =>
    IsBmi1(instruction.Mnemonic) ? RuntimeCpuFeatures.Bmi1
      : IsBmi2(instruction.Mnemonic) ? RuntimeCpuFeatures.Bmi2
      : RuntimeCpuFeatures.None;

  private bool TryEmitNativeBmiInstruction(InlineInstruction instruction, InlineAsmResolver resolver, out string? error) {
    error = null;
    if (!IsBmi1(instruction.Mnemonic) && !IsBmi2(instruction.Mnemonic))
      return false;
    this._textAssembler ??= new(this._asm);
    if (!this._textAssembler.TryParseOperands(instruction.Operands, resolver, out var operands, out error))
      return true;
    return this.EmitNativeBmi(instruction.Mnemonic, operands, out error);
  }

  private bool EmitNativeBmi(string mnemonic, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    error = null;
    if (mnemonic == "RORX") {
      if (operands.Count != 3 || !BmiRegister(operands[0], out var d) || operands[2] is not TextAssembler.ParsedAsmImmediate imm
          || !BmiSource(operands[1], out var sr, out var sm)) { error = "RORX expects r32, r/m32, imm8"; return true; }
      if (sr is { } r) this._asm.Rorx(d, r, unchecked((byte)imm.Value)); else this._asm.Rorx(d, sm!.Value, unchecked((byte)imm.Value));
      return true;
    }
    if (mnemonic == "MULX") {
      if (operands.Count != 3 || !BmiRegister(operands[0], out var lo) || !BmiRegister(operands[1], out var hi)
          || !BmiSource(operands[2], out var sr, out var sm)) { error = "MULX expects r32, r32, r/m32"; return true; }
      if (sr is { } r) this._asm.Mulx(lo, hi, r); else this._asm.Mulx(lo, hi, sm!.Value);
      return true;
    }
    if (mnemonic is "ANDN" or "BEXTR" or "BZHI" or "PDEP" or "PEXT" or "SARX" or "SHLX" or "SHRX") {
      if (operands.Count != 3 || !BmiRegister(operands[0], out var d) || !BmiRegister(operands[mnemonic == "ANDN" ? 1 : 2], out var v)
          || !BmiSource(operands[mnemonic == "ANDN" ? 2 : 1], out var sr, out var sm)) {
        error = $"{mnemonic} expects r32, r32, r/m32 (with the architectural source/control order)"; return true;
      }
      if (mnemonic == "ANDN") { if (sr is { } r) this._asm.Andn(d, v, r); else this._asm.Andn(d, v, sm!.Value); return true; }
      if (mnemonic == "BEXTR") { if (sr is { } r) this._asm.Bextr(d, r, v); else this._asm.Bextr(d, sm!.Value, v); return true; }
      if (mnemonic == "BZHI") { if (sr is { } r) this._asm.Bzhi(d, r, v); else this._asm.Bzhi(d, sm!.Value, v); return true; }
      if (mnemonic == "PDEP") { if (sr is { } r) this._asm.Pdep(d, r, v); else this._asm.Pdep(d, r: default, mask: default); }
      if (mnemonic == "PDEP") { if (sr is { } r) this._asm.Pdep(d, r, v); else { error = "PDEP memory mask must be the third operand"; } return true; }
      if (mnemonic == "PEXT") { if (sr is { } r) this._asm.Pext(d, r, v); else { error = "PEXT memory mask must be the third operand"; } return true; }
      if (mnemonic == "SARX") { if (sr is { } r) this._asm.Sarx(d, r, v); else this._asm.Sarx(d, sm!.Value, v); return true; }
      if (mnemonic == "SHLX") { if (sr is { } r) this._asm.Shlx(d, r, v); else this._asm.Shlx(d, sm!.Value, v); return true; }
      if (sr is { } rr) this._asm.Shrx(d, rr, v); else this._asm.Shrx(d, sm!.Value, v);
      return true;
    }
    if (mnemonic is "BLSI" or "BLSMSK" or "BLSR" or "TZCNT") {
      if (operands.Count != 2 || !BmiRegister(operands[0], out var d) || !BmiSource(operands[1], out var sr, out var sm)) {
        error = $"{mnemonic} expects r32, r/m32"; return true;
      }
      switch (mnemonic) {
        case "BLSI": if (sr is { } r) this._asm.Blsi(d, r); else this._asm.Blsi(d, sm!.Value); break;
        case "BLSMSK": if (sr is { } r) this._asm.Blsmsk(d, r); else this._asm.Blsmsk(d, sm!.Value); break;
        case "BLSR": if (sr is { } r) this._asm.Blsr(d, r); else this._asm.Blsr(d, sm!.Value); break;
        default: if (sr is { } r) this._asm.Tzcnt(d, r); else this._asm.Tzcnt(d, sm!.Value); break;
      }
      return true;
    }
    error = $"unsupported BMI instruction {mnemonic}";
    return true;
  }

  private static bool BmiRegister(TextAssembler.ParsedAsmOperand operand, out Reg register) {
    if (operand is TextAssembler.ParsedAsmRegister { Register: var r } && r.IsDword() && r != Reg.ESP) { register = r; return true; }
    register = default; return false;
  }

  private static bool BmiSource(TextAssembler.ParsedAsmOperand operand, out Reg? register, out Mem? memory) {
    if (operand is TextAssembler.ParsedAsmRegister { Register: var r } && r.IsDword() && r != Reg.ESP) { register = r; memory = null; return true; }
    if (operand is TextAssembler.ParsedAsmMemory m && m.Memory.Size is OperandSize.None or OperandSize.Dword) { register = null; memory = m.Memory.WithSize(OperandSize.Dword); return true; }
    register = null; memory = null; return false;
  }

  private bool TryEmitVirtualBmiInstruction(InlineInstruction instruction, InlineAsmResolver resolver, RuntimeTarget target, out string? error) {
    error = null;
    if (!IsBmi1(instruction.Mnemonic) && !IsBmi2(instruction.Mnemonic))
      return false;
    this._textAssembler ??= new(this._asm);
    if (!this._textAssembler.TryParseOperands(instruction.Operands, resolver, out var operands, out error))
      return true;
    var state = this.EnsureVirtualIsaState();
    return IsBmi1(instruction.Mnemonic)
      ? this.EmitVirtualBmi1(state, instruction.Mnemonic, operands, target, out error)
      : this.EmitVirtualBmi2(state, instruction.Mnemonic, operands, target, out error);
  }

  private bool EmitVirtualBmi1(VirtualIsaState state, string mnemonic, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands,
      RuntimeTarget target, out string? error) {
    error = null;
    if (!BmiRegister(operands.ElementAtOrDefault(0)!, out var destination)) { error = $"{mnemonic} requires a dword GP destination other than ESP"; return true; }
    var expected = mnemonic is "ANDN" or "BEXTR" ? 3 : 2;
    if (operands.Count != expected) { error = $"{mnemonic} expects {expected} operands"; return true; }
    this.SaveBmiFlags(state);

    if (mnemonic == "ANDN") {
      if (!BmiVirtualSource(operands[1], out var left) || !BmiVirtualSource(operands[2], out var right)) { error = "ANDN expects r32, r32, r/m32"; return true; }
      this.StageDword(state, left, BmiA, target); this.StageDword(state, right, BmiB, target);
      this.BmiWordBinary(state, BmiA, BmiB, BmiC, static (asm, a, b) => { asm.Not(a); asm.And(a, b); });
      this.WriteDwordPlace(state, DwordPlace.Of(destination), BmiC, target);
      this.RestoreBmiResultFlags(state, BmiC, 0xF73E, setParity: false, carryMode: 0, sourceOffset: BmiA);
      return true;
    }

    if (!BmiVirtualSource(operands[1], out var source)) { error = $"{mnemonic} expects r32, r/m32"; return true; }
    this.StageDword(state, source, BmiA, target);
    switch (mnemonic) {
      case "BEXTR":
        if (!BmiVirtualSource(operands[2], out var control)) { error = "BEXTR control must be r32"; return true; }
        this.StageDword(state, control, BmiB, target);
        this.EmitBextr(state);
        this.WriteDwordPlace(state, DwordPlace.Of(destination), BmiC, target);
        this.RestoreBmiResultFlags(state, BmiC, 0xF7BE, false, 0, BmiA);
        break;
      case "BLSI": this.EmitBlsi(state); this.WriteDwordPlace(state, DwordPlace.Of(destination), BmiC, target); this.RestoreBmiResultFlags(state, BmiC, 0xF73E, false, 2, BmiA); break;
      case "BLSMSK": this.EmitBls(state, xor: true); this.WriteDwordPlace(state, DwordPlace.Of(destination), BmiC, target); this.RestoreBmiResultFlags(state, BmiC, 0xF73E, false, 1, BmiA); break;
      case "BLSR": this.EmitBls(state, xor: false); this.WriteDwordPlace(state, DwordPlace.Of(destination), BmiC, target); this.RestoreBmiResultFlags(state, BmiC, 0xF73E, false, 1, BmiA); break;
      case "TZCNT": this.EmitTzcnt(state); this.WriteDwordPlace(state, DwordPlace.Of(destination), BmiC, target); this.RestoreTzcntFlags(state); break;
    }
    return true;
  }

  private static bool BmiVirtualSource(TextAssembler.ParsedAsmOperand operand, out TextAssembler.ParsedAsmOperand source) {
    if (operand is TextAssembler.ParsedAsmRegister { Register: var r } && r.IsDword() && r != Reg.ESP) { source = operand; return true; }
    if (operand is TextAssembler.ParsedAsmMemory m && m.Memory.Size is OperandSize.None or OperandSize.Dword) { source = new TextAssembler.ParsedAsmMemory(m.Memory.WithSize(OperandSize.Dword)); return true; }
    source = null!; return false;
  }

  private void SaveBmiFlags(VirtualIsaState state) {
    this._asm.Push(Reg.AX); this._asm.Pushf(); this._asm.Pop(Reg.AX); this._asm.Mov(this.GpScratch(state, BmiFlags), Reg.AX); this._asm.Pop(Reg.AX);
  }

  private delegate void BmiWordOp(Assembler asm, Reg accumulator, Mem right);
  private void BmiWordBinary(VirtualIsaState state, int left, int right, int result, BmiWordOp operation) {
    this._asm.Push(Reg.AX);
    for (var word = 0; word < 2; ++word) {
      this._asm.Mov(Reg.AX, this.GpScratch(state, left + word * 2));
      operation(this._asm, Reg.AX, this.GpScratch(state, right + word * 2));
      this._asm.Mov(this.GpScratch(state, result + word * 2), Reg.AX);
    }
    this._asm.Pop(Reg.AX);
  }

  private void EmitBls(VirtualIsaState state, bool xor) {
    this.BmiCopyDword(state, BmiA, BmiB);
    this._asm.Sub(this.GpScratch(state, BmiB), 1); this._asm.Sbb(this.GpScratch(state, BmiB + 2), 0);
    this.BmiWordBinary(state, BmiA, BmiB, BmiC, xor
      ? static (asm, a, b) => asm.Xor(a, b)
      : static (asm, a, b) => asm.And(a, b));
  }

  private void EmitBlsi(VirtualIsaState state) {
    this._asm.Push(Reg.AX); this._asm.Push(Reg.DX);
    this._asm.Xor(Reg.AX, Reg.AX); this._asm.Sub(Reg.AX, this.GpScratch(state, BmiA));
    this._asm.Xor(Reg.DX, Reg.DX); this._asm.Sbb(Reg.DX, this.GpScratch(state, BmiA + 2));
    this._asm.And(Reg.AX, this.GpScratch(state, BmiA)); this._asm.And(Reg.DX, this.GpScratch(state, BmiA + 2));
    this._asm.Mov(this.GpScratch(state, BmiC), Reg.AX); this._asm.Mov(this.GpScratch(state, BmiC + 2), Reg.DX);
    this._asm.Pop(Reg.DX); this._asm.Pop(Reg.AX);
  }

  private void EmitBextr(VirtualIsaState state) {
    this.BmiCopyDword(state, BmiA, BmiC);
    this._asm.Push(Reg.CX);
    this._asm.Mov(Reg.CX, this.GpScratch(state, BmiB)); this._asm.And(Reg.CX, 0x00FF);
    var zero = this._asm.DefineLabel(); var startLoop = this._asm.DefineLabel(); var startDone = this._asm.DefineLabel();
    this._asm.Cmp(Reg.CX, 32); this._asm.J(Condition.AboveOrEqual, zero); this._asm.Cmp(Reg.CX, 0); this._asm.J(Condition.Equal, startDone);
    this._asm.MarkLabel(startLoop); this.BmiShiftRightOne(state, BmiC, arithmetic: false); this._asm.Loop(startLoop); this._asm.MarkLabel(startDone);
    this._asm.Mov(Reg.CX, this.GpScratch(state, BmiB)); this._asm.Shr(Reg.CX, 8); this._asm.And(Reg.CX, 0x00FF);
    this._asm.Cmp(Reg.CX, 32); var lengthReady = this._asm.DefineLabel(); this._asm.J(Condition.Below, lengthReady); this._asm.Mov(Reg.CX, 32); this._asm.MarkLabel(lengthReady);
    this._asm.Cmp(Reg.CX, 0); this._asm.J(Condition.Equal, zero);
    this._asm.Mov(Reg.AX, 32); this._asm.Sub(Reg.AX, Reg.CX); this._asm.Mov(Reg.CX, Reg.AX);
    var masked = this._asm.DefineLabel(); this._asm.Cmp(Reg.CX, 0); this._asm.J(Condition.Equal, masked);
    var leftLoop = this._asm.DefineLabel(); this._asm.MarkLabel(leftLoop); this.BmiShiftLeftOne(state, BmiC); this._asm.Loop(leftLoop);
    this._asm.Mov(Reg.CX, Reg.AX); var rightLoop = this._asm.DefineLabel(); this._asm.MarkLabel(rightLoop); this.BmiShiftRightOne(state, BmiC, false); this._asm.Loop(rightLoop); this._asm.Jmp(masked);
    this._asm.MarkLabel(zero); this._asm.Mov(this.GpScratch(state, BmiC), 0); this._asm.Mov(this.GpScratch(state, BmiC + 2), 0);
    this._asm.MarkLabel(masked); this._asm.Pop(Reg.CX);
  }

  private void EmitTzcnt(VirtualIsaState state) {
    this.BmiCopyDword(state, BmiA, BmiB);
    this._asm.Push(Reg.BX); this._asm.Xor(Reg.BX, Reg.BX);
    var nonZero = this._asm.DefineLabel(); var done = this._asm.DefineLabel(); var loop = this._asm.DefineLabel();
    this._asm.Mov(Reg.AX, this.GpScratch(state, BmiB)); this._asm.Or(Reg.AX, this.GpScratch(state, BmiB + 2)); this._asm.J(Condition.NotEqual, nonZero);
    this._asm.Mov(Reg.BX, 32); this._asm.Jmp(done);
    this._asm.MarkLabel(nonZero); this._asm.MarkLabel(loop); this._asm.Test(this.GpScratch(state, BmiB), 1); this._asm.J(Condition.NotEqual, done);
    this.BmiShiftRightOne(state, BmiB, false); this._asm.Inc(Reg.BX); this._asm.Jmp(loop);
    this._asm.MarkLabel(done); this._asm.Mov(this.GpScratch(state, BmiC), Reg.BX); this._asm.Mov(this.GpScratch(state, BmiC + 2), 0); this._asm.Pop(Reg.BX);
  }

  private void RestoreTzcntFlags(VirtualIsaState state) {
    this._asm.Push(Reg.AX); this._asm.Mov(Reg.AX, this.GpScratch(state, BmiFlags)); this._asm.And(Reg.AX, 0xFFBE);
    var srcNonzero = this._asm.DefineLabel(); this._asm.Mov(Reg.DX, this.GpScratch(state, BmiA)); this._asm.Or(Reg.DX, this.GpScratch(state, BmiA + 2)); this._asm.J(Condition.NotEqual, srcNonzero); this._asm.Or(Reg.AX, 1); this._asm.MarkLabel(srcNonzero);
    var resultNonzero = this._asm.DefineLabel(); this._asm.Cmp(this.GpScratch(state, BmiC), 0); this._asm.J(Condition.NotEqual, resultNonzero); this._asm.Or(Reg.AX, 0x0040); this._asm.MarkLabel(resultNonzero);
    this._asm.Push(Reg.AX); this._asm.Popf(); this._asm.Pop(Reg.AX);
  }

  private void RestoreBmiResultFlags(VirtualIsaState state, int result, ushort baseMask, bool setParity, int carryMode, int sourceOffset) {
    this._asm.Push(Reg.AX); this._asm.Push(Reg.DX); this._asm.Mov(Reg.AX, this.GpScratch(state, BmiFlags)); this._asm.And(Reg.AX, baseMask);
    var nonzero = this._asm.DefineLabel(); this._asm.Mov(Reg.DX, this.GpScratch(state, result)); this._asm.Or(Reg.DX, this.GpScratch(state, result + 2)); this._asm.J(Condition.NotEqual, nonzero); this._asm.Or(Reg.AX, 0x0040); this._asm.MarkLabel(nonzero);
    this._asm.Test(this.GpScratch(state, result + 2), 0x8000); var nonnegative = this._asm.DefineLabel(); this._asm.J(Condition.Equal, nonnegative); this._asm.Or(Reg.AX, 0x0080); this._asm.MarkLabel(nonnegative);
    if (carryMode != 0) {
      var sourceNonzero = this._asm.DefineLabel(); this._asm.Mov(Reg.DX, this.GpScratch(state, sourceOffset)); this._asm.Or(Reg.DX, this.GpScratch(state, sourceOffset + 2)); this._asm.J(Condition.NotEqual, sourceNonzero);
      if (carryMode == 1) this._asm.Or(Reg.AX, 1);
      var carryDone = this._asm.DefineLabel(); this._asm.Jmp(carryDone); this._asm.MarkLabel(sourceNonzero); if (carryMode == 2) this._asm.Or(Reg.AX, 1); this._asm.MarkLabel(carryDone);
    }
    this._asm.Push(Reg.AX); this._asm.Popf(); this._asm.Pop(Reg.DX); this._asm.Pop(Reg.AX);
  }

  private bool EmitVirtualBmi2(VirtualIsaState state, string mnemonic, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands,
      RuntimeTarget target, out string? error) {
    error = null;
    if (!BmiRegister(operands.ElementAtOrDefault(0)!, out var destination)) { error = $"{mnemonic} requires a dword GP destination other than ESP"; return true; }
    this._asm.Pushf();
    if (mnemonic == "RORX") {
      if (operands.Count != 3 || !BmiVirtualSource(operands[1], out var source) || operands[2] is not TextAssembler.ParsedAsmImmediate immediate) { error = "RORX expects r32, r/m32, imm8"; this._asm.Popf(); return true; }
      this.StageDword(state, source, BmiA, target); this.BmiCopyDword(state, BmiA, BmiC); this.EmitRorx(state, unchecked((byte)immediate.Value) & 31);
      this.WriteDwordPlace(state, DwordPlace.Of(destination), BmiC, target); this._asm.Popf(); return true;
    }
    if (mnemonic == "MULX") {
      if (operands.Count != 3 || !BmiRegister(operands[1], out var highDestination) || !BmiVirtualSource(operands[2], out var multiplier)) { error = "MULX expects r32, r32, r/m32"; this._asm.Popf(); return true; }
      this.StageDword(state, new TextAssembler.ParsedAsmRegister(Reg.EDX), BmiA, target); this.StageDword(state, multiplier, BmiB, target); this.EmitMulx(state);
      this.WriteDwordPlace(state, DwordPlace.Of(destination), BmiC, target); this.WriteDwordPlace(state, DwordPlace.Of(highDestination), BmiD, target); this._asm.Popf(); return true;
    }
    if (operands.Count != 3 || !BmiVirtualSource(operands[1], out var data) || !BmiVirtualSource(operands[2], out var third)) { error = $"{mnemonic} expects r32, r/m32, r32"; this._asm.Popf(); return true; }
    this.StageDword(state, data, BmiA, target); this.StageDword(state, third, BmiB, target);
    switch (mnemonic) {
      case "BZHI": this.EmitBzhi(state); break;
      case "PDEP": this.EmitPdep(state); break;
      case "PEXT": this.EmitPext(state); break;
      case "SARX": case "SHLX": case "SHRX": this.EmitBmiVariableShift(state, mnemonic); break;
      default: error = $"unsupported BMI2 instruction {mnemonic}"; this._asm.Popf(); return true;
    }
    this.WriteDwordPlace(state, DwordPlace.Of(destination), BmiC, target);
    if (mnemonic == "BZHI") { this._asm.Popf(); this.RestoreBzhiFlags(state); } else this._asm.Popf();
    return true;
  }

  private void EmitBzhi(VirtualIsaState state) {
    this._asm.Mov(this.GpScratch(state, BmiC), 0); this._asm.Mov(this.GpScratch(state, BmiC + 2), 0);
    this._asm.Push(Reg.CX); this._asm.Mov(Reg.CX, this.GpScratch(state, BmiB)); this._asm.And(Reg.CX, 0x00FF);
    for (var bit = 0; bit < 32; ++bit) {
      var skip = this._asm.DefineLabel(); this._asm.Cmp(Reg.CX, bit + 1); this._asm.J(Condition.Below, skip);
      var word = bit >> 4; var mask = 1 << (bit & 15); this._asm.Test(this.GpScratch(state, BmiA + word * 2), mask); var clear = this._asm.DefineLabel(); this._asm.J(Condition.Equal, clear);
      this._asm.Or(this.GpScratch(state, BmiC + word * 2), mask); this._asm.MarkLabel(clear); this._asm.MarkLabel(skip);
    }
    this._asm.Pop(Reg.CX);
  }

  private void RestoreBzhiFlags(VirtualIsaState state) {
    this._asm.Push(Reg.AX); this._asm.Push(Reg.DX); this._asm.Pushf(); this._asm.Pop(Reg.AX); this._asm.And(Reg.AX, 0xF73A);
    this._asm.Mov(Reg.DX, this.GpScratch(state, BmiC)); this._asm.Or(Reg.DX, this.GpScratch(state, BmiC + 2)); var nz = this._asm.DefineLabel(); this._asm.J(Condition.NotEqual, nz); this._asm.Or(Reg.AX, 0x0040); this._asm.MarkLabel(nz);
    this._asm.Test(this.GpScratch(state, BmiC + 2), 0x8000); var ns = this._asm.DefineLabel(); this._asm.J(Condition.Equal, ns); this._asm.Or(Reg.AX, 0x0080); this._asm.MarkLabel(ns);
    this._asm.Test(Mem.Byte(state.Scratch, BmiC).Cs(), 0xFF); this._asm.Pushf(); this._asm.Pop(Reg.DX); this._asm.And(Reg.DX, 4); this._asm.Or(Reg.AX, Reg.DX);
    this._asm.Mov(Reg.DX, this.GpScratch(state, BmiB)); this._asm.And(Reg.DX, 0x00FF); this._asm.Cmp(Reg.DX, 32); var noCarry = this._asm.DefineLabel(); this._asm.J(Condition.Below, noCarry); this._asm.Or(Reg.AX, 1); this._asm.MarkLabel(noCarry);
    this._asm.Push(Reg.AX); this._asm.Popf(); this._asm.Pop(Reg.DX); this._asm.Pop(Reg.AX);
  }

  private void EmitPdep(VirtualIsaState state) {
    this._asm.Mov(this.GpScratch(state, BmiC), 0); this._asm.Mov(this.GpScratch(state, BmiC + 2), 0); this.BmiCopyDword(state, BmiA, BmiD);
    for (var bit = 0; bit < 32; ++bit) {
      var word = bit >> 4; var mask = 1 << (bit & 15); var skip = this._asm.DefineLabel();
      this._asm.Test(this.GpScratch(state, BmiB + word * 2), mask); this._asm.J(Condition.Equal, skip);
      var dataZero = this._asm.DefineLabel(); this._asm.Test(this.GpScratch(state, BmiD), 1); this._asm.J(Condition.Equal, dataZero); this._asm.Or(this.GpScratch(state, BmiC + word * 2), mask); this._asm.MarkLabel(dataZero);
      this.BmiShiftRightOne(state, BmiD, false); this._asm.MarkLabel(skip);
    }
  }

  private void EmitPext(VirtualIsaState state) {
    this._asm.Mov(this.GpScratch(state, BmiC), 0); this._asm.Mov(this.GpScratch(state, BmiC + 2), 0);
    this._asm.Mov(this.GpScratch(state, BmiD), 1); this._asm.Mov(this.GpScratch(state, BmiD + 2), 0);
    for (var bit = 0; bit < 32; ++bit) {
      var word = bit >> 4; var mask = 1 << (bit & 15); var skip = this._asm.DefineLabel();
      this._asm.Test(this.GpScratch(state, BmiB + word * 2), mask); this._asm.J(Condition.Equal, skip);
      var dataZero = this._asm.DefineLabel(); this._asm.Test(this.GpScratch(state, BmiA + word * 2), mask); this._asm.J(Condition.Equal, dataZero);
      this._asm.Push(Reg.AX); this._asm.Mov(Reg.AX, this.GpScratch(state, BmiD)); this._asm.Or(this.GpScratch(state, BmiC), Reg.AX); this._asm.Mov(Reg.AX, this.GpScratch(state, BmiD + 2)); this._asm.Or(this.GpScratch(state, BmiC + 2), Reg.AX); this._asm.Pop(Reg.AX);
      this._asm.MarkLabel(dataZero); this.BmiShiftLeftOne(state, BmiD); this._asm.MarkLabel(skip);
    }
  }

  private void EmitBmiVariableShift(VirtualIsaState state, string mnemonic) {
    this.BmiCopyDword(state, BmiA, BmiC); this._asm.Push(Reg.CX); this._asm.Mov(Reg.CX, this.GpScratch(state, BmiB)); this._asm.And(Reg.CX, 31);
    var done = this._asm.DefineLabel(); var loop = this._asm.DefineLabel(); this._asm.Cmp(Reg.CX, 0); this._asm.J(Condition.Equal, done); this._asm.MarkLabel(loop);
    if (mnemonic == "SHLX") this.BmiShiftLeftOne(state, BmiC); else this.BmiShiftRightOne(state, BmiC, mnemonic == "SARX");
    this._asm.Loop(loop); this._asm.MarkLabel(done); this._asm.Pop(Reg.CX);
  }

  private void EmitRorx(VirtualIsaState state, int count) {
    for (var i = 0; i < count; ++i) {
      this._asm.Shr(this.GpScratch(state, BmiC + 2), 1); this._asm.Rcr(this.GpScratch(state, BmiC), 1);
      var noWrap = this._asm.DefineLabel(); this._asm.J(Condition.AboveOrEqual, noWrap); this._asm.Or(this.GpScratch(state, BmiC + 2), 0x8000); this._asm.MarkLabel(noWrap);
    }
  }

  private void EmitMulx(VirtualIsaState state) {
    // Four 16x16 products; C=low32, D=high32. No 32-bit hardware is required.
    this._asm.Push(Reg.AX); this._asm.Push(Reg.BX); this._asm.Push(Reg.DX);
    this._asm.Mov(Reg.AX, this.GpScratch(state, BmiA)); this._asm.Mul(this.GpScratch(state, BmiB)); this._asm.Mov(this.GpScratch(state, BmiC), Reg.AX); this._asm.Mov(this.GpScratch(state, 112), Reg.DX);
    this._asm.Mov(Reg.AX, this.GpScratch(state, BmiA)); this._asm.Mul(this.GpScratch(state, BmiB + 2)); this._asm.Mov(this.GpScratch(state, 114), Reg.AX); this._asm.Mov(this.GpScratch(state, 116), Reg.DX);
    this._asm.Mov(Reg.AX, this.GpScratch(state, BmiA + 2)); this._asm.Mul(this.GpScratch(state, BmiB)); this._asm.Mov(this.GpScratch(state, 118), Reg.AX); this._asm.Mov(this.GpScratch(state, 120), Reg.DX);
    this._asm.Mov(Reg.AX, this.GpScratch(state, BmiA + 2)); this._asm.Mul(this.GpScratch(state, BmiB + 2)); this._asm.Mov(this.GpScratch(state, 122), Reg.AX); this._asm.Mov(this.GpScratch(state, BmiFlags), Reg.DX);
    this._asm.Mov(Reg.AX, this.GpScratch(state, 112)); this._asm.Xor(Reg.BX, Reg.BX); this._asm.Add(Reg.AX, this.GpScratch(state, 114)); var c1 = this._asm.DefineLabel(); this._asm.J(Condition.AboveOrEqual, c1); this._asm.Inc(Reg.BX); this._asm.MarkLabel(c1); this._asm.Add(Reg.AX, this.GpScratch(state, 118)); var c2 = this._asm.DefineLabel(); this._asm.J(Condition.AboveOrEqual, c2); this._asm.Inc(Reg.BX); this._asm.MarkLabel(c2); this._asm.Mov(this.GpScratch(state, BmiC + 2), Reg.AX);
    this._asm.Mov(Reg.AX, this.GpScratch(state, 116)); this._asm.Xor(Reg.DX, Reg.DX); this._asm.Add(Reg.AX, this.GpScratch(state, 120)); var c3 = this._asm.DefineLabel(); this._asm.J(Condition.AboveOrEqual, c3); this._asm.Inc(Reg.DX); this._asm.MarkLabel(c3); this._asm.Add(Reg.AX, this.GpScratch(state, 122)); var c4 = this._asm.DefineLabel(); this._asm.J(Condition.AboveOrEqual, c4); this._asm.Inc(Reg.DX); this._asm.MarkLabel(c4); this._asm.Add(Reg.AX, Reg.BX); var c5 = this._asm.DefineLabel(); this._asm.J(Condition.AboveOrEqual, c5); this._asm.Inc(Reg.DX); this._asm.MarkLabel(c5); this._asm.Mov(this.GpScratch(state, BmiD), Reg.AX);
    this._asm.Mov(Reg.AX, this.GpScratch(state, BmiFlags)); this._asm.Add(Reg.AX, Reg.DX); this._asm.Mov(this.GpScratch(state, BmiD + 2), Reg.AX);
    this._asm.Pop(Reg.DX); this._asm.Pop(Reg.BX); this._asm.Pop(Reg.AX);
  }

  private void BmiCopyDword(VirtualIsaState state, int source, int destination) {
    this._asm.Push(Reg.AX); this._asm.Mov(Reg.AX, this.GpScratch(state, source)); this._asm.Mov(this.GpScratch(state, destination), Reg.AX); this._asm.Mov(Reg.AX, this.GpScratch(state, source + 2)); this._asm.Mov(this.GpScratch(state, destination + 2), Reg.AX); this._asm.Pop(Reg.AX);
  }
  private void BmiShiftLeftOne(VirtualIsaState state, int offset) { this._asm.Shl(this.GpScratch(state, offset), 1); this._asm.Rcl(this.GpScratch(state, offset + 2), 1); }
  private void BmiShiftRightOne(VirtualIsaState state, int offset, bool arithmetic) { if (arithmetic) this._asm.Sar(this.GpScratch(state, offset + 2), 1); else this._asm.Shr(this.GpScratch(state, offset + 2), 1); this._asm.Rcr(this.GpScratch(state, offset), 1); }
}
