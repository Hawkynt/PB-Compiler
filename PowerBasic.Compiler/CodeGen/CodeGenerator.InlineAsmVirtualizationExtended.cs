using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Runtime;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {
  /// <summary>
  /// Scalar 8086 lowerings for SSSE3/SSE4 packed-integer instructions that are not covered by the
  /// legacy packed-SIMD emulator. The architectural vector operands are staged through the virtual
  /// ISA scratch area before any destination lane is overwritten, so source/destination aliasing has
  /// exactly the same snapshot semantics as the hardware instruction.
  /// </summary>
  private bool TryEmitVirtualExtendedVectorInstruction(InlineInstruction instruction, InlineAsmResolver resolver, RuntimeTarget target, out string? error) {
    error = null;
    if (!IsExtendedVectorEmulationSupported(instruction.Mnemonic))
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
      return this.EmitVirtualExtendedVectorCore(state, instruction.Mnemonic, operands, out error);
    } finally {
      this._asm.Pop(Reg.DX);
      this._asm.Pop(Reg.CX);
      this._asm.Pop(Reg.AX);
      this._asm.Popf();
    }
  }

  private static bool IsExtendedVectorEmulationSupported(string mnemonic) => mnemonic is
    "PABSB" or "PABSW" or "PABSD" or "PSHUFB" or "PSIGNB" or "PSIGNW" or "PSIGND" or "PALIGNR" or
    "PBLENDW" or "PMULLD" or "PMINSB" or "PMAXSB" or "PMINUW" or "PMAXUW" or "PMINUD" or "PMAXUD" or
    "PCMPEQQ" or "PACKUSDW" or "PHMINPOSUW" or "PCMPGTQ";

  private bool EmitVirtualExtendedVectorCore(
    VirtualIsaState state,
    string mnemonic,
    IReadOnlyList<TextAssembler.ParsedAsmOperand> operands,
    out string? error) {
    error = null;

    if (mnemonic == "PALIGNR")
      return this.EmitVirtualPalignr(state, operands, out error);
    if (mnemonic == "PBLENDW")
      return this.EmitVirtualPblendw(state, operands, out error);

    if (!TryExtendedBinaryOperands(state, mnemonic, operands, out var destination, out var source, out var width, out error))
      return true;

    var d = VirtualOperand.Of(destination);
    var a = VirtualOperand.Of(Mem.At(state.Scratch, 0).Cs());
    var b = VirtualOperand.Of(Mem.At(state.Scratch, 64).Cs());
    this.CopyToScratch(state, d, 0, width);
    this.CopyToScratch(state, source, 64, width);

    switch (mnemonic) {
      case "PABSB": this.EmitVirtualAbs(state, d, b, width, 1); break;
      case "PABSW": this.EmitVirtualAbs(state, d, b, width, 2); break;
      case "PABSD": this.EmitVirtualAbs(state, d, b, width, 4); break;
      case "PSHUFB": this.EmitVirtualPshufb(state, d, a, b, width); break;
      case "PSIGNB": this.EmitVirtualSign(state, d, a, b, width, 1); break;
      case "PSIGNW": this.EmitVirtualSign(state, d, a, b, width, 2); break;
      case "PSIGND": this.EmitVirtualSign(state, d, a, b, width, 4); break;
      case "PMULLD": this.EmitVirtualPmulld(state, d, a, b, width); break;
      case "PMINSB": this.EmitVirtualMinMax(state, d, a, b, width, 1, signed: true, wantMax: false); break;
      case "PMAXSB": this.EmitVirtualMinMax(state, d, a, b, width, 1, signed: true, wantMax: true); break;
      case "PMINUW": this.EmitVirtualMinMax(state, d, a, b, width, 2, signed: false, wantMax: false); break;
      case "PMAXUW": this.EmitVirtualMinMax(state, d, a, b, width, 2, signed: false, wantMax: true); break;
      case "PMINUD": this.EmitVirtualMinMax(state, d, a, b, width, 4, signed: false, wantMax: false); break;
      case "PMAXUD": this.EmitVirtualMinMax(state, d, a, b, width, 4, signed: false, wantMax: true); break;
      case "PCMPEQQ": this.EmitVirtualCompareQword(state, d, a, b, width, greater: false); break;
      case "PCMPGTQ": this.EmitVirtualCompareQword(state, d, a, b, width, greater: true); break;
      case "PACKUSDW": this.EmitVirtualPackUnsignedDwords(state, d, a, b, width); break;
      case "PHMINPOSUW": this.EmitVirtualPhminposuw(state, d, b); break;
      default:
        error = $"extended packed-SIMD emulator has no {mnemonic} lowering";
        break;
    }
    return true;
  }

  private static bool TryExtendedBinaryOperands(
    VirtualIsaState state,
    string mnemonic,
    IReadOnlyList<TextAssembler.ParsedAsmOperand> operands,
    out Reg destination,
    out VirtualOperand source,
    out int width,
    out string? error) {
    destination = default;
    source = default;
    width = 0;
    error = null;
    if (operands.Count != 2 || operands[0] is not TextAssembler.ParsedAsmRegister d || !IsVirtualVector(d.Register)
        || !TryVectorOperand(operands[1], out source)) {
      error = $"{mnemonic} expects vector destination and vector-register/memory source";
      return false;
    }

    destination = d.Register;
    width = VectorWidth(destination);
    var ssse3AllowsMmx = IsSsse3(mnemonic);
    if (destination.IsMmx() && !ssse3AllowsMmx) {
      error = $"{mnemonic} requires an XMM destination";
      return false;
    }
    if (source.Register is { } sr && VectorWidth(sr) != width) {
      error = $"{mnemonic} operand widths differ";
      return false;
    }
    if (source.Register is { } smr && smr.IsMmx() != destination.IsMmx()) {
      error = $"{mnemonic} operand register classes differ";
      return false;
    }
    return true;
  }

  private void EmitVirtualAbs(VirtualIsaState state, VirtualOperand destination, VirtualOperand source, int width, int laneBytes) {
    for (var lane = 0; lane < width; lane += laneBytes) {
      var nonNegative = this._asm.DefineLabel();
      var done = this._asm.DefineLabel();
      if (laneBytes == 1) {
        this.LoadByte(state, Reg.AL, source, lane);
        this._asm.Test(Reg.AL, Reg.AL);
        this._asm.J(Condition.NotSign, nonNegative);
        this._asm.Neg(Reg.AL);
        this._asm.MarkLabel(nonNegative);
        this.StoreByte(state, destination, lane, Reg.AL);
        continue;
      }
      if (laneBytes == 2) {
        this.LoadWord(state, Reg.AX, source, lane);
        this._asm.Test(Reg.AX, Reg.AX);
        this._asm.J(Condition.NotSign, nonNegative);
        this._asm.Neg(Reg.AX);
        this._asm.MarkLabel(nonNegative);
        this.StoreWord(state, destination, lane, Reg.AX);
        continue;
      }

      this.LoadWord(state, Reg.AX, source, lane + 2);
      this._asm.Test(Reg.AX, Reg.AX);
      this._asm.J(Condition.NotSign, nonNegative);
      this.LoadWord(state, Reg.AX, source, lane);
      this._asm.Not(Reg.AX);
      this._asm.Add(Reg.AX, 1); // carry is exactly the carry into the high half of two's-complement negation
      this.StoreWord(state, destination, lane, Reg.AX);
      this.LoadWord(state, Reg.AX, source, lane + 2); // MOV preserves the carry from the low-half add
      this._asm.Not(Reg.AX);
      this._asm.Adc(Reg.AX, 0);
      this.StoreWord(state, destination, lane + 2, Reg.AX);
      this._asm.Jmp(done);
      this._asm.MarkLabel(nonNegative);
      this.LoadWord(state, Reg.AX, source, lane);
      this.StoreWord(state, destination, lane, Reg.AX);
      this.LoadWord(state, Reg.AX, source, lane + 2);
      this.StoreWord(state, destination, lane + 2, Reg.AX);
      this._asm.MarkLabel(done);
    }
  }

  private void EmitVirtualSign(VirtualIsaState state, VirtualOperand destination, VirtualOperand value, VirtualOperand sign, int width, int laneBytes) {
    for (var lane = 0; lane < width; lane += laneBytes) {
      var zero = this._asm.DefineLabel();
      var positive = this._asm.DefineLabel();
      var done = this._asm.DefineLabel();
      if (laneBytes == 1) {
        this.LoadByte(state, Reg.AL, sign, lane);
        this._asm.Test(Reg.AL, Reg.AL);
        this._asm.J(Condition.Equal, zero);
        this._asm.J(Condition.NotSign, positive);
        this.LoadByte(state, Reg.AL, value, lane);
        this._asm.Neg(Reg.AL);
        this.StoreByte(state, destination, lane, Reg.AL);
        this._asm.Jmp(done);
        this._asm.MarkLabel(positive);
        this.LoadByte(state, Reg.AL, value, lane);
        this.StoreByte(state, destination, lane, Reg.AL);
        this._asm.Jmp(done);
        this._asm.MarkLabel(zero);
        this._asm.Mov(Reg.AL, 0);
        this.StoreByte(state, destination, lane, Reg.AL);
        this._asm.MarkLabel(done);
        continue;
      }

      var signOffset = lane + laneBytes - 2;
      this.LoadWord(state, Reg.AX, sign, signOffset);
      if (laneBytes == 4) {
        var signNonzero = this._asm.DefineLabel();
        this._asm.Test(Reg.AX, Reg.AX);
        this._asm.J(Condition.NotEqual, signNonzero);
        this.LoadWord(state, Reg.AX, sign, lane);
        this._asm.Test(Reg.AX, Reg.AX);
        this._asm.J(Condition.Equal, zero);
        this._asm.Jmp(positive);
        this._asm.MarkLabel(signNonzero);
      } else {
        this._asm.Test(Reg.AX, Reg.AX);
        this._asm.J(Condition.Equal, zero);
      }
      this._asm.J(Condition.NotSign, positive);

      if (laneBytes == 2) {
        this.LoadWord(state, Reg.AX, value, lane);
        this._asm.Neg(Reg.AX);
        this.StoreWord(state, destination, lane, Reg.AX);
      } else {
        this.LoadWord(state, Reg.AX, value, lane);
        this._asm.Not(Reg.AX);
        this._asm.Add(Reg.AX, 1);
        this.StoreWord(state, destination, lane, Reg.AX);
        this.LoadWord(state, Reg.AX, value, lane + 2);
        this._asm.Not(Reg.AX);
        this._asm.Adc(Reg.AX, 0);
        this.StoreWord(state, destination, lane + 2, Reg.AX);
      }
      this._asm.Jmp(done);

      this._asm.MarkLabel(positive);
      for (var offset = 0; offset < laneBytes; offset += 2) {
        this.LoadWord(state, Reg.AX, value, lane + offset);
        this.StoreWord(state, destination, lane + offset, Reg.AX);
      }
      this._asm.Jmp(done);

      this._asm.MarkLabel(zero);
      for (var offset = 0; offset < laneBytes; offset += 2)
        this._asm.Mov(OperandCell(destination, lane + offset, OperandSize.Word, (r, p, s) => this.VirtualCell(state, r, p, s)), 0);
      this._asm.MarkLabel(done);
    }
  }

  private void EmitVirtualPshufb(VirtualIsaState state, VirtualOperand destination, VirtualOperand value, VirtualOperand mask, int width) {
    var indexMask = width == 8 ? 7 : 15;
    for (var lane = 0; lane < width; ++lane) {
      this.LoadByte(state, Reg.DL, mask, lane);
      var zero = this._asm.DefineLabel();
      var done = this._asm.DefineLabel();
      this._asm.Test(Reg.DL, (Imm)0x80);
      this._asm.J(Condition.NotEqual, zero);

      // The source index is dynamic. Select it with a compact compare chain; the values have already
      // been snapshotted in scratch, so writing the destination cannot affect subsequent selections.
      this._asm.And(Reg.DL, indexMask);
      for (var sourceIndex = 0; sourceIndex < width; ++sourceIndex) {
        var next = this._asm.DefineLabel();
        this._asm.Cmp(Reg.DL, sourceIndex);
        this._asm.J(Condition.NotEqual, next);
        this.LoadByte(state, Reg.AL, value, sourceIndex);
        this.StoreByte(state, destination, lane, Reg.AL);
        this._asm.Jmp(done);
        this._asm.MarkLabel(next);
      }

      this._asm.MarkLabel(zero);
      this._asm.Mov(Reg.AL, 0);
      this.StoreByte(state, destination, lane, Reg.AL);
      this._asm.MarkLabel(done);
    }
  }

  private void EmitVirtualPmulld(VirtualIsaState state, VirtualOperand destination, VirtualOperand a, VirtualOperand b, int width) {
    // low32(a*b) = aLo*bLo + ((aLo*bHi + aHi*bLo) << 16); the high*high term is outside the result.
    for (var lane = 0; lane < width; lane += 4) {
      this.LoadWord(state, Reg.AX, a, lane);
      this.LoadWord(state, Reg.CX, b, lane);
      this._asm.Mul(Reg.CX);                    // DX:AX = aLo*bLo
      this.StoreWord(state, destination, lane, Reg.AX);
      this._asm.Mov(Reg.CX, Reg.DX);            // CX = high(aLo*bLo)

      this.LoadWord(state, Reg.AX, a, lane);
      this.LoadWord(state, Reg.DX, b, lane + 2);
      this._asm.Mul(Reg.DX);
      this._asm.Add(Reg.CX, Reg.AX);

      this.LoadWord(state, Reg.AX, a, lane + 2);
      this.LoadWord(state, Reg.DX, b, lane);
      this._asm.Mul(Reg.DX);
      this._asm.Add(Reg.CX, Reg.AX);
      this.StoreWord(state, destination, lane + 2, Reg.CX);
    }
  }

  private void EmitVirtualMinMax(
    VirtualIsaState state,
    VirtualOperand destination,
    VirtualOperand a,
    VirtualOperand b,
    int width,
    int laneBytes,
    bool signed,
    bool wantMax) {
    for (var lane = 0; lane < width; lane += laneBytes) {
      var takeA = this._asm.DefineLabel();
      var takeB = this._asm.DefineLabel();
      var done = this._asm.DefineLabel();

      if (laneBytes == 1) {
        this.LoadByte(state, Reg.AL, a, lane);
        this.LoadByte(state, Reg.DL, b, lane);
        this._asm.Cmp(Reg.AL, Reg.DL);
        this._asm.J(wantMax ? Condition.GreaterOrEqual : Condition.LessOrEqual, takeA);
        this._asm.Jmp(takeB);
      } else if (laneBytes == 2) {
        this.LoadWord(state, Reg.AX, a, lane);
        this.LoadWord(state, Reg.DX, b, lane);
        this._asm.Cmp(Reg.AX, Reg.DX);
        var keep = wantMax
          ? (signed ? Condition.GreaterOrEqual : Condition.AboveOrEqual)
          : (signed ? Condition.LessOrEqual : Condition.BelowOrEqual);
        this._asm.J(keep, takeA);
        this._asm.Jmp(takeB);
      } else {
        this.LoadWord(state, Reg.AX, a, lane + 2);
        this.LoadWord(state, Reg.DX, b, lane + 2);
        this._asm.Cmp(Reg.AX, Reg.DX);
        if (wantMax) {
          this._asm.J(Condition.Above, takeA);
          this._asm.J(Condition.Below, takeB);
        } else {
          this._asm.J(Condition.Below, takeA);
          this._asm.J(Condition.Above, takeB);
        }
        this.LoadWord(state, Reg.AX, a, lane);
        this.LoadWord(state, Reg.DX, b, lane);
        this._asm.Cmp(Reg.AX, Reg.DX);
        this._asm.J(wantMax ? Condition.AboveOrEqual : Condition.BelowOrEqual, takeA);
        this._asm.Jmp(takeB);
      }

      this._asm.MarkLabel(takeA);
      for (var offset = 0; offset < laneBytes; offset += 2) {
        if (laneBytes == 1) {
          this.LoadByte(state, Reg.AL, a, lane);
          this.StoreByte(state, destination, lane, Reg.AL);
        } else {
          this.LoadWord(state, Reg.AX, a, lane + offset);
          this.StoreWord(state, destination, lane + offset, Reg.AX);
        }
      }
      this._asm.Jmp(done);

      this._asm.MarkLabel(takeB);
      for (var offset = 0; offset < laneBytes; offset += 2) {
        if (laneBytes == 1) {
          this.LoadByte(state, Reg.AL, b, lane);
          this.StoreByte(state, destination, lane, Reg.AL);
        } else {
          this.LoadWord(state, Reg.AX, b, lane + offset);
          this.StoreWord(state, destination, lane + offset, Reg.AX);
        }
      }
      this._asm.MarkLabel(done);
    }
  }

  private void EmitVirtualCompareQword(VirtualIsaState state, VirtualOperand destination, VirtualOperand a, VirtualOperand b, int width, bool greater) {
    for (var lane = 0; lane < width; lane += 8) {
      var yes = this._asm.DefineLabel();
      var no = this._asm.DefineLabel();
      var done = this._asm.DefineLabel();

      if (!greater) {
        for (var offset = 0; offset < 8; offset += 2) {
          this.LoadWord(state, Reg.AX, a, lane + offset);
          this.LoadWord(state, Reg.DX, b, lane + offset);
          this._asm.Cmp(Reg.AX, Reg.DX);
          this._asm.J(Condition.NotEqual, no);
        }
        this._asm.Jmp(yes);
      } else {
        this.LoadWord(state, Reg.AX, a, lane + 6);
        this.LoadWord(state, Reg.DX, b, lane + 6);
        this._asm.Cmp(Reg.AX, Reg.DX);
        this._asm.J(Condition.Greater, yes);
        this._asm.J(Condition.Less, no);
        for (var offset = 4; offset >= 0; offset -= 2) {
          this.LoadWord(state, Reg.AX, a, lane + offset);
          this.LoadWord(state, Reg.DX, b, lane + offset);
          this._asm.Cmp(Reg.AX, Reg.DX);
          this._asm.J(Condition.Above, yes);
          this._asm.J(Condition.Below, no);
        }
        this._asm.Jmp(no);
      }

      this._asm.MarkLabel(no);
      for (var offset = 0; offset < 8; offset += 2)
        this._asm.Mov(OperandCell(destination, lane + offset, OperandSize.Word, (r, p, s) => this.VirtualCell(state, r, p, s)), 0);
      this._asm.Jmp(done);
      this._asm.MarkLabel(yes);
      for (var offset = 0; offset < 8; offset += 2)
        this._asm.Mov(OperandCell(destination, lane + offset, OperandSize.Word, (r, p, s) => this.VirtualCell(state, r, p, s)), -1);
      this._asm.MarkLabel(done);
    }
  }

  private void EmitVirtualPackUnsignedDwords(VirtualIsaState state, VirtualOperand destination, VirtualOperand a, VirtualOperand b, int width) {
    // PACKUSDW packs signed dwords from destination followed by source to unsigned words [0,65535].
    var output = 0;
    for (var half = 0; half < 2; ++half) {
      var input = half == 0 ? a : b;
      for (var lane = 0; lane < width; lane += 4, output += 2) {
        var zero = this._asm.DefineLabel();
        var max = this._asm.DefineLabel();
        var store = this._asm.DefineLabel();
        this.LoadWord(state, Reg.DX, input, lane + 2);
        this._asm.Test(Reg.DX, Reg.DX);
        this._asm.J(Condition.Sign, zero);
        this._asm.Cmp(Reg.DX, 0);
        this._asm.J(Condition.NotEqual, max); // any positive high word is > 65535
        this.LoadWord(state, Reg.AX, input, lane);
        this._asm.Jmp(store);
        this._asm.MarkLabel(zero);
        this._asm.Xor(Reg.AX, Reg.AX);
        this._asm.Jmp(store);
        this._asm.MarkLabel(max);
        this._asm.Mov(Reg.AX, -1);
        this._asm.MarkLabel(store);
        this.StoreWord(state, destination, output, Reg.AX);
      }
    }
  }

  private void EmitVirtualPhminposuw(VirtualIsaState state, VirtualOperand destination, VirtualOperand source) {
    // SSE4.1 PHMINPOSUW is XMM-only: word0=min, word1=index, words2..7=0.
    this.LoadWord(state, Reg.AX, source, 0); // AX = current min
    this._asm.Xor(Reg.CX, Reg.CX);          // CX = current index
    for (var lane = 1; lane < 8; ++lane) {
      this.LoadWord(state, Reg.DX, source, lane * 2);
      this._asm.Cmp(Reg.DX, Reg.AX);
      var keep = this._asm.DefineLabel();
      this._asm.J(Condition.AboveOrEqual, keep); // ties keep the lowest index
      this._asm.Mov(Reg.AX, Reg.DX);
      this._asm.Mov(Reg.CX, lane);
      this._asm.MarkLabel(keep);
    }
    this.StoreWord(state, destination, 0, Reg.AX);
    this.StoreWord(state, destination, 2, Reg.CX);
    for (var offset = 4; offset < 16; offset += 2)
      this._asm.Mov(OperandCell(destination, offset, OperandSize.Word, (r, p, s) => this.VirtualCell(state, r, p, s)), 0);
  }

  private bool EmitVirtualPblendw(VirtualIsaState state, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    error = null;
    if (operands.Count != 3 || operands[0] is not TextAssembler.ParsedAsmRegister d || !d.Register.IsXmm()
        || !TryVectorOperand(operands[1], out var source) || operands[2] is not TextAssembler.ParsedAsmImmediate immediate) {
      error = "PBLENDW expects XMM destination, XMM/m128 source, imm8";
      return true;
    }
    if (source.Register is { } sr && !sr.IsXmm()) { error = "PBLENDW source register must be XMM"; return true; }

    var destination = VirtualOperand.Of(d.Register);
    this.CopyToScratch(state, destination, 0, 16);
    this.CopyToScratch(state, source, 64, 16); // preserve the full architectural memory read even for a zero mask
    var old = VirtualOperand.Of(Mem.At(state.Scratch, 0).Cs());
    var stagedSource = VirtualOperand.Of(Mem.At(state.Scratch, 64).Cs());
    var mask = unchecked((byte)immediate.Value);
    for (var lane = 0; lane < 8; ++lane) {
      var selected = (mask & (1 << lane)) != 0 ? stagedSource : old;
      this.LoadWord(state, Reg.AX, selected, lane * 2);
      this.StoreWord(state, destination, lane * 2, Reg.AX);
    }
    return true;
  }

  private bool EmitVirtualPalignr(VirtualIsaState state, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    error = null;
    if (operands.Count != 3 || operands[0] is not TextAssembler.ParsedAsmRegister d || !(d.Register.IsMmx() || d.Register.IsXmm())
        || !TryVectorOperand(operands[1], out var source) || operands[2] is not TextAssembler.ParsedAsmImmediate immediate) {
      error = "PALIGNR expects MMX/XMM destination, matching register/memory source, imm8";
      return true;
    }
    var width = VectorWidth(d.Register);
    if (source.Register is { } sr && (VectorWidth(sr) != width || sr.IsMmx() != d.Register.IsMmx())) {
      error = "PALIGNR operand register classes/widths differ";
      return true;
    }

    var destination = VirtualOperand.Of(d.Register);
    this.CopyToScratch(state, destination, 0, width);
    this.CopyToScratch(state, source, 64, width);
    var old = VirtualOperand.Of(Mem.At(state.Scratch, 0).Cs());
    var stagedSource = VirtualOperand.Of(Mem.At(state.Scratch, 64).Cs());
    var shift = unchecked((byte)immediate.Value);
    for (var output = 0; output < width; ++output) {
      var index = output + shift;
      if (index >= 2 * width) {
        this._asm.Mov(Reg.AL, 0);
      } else if (index < width) {
        this.LoadByte(state, Reg.AL, stagedSource, index);
      } else {
        this.LoadByte(state, Reg.AL, old, index - width);
      }
      this.StoreByte(state, destination, output, Reg.AL);
    }
    return true;
  }
}
