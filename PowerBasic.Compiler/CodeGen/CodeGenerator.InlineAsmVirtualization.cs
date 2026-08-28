using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Runtime;
using PowerBasic.Compiler.Semantics;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {
  private sealed record VirtualIsaState(Label Vector, Label Mmx, Label GpHigh, Label Scratch);
  private VirtualIsaState? _mainVirtualIsaState;
  private readonly Dictionary<ProcedureSymbol, VirtualIsaState> _procVirtualIsaStates = new(ReferenceEqualityComparer.Instance);

  private readonly record struct VirtualOperand(Reg? Register, Mem? Memory) {
    public static VirtualOperand Of(Reg register) => new(register, null);
    public static VirtualOperand Of(Mem memory) => new(null, memory);
  }

  /// <summary>
  /// Lowers the packed-integer inline-assembler surface onto plain 8086 instructions. Architectural
  /// vector state is represented by compiler-owned static cells scoped to the generated procedure:
  /// XMM/YMM/ZMM n alias the low 16/32/64 bytes of the same 64-byte slot, MMX has its own 8-byte
  /// slots, and pre-386 EAX..EDI use the real 16-bit low half plus a virtual high-word bank.
  /// Integer flags and all scalar temporaries used by the lowering are restored around SIMD ops.
  /// </summary>
  private bool TryEmitVirtualInstruction(InlineInstruction instruction, InlineAsmResolver resolver, RuntimeTarget target, out string? error) {
    error = null;
    if (IsX87InlineMnemonic(instruction.Mnemonic) || !IsVectorMnemonic(instruction.Mnemonic))
      return false;

    this._textAssembler ??= new(this._asm);
    if (!this._textAssembler.TryParseOperands(instruction.Operands, resolver, out var operands, out error))
      return true;

    var state = this.EnsureVirtualIsaState();
    if (instruction.Mnemonic == "MOVD")
      return this.EmitVirtualMovd(state, operands, target, out error);

    this._asm.Pushf();
    this._asm.Push(Reg.AX);
    this._asm.Push(Reg.CX);
    this._asm.Push(Reg.DX);
    try {
      return this.EmitVirtualVectorCore(state, instruction.Mnemonic, operands, out error);
    } finally {
      this._asm.Pop(Reg.DX);
      this._asm.Pop(Reg.CX);
      this._asm.Pop(Reg.AX);
      this._asm.Popf();
    }
  }

  private VirtualIsaState EnsureVirtualIsaState() {
    if (this._currentProc is { } proc && this._procVirtualIsaStates.TryGetValue(proc, out var existingProc))
      return existingProc;
    if (this._currentProc is null && this._mainVirtualIsaState is { } existingMain)
      return existingMain;

    var state = new VirtualIsaState(this._asm.DefineLabel(), this._asm.DefineLabel(), this._asm.DefineLabel(), this._asm.DefineLabel());
    var over = this._asm.DefineLabel();
    this._asm.Jmp(over);
    this._asm.Align(2);
    this._asm.MarkLabel(state.Vector);
    this._asm.Db(new byte[RuntimeIsaState.VectorBankBytes]);
    this._asm.MarkLabel(state.Mmx);
    this._asm.Db(new byte[RuntimeIsaState.MmxBankBytes]);
    this._asm.MarkLabel(state.GpHigh);
    this._asm.Db(new byte[RuntimeIsaState.GpHighBankBytes]);
    this._asm.MarkLabel(state.Scratch);
    this._asm.Db(new byte[RuntimeIsaState.ScratchBytes]);
    this._asm.MarkLabel(over);

    if (this._currentProc is { } owner)
      this._procVirtualIsaStates[owner] = state;
    else
      this._mainVirtualIsaState = state;
    return state;
  }

  private bool EmitVirtualVectorCore(VirtualIsaState state, string mnemonic, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    error = null;
    switch (mnemonic) {
      case "EMMS":
        if (operands.Count != 0) error = "EMMS takes no operands";
        return true;
      case "MOVQ": return this.EmitVirtualMovq(state, operands, out error);
      case "MOVDQA" or "MOVDQU": return this.EmitVirtualMove(state, operands, vex: false, out error);
      case "VMOVDQA" or "VMOVDQU": return this.EmitVirtualMove(state, operands, vex: true, out error);
      case "PSLLW" or "PSLLD" or "PSLLQ" or "PSRLW" or "PSRLD" or "PSRLQ" or "PSRAW" or "PSRAD":
        return this.EmitVirtualShift(state, mnemonic, operands, out error);
      case "PACKSSWB" or "PACKSSDW" or "PACKUSWB":
        return this.EmitVirtualPack(state, mnemonic, operands, out error);
      case "PUNPCKLBW" or "PUNPCKLWD" or "PUNPCKLDQ" or "PUNPCKHBW" or "PUNPCKHWD" or "PUNPCKHDQ":
        return this.EmitVirtualUnpack(state, mnemonic, operands, out error);
    }

    if (mnemonic.StartsWith('V'))
      return this.EmitVirtualPackedBinary(state, mnemonic[1..], operands, vex: true, out error);
    return this.EmitVirtualPackedBinary(state, mnemonic, operands, vex: false, out error);
  }

  #region operand/cell helpers

  private static bool IsVirtualVector(Reg r) => r.IsMmx() || r.IsXmm() || r.IsYmm() || r.IsZmm();
  private static int VectorWidth(Reg r) => r.IsMmx() ? 8 : r.IsXmm() ? 16 : r.IsYmm() ? 32 : r.IsZmm() ? 64 : 0;
  private static Reg WordGp(Reg r) => r switch {
    Reg.EAX => Reg.AX, Reg.ECX => Reg.CX, Reg.EDX => Reg.DX, Reg.EBX => Reg.BX,
    Reg.ESP => Reg.SP, Reg.EBP => Reg.BP, Reg.ESI => Reg.SI, Reg.EDI => Reg.DI,
    _ => r,
  };

  private Mem VirtualCell(VirtualIsaState state, Reg r, int offset, OperandSize size) {
    var (label, baseOffset) = r.IsMmx()
      ? (state.Mmx, r.Index() * RuntimeIsaState.MmxRegisterBytes)
      : (state.Vector, r.Index() * RuntimeIsaState.VectorRegisterBytes);
    return Mem.At(label, baseOffset + offset).WithSize(size).Cs();
  }

  private Mem GpHighCell(VirtualIsaState state, Reg r) =>
    Mem.Word(state.GpHigh, RuntimeIsaState.GpHighOffset(r)).Cs();

  private static Mem OperandCell(VirtualOperand operand, int offset, OperandSize size, Func<Reg, int, OperandSize, Mem> virtualCell) =>
    operand.Register is { } r ? virtualCell(r, offset, size) : operand.Memory!.Value.Offset(offset).WithSize(size);

  private static bool TryVectorOperand(TextAssembler.ParsedAsmOperand operand, out VirtualOperand result) {
    switch (operand) {
      case TextAssembler.ParsedAsmRegister r when IsVirtualVector(r.Register): result = VirtualOperand.Of(r.Register); return true;
      case TextAssembler.ParsedAsmMemory m: result = VirtualOperand.Of(m.Memory); return true;
      default: result = default; return false;
    }
  }

  private void LoadByte(VirtualIsaState state, Reg target, VirtualOperand source, int offset) =>
    this._asm.Mov(target, OperandCell(source, offset, OperandSize.Byte, (r, o, s) => this.VirtualCell(state, r, o, s)));
  private void LoadWord(VirtualIsaState state, Reg target, VirtualOperand source, int offset) =>
    this._asm.Mov(target, OperandCell(source, offset, OperandSize.Word, (r, o, s) => this.VirtualCell(state, r, o, s)));
  private void StoreByte(VirtualIsaState state, VirtualOperand destination, int offset, Reg source) =>
    this._asm.Mov(OperandCell(destination, offset, OperandSize.Byte, (r, o, s) => this.VirtualCell(state, r, o, s)), source);
  private void StoreWord(VirtualIsaState state, VirtualOperand destination, int offset, Reg source) =>
    this._asm.Mov(OperandCell(destination, offset, OperandSize.Word, (r, o, s) => this.VirtualCell(state, r, o, s)), source);

  private void CopyBytes(VirtualIsaState state, VirtualOperand source, VirtualOperand destination, int count) {
    var offset = 0;
    for (; offset + 1 < count; offset += 2) {
      this.LoadWord(state, Reg.AX, source, offset);
      this.StoreWord(state, destination, offset, Reg.AX);
    }
    if (offset < count) {
      this.LoadByte(state, Reg.AL, source, offset);
      this.StoreByte(state, destination, offset, Reg.AL);
    }
  }

  private void ZeroBytes(VirtualIsaState state, Reg destination, int start, int end) {
    for (var offset = start; offset + 1 < end; offset += 2)
      this._asm.Mov(this.VirtualCell(state, destination, offset, OperandSize.Word), 0);
    if (((end - start) & 1) != 0)
      this._asm.Mov(this.VirtualCell(state, destination, end - 1, OperandSize.Byte), 0);
  }

  private void CopyToScratch(VirtualIsaState state, VirtualOperand source, int scratchOffset, int count) {
    var scratch = VirtualOperand.Of(Mem.At(state.Scratch, scratchOffset).Cs());
    this.CopyBytes(state, source, scratch, count);
  }

  #endregion

  #region moves

  private bool EmitVirtualMove(VirtualIsaState state, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, bool vex, out string? error) {
    error = null;
    if (operands.Count != 2) { error = "vector move expects two operands"; return true; }
    if (!TryVectorOperand(operands[0], out var destination) || !TryVectorOperand(operands[1], out var source)) {
      error = "vector move expects vector-register/memory operands";
      return true;
    }

    Reg? widthRegister = destination.Register ?? source.Register;
    if (widthRegister is not { } wr || wr.IsMmx()) { error = "MOVDQA/VMOVDQA expects XMM/YMM/ZMM operands"; return true; }
    var width = VectorWidth(wr);
    if (destination.Register is { } dr && VectorWidth(dr) != width || source.Register is { } sr && VectorWidth(sr) != width) {
      error = "vector move operand widths differ";
      return true;
    }
    if (destination.Register is null && source.Register is null) { error = "memory-to-memory vector move is invalid"; return true; }

    this.CopyBytes(state, source, destination, width);
    if (vex && destination.Register is { } d)
      this.ZeroBytes(state, d, width, RuntimeIsaState.VectorRegisterBytes);
    return true;
  }

  private bool EmitVirtualMovq(VirtualIsaState state, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    error = null;
    if (operands.Count != 2) { error = "MOVQ expects two operands"; return true; }
    if (!TryVectorOperand(operands[0], out var destination) || !TryVectorOperand(operands[1], out var source)
        || (destination.Register is { } dr && !dr.IsMmx()) || (source.Register is { } sr && !sr.IsMmx())) {
      error = "MOVQ emulation supports MMX register/memory operands";
      return true;
    }
    if (destination.Register is null && source.Register is null) { error = "MOVQ memory-to-memory is invalid"; return true; }
    this.CopyBytes(state, source, destination, 8);
    return true;
  }

  private bool EmitVirtualMovd(VirtualIsaState state, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, RuntimeTarget target, out string? error) {
    error = null;
    if (operands.Count != 2) { error = "MOVD expects two operands"; return true; }

    // MOVD vector <- r/m32. On a pre-386 target the low half is the real AX..DI register and the
    // high half lives in GpHigh; on a 386+ target use the real dword register so native GP code and
    // emulated SIMD observe the same architectural value.
    if (operands[0] is TextAssembler.ParsedAsmRegister vd && (vd.Register.IsMmx() || vd.Register.IsXmm())) {
      if (operands[1] is TextAssembler.ParsedAsmRegister gp && gp.Register.IsDword()) {
        if (target.Has32BitGeneralPurpose) {
          this._asm.Mov(this.VirtualCell(state, vd.Register, 0, OperandSize.Dword), gp.Register);
        } else {
          this._asm.Mov(this.VirtualCell(state, vd.Register, 0, OperandSize.Word), WordGp(gp.Register));
          this._asm.Push(Reg.AX);
          this._asm.Mov(Reg.AX, this.GpHighCell(state, gp.Register));
          this._asm.Mov(this.VirtualCell(state, vd.Register, 2, OperandSize.Word), Reg.AX);
          this._asm.Pop(Reg.AX);
        }
      } else if (operands[1] is TextAssembler.ParsedAsmMemory memory) {
        this._asm.Push(Reg.AX);
        this._asm.Mov(Reg.AX, memory.Memory.WithSize(OperandSize.Word));
        this._asm.Mov(this.VirtualCell(state, vd.Register, 0, OperandSize.Word), Reg.AX);
        this._asm.Mov(Reg.AX, memory.Memory.Offset(2).WithSize(OperandSize.Word));
        this._asm.Mov(this.VirtualCell(state, vd.Register, 2, OperandSize.Word), Reg.AX);
        this._asm.Pop(Reg.AX);
      } else {
        error = "MOVD vector destination expects a dword GP register or memory source";
        return true;
      }
      this.ZeroBytes(state, vd.Register, 4, VectorWidth(vd.Register));
      return true;
    }

    // MOVD r/m32 <- vector.
    if (operands[1] is TextAssembler.ParsedAsmRegister vs && (vs.Register.IsMmx() || vs.Register.IsXmm())) {
      if (operands[0] is TextAssembler.ParsedAsmRegister gp && gp.Register.IsDword()) {
        if (target.Has32BitGeneralPurpose) {
          this._asm.Mov(gp.Register, this.VirtualCell(state, vs.Register, 0, OperandSize.Dword));
        } else {
          this._asm.Mov(WordGp(gp.Register), this.VirtualCell(state, vs.Register, 0, OperandSize.Word));
          this._asm.Push(Reg.AX);
          this._asm.Mov(Reg.AX, this.VirtualCell(state, vs.Register, 2, OperandSize.Word));
          this._asm.Mov(this.GpHighCell(state, gp.Register), Reg.AX);
          this._asm.Pop(Reg.AX);
        }
      } else if (operands[0] is TextAssembler.ParsedAsmMemory memory) {
        this._asm.Push(Reg.AX);
        this._asm.Mov(Reg.AX, this.VirtualCell(state, vs.Register, 0, OperandSize.Word));
        this._asm.Mov(memory.Memory.WithSize(OperandSize.Word), Reg.AX);
        this._asm.Mov(Reg.AX, this.VirtualCell(state, vs.Register, 2, OperandSize.Word));
        this._asm.Mov(memory.Memory.Offset(2).WithSize(OperandSize.Word), Reg.AX);
        this._asm.Pop(Reg.AX);
      } else {
        error = "MOVD vector source expects a dword GP register or memory destination";
      }
      return true;
    }

    error = "MOVD requires one MMX/XMM operand";
    return true;
  }

  #endregion

  #region packed binary arithmetic/logic/compare

  private bool EmitVirtualPackedBinary(VirtualIsaState state, string mnemonic, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, bool vex, out string? error) {
    error = null;
    var expected = vex ? 3 : 2;
    if (operands.Count != expected) { error = $"{(vex ? "V" : "")}{mnemonic} expects {expected} operands"; return true; }
    if (operands[0] is not TextAssembler.ParsedAsmRegister destination || !IsVirtualVector(destination.Register)) {
      error = "packed SIMD destination must be a vector register";
      return true;
    }
    var width = VectorWidth(destination.Register);
    if (vex && destination.Register.IsMmx()) { error = "VEX operations cannot use MMX registers"; return true; }

    var source1 = vex ? operands[1] : operands[0];
    var source2 = vex ? operands[2] : operands[1];
    if (!TryVectorOperand(source1, out var a) || !TryVectorOperand(source2, out var b)) {
      error = "packed SIMD sources must be vector registers or memory";
      return true;
    }
    if (a.Register is { } ar && VectorWidth(ar) != width || b.Register is { } br && VectorWidth(br) != width) {
      error = "packed SIMD operand widths differ";
      return true;
    }
    var d = VirtualOperand.Of(destination.Register);

    switch (mnemonic) {
      case "PADDB": this.EmitAddSubLanes(state, d, a, b, width, 1, subtract: false); break;
      case "PADDW": this.EmitAddSubLanes(state, d, a, b, width, 2, subtract: false); break;
      case "PADDD": this.EmitAddSubLanes(state, d, a, b, width, 4, subtract: false); break;
      case "PADDQ": this.EmitAddSubLanes(state, d, a, b, width, 8, subtract: false); break;
      case "PSUBB": this.EmitAddSubLanes(state, d, a, b, width, 1, subtract: true); break;
      case "PSUBW": this.EmitAddSubLanes(state, d, a, b, width, 2, subtract: true); break;
      case "PSUBD": this.EmitAddSubLanes(state, d, a, b, width, 4, subtract: true); break;
      case "PSUBQ": this.EmitAddSubLanes(state, d, a, b, width, 8, subtract: true); break;
      case "PAND" or "PANDN" or "POR" or "PXOR": this.EmitBitwise(state, mnemonic, d, a, b, width); break;
      case "PMULLW" or "PMULHW": this.EmitMultiplyWords(state, mnemonic, d, a, b, width); break;
      case "PADDSW" or "PADDUSW" or "PSUBSW" or "PSUBUSW": this.EmitSaturatingWords(state, mnemonic, d, a, b, width); break;
      case "PCMPEQB": this.EmitCompareLanes(state, d, a, b, width, 1, signedGreater: false, equality: true); break;
      case "PCMPEQW": this.EmitCompareLanes(state, d, a, b, width, 2, signedGreater: false, equality: true); break;
      case "PCMPEQD": this.EmitCompareLanes(state, d, a, b, width, 4, signedGreater: false, equality: true); break;
      case "PCMPGTB": this.EmitCompareLanes(state, d, a, b, width, 1, signedGreater: true, equality: false); break;
      case "PCMPGTW": this.EmitCompareLanes(state, d, a, b, width, 2, signedGreater: true, equality: false); break;
      case "PCMPGTD": this.EmitCompareLanes(state, d, a, b, width, 4, signedGreater: true, equality: false); break;
      default:
        error = $"packed-SIMD emulator has no {mnemonic} lowering";
        return true;
    }

    if (vex)
      this.ZeroBytes(state, destination.Register, width, RuntimeIsaState.VectorRegisterBytes);
    return true;
  }

  private void EmitAddSubLanes(VirtualIsaState state, VirtualOperand d, VirtualOperand a, VirtualOperand b, int width, int laneBytes, bool subtract) {
    for (var lane = 0; lane < width; lane += laneBytes) {
      if (laneBytes == 1) {
        this.LoadByte(state, Reg.AL, a, lane);
        this.LoadByte(state, Reg.DL, b, lane);
        if (subtract) this._asm.Sub(Reg.AL, Reg.DL); else this._asm.Add(Reg.AL, Reg.DL);
        this.StoreByte(state, d, lane, Reg.AL);
        continue;
      }
      for (var word = 0; word < laneBytes; word += 2) {
        this.LoadWord(state, Reg.AX, a, lane + word);
        this.LoadWord(state, Reg.DX, b, lane + word);
        if (word == 0) {
          if (subtract) this._asm.Sub(Reg.AX, Reg.DX); else this._asm.Add(Reg.AX, Reg.DX);
        } else {
          if (subtract) this._asm.Sbb(Reg.AX, Reg.DX); else this._asm.Adc(Reg.AX, Reg.DX);
        }
        this.StoreWord(state, d, lane + word, Reg.AX);
      }
    }
  }

  private void EmitBitwise(VirtualIsaState state, string mnemonic, VirtualOperand d, VirtualOperand a, VirtualOperand b, int width) {
    for (var offset = 0; offset < width; offset += 2) {
      this.LoadWord(state, Reg.AX, a, offset);
      this.LoadWord(state, Reg.DX, b, offset);
      switch (mnemonic) {
        case "PAND": this._asm.And(Reg.AX, Reg.DX); break;
        case "PANDN": this._asm.Not(Reg.AX); this._asm.And(Reg.AX, Reg.DX); break;
        case "POR": this._asm.Or(Reg.AX, Reg.DX); break;
        case "PXOR": this._asm.Xor(Reg.AX, Reg.DX); break;
      }
      this.StoreWord(state, d, offset, Reg.AX);
    }
  }

  private void EmitMultiplyWords(VirtualIsaState state, string mnemonic, VirtualOperand d, VirtualOperand a, VirtualOperand b, int width) {
    for (var offset = 0; offset < width; offset += 2) {
      this.LoadWord(state, Reg.AX, a, offset);
      this.LoadWord(state, Reg.CX, b, offset);
      if (mnemonic == "PMULHW") {
        this._asm.Imul(Reg.CX);
        this.StoreWord(state, d, offset, Reg.DX);
      } else {
        this._asm.Mul(Reg.CX);
        this.StoreWord(state, d, offset, Reg.AX);
      }
    }
  }

  private void EmitSaturatingWords(VirtualIsaState state, string mnemonic, VirtualOperand d, VirtualOperand a, VirtualOperand b, int width) {
    for (var offset = 0; offset < width; offset += 2) {
      this.LoadWord(state, Reg.AX, a, offset);
      this.LoadWord(state, Reg.DX, b, offset);
      var done = this._asm.DefineLabel();
      switch (mnemonic) {
        case "PADDUSW": {
          var normal = this._asm.DefineLabel();
          this._asm.Add(Reg.AX, Reg.DX);
          this._asm.J(Condition.AboveOrEqual, normal); // CF=0
          this._asm.Mov(Reg.AX, -1);
          this._asm.MarkLabel(normal);
          break;
        }
        case "PSUBUSW": {
          var normal = this._asm.DefineLabel();
          this._asm.Sub(Reg.AX, Reg.DX);
          this._asm.J(Condition.AboveOrEqual, normal); // CF=0
          this._asm.Xor(Reg.AX, Reg.AX);
          this._asm.MarkLabel(normal);
          break;
        }
        case "PADDSW": {
          var normal = this._asm.DefineLabel();
          this._asm.Add(Reg.AX, Reg.DX);
          this._asm.J(Condition.NotOverflow, normal);
          this._asm.Test(Reg.DX, Reg.DX);
          var positive = this._asm.DefineLabel();
          this._asm.J(Condition.NotSign, positive);
          this._asm.Mov(Reg.AX, 0x8000);
          this._asm.Jmp(done);
          this._asm.MarkLabel(positive);
          this._asm.Mov(Reg.AX, 0x7FFF);
          this._asm.MarkLabel(normal);
          break;
        }
        case "PSUBSW": {
          this._asm.Mov(Reg.CX, Reg.AX);
          var normal = this._asm.DefineLabel();
          this._asm.Sub(Reg.AX, Reg.DX);
          this._asm.J(Condition.NotOverflow, normal);
          this._asm.Test(Reg.CX, Reg.CX);
          var positive = this._asm.DefineLabel();
          this._asm.J(Condition.NotSign, positive);
          this._asm.Mov(Reg.AX, 0x8000);
          this._asm.Jmp(done);
          this._asm.MarkLabel(positive);
          this._asm.Mov(Reg.AX, 0x7FFF);
          this._asm.MarkLabel(normal);
          break;
        }
      }
      this._asm.MarkLabel(done);
      this.StoreWord(state, d, offset, Reg.AX);
    }
  }

  private void EmitCompareLanes(VirtualIsaState state, VirtualOperand d, VirtualOperand a, VirtualOperand b, int width, int laneBytes, bool signedGreater, bool equality) {
    for (var lane = 0; lane < width; lane += laneBytes) {
      var yes = this._asm.DefineLabel();
      var no = this._asm.DefineLabel();
      var done = this._asm.DefineLabel();
      if (laneBytes == 1) {
        this.LoadByte(state, Reg.AL, a, lane);
        this.LoadByte(state, Reg.DL, b, lane);
        this._asm.Cmp(Reg.AL, Reg.DL);
        this._asm.J(equality ? Condition.Equal : Condition.Greater, yes);
      } else if (laneBytes == 2) {
        this.LoadWord(state, Reg.AX, a, lane);
        this.LoadWord(state, Reg.DX, b, lane);
        this._asm.Cmp(Reg.AX, Reg.DX);
        this._asm.J(equality ? Condition.Equal : Condition.Greater, yes);
      } else {
        // dword equality / signed greater: high word decides signed order, low word unsigned on tie.
        this.LoadWord(state, Reg.AX, a, lane + 2);
        this.LoadWord(state, Reg.DX, b, lane + 2);
        this._asm.Cmp(Reg.AX, Reg.DX);
        if (equality) {
          this._asm.J(Condition.NotEqual, no);
          this.LoadWord(state, Reg.AX, a, lane);
          this.LoadWord(state, Reg.DX, b, lane);
          this._asm.Cmp(Reg.AX, Reg.DX);
          this._asm.J(Condition.Equal, yes);
        } else {
          this._asm.J(Condition.Greater, yes);
          this._asm.J(Condition.Less, no);
          this.LoadWord(state, Reg.AX, a, lane);
          this.LoadWord(state, Reg.DX, b, lane);
          this._asm.Cmp(Reg.AX, Reg.DX);
          this._asm.J(Condition.Above, yes);
        }
      }
      this._asm.MarkLabel(no);
      for (var o = 0; o < laneBytes; o += 2)
        this._asm.Mov(OperandCell(d, lane + o, laneBytes == 1 ? OperandSize.Byte : OperandSize.Word, (r, p, s) => this.VirtualCell(state, r, p, s)), 0);
      this._asm.Jmp(done);
      this._asm.MarkLabel(yes);
      if (laneBytes == 1)
        this._asm.Mov(OperandCell(d, lane, OperandSize.Byte, (r, p, s) => this.VirtualCell(state, r, p, s)), 0xFF);
      else
        for (var o = 0; o < laneBytes; o += 2)
          this._asm.Mov(OperandCell(d, lane + o, OperandSize.Word, (r, p, s) => this.VirtualCell(state, r, p, s)), -1);
      this._asm.MarkLabel(done);
    }
  }

  #endregion

  #region pack/unpack

  private bool EmitVirtualPack(VirtualIsaState state, string mnemonic, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    error = null;
    if (operands.Count != 2 || operands[0] is not TextAssembler.ParsedAsmRegister destination || !IsVirtualVector(destination.Register)
        || !TryVectorOperand(operands[1], out var source)) {
      error = $"{mnemonic} expects vector destination and vector/memory source";
      return true;
    }
    var width = VectorWidth(destination.Register);
    if (source.Register is { } sr && VectorWidth(sr) != width) { error = "pack operand widths differ"; return true; }
    this.CopyToScratch(state, VirtualOperand.Of(destination.Register), 0, width);
    this.CopyToScratch(state, source, 64, width);
    var inElement = mnemonic == "PACKSSDW" ? 4 : 2;
    var outElement = inElement / 2;
    var elementsPerSource = width / inElement;
    var outIndex = 0;
    for (var half = 0; half < 2; ++half)
      for (var i = 0; i < elementsPerSource; ++i, ++outIndex) {
        var input = VirtualOperand.Of(Mem.At(state.Scratch, half * 64 + i * inElement).Cs());
        if (mnemonic == "PACKSSDW")
          this.EmitPackDwordToSignedWord(state, input, VirtualOperand.Of(destination.Register), outIndex * 2);
        else
          this.EmitPackWordToByte(state, input, VirtualOperand.Of(destination.Register), outIndex, unsignedOutput: mnemonic == "PACKUSWB");
      }
    return true;
  }

  private void EmitPackWordToByte(VirtualIsaState state, VirtualOperand input, VirtualOperand output, int outputOffset, bool unsignedOutput) {
    this.LoadWord(state, Reg.AX, input, 0);
    var low = this._asm.DefineLabel();
    var high = this._asm.DefineLabel();
    var done = this._asm.DefineLabel();
    if (unsignedOutput) {
      this._asm.Cmp(Reg.AX, 0);
      this._asm.J(Condition.Less, low);
      this._asm.Cmp(Reg.AX, 255);
      this._asm.J(Condition.Greater, high);
      this.StoreByte(state, output, outputOffset, Reg.AL);
      this._asm.Jmp(done);
      this._asm.MarkLabel(low); this._asm.Mov(Reg.AL, 0); this.StoreByte(state, output, outputOffset, Reg.AL); this._asm.Jmp(done);
      this._asm.MarkLabel(high); this._asm.Mov(Reg.AL, 255); this.StoreByte(state, output, outputOffset, Reg.AL);
    } else {
      this._asm.Cmp(Reg.AX, -128);
      this._asm.J(Condition.Less, low);
      this._asm.Cmp(Reg.AX, 127);
      this._asm.J(Condition.Greater, high);
      this.StoreByte(state, output, outputOffset, Reg.AL);
      this._asm.Jmp(done);
      this._asm.MarkLabel(low); this._asm.Mov(Reg.AL, 0x80); this.StoreByte(state, output, outputOffset, Reg.AL); this._asm.Jmp(done);
      this._asm.MarkLabel(high); this._asm.Mov(Reg.AL, 0x7F); this.StoreByte(state, output, outputOffset, Reg.AL);
    }
    this._asm.MarkLabel(done);
  }

  private void EmitPackDwordToSignedWord(VirtualIsaState state, VirtualOperand input, VirtualOperand output, int outputOffset) {
    this.LoadWord(state, Reg.AX, input, 0);
    this.LoadWord(state, Reg.DX, input, 2);
    var min = this._asm.DefineLabel();
    var max = this._asm.DefineLabel();
    var store = this._asm.DefineLabel();
    this._asm.Cmp(Reg.DX, 0);
    this._asm.J(Condition.Greater, max);
    this._asm.J(Condition.Less, min);
    this._asm.Cmp(Reg.AX, 0x7FFF);
    this._asm.J(Condition.Above, max);
    this._asm.Jmp(store);
    this._asm.MarkLabel(min);
    this._asm.Cmp(Reg.DX, -1);
    this._asm.J(Condition.Less, min = this._asm.DefineLabel());
    this._asm.Cmp(Reg.AX, 0x8000);
    this._asm.J(Condition.Below, min);
    this._asm.Jmp(store);
    this._asm.MarkLabel(min); this._asm.Mov(Reg.AX, 0x8000); this._asm.Jmp(store);
    this._asm.MarkLabel(max); this._asm.Mov(Reg.AX, 0x7FFF);
    this._asm.MarkLabel(store);
    this.StoreWord(state, output, outputOffset, Reg.AX);
  }

  private bool EmitVirtualUnpack(VirtualIsaState state, string mnemonic, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    error = null;
    if (operands.Count != 2 || operands[0] is not TextAssembler.ParsedAsmRegister destination || !IsVirtualVector(destination.Register)
        || !TryVectorOperand(operands[1], out var source)) {
      error = $"{mnemonic} expects vector destination and vector/memory source";
      return true;
    }
    var width = VectorWidth(destination.Register);
    if (source.Register is { } sr && VectorWidth(sr) != width) { error = "unpack operand widths differ"; return true; }
    var elementBytes = mnemonic.EndsWith("BW", StringComparison.Ordinal) ? 1 : mnemonic.EndsWith("WD", StringComparison.Ordinal) ? 2 : 4;
    var high = mnemonic.Contains("PUNPCKH", StringComparison.Ordinal);
    this.CopyToScratch(state, VirtualOperand.Of(destination.Register), 0, width);
    this.CopyToScratch(state, source, 64, width);
    var elements = width / elementBytes / 2;
    var inputBase = high ? width / 2 : 0;
    for (var i = 0; i < elements; ++i) {
      var left = VirtualOperand.Of(Mem.At(state.Scratch, inputBase + i * elementBytes).Cs());
      var right = VirtualOperand.Of(Mem.At(state.Scratch, 64 + inputBase + i * elementBytes).Cs());
      this.CopyBytes(state, left, VirtualOperand.Of(this.VirtualCell(state, destination.Register, (i * 2) * elementBytes, OperandSize.None)), elementBytes);
      this.CopyBytes(state, right, VirtualOperand.Of(this.VirtualCell(state, destination.Register, (i * 2 + 1) * elementBytes, OperandSize.None)), elementBytes);
    }
    return true;
  }

  #endregion

  #region packed shifts

  private bool EmitVirtualShift(VirtualIsaState state, string mnemonic, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    error = null;
    if (operands.Count != 2 || operands[0] is not TextAssembler.ParsedAsmRegister destination || !IsVirtualVector(destination.Register)) {
      error = $"{mnemonic} expects a vector destination";
      return true;
    }
    var laneBytes = mnemonic.EndsWith('W') ? 2 : mnemonic.EndsWith('D') ? 4 : 8;
    var width = VectorWidth(destination.Register);
    var countCell = Mem.Word(state.Scratch, RuntimeIsaState.ScratchBytes - 2).Cs();
    switch (operands[1]) {
      case TextAssembler.ParsedAsmImmediate immediate:
        this._asm.Mov(countCell, Math.Clamp(immediate.Value, 0, 255));
        break;
      case TextAssembler.ParsedAsmRegister countRegister when countRegister.Register.IsMmx(): {
        this._asm.Mov(Reg.CX, this.VirtualCell(state, countRegister.Register, 0, OperandSize.Word));
        this._asm.Xor(Reg.AX, Reg.AX);
        for (var o = 2; o < 8; o += 2)
          this._asm.Or(Reg.AX, this.VirtualCell(state, countRegister.Register, o, OperandSize.Word));
        var lowOnly = this._asm.DefineLabel();
        this._asm.Cmp(Reg.AX, 0);
        this._asm.J(Condition.Equal, lowOnly);
        this._asm.Mov(Reg.CX, laneBytes * 8);
        this._asm.MarkLabel(lowOnly);
        this._asm.Mov(countCell, Reg.CX);
        break;
      }
      default:
        error = "packed shift count must be immediate (or MMX register for MMX form)";
        return true;
    }

    for (var lane = 0; lane < width; lane += laneBytes)
      this.EmitShiftLane(state, destination.Register, lane, laneBytes, countCell,
        left: mnemonic.StartsWith("PSLL", StringComparison.Ordinal), arithmetic: mnemonic.StartsWith("PSRA", StringComparison.Ordinal));
    return true;
  }

  private void EmitShiftLane(VirtualIsaState state, Reg destination, int laneOffset, int laneBytes, Mem countCell, bool left, bool arithmetic) {
    var bits = laneBytes * 8;
    var zeroOrSign = this._asm.DefineLabel();
    var loop = this._asm.DefineLabel();
    var done = this._asm.DefineLabel();
    this._asm.Mov(Reg.CX, countCell);
    this._asm.Cmp(Reg.CX, bits);
    this._asm.J(Condition.AboveOrEqual, zeroOrSign);
    this._asm.Cmp(Reg.CX, 0);
    this._asm.J(Condition.Equal, done);
    this._asm.MarkLabel(loop);
    if (left) {
      this._asm.Shl(this.VirtualCell(state, destination, laneOffset, OperandSize.Word), 1);
      for (var o = 2; o < laneBytes; o += 2)
        this._asm.Rcl(this.VirtualCell(state, destination, laneOffset + o, OperandSize.Word), 1);
    } else {
      var high = laneOffset + laneBytes - 2;
      if (arithmetic) this._asm.Sar(this.VirtualCell(state, destination, high, OperandSize.Word), 1);
      else this._asm.Shr(this.VirtualCell(state, destination, high, OperandSize.Word), 1);
      for (var o = high - 2; o >= laneOffset; o -= 2)
        this._asm.Rcr(this.VirtualCell(state, destination, o, OperandSize.Word), 1);
    }
    this._asm.Loop(loop);
    this._asm.Jmp(done);

    this._asm.MarkLabel(zeroOrSign);
    if (!arithmetic) {
      for (var o = 0; o < laneBytes; o += 2)
        this._asm.Mov(this.VirtualCell(state, destination, laneOffset + o, OperandSize.Word), 0);
    } else {
      this._asm.Mov(Reg.AX, this.VirtualCell(state, destination, laneOffset + laneBytes - 2, OperandSize.Word));
      this._asm.Sar(Reg.AX, 15);
      for (var o = 0; o < laneBytes; o += 2)
        this._asm.Mov(this.VirtualCell(state, destination, laneOffset + o, OperandSize.Word), Reg.AX);
    }
    this._asm.MarkLabel(done);
  }

  #endregion
}
