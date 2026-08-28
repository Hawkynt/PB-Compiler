using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Runtime;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {
  private readonly record struct DwordPlace(Reg? Register, Mem? Memory) {
    public static DwordPlace Of(Reg register) => new(register, null);
    public static DwordPlace Of(Mem memory) => new(null, memory);
  }

  private const int GpSourceScratch = 80;
  private const int GpDestScratch = 84;
  private const int GpOrigFlagsScratch = 88;
  private const int GpLowFlagsScratch = 90;
  private const int GpHighFlagsScratch = 92;
  private const int GpMergedFlagsScratch = 94;

  /// <summary>
  /// Pre-386 lowering for the common 32-bit inline-assembly integer surface. The real 16-bit low
  /// register remains architectural state, so AX writes immediately affect virtual EAX and vice
  /// versa; only bits 16..31 need persistent memory. ESP is deliberately excluded because using the
  /// compiler's own stack while pretending SP is an unrelated data register would be unsound.
  /// </summary>
  private bool TryEmitVirtualGp32Instruction(InlineInstruction instruction, InlineAsmResolver resolver, RuntimeTarget target, out string? error) {
    error = null;
    var mnemonic = instruction.Mnemonic;
    if (mnemonic is not ("MOV" or "XCHG" or "ADD" or "ADC" or "SUB" or "SBB" or "AND" or "OR" or "XOR" or "CMP" or "TEST"
        or "INC" or "DEC" or "NOT" or "NEG" or "BSWAP" or "MOVZX" or "MOVSX")
        && !mnemonic.StartsWith("CMOV", StringComparison.Ordinal))
      return false;

    this._textAssembler ??= new(this._asm);
    if (!this._textAssembler.TryParseOperands(instruction.Operands, resolver, out var operands, out error))
      return true;
    if (!UsesDwordGpOrDwordMemory(operands))
      return false;

    var state = this.EnsureVirtualIsaState();
    if (mnemonic == "MOV") return this.EmitVirtualDwordMove(state, operands, target, out error);
    if (mnemonic == "XCHG") return this.EmitVirtualDwordXchg(state, operands, target, out error);
    if (mnemonic is "MOVZX" or "MOVSX") return this.EmitVirtualDwordExtend(state, mnemonic, operands, target, out error);
    if (mnemonic == "BSWAP") return this.EmitVirtualDwordBswap(state, operands, target, out error);
    if (mnemonic.StartsWith("CMOV", StringComparison.Ordinal)) return this.EmitVirtualDwordCmov(state, mnemonic, operands, target, out error);
    if (mnemonic is "INC" or "DEC" or "NOT" or "NEG") return this.EmitVirtualDwordUnary(state, mnemonic, operands, target, out error);
    return this.EmitVirtualDwordAlu(state, mnemonic, operands, target, out error);
  }

  private static bool UsesDwordGpOrDwordMemory(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands) => operands.Any(o => o switch {
    TextAssembler.ParsedAsmRegister r => r.Register.IsDword(),
    TextAssembler.ParsedAsmMemory m => m.Memory.Size == OperandSize.Dword,
    _ => false,
  });

  private static bool TryDwordPlace(TextAssembler.ParsedAsmOperand operand, out DwordPlace place) {
    switch (operand) {
      case TextAssembler.ParsedAsmRegister r when r.Register.IsDword() && r.Register != Reg.ESP:
        place = DwordPlace.Of(r.Register); return true;
      case TextAssembler.ParsedAsmMemory m:
        place = DwordPlace.Of(m.Memory); return true;
      default:
        place = default; return false;
    }
  }

  private Mem DwordHighCell(VirtualIsaState state, DwordPlace place) => place.Register is { } r
    ? this.GpHighCell(state, r)
    : place.Memory!.Value.Offset(2).WithSize(OperandSize.Word);

  private Reg DwordTemp(DwordPlace destination) {
    var low = destination.Register is { } r ? WordGp(r) : (Reg?)null;
    if (low != Reg.AX) return Reg.AX;
    if (low != Reg.CX) return Reg.CX;
    return Reg.DX;
  }

  private Mem GpScratch(VirtualIsaState state, int offset) => Mem.Word(state.Scratch, offset).Cs();

  private void BridgeHighFromNativeIfAvailable(VirtualIsaState state, Reg register, RuntimeTarget target) {
    if (!target.Has32BitGeneralPurpose)
      return;
    var bridge = Mem.Dword(state.Scratch, GpDestScratch).Cs();
    this._asm.Mov(bridge, register);
    this._asm.Push(Reg.AX);
    this._asm.Mov(Reg.AX, bridge.Offset(2).WithSize(OperandSize.Word));
    this._asm.Mov(this.GpHighCell(state, register), Reg.AX);
    this._asm.Pop(Reg.AX);
  }

  private void BridgeHighToNativeIfAvailable(VirtualIsaState state, Reg register, RuntimeTarget target) {
    if (!target.Has32BitGeneralPurpose)
      return;
    var bridge = Mem.Dword(state.Scratch, GpDestScratch).Cs();
    var low = WordGp(register);
    this._asm.Mov(bridge.WithSize(OperandSize.Word), low);
    var temp = low == Reg.AX ? Reg.CX : Reg.AX;
    this._asm.Push(temp);
    this._asm.Mov(temp, this.GpHighCell(state, register));
    this._asm.Mov(bridge.Offset(2).WithSize(OperandSize.Word), temp);
    this._asm.Pop(temp);
    this._asm.Mov(register, bridge);
  }

  private void StageDword(VirtualIsaState state, TextAssembler.ParsedAsmOperand source, int scratchOffset, RuntimeTarget target) {
    var lowCell = this.GpScratch(state, scratchOffset);
    var highCell = this.GpScratch(state, scratchOffset + 2);
    switch (source) {
      case TextAssembler.ParsedAsmImmediate immediate:
        this._asm.Mov(lowCell, unchecked((ushort)immediate.Value));
        this._asm.Mov(highCell, unchecked((ushort)((uint)immediate.Value >> 16)));
        return;
      case TextAssembler.ParsedAsmRegister r when r.Register.IsDword() && r.Register != Reg.ESP:
        this.BridgeHighFromNativeIfAvailable(state, r.Register, target);
        this._asm.Mov(lowCell, WordGp(r.Register));
        this._asm.Push(Reg.AX);
        this._asm.Mov(Reg.AX, this.GpHighCell(state, r.Register));
        this._asm.Mov(highCell, Reg.AX);
        this._asm.Pop(Reg.AX);
        return;
      case TextAssembler.ParsedAsmMemory memory:
        this._asm.Push(Reg.AX);
        this._asm.Mov(Reg.AX, memory.Memory.WithSize(OperandSize.Word));
        this._asm.Mov(lowCell, Reg.AX);
        this._asm.Mov(Reg.AX, memory.Memory.Offset(2).WithSize(OperandSize.Word));
        this._asm.Mov(highCell, Reg.AX);
        this._asm.Pop(Reg.AX);
        return;
      default:
        throw new InvalidOperationException("not a dword source");
    }
  }

  private void StageDwordPlace(VirtualIsaState state, DwordPlace source, int scratchOffset, RuntimeTarget target) {
    if (source.Register is { } r)
      this.StageDword(state, new TextAssembler.ParsedAsmRegister(r), scratchOffset, target);
    else
      this.StageDword(state, new TextAssembler.ParsedAsmMemory(source.Memory!.Value), scratchOffset, target);
  }

  private void WriteDwordPlace(VirtualIsaState state, DwordPlace destination, int scratchOffset, RuntimeTarget target) {
    var lowCell = this.GpScratch(state, scratchOffset);
    var highCell = this.GpScratch(state, scratchOffset + 2);
    var temp = this.DwordTemp(destination);
    if (destination.Register is { } r) {
      this._asm.Mov(WordGp(r), lowCell);
      this._asm.Push(temp);
      this._asm.Mov(temp, highCell);
      this._asm.Mov(this.GpHighCell(state, r), temp);
      this._asm.Pop(temp);
      this.BridgeHighToNativeIfAvailable(state, r, target);
      return;
    }
    this._asm.Push(temp);
    this._asm.Mov(temp, lowCell);
    this._asm.Mov(destination.Memory!.Value.WithSize(OperandSize.Word), temp);
    this._asm.Mov(temp, highCell);
    this._asm.Mov(destination.Memory.Value.Offset(2).WithSize(OperandSize.Word), temp);
    this._asm.Pop(temp);
  }

  private bool EmitVirtualDwordMove(VirtualIsaState state, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, RuntimeTarget target, out string? error) {
    error = null;
    if (operands.Count != 2 || !TryDwordPlace(operands[0], out var destination)) { error = "32-bit MOV emulation needs a dword register/memory destination (ESP is not virtualizable)"; return true; }
    if (operands[1] is not (TextAssembler.ParsedAsmImmediate or TextAssembler.ParsedAsmMemory or TextAssembler.ParsedAsmRegister)) { error = "invalid 32-bit MOV source"; return true; }
    if (operands[1] is TextAssembler.ParsedAsmRegister sr && (!sr.Register.IsDword() || sr.Register == Reg.ESP)) { error = "32-bit MOV source must be a dword register (except ESP)"; return true; }
    this.StageDword(state, operands[1], GpSourceScratch, target);
    this.WriteDwordPlace(state, destination, GpSourceScratch, target);
    return true;
  }

  private bool EmitVirtualDwordXchg(VirtualIsaState state, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, RuntimeTarget target, out string? error) {
    error = null;
    if (operands.Count != 2 || !TryDwordPlace(operands[0], out var left) || !TryDwordPlace(operands[1], out var right)) { error = "32-bit XCHG emulation requires dword register/memory operands other than ESP"; return true; }
    if (left.Register is null && right.Register is null) { error = "XCHG memory,memory is invalid"; return true; }
    this.StageDwordPlace(state, left, GpDestScratch, target);
    this.StageDwordPlace(state, right, GpSourceScratch, target);
    this.WriteDwordPlace(state, left, GpSourceScratch, target);
    this.WriteDwordPlace(state, right, GpDestScratch, target);
    return true;
  }

  private bool EmitVirtualDwordBswap(VirtualIsaState state, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, RuntimeTarget target, out string? error) {
    error = null;
    if (operands.Count != 1 || operands[0] is not TextAssembler.ParsedAsmRegister r || !r.Register.IsDword() || r.Register == Reg.ESP) { error = "BSWAP emulation needs EAX/ECX/EDX/EBX/EBP/ESI/EDI"; return true; }
    this.StageDword(state, operands[0], GpSourceScratch, target);
    this._asm.Pushf();
    this._asm.Push(Reg.AX);
    this._asm.Mov(Reg.AX, this.GpScratch(state, GpSourceScratch + 2));
    this._asm.Xchg(Reg.AL, Reg.AH);
    this._asm.Mov(this.GpScratch(state, GpDestScratch), Reg.AX);
    this._asm.Mov(Reg.AX, this.GpScratch(state, GpSourceScratch));
    this._asm.Xchg(Reg.AL, Reg.AH);
    this._asm.Mov(this.GpScratch(state, GpDestScratch + 2), Reg.AX);
    this._asm.Pop(Reg.AX);
    this.WriteDwordPlace(state, DwordPlace.Of(r.Register), GpDestScratch, target);
    this._asm.Popf();
    return true;
  }

  private bool EmitVirtualDwordCmov(VirtualIsaState state, string mnemonic, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, RuntimeTarget target, out string? error) {
    error = null;
    if (!TryCmovCondition(mnemonic, out var condition) || operands.Count != 2
        || operands[0] is not TextAssembler.ParsedAsmRegister d || !d.Register.IsDword() || d.Register == Reg.ESP) {
      error = "32-bit CMOVcc emulation requires a dword GP destination other than ESP";
      return true;
    }
    if (operands[1] is TextAssembler.ParsedAsmRegister sr && (!sr.Register.IsDword() || sr.Register == Reg.ESP)) { error = "CMOVcc source register must be dword"; return true; }
    if (operands[1] is not (TextAssembler.ParsedAsmRegister or TextAssembler.ParsedAsmMemory)) { error = "CMOVcc source must be register or memory"; return true; }
    // Intel CMOV always performs the source read; stage it before testing the condition.
    this.StageDword(state, operands[1], GpSourceScratch, target);
    var skip = this._asm.DefineLabel();
    this._asm.J(Invert(condition), skip);
    this.WriteDwordPlace(state, DwordPlace.Of(d.Register), GpSourceScratch, target);
    this._asm.MarkLabel(skip);
    return true;
  }

  private bool EmitVirtualDwordExtend(VirtualIsaState state, string mnemonic, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, RuntimeTarget target, out string? error) {
    error = null;
    if (operands.Count != 2 || operands[0] is not TextAssembler.ParsedAsmRegister d || !d.Register.IsDword() || d.Register == Reg.ESP) { error = $"{mnemonic} emulation requires a dword GP destination other than ESP"; return true; }
    var sourceSize = operands[1] switch {
      TextAssembler.ParsedAsmRegister r when r.Register.IsByte() => 1,
      TextAssembler.ParsedAsmRegister r when r.Register.IsWord() => 2,
      TextAssembler.ParsedAsmMemory m when m.Memory.Size == OperandSize.Byte => 1,
      TextAssembler.ParsedAsmMemory m when m.Memory.Size == OperandSize.Word => 2,
      _ => 0,
    };
    if (sourceSize == 0) { error = $"{mnemonic} source must have byte or word width"; return true; }

    var srcByte = Mem.Byte(state.Scratch, GpSourceScratch).Cs();
    var srcWord = Mem.Word(state.Scratch, GpSourceScratch).Cs();
    switch (operands[1]) {
      case TextAssembler.ParsedAsmRegister r:
        if (sourceSize == 1) this._asm.Mov(srcByte, r.Register); else this._asm.Mov(srcWord, r.Register);
        break;
      case TextAssembler.ParsedAsmMemory m:
        this._asm.Push(Reg.AX);
        if (sourceSize == 1) { this._asm.Mov(Reg.AL, m.Memory.WithSize(OperandSize.Byte)); this._asm.Mov(srcByte, Reg.AL); }
        else { this._asm.Mov(Reg.AX, m.Memory.WithSize(OperandSize.Word)); this._asm.Mov(srcWord, Reg.AX); }
        this._asm.Pop(Reg.AX);
        break;
    }

    this._asm.Pushf();
    this._asm.Push(Reg.AX);
    if (sourceSize == 1) {
      this._asm.Mov(Reg.AL, srcByte);
      if (mnemonic == "MOVSX") this._asm.Cbw(); else this._asm.Xor(Reg.AH, Reg.AH);
    } else {
      this._asm.Mov(Reg.AX, srcWord);
    }
    this._asm.Mov(this.GpScratch(state, GpDestScratch), Reg.AX);
    if (mnemonic == "MOVZX") {
      this._asm.Mov(this.GpScratch(state, GpDestScratch + 2), 0);
    } else {
      this._asm.Test(Reg.AX, Reg.AX);
      var nonnegative = this._asm.DefineLabel();
      var highDone = this._asm.DefineLabel();
      this._asm.J(Condition.NotSign, nonnegative);
      this._asm.Mov(this.GpScratch(state, GpDestScratch + 2), -1);
      this._asm.Jmp(highDone);
      this._asm.MarkLabel(nonnegative);
      this._asm.Mov(this.GpScratch(state, GpDestScratch + 2), 0);
      this._asm.MarkLabel(highDone);
    }
    this._asm.Pop(Reg.AX);
    this.WriteDwordPlace(state, DwordPlace.Of(d.Register), GpDestScratch, target);
    this._asm.Popf();
    return true;
  }

  private bool EmitVirtualDwordUnary(VirtualIsaState state, string mnemonic, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, RuntimeTarget target, out string? error) {
    error = null;
    if (operands.Count != 1 || !TryDwordPlace(operands[0], out var destination)) { error = $"32-bit {mnemonic} emulation requires dword register/memory operand other than ESP"; return true; }
    if (mnemonic == "NOT") {
      this.StageDwordPlace(state, destination, GpSourceScratch, target);
      this._asm.Pushf();
      this._asm.Push(Reg.AX);
      this._asm.Mov(Reg.AX, this.GpScratch(state, GpSourceScratch)); this._asm.Not(Reg.AX); this._asm.Mov(this.GpScratch(state, GpDestScratch), Reg.AX);
      this._asm.Mov(Reg.AX, this.GpScratch(state, GpSourceScratch + 2)); this._asm.Not(Reg.AX); this._asm.Mov(this.GpScratch(state, GpDestScratch + 2), Reg.AX);
      this._asm.Pop(Reg.AX);
      this.WriteDwordPlace(state, destination, GpDestScratch, target);
      this._asm.Popf();
      return true;
    }

    this.StageDwordPlace(state, destination, GpSourceScratch, target);
    if (mnemonic == "NEG") {
      this._asm.Mov(this.GpScratch(state, GpDestScratch), 0);
      this._asm.Mov(this.GpScratch(state, GpDestScratch + 2), 0);
      return this.EmitVirtualDwordAluFromStaged(state, "SUB", destination, GpSourceScratch, target, preserveCarry: false, out error, destinationInitiallyInScratch: true);
    }

    // INC/DEC = ADD/SUB 1 with the incoming carry flag preserved.
    this._asm.Mov(this.GpScratch(state, GpSourceScratch), 1);
    this._asm.Mov(this.GpScratch(state, GpSourceScratch + 2), 0);
    return this.EmitVirtualDwordAluFromStaged(state, mnemonic == "INC" ? "ADD" : "SUB", destination, GpSourceScratch, target, preserveCarry: true, out error);
  }

  private bool EmitVirtualDwordAlu(VirtualIsaState state, string mnemonic, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, RuntimeTarget target, out string? error) {
    error = null;
    if (operands.Count != 2 || !TryDwordPlace(operands[0], out var destination)) { error = $"32-bit {mnemonic} emulation requires dword register/memory destination other than ESP"; return true; }
    if (operands[1] is TextAssembler.ParsedAsmRegister sr && (!sr.Register.IsDword() || sr.Register == Reg.ESP)) { error = $"32-bit {mnemonic} source register must be dword and not ESP"; return true; }
    if (operands[1] is not (TextAssembler.ParsedAsmRegister or TextAssembler.ParsedAsmMemory or TextAssembler.ParsedAsmImmediate)) { error = $"invalid 32-bit {mnemonic} source"; return true; }
    this.StageDword(state, operands[1], GpSourceScratch, target);
    return this.EmitVirtualDwordAluFromStaged(state, mnemonic, destination, GpSourceScratch, target, preserveCarry: false, out error);
  }

  private bool EmitVirtualDwordAluFromStaged(VirtualIsaState state, string mnemonic, DwordPlace destination, int sourceScratch, RuntimeTarget target, bool preserveCarry, out string? error, bool destinationInitiallyInScratch = false) {
    error = null;
    var temp = this.DwordTemp(destination);
    var sourceLow = this.GpScratch(state, sourceScratch);
    var sourceHigh = this.GpScratch(state, sourceScratch + 2);
    var destHigh = destinationInitiallyInScratch ? this.GpScratch(state, GpDestScratch + 2) : this.DwordHighCell(state, destination);

    if (destination.Register is { } dr)
      this.BridgeHighFromNativeIfAvailable(state, dr, target);
    if (destinationInitiallyInScratch) {
      // NEG starts from an explicit zero dword already placed in GpDestScratch.
    } else if (mnemonic is "CMP" or "TEST") {
      this.StageDwordPlace(state, destination, GpDestScratch, target);
    }

    this._asm.Pushf(); this._asm.Pop(temp); this._asm.Mov(this.GpScratch(state, GpOrigFlagsScratch), temp);

    var modifies = mnemonic is not ("CMP" or "TEST");
    var logical = mnemonic is "AND" or "OR" or "XOR" or "TEST";

    if (mnemonic is "CMP" or "TEST") {
      this._asm.Mov(temp, this.GpScratch(state, GpDestScratch));
      if (mnemonic == "CMP") this._asm.Cmp(temp, sourceLow); else this._asm.And(temp, sourceLow);
    } else if (destinationInitiallyInScratch) {
      this.EmitWordAlu(mnemonic, this.GpScratch(state, GpDestScratch), sourceLow, temp, highHalf: false);
    } else {
      this.EmitLowDwordAlu(mnemonic, destination, sourceLow, temp);
    }

    this._asm.Pushf(); this._asm.Pop(temp); this._asm.Mov(this.GpScratch(state, GpLowFlagsScratch), temp);

    if (mnemonic is "CMP" or "TEST") {
      this._asm.Mov(temp, this.GpScratch(state, GpDestScratch + 2));
      if (mnemonic == "CMP") this._asm.Sbb(temp, sourceHigh); else this._asm.And(temp, sourceHigh);
    } else {
      this.EmitWordAlu(mnemonic, destHigh, sourceHigh, temp, highHalf: true);
    }

    this._asm.Pushf(); this._asm.Pop(temp); this._asm.Mov(this.GpScratch(state, GpHighFlagsScratch), temp);

    if (modifies) {
      if (destinationInitiallyInScratch)
        this.WriteDwordPlace(state, destination, GpDestScratch, target);
      else if (destination.Register is { } rr)
        this.BridgeHighToNativeIfAvailable(state, rr, target);
    }

    this.MergeDwordFlags(state, temp, logical, preserveCarry);
    return true;
  }

  private void EmitLowDwordAlu(string mnemonic, DwordPlace destination, Mem sourceLow, Reg temp) {
    if (destination.Register is { } r) {
      var low = WordGp(r);
      switch (mnemonic) {
        case "ADD": this._asm.Add(low, sourceLow); break;
        case "ADC": this._asm.Adc(low, sourceLow); break;
        case "SUB": this._asm.Sub(low, sourceLow); break;
        case "SBB": this._asm.Sbb(low, sourceLow); break;
        case "AND": this._asm.And(low, sourceLow); break;
        case "OR": this._asm.Or(low, sourceLow); break;
        case "XOR": this._asm.Xor(low, sourceLow); break;
      }
      return;
    }
    this.EmitWordAlu(mnemonic, destination.Memory!.Value.WithSize(OperandSize.Word), sourceLow, temp, highHalf: false);
  }

  private void EmitWordAlu(string mnemonic, Mem destination, Mem source, Reg temp, bool highHalf) {
    this._asm.Mov(temp, source);
    switch (mnemonic) {
      case "ADD" when highHalf: this._asm.Adc(destination, temp); break;
      case "ADD": this._asm.Add(destination, temp); break;
      case "ADC": this._asm.Adc(destination, temp); break;
      case "SUB" when highHalf: this._asm.Sbb(destination, temp); break;
      case "SUB": this._asm.Sub(destination, temp); break;
      case "SBB": this._asm.Sbb(destination, temp); break;
      case "AND": this._asm.And(destination, temp); break;
      case "OR": this._asm.Or(destination, temp); break;
      case "XOR": this._asm.Xor(destination, temp); break;
    }
  }

  private void MergeDwordFlags(VirtualIsaState state, Reg temp, bool logical, bool preserveCarry) {
    var merged = this.GpScratch(state, GpMergedFlagsScratch);
    var original = this.GpScratch(state, GpOrigFlagsScratch);
    var low = this.GpScratch(state, GpLowFlagsScratch);
    var high = this.GpScratch(state, GpHighFlagsScratch);
    var statusClear = logical ? 0xF73A : 0xF72A; // logical keeps undefined AF; arithmetic replaces AF too
    this._asm.Mov(temp, original); this._asm.And(temp, statusClear); this._asm.Mov(merged, temp);

    this._asm.Mov(temp, high); this._asm.And(temp, logical ? 0x0080 : 0x0881); this._asm.Or(merged, temp);
    this._asm.Mov(temp, low); this._asm.And(temp, logical ? 0x0004 : 0x0014); this._asm.Or(merged, temp);

    var noZero = this._asm.DefineLabel();
    this._asm.Mov(temp, low); this._asm.Test(temp, 0x0040); this._asm.J(Condition.Equal, noZero);
    this._asm.Mov(temp, high); this._asm.Test(temp, 0x0040); this._asm.J(Condition.Equal, noZero);
    this._asm.Or(merged, 0x0040);
    this._asm.MarkLabel(noZero);

    if (preserveCarry) {
      this._asm.And(merged, 0xFFFE);
      this._asm.Mov(temp, original); this._asm.And(temp, 1); this._asm.Or(merged, temp);
    }
    this._asm.Push(merged);
    this._asm.Popf();
  }
}
