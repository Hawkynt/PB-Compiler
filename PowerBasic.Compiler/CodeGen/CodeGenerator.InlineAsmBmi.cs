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
    switch (mnemonic) {
      case "ANDN": {
        if (operands.Count != 3 || !BmiNativeRegister(operands[0], out var destination)
            || !BmiNativeRegister(operands[1], out var left) || !BmiSource(operands[2], out var rightRegister, out var rightMemory)) {
          error = "ANDN expects r32, r32, r/m32";
          return true;
        }
        if (rightRegister is { } right)
          this._asm.Andn(destination, left, right);
        else
          this._asm.Andn(destination, left, rightMemory!.Value);
        return true;
      }

      case "BEXTR": {
        if (operands.Count != 3 || !BmiNativeRegister(operands[0], out var destination)
            || !BmiSource(operands[1], out var sourceRegister, out var sourceMemory)
            || !BmiNativeRegister(operands[2], out var control)) {
          error = "BEXTR expects r32, r/m32, r32";
          return true;
        }
        if (sourceRegister is { } source)
          this._asm.Bextr(destination, source, control);
        else
          this._asm.Bextr(destination, sourceMemory!.Value, control);
        return true;
      }

      case "BLSI" or "BLSMSK" or "BLSR" or "TZCNT": {
        if (operands.Count != 2 || !BmiNativeRegister(operands[0], out var destination)
            || !BmiSource(operands[1], out var sourceRegister, out var sourceMemory)) {
          error = $"{mnemonic} expects r32, r/m32";
          return true;
        }
        if (sourceRegister is { } source) {
          switch (mnemonic) {
            case "BLSI": this._asm.Blsi(destination, source); break;
            case "BLSMSK": this._asm.Blsmsk(destination, source); break;
            case "BLSR": this._asm.Blsr(destination, source); break;
            default: this._asm.Tzcnt(destination, source); break;
          }
        } else {
          var source = sourceMemory!.Value;
          switch (mnemonic) {
            case "BLSI": this._asm.Blsi(destination, source); break;
            case "BLSMSK": this._asm.Blsmsk(destination, source); break;
            case "BLSR": this._asm.Blsr(destination, source); break;
            default: this._asm.Tzcnt(destination, source); break;
          }
        }
        return true;
      }

      case "BZHI" or "SARX" or "SHLX" or "SHRX": {
        if (operands.Count != 3 || !BmiNativeRegister(operands[0], out var destination)
            || !BmiSource(operands[1], out var sourceRegister, out var sourceMemory)
            || !BmiNativeRegister(operands[2], out var control)) {
          error = $"{mnemonic} expects r32, r/m32, r32";
          return true;
        }
        if (sourceRegister is { } source) {
          switch (mnemonic) {
            case "BZHI": this._asm.Bzhi(destination, source, control); break;
            case "SARX": this._asm.Sarx(destination, source, control); break;
            case "SHLX": this._asm.Shlx(destination, source, control); break;
            default: this._asm.Shrx(destination, source, control); break;
          }
        } else {
          var source = sourceMemory!.Value;
          switch (mnemonic) {
            case "BZHI": this._asm.Bzhi(destination, source, control); break;
            case "SARX": this._asm.Sarx(destination, source, control); break;
            case "SHLX": this._asm.Shlx(destination, source, control); break;
            default: this._asm.Shrx(destination, source, control); break;
          }
        }
        return true;
      }

      case "PDEP" or "PEXT": {
        if (operands.Count != 3 || !BmiNativeRegister(operands[0], out var destination)
            || !BmiNativeRegister(operands[1], out var source)
            || !BmiSource(operands[2], out var maskRegister, out var maskMemory)) {
          error = $"{mnemonic} expects r32, r32, r/m32";
          return true;
        }
        if (mnemonic == "PDEP") {
          if (maskRegister is { } mask) this._asm.Pdep(destination, source, mask);
          else this._asm.Pdep(destination, source, maskMemory!.Value);
        } else {
          if (maskRegister is { } mask) this._asm.Pext(destination, source, mask);
          else this._asm.Pext(destination, source, maskMemory!.Value);
        }
        return true;
      }

      case "MULX": {
        if (operands.Count != 3 || !BmiNativeRegister(operands[0], out var highDestination)
            || !BmiNativeRegister(operands[1], out var lowDestination)
            || !BmiSource(operands[2], out var sourceRegister, out var sourceMemory)) {
          error = "MULX expects high-r32, low-r32, r/m32";
          return true;
        }
        if (sourceRegister is { } source)
          this._asm.Mulx(highDestination, lowDestination, source);
        else
          this._asm.Mulx(highDestination, lowDestination, sourceMemory!.Value);
        return true;
      }

      case "RORX": {
        if (operands.Count != 3 || !BmiNativeRegister(operands[0], out var destination)
            || !BmiSource(operands[1], out var sourceRegister, out var sourceMemory)
            || operands[2] is not TextAssembler.ParsedAsmImmediate immediate) {
          error = "RORX expects r32, r/m32, imm8";
          return true;
        }
        var count = unchecked((byte)immediate.Value);
        if (sourceRegister is { } source)
          this._asm.Rorx(destination, source, count);
        else
          this._asm.Rorx(destination, sourceMemory!.Value, count);
        return true;
      }

      default:
        error = $"unsupported BMI instruction {mnemonic}";
        return true;
    }
  }

  private static bool BmiNativeRegister(TextAssembler.ParsedAsmOperand operand, out Reg register) {
    if (operand is TextAssembler.ParsedAsmRegister { Register: var candidate } && candidate.IsDword()) {
      register = candidate;
      return true;
    }
    register = default;
    return false;
  }

  private static bool BmiVirtualRegister(TextAssembler.ParsedAsmOperand operand, out Reg register) {
    if (operand is TextAssembler.ParsedAsmRegister { Register: var candidate } && candidate.IsDword() && candidate != Reg.ESP) {
      register = candidate;
      return true;
    }
    register = default;
    return false;
  }

  private static bool BmiSource(TextAssembler.ParsedAsmOperand operand, out Reg? register, out Mem? memory) {
    if (operand is TextAssembler.ParsedAsmRegister { Register: var candidate } && candidate.IsDword()) {
      register = candidate;
      memory = null;
      return true;
    }
    if (operand is TextAssembler.ParsedAsmMemory source && source.Memory.Size is OperandSize.None or OperandSize.Dword) {
      register = null;
      memory = source.Memory.WithSize(OperandSize.Dword);
      return true;
    }
    register = null;
    memory = null;
    return false;
  }

  private static bool BmiVirtualSource(TextAssembler.ParsedAsmOperand operand, out TextAssembler.ParsedAsmOperand source) {
    if (operand is TextAssembler.ParsedAsmRegister { Register: var register } && register.IsDword() && register != Reg.ESP) {
      source = operand;
      return true;
    }
    if (operand is TextAssembler.ParsedAsmMemory memory && memory.Memory.Size is OperandSize.None or OperandSize.Dword) {
      source = new TextAssembler.ParsedAsmMemory(memory.Memory.WithSize(OperandSize.Dword));
      return true;
    }
    source = null!;
    return false;
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
    if (!BmiVirtualRegister(operands.ElementAtOrDefault(0)!, out var destination)) {
      error = $"{mnemonic} requires a dword GP destination other than ESP";
      return true;
    }

    switch (mnemonic) {
      case "ANDN": {
        if (operands.Count != 3 || !BmiVirtualRegister(operands[1], out var leftRegister)
            || !BmiVirtualSource(operands[2], out var right)) {
          error = "ANDN expects r32, r32, r/m32";
          return true;
        }
        this.StageDword(state, new TextAssembler.ParsedAsmRegister(leftRegister), BmiA, target);
        this.StageDword(state, right, BmiB, target);
        this.SaveBmiFlags(state);
        this.BmiWordBinary(state, BmiA, BmiB, BmiC, static (asm, left, rightMemory) => {
          asm.Not(left);
          asm.And(left, rightMemory);
        });
        this.WriteDwordPlace(state, DwordPlace.Of(destination), BmiC, target);
        this.RestoreBmiResultFlags(state, BmiC, 0xF73E, setSign: true, carryMode: 0, sourceOffset: BmiA);
        return true;
      }

      case "BEXTR": {
        if (operands.Count != 3 || !BmiVirtualSource(operands[1], out var source)
            || !BmiVirtualRegister(operands[2], out var controlRegister)) {
          error = "BEXTR expects r32, r/m32, r32";
          return true;
        }
        this.StageDword(state, source, BmiA, target);
        this.StageDword(state, new TextAssembler.ParsedAsmRegister(controlRegister), BmiB, target);
        this.SaveBmiFlags(state);
        this.EmitBextr(state);
        this.WriteDwordPlace(state, DwordPlace.Of(destination), BmiC, target);
        // CF/OF are cleared and ZF is defined. SF/PF/AF are undefined; preserving them is the least surprising lowering.
        this.RestoreBmiResultFlags(state, BmiC, 0xF7BE, setSign: false, carryMode: 0, sourceOffset: BmiA);
        return true;
      }

      case "BLSI" or "BLSMSK" or "BLSR" or "TZCNT": {
        if (operands.Count != 2 || !BmiVirtualSource(operands[1], out var source)) {
          error = $"{mnemonic} expects r32, r/m32";
          return true;
        }
        this.StageDword(state, source, BmiA, target);
        this.SaveBmiFlags(state);
        switch (mnemonic) {
          case "BLSI":
            this.EmitBlsi(state);
            this.WriteDwordPlace(state, DwordPlace.Of(destination), BmiC, target);
            this.RestoreBmiResultFlags(state, BmiC, 0xF73E, setSign: true, carryMode: 2, sourceOffset: BmiA);
            break;
          case "BLSMSK":
            this.EmitBls(state, xor: true);
            this.WriteDwordPlace(state, DwordPlace.Of(destination), BmiC, target);
            this.RestoreBmiResultFlags(state, BmiC, 0xF73E, setSign: true, carryMode: 1, sourceOffset: BmiA);
            break;
          case "BLSR":
            this.EmitBls(state, xor: false);
            this.WriteDwordPlace(state, DwordPlace.Of(destination), BmiC, target);
            this.RestoreBmiResultFlags(state, BmiC, 0xF73E, setSign: true, carryMode: 1, sourceOffset: BmiA);
            break;
          default:
            this.EmitTzcnt(state);
            this.WriteDwordPlace(state, DwordPlace.Of(destination), BmiC, target);
            this.RestoreTzcntFlags(state);
            break;
        }
        return true;
      }

      default:
        error = $"unsupported BMI1 instruction {mnemonic}";
        return true;
    }
  }

  private void SaveBmiFlags(VirtualIsaState state) {
    this._asm.Push(Reg.AX);
    this._asm.Pushf();
    this._asm.Pop(Reg.AX);
    this._asm.Mov(this.GpScratch(state, BmiFlags), Reg.AX);
    this._asm.Pop(Reg.AX);
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
    this._asm.Sub(this.GpScratch(state, BmiB), 1);
    this._asm.Sbb(this.GpScratch(state, BmiB + 2), 0);
    this.BmiWordBinary(state, BmiA, BmiB, BmiC, xor
      ? static (asm, accumulator, right) => asm.Xor(accumulator, right)
      : static (asm, accumulator, right) => asm.And(accumulator, right));
  }

  private void EmitBlsi(VirtualIsaState state) {
    this._asm.Push(Reg.AX);
    this._asm.Push(Reg.DX);
    this._asm.Xor(Reg.AX, Reg.AX);
    this._asm.Sub(Reg.AX, this.GpScratch(state, BmiA));
    this._asm.Xor(Reg.DX, Reg.DX);
    this._asm.Sbb(Reg.DX, this.GpScratch(state, BmiA + 2));
    this._asm.And(Reg.AX, this.GpScratch(state, BmiA));
    this._asm.And(Reg.DX, this.GpScratch(state, BmiA + 2));
    this._asm.Mov(this.GpScratch(state, BmiC), Reg.AX);
    this._asm.Mov(this.GpScratch(state, BmiC + 2), Reg.DX);
    this._asm.Pop(Reg.DX);
    this._asm.Pop(Reg.AX);
  }

  private void EmitBextr(VirtualIsaState state) {
    this.BmiCopyDword(state, BmiA, BmiC);
    this._asm.Push(Reg.AX);
    this._asm.Push(Reg.CX);
    this._asm.Mov(Reg.CX, this.GpScratch(state, BmiB));
    this._asm.And(Reg.CX, 0x00FF);
    var zero = this._asm.DefineLabel();
    var startLoop = this._asm.DefineLabel();
    var startDone = this._asm.DefineLabel();
    this._asm.Cmp(Reg.CX, 32);
    this._asm.J(Condition.AboveOrEqual, zero);
    this._asm.Cmp(Reg.CX, 0);
    this._asm.J(Condition.Equal, startDone);
    this._asm.MarkLabel(startLoop);
    this.BmiShiftRightOne(state, BmiC, arithmetic: false);
    this._asm.Loop(startLoop);
    this._asm.MarkLabel(startDone);

    this._asm.Mov(Reg.CX, this.GpScratch(state, BmiB));
    this._asm.Shr(Reg.CX, 8);
    this._asm.And(Reg.CX, 0x00FF);
    this._asm.Cmp(Reg.CX, 32);
    var lengthReady = this._asm.DefineLabel();
    this._asm.J(Condition.Below, lengthReady);
    this._asm.Mov(Reg.CX, 32);
    this._asm.MarkLabel(lengthReady);
    this._asm.Cmp(Reg.CX, 0);
    this._asm.J(Condition.Equal, zero);

    this._asm.Mov(Reg.AX, 32);
    this._asm.Sub(Reg.AX, Reg.CX);
    this._asm.Mov(Reg.CX, Reg.AX);
    var masked = this._asm.DefineLabel();
    this._asm.Cmp(Reg.CX, 0);
    this._asm.J(Condition.Equal, masked);
    var leftLoop = this._asm.DefineLabel();
    this._asm.MarkLabel(leftLoop);
    this.BmiShiftLeftOne(state, BmiC);
    this._asm.Loop(leftLoop);
    this._asm.Mov(Reg.CX, Reg.AX);
    var rightLoop = this._asm.DefineLabel();
    this._asm.MarkLabel(rightLoop);
    this.BmiShiftRightOne(state, BmiC, arithmetic: false);
    this._asm.Loop(rightLoop);
    this._asm.Jmp(masked);

    this._asm.MarkLabel(zero);
    this._asm.Mov(this.GpScratch(state, BmiC), 0);
    this._asm.Mov(this.GpScratch(state, BmiC + 2), 0);
    this._asm.MarkLabel(masked);
    this._asm.Pop(Reg.CX);
    this._asm.Pop(Reg.AX);
  }

  private void EmitTzcnt(VirtualIsaState state) {
    this.BmiCopyDword(state, BmiA, BmiB);
    this._asm.Push(Reg.AX);
    this._asm.Push(Reg.BX);
    this._asm.Xor(Reg.BX, Reg.BX);
    var nonZero = this._asm.DefineLabel();
    var done = this._asm.DefineLabel();
    var loop = this._asm.DefineLabel();
    this._asm.Mov(Reg.AX, this.GpScratch(state, BmiB));
    this._asm.Or(Reg.AX, this.GpScratch(state, BmiB + 2));
    this._asm.J(Condition.NotEqual, nonZero);
    this._asm.Mov(Reg.BX, 32);
    this._asm.Jmp(done);
    this._asm.MarkLabel(nonZero);
    this._asm.MarkLabel(loop);
    this._asm.Test(this.GpScratch(state, BmiB), 1);
    this._asm.J(Condition.NotEqual, done);
    this.BmiShiftRightOne(state, BmiB, arithmetic: false);
    this._asm.Inc(Reg.BX);
    this._asm.Jmp(loop);
    this._asm.MarkLabel(done);
    this._asm.Mov(this.GpScratch(state, BmiC), Reg.BX);
    this._asm.Mov(this.GpScratch(state, BmiC + 2), 0);
    this._asm.Pop(Reg.BX);
    this._asm.Pop(Reg.AX);
  }

  private void RestoreTzcntFlags(VirtualIsaState state) {
    this._asm.Push(Reg.AX);
    this._asm.Push(Reg.DX);
    this._asm.Mov(Reg.AX, this.GpScratch(state, BmiFlags));
    this._asm.And(Reg.AX, 0xFFBE); // clear only defined CF/ZF; preserve undefined OF/SF/PF/AF.
    var sourceNonzero = this._asm.DefineLabel();
    this._asm.Mov(Reg.DX, this.GpScratch(state, BmiA));
    this._asm.Or(Reg.DX, this.GpScratch(state, BmiA + 2));
    this._asm.J(Condition.NotEqual, sourceNonzero);
    this._asm.Or(Reg.AX, 1);
    this._asm.MarkLabel(sourceNonzero);
    var resultNonzero = this._asm.DefineLabel();
    this._asm.Cmp(this.GpScratch(state, BmiC), 0);
    this._asm.J(Condition.NotEqual, resultNonzero);
    this._asm.Or(Reg.AX, 0x0040);
    this._asm.MarkLabel(resultNonzero);
    this._asm.Push(Reg.AX);
    this._asm.Popf();
    this._asm.Pop(Reg.DX);
    this._asm.Pop(Reg.AX);
  }

  private void RestoreBmiResultFlags(VirtualIsaState state, int result, ushort baseMask, bool setSign, int carryMode, int sourceOffset) {
    this._asm.Push(Reg.AX);
    this._asm.Push(Reg.DX);
    this._asm.Mov(Reg.AX, this.GpScratch(state, BmiFlags));
    this._asm.And(Reg.AX, baseMask);
    var nonzero = this._asm.DefineLabel();
    this._asm.Mov(Reg.DX, this.GpScratch(state, result));
    this._asm.Or(Reg.DX, this.GpScratch(state, result + 2));
    this._asm.J(Condition.NotEqual, nonzero);
    this._asm.Or(Reg.AX, 0x0040);
    this._asm.MarkLabel(nonzero);
    if (setSign) {
      this._asm.Test(this.GpScratch(state, result + 2), 0x8000);
      var nonnegative = this._asm.DefineLabel();
      this._asm.J(Condition.Equal, nonnegative);
      this._asm.Or(Reg.AX, 0x0080);
      this._asm.MarkLabel(nonnegative);
    }
    if (carryMode != 0) {
      var sourceNonzero = this._asm.DefineLabel();
      this._asm.Mov(Reg.DX, this.GpScratch(state, sourceOffset));
      this._asm.Or(Reg.DX, this.GpScratch(state, sourceOffset + 2));
      this._asm.J(Condition.NotEqual, sourceNonzero);
      if (carryMode == 1)
        this._asm.Or(Reg.AX, 1);
      var carryDone = this._asm.DefineLabel();
      this._asm.Jmp(carryDone);
      this._asm.MarkLabel(sourceNonzero);
      if (carryMode == 2)
        this._asm.Or(Reg.AX, 1);
      this._asm.MarkLabel(carryDone);
    }
    this._asm.Push(Reg.AX);
    this._asm.Popf();
    this._asm.Pop(Reg.DX);
    this._asm.Pop(Reg.AX);
  }

  private bool EmitVirtualBmi2(VirtualIsaState state, string mnemonic, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands,
      RuntimeTarget target, out string? error) {
    error = null;
    if (!BmiVirtualRegister(operands.ElementAtOrDefault(0)!, out var destination)) {
      error = $"{mnemonic} requires a dword GP destination other than ESP";
      return true;
    }

    switch (mnemonic) {
      case "RORX": {
        if (operands.Count != 3 || !BmiVirtualSource(operands[1], out var source)
            || operands[2] is not TextAssembler.ParsedAsmImmediate immediate) {
          error = "RORX expects r32, r/m32, imm8";
          return true;
        }
        this._asm.Pushf();
        this.StageDword(state, source, BmiA, target);
        this.BmiCopyDword(state, BmiA, BmiC);
        this.EmitRorx(state, unchecked((byte)immediate.Value) & 31);
        this.WriteDwordPlace(state, DwordPlace.Of(destination), BmiC, target);
        this._asm.Popf();
        return true;
      }

      case "MULX": {
        if (operands.Count != 3 || !BmiVirtualRegister(operands[1], out var lowDestination)
            || !BmiVirtualSource(operands[2], out var multiplier)) {
          error = "MULX expects high-r32, low-r32, r/m32";
          return true;
        }
        this._asm.Pushf();
        this.StageDword(state, new TextAssembler.ParsedAsmRegister(Reg.EDX), BmiA, target);
        this.StageDword(state, multiplier, BmiB, target);
        this.EmitMulx(state);
        // Intel specifies operand 1 as high and operand 2 as low. Write low first so identical
        // destinations retain the high half, exactly matching hardware alias semantics.
        this.WriteDwordPlace(state, DwordPlace.Of(lowDestination), BmiC, target);
        this.WriteDwordPlace(state, DwordPlace.Of(destination), BmiD, target);
        this._asm.Popf();
        return true;
      }

      case "PDEP" or "PEXT": {
        if (operands.Count != 3 || !BmiVirtualRegister(operands[1], out var sourceRegister)
            || !BmiVirtualSource(operands[2], out var mask)) {
          error = $"{mnemonic} expects r32, r32, r/m32";
          return true;
        }
        this._asm.Pushf();
        this.StageDword(state, new TextAssembler.ParsedAsmRegister(sourceRegister), BmiA, target);
        this.StageDword(state, mask, BmiB, target);
        if (mnemonic == "PDEP") this.EmitPdep(state); else this.EmitPext(state);
        this.WriteDwordPlace(state, DwordPlace.Of(destination), BmiC, target);
        this._asm.Popf();
        return true;
      }

      case "BZHI" or "SARX" or "SHLX" or "SHRX": {
        if (operands.Count != 3 || !BmiVirtualSource(operands[1], out var data)
            || !BmiVirtualRegister(operands[2], out var controlRegister)) {
          error = $"{mnemonic} expects r32, r/m32, r32";
          return true;
        }
        this._asm.Pushf();
        this.StageDword(state, data, BmiA, target);
        this.StageDword(state, new TextAssembler.ParsedAsmRegister(controlRegister), BmiB, target);
        switch (mnemonic) {
          case "BZHI": this.EmitBzhi(state); break;
          case "SARX" or "SHLX" or "SHRX": this.EmitBmiVariableShift(state, mnemonic); break;
        }
        this.WriteDwordPlace(state, DwordPlace.Of(destination), BmiC, target);
        this._asm.Popf();
        if (mnemonic == "BZHI")
          this.RestoreBzhiFlags(state);
        return true;
      }

      default:
        error = $"unsupported BMI2 instruction {mnemonic}";
        return true;
    }
  }

  private void EmitBzhi(VirtualIsaState state) {
    this._asm.Mov(this.GpScratch(state, BmiC), 0);
    this._asm.Mov(this.GpScratch(state, BmiC + 2), 0);
    this._asm.Push(Reg.CX);
    this._asm.Mov(Reg.CX, this.GpScratch(state, BmiB));
    this._asm.And(Reg.CX, 0x00FF);
    for (var bit = 0; bit < 32; ++bit) {
      var skip = this._asm.DefineLabel();
      this._asm.Cmp(Reg.CX, bit + 1);
      this._asm.J(Condition.Below, skip);
      var word = bit >> 4;
      var mask = 1 << (bit & 15);
      this._asm.Test(this.GpScratch(state, BmiA + word * 2), mask);
      var clear = this._asm.DefineLabel();
      this._asm.J(Condition.Equal, clear);
      this._asm.Or(this.GpScratch(state, BmiC + word * 2), mask);
      this._asm.MarkLabel(clear);
      this._asm.MarkLabel(skip);
    }
    this._asm.Pop(Reg.CX);
  }

  private void RestoreBzhiFlags(VirtualIsaState state) {
    this._asm.Push(Reg.AX);
    this._asm.Push(Reg.DX);
    this._asm.Pushf();
    this._asm.Pop(Reg.AX);
    this._asm.And(Reg.AX, 0xF73E); // clear defined CF/ZF/SF/OF; preserve undefined PF/AF.
    this._asm.Mov(Reg.DX, this.GpScratch(state, BmiC));
    this._asm.Or(Reg.DX, this.GpScratch(state, BmiC + 2));
    var nonzero = this._asm.DefineLabel();
    this._asm.J(Condition.NotEqual, nonzero);
    this._asm.Or(Reg.AX, 0x0040);
    this._asm.MarkLabel(nonzero);
    this._asm.Test(this.GpScratch(state, BmiC + 2), 0x8000);
    var nonnegative = this._asm.DefineLabel();
    this._asm.J(Condition.Equal, nonnegative);
    this._asm.Or(Reg.AX, 0x0080);
    this._asm.MarkLabel(nonnegative);
    this._asm.Mov(Reg.DX, this.GpScratch(state, BmiB));
    this._asm.And(Reg.DX, 0x00FF);
    this._asm.Cmp(Reg.DX, 32);
    var noCarry = this._asm.DefineLabel();
    this._asm.J(Condition.Below, noCarry);
    this._asm.Or(Reg.AX, 1);
    this._asm.MarkLabel(noCarry);
    this._asm.Push(Reg.AX);
    this._asm.Popf();
    this._asm.Pop(Reg.DX);
    this._asm.Pop(Reg.AX);
  }

  private void EmitPdep(VirtualIsaState state) {
    this._asm.Mov(this.GpScratch(state, BmiC), 0);
    this._asm.Mov(this.GpScratch(state, BmiC + 2), 0);
    this.BmiCopyDword(state, BmiA, BmiD);
    for (var bit = 0; bit < 32; ++bit) {
      var word = bit >> 4;
      var mask = 1 << (bit & 15);
      var skip = this._asm.DefineLabel();
      this._asm.Test(this.GpScratch(state, BmiB + word * 2), mask);
      this._asm.J(Condition.Equal, skip);
      var dataZero = this._asm.DefineLabel();
      this._asm.Test(this.GpScratch(state, BmiD), 1);
      this._asm.J(Condition.Equal, dataZero);
      this._asm.Or(this.GpScratch(state, BmiC + word * 2), mask);
      this._asm.MarkLabel(dataZero);
      this.BmiShiftRightOne(state, BmiD, arithmetic: false);
      this._asm.MarkLabel(skip);
    }
  }

  private void EmitPext(VirtualIsaState state) {
    this._asm.Mov(this.GpScratch(state, BmiC), 0);
    this._asm.Mov(this.GpScratch(state, BmiC + 2), 0);
    this._asm.Mov(this.GpScratch(state, BmiD), 1);
    this._asm.Mov(this.GpScratch(state, BmiD + 2), 0);
    for (var bit = 0; bit < 32; ++bit) {
      var word = bit >> 4;
      var mask = 1 << (bit & 15);
      var skip = this._asm.DefineLabel();
      this._asm.Test(this.GpScratch(state, BmiB + word * 2), mask);
      this._asm.J(Condition.Equal, skip);
      var dataZero = this._asm.DefineLabel();
      this._asm.Test(this.GpScratch(state, BmiA + word * 2), mask);
      this._asm.J(Condition.Equal, dataZero);
      this._asm.Push(Reg.AX);
      this._asm.Mov(Reg.AX, this.GpScratch(state, BmiD));
      this._asm.Or(this.GpScratch(state, BmiC), Reg.AX);
      this._asm.Mov(Reg.AX, this.GpScratch(state, BmiD + 2));
      this._asm.Or(this.GpScratch(state, BmiC + 2), Reg.AX);
      this._asm.Pop(Reg.AX);
      this._asm.MarkLabel(dataZero);
      this.BmiShiftLeftOne(state, BmiD);
      this._asm.MarkLabel(skip);
    }
  }

  private void EmitBmiVariableShift(VirtualIsaState state, string mnemonic) {
    this.BmiCopyDword(state, BmiA, BmiC);
    this._asm.Push(Reg.CX);
    this._asm.Mov(Reg.CX, this.GpScratch(state, BmiB));
    this._asm.And(Reg.CX, 31);
    var done = this._asm.DefineLabel();
    var loop = this._asm.DefineLabel();
    this._asm.Cmp(Reg.CX, 0);
    this._asm.J(Condition.Equal, done);
    this._asm.MarkLabel(loop);
    if (mnemonic == "SHLX")
      this.BmiShiftLeftOne(state, BmiC);
    else
      this.BmiShiftRightOne(state, BmiC, arithmetic: mnemonic == "SARX");
    this._asm.Loop(loop);
    this._asm.MarkLabel(done);
    this._asm.Pop(Reg.CX);
  }

  private void EmitRorx(VirtualIsaState state, int count) {
    for (var i = 0; i < count; ++i) {
      this._asm.Shr(this.GpScratch(state, BmiC + 2), 1);
      this._asm.Rcr(this.GpScratch(state, BmiC), 1);
      var noWrap = this._asm.DefineLabel();
      this._asm.J(Condition.AboveOrEqual, noWrap);
      this._asm.Or(this.GpScratch(state, BmiC + 2), 0x8000);
      this._asm.MarkLabel(noWrap);
    }
  }

  private void EmitMulx(VirtualIsaState state) {
    // Four 16x16 products; C=low32, D=high32. No 32-bit hardware is required.
    this._asm.Push(Reg.AX);
    this._asm.Push(Reg.BX);
    this._asm.Push(Reg.DX);
    this._asm.Mov(Reg.AX, this.GpScratch(state, BmiA));
    this._asm.Mul(this.GpScratch(state, BmiB));
    this._asm.Mov(this.GpScratch(state, BmiC), Reg.AX);
    this._asm.Mov(this.GpScratch(state, 112), Reg.DX);
    this._asm.Mov(Reg.AX, this.GpScratch(state, BmiA));
    this._asm.Mul(this.GpScratch(state, BmiB + 2));
    this._asm.Mov(this.GpScratch(state, 114), Reg.AX);
    this._asm.Mov(this.GpScratch(state, 116), Reg.DX);
    this._asm.Mov(Reg.AX, this.GpScratch(state, BmiA + 2));
    this._asm.Mul(this.GpScratch(state, BmiB));
    this._asm.Mov(this.GpScratch(state, 118), Reg.AX);
    this._asm.Mov(this.GpScratch(state, 120), Reg.DX);
    this._asm.Mov(Reg.AX, this.GpScratch(state, BmiA + 2));
    this._asm.Mul(this.GpScratch(state, BmiB + 2));
    this._asm.Mov(this.GpScratch(state, 122), Reg.AX);
    this._asm.Mov(this.GpScratch(state, BmiFlags), Reg.DX);

    this._asm.Mov(Reg.AX, this.GpScratch(state, 112));
    this._asm.Xor(Reg.BX, Reg.BX);
    this._asm.Add(Reg.AX, this.GpScratch(state, 114));
    var carry1 = this._asm.DefineLabel();
    this._asm.J(Condition.AboveOrEqual, carry1);
    this._asm.Inc(Reg.BX);
    this._asm.MarkLabel(carry1);
    this._asm.Add(Reg.AX, this.GpScratch(state, 118));
    var carry2 = this._asm.DefineLabel();
    this._asm.J(Condition.AboveOrEqual, carry2);
    this._asm.Inc(Reg.BX);
    this._asm.MarkLabel(carry2);
    this._asm.Mov(this.GpScratch(state, BmiC + 2), Reg.AX);

    this._asm.Mov(Reg.AX, this.GpScratch(state, 116));
    this._asm.Xor(Reg.DX, Reg.DX);
    this._asm.Add(Reg.AX, this.GpScratch(state, 120));
    var carry3 = this._asm.DefineLabel();
    this._asm.J(Condition.AboveOrEqual, carry3);
    this._asm.Inc(Reg.DX);
    this._asm.MarkLabel(carry3);
    this._asm.Add(Reg.AX, this.GpScratch(state, 122));
    var carry4 = this._asm.DefineLabel();
    this._asm.J(Condition.AboveOrEqual, carry4);
    this._asm.Inc(Reg.DX);
    this._asm.MarkLabel(carry4);
    this._asm.Add(Reg.AX, Reg.BX);
    var carry5 = this._asm.DefineLabel();
    this._asm.J(Condition.AboveOrEqual, carry5);
    this._asm.Inc(Reg.DX);
    this._asm.MarkLabel(carry5);
    this._asm.Mov(this.GpScratch(state, BmiD), Reg.AX);
    this._asm.Mov(Reg.AX, this.GpScratch(state, BmiFlags));
    this._asm.Add(Reg.AX, Reg.DX);
    this._asm.Mov(this.GpScratch(state, BmiD + 2), Reg.AX);
    this._asm.Pop(Reg.DX);
    this._asm.Pop(Reg.BX);
    this._asm.Pop(Reg.AX);
  }

  private void BmiCopyDword(VirtualIsaState state, int source, int destination) {
    this._asm.Push(Reg.AX);
    this._asm.Mov(Reg.AX, this.GpScratch(state, source));
    this._asm.Mov(this.GpScratch(state, destination), Reg.AX);
    this._asm.Mov(Reg.AX, this.GpScratch(state, source + 2));
    this._asm.Mov(this.GpScratch(state, destination + 2), Reg.AX);
    this._asm.Pop(Reg.AX);
  }

  private void BmiShiftLeftOne(VirtualIsaState state, int offset) {
    this._asm.Shl(this.GpScratch(state, offset), 1);
    this._asm.Rcl(this.GpScratch(state, offset + 2), 1);
  }

  private void BmiShiftRightOne(VirtualIsaState state, int offset, bool arithmetic) {
    if (arithmetic)
      this._asm.Sar(this.GpScratch(state, offset + 2), 1);
    else
      this._asm.Shr(this.GpScratch(state, offset + 2), 1);
    this._asm.Rcr(this.GpScratch(state, offset), 1);
  }
}
