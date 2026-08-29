using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

/// <summary>
/// 8086 software implementation of the x87 architectural stack. Values are stored in their native
/// 80-bit extended representation (64-bit explicit significand followed by sign/exponent), never as
/// host <see cref="double"/>. This first layer owns stack/control/status semantics and exact extended
/// loads/stores/constants; arithmetic helpers build on the same raw representation.
/// </summary>
public sealed class SoftwareX87Backend {
  private const int SlotBytes = 10;
  private const int SlotCount = 8;
  private const int SlotsBytes = SlotBytes * SlotCount;

  private readonly Assembler _asm;
  private readonly Label _slots;
  private readonly Label _valid;
  private readonly Label _control;
  private readonly Label _status;
  private readonly Label _scratch;
  private readonly TextAssembler _parser;
  private bool _emitted;

  public SoftwareX87Backend(Assembler assembler) {
    this._asm = assembler;
    this._slots = assembler.DefineLabel();
    this._valid = assembler.DefineLabel();
    this._control = assembler.DefineLabel();
    this._status = assembler.DefineLabel();
    this._scratch = assembler.DefineLabel();
    this._parser = new TextAssembler(assembler);
  }

  /// <summary>Emits one software x87 inline-assembly instruction.</summary>
  public bool TryEmitInline(string mnemonic, string operandsText, IAsmSymbolResolver? resolver, out string? error) {
    this.EnsureState();
    error = null;
    mnemonic = mnemonic.ToUpperInvariant();
    if (!this._parser.TryParseOperands(operandsText, resolver, out var operands, out error))
      return false;

    switch (mnemonic) {
      case "FINIT" or "FNINIT": return this.EmitInit(operands, out error);
      case "FCLEX" or "FNCLEX": return this.EmitClearExceptions(operands, out error);
      case "FWAIT" or "WAIT": return this.RequireNoOperands(operands, out error);
      case "FCHS": return this.EmitSignChange(operands, clear: false, out error);
      case "FABS": return this.EmitSignChange(operands, clear: true, out error);
      case "FLDZ": return this.EmitConstant(operands, 0x0000, 0, 0, 0, 0, out error);
      case "FLD1": return this.EmitConstant(operands, 0x3FFF, 0x0000, 0x0000, 0x0000, 0x8000, out error);
      case "FLDPI": return this.EmitConstant(operands, 0x4000, 0xC235, 0x2168, 0xDAA2, 0xC90F, out error);
      case "FLDL2E": return this.EmitConstant(operands, 0x3FFF, 0xF0BC, 0x5C17, 0x3B29, 0xB8AA, out error);
      case "FLDL2T": return this.EmitConstant(operands, 0x4000, 0x8AFE, 0xCD1B, 0x784B, 0xD49A, out error);
      case "FLDLG2": return this.EmitConstant(operands, 0x3FFD, 0xF799, 0xFBCF, 0x9A84, 0x9A20, out error);
      case "FLDLN2": return this.EmitConstant(operands, 0x3FFE, 0x79AC, 0xD1CF, 0x17F7, 0xB172, out error);
      case "FLD": return this.EmitFld(operands, out error);
      case "FST": return this.EmitFst(operands, pop: false, out error);
      case "FSTP": return this.EmitFst(operands, pop: true, out error);
      case "FXCH": return this.EmitFxch(operands, out error);
      case "FFREE": return this.EmitFfree(operands, out error);
      case "FINCSTP": return this.EmitRotateStack(operands, incrementTop: true, out error);
      case "FDECSTP": return this.EmitRotateStack(operands, incrementTop: false, out error);
      case "FSTSW" or "FNSTSW": return this.EmitStoreStatus(operands, out error);
      case "FSTCW" or "FNSTCW": return this.EmitStoreControl(operands, out error);
      case "FLDCW": return this.EmitLoadControl(operands, out error);
      default:
        error = null;
        return false;
    }
  }

  private void EnsureState() {
    if (this._emitted)
      return;
    this._emitted = true;
    var over = this._asm.DefineLabel();
    this._asm.Jmp(over);
    this._asm.Align(2);
    this._asm.MarkLabel(this._slots);
    this._asm.Db(new byte[SlotsBytes]);
    this._asm.MarkLabel(this._valid);
    this._asm.Db(0);                 // bit n = logical ST(n) contains a value
    this._asm.Align(2);
    this._asm.MarkLabel(this._control);
    this._asm.Dw(0x037F);            // all exceptions masked, 64-bit precision, round-to-nearest/even
    this._asm.MarkLabel(this._status);
    this._asm.Dw(0);                 // TOP is maintained in bits 11..13
    this._asm.MarkLabel(this._scratch);
    this._asm.Db(new byte[64]);       // conversions/arithmetic staging; CS-relative
    this._asm.MarkLabel(over);
  }

  private Mem Slot(int index, int displacement = 0, OperandSize size = OperandSize.None) =>
    Mem.At(this._slots, index * SlotBytes + displacement).WithSize(size).Cs();

  private Mem Scratch(int displacement = 0, OperandSize size = OperandSize.None) =>
    Mem.At(this._scratch, displacement).WithSize(size).Cs();

  private static bool RequireNoOperands(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    if (operands.Count == 0) { error = null; return true; }
    error = "x87 instruction takes no operands";
    return false;
  }

  private bool EmitInit(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    if (!RequireNoOperands(operands, out error)) return false;
    this._asm.Push(Reg.AX);
    this._asm.Mov(Reg.AX, 0x037F);
    this._asm.Mov(Mem.Word(this._control).Cs(), Reg.AX);
    this._asm.Xor(Reg.AX, Reg.AX);
    this._asm.Mov(Mem.Word(this._status).Cs(), Reg.AX);
    this._asm.Mov(Mem.Byte(this._valid).Cs(), Reg.AL);
    this._asm.Pop(Reg.AX);
    return true;
  }

  private bool EmitClearExceptions(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    if (!RequireNoOperands(operands, out error)) return false;
    this._asm.Push(Reg.AX);
    this._asm.Mov(Reg.AX, Mem.Word(this._status).Cs());
    this._asm.And(Reg.AX, 0x7F00); // keep TOP/condition/busy, clear IE..PE/SF/ES
    this._asm.Mov(Mem.Word(this._status).Cs(), Reg.AX);
    this._asm.Pop(Reg.AX);
    return true;
  }

  private bool EmitSignChange(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, bool clear, out string? error) {
    if (!RequireNoOperands(operands, out error)) return false;
    this._asm.Push(Reg.AX);
    this._asm.Mov(Reg.AX, this.Slot(0, 8, OperandSize.Word));
    if (clear) this._asm.And(Reg.AX, 0x7FFF); else this._asm.Xor(Reg.AX, 0x8000);
    this._asm.Mov(this.Slot(0, 8, OperandSize.Word), Reg.AX);
    this._asm.Pop(Reg.AX);
    return true;
  }

  private bool EmitConstant(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, ushort signExp,
      ushort w0, ushort w1, ushort w2, ushort w3, out string? error) {
    if (!RequireNoOperands(operands, out error)) return false;
    this.EmitPushEmpty();
    this._asm.Mov(this.Slot(0, 0, OperandSize.Word), w0);
    this._asm.Mov(this.Slot(0, 2, OperandSize.Word), w1);
    this._asm.Mov(this.Slot(0, 4, OperandSize.Word), w2);
    this._asm.Mov(this.Slot(0, 6, OperandSize.Word), w3);
    this._asm.Mov(this.Slot(0, 8, OperandSize.Word), signExp);
    return true;
  }

  private bool EmitFld(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    error = null;
    if (operands.Count != 1) { error = "FLD expects one memory or ST(i) operand"; return false; }
    switch (operands[0]) {
      case TextAssembler.ParsedAsmSt st:
        if (st.Register.Index is < 0 or > 7) { error = "ST index must be 0..7"; return false; }
        // Stage before push because logical stack shifting changes ST(i).
        this.CopySlotToScratch(st.Register.Index, 0);
        this.EmitPushEmpty();
        this.CopyScratchToSlot(0, 0);
        return true;
      case TextAssembler.ParsedAsmMemory memory when memory.Memory.Size == OperandSize.Tbyte:
        this.CopyRawMemoryToScratch(memory.Memory, 10);
        this.EmitPushEmpty();
        this.CopyScratchToSlot(0, 0);
        return true;
      case TextAssembler.ParsedAsmMemory memory when memory.Memory.Size is OperandSize.Dword or OperandSize.Qword:
        error = "FLD m32/m64 conversion is provided by the software x87 arithmetic/conversion layer";
        return false;
      default:
        error = "FLD software path expects ST(i) or explicitly-sized real memory";
        return false;
    }
  }

  private bool EmitFst(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, bool pop, out string? error) {
    error = null;
    if (operands.Count != 1) { error = $"{(pop ? "FSTP" : "FST")} expects one operand"; return false; }
    switch (operands[0]) {
      case TextAssembler.ParsedAsmSt st:
        this.CopySlot(0, st.Register.Index);
        if (pop) this.EmitPop();
        return true;
      case TextAssembler.ParsedAsmMemory memory when memory.Memory.Size == OperandSize.Tbyte:
        this.CopySlotToRawMemory(0, memory.Memory, 10);
        if (pop) this.EmitPop();
        return true;
      case TextAssembler.ParsedAsmMemory memory when memory.Memory.Size is OperandSize.Dword or OperandSize.Qword:
        error = "FST/FSTP m32/m64 conversion is provided by the software x87 arithmetic/conversion layer";
        return false;
      default:
        error = "FST/FSTP software path expects ST(i) or explicitly-sized real memory";
        return false;
    }
  }

  private bool EmitFxch(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    error = null;
    var index = operands.Count switch {
      0 => 1,
      1 when operands[0] is TextAssembler.ParsedAsmSt st => st.Register.Index,
      _ => -1,
    };
    if (index is < 0 or > 7) { error = "FXCH expects optional ST(i)"; return false; }
    this.CopySlotToScratch(0, 0);
    this.CopySlot(index, 0);
    this.CopyScratchToSlot(index, 0);
    this.EmitSwapValidity(0, index);
    return true;
  }

  private bool EmitFfree(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    error = null;
    if (operands.Count != 1 || operands[0] is not TextAssembler.ParsedAsmSt st) { error = "FFREE expects ST(i)"; return false; }
    this._asm.Push(Reg.AX);
    this._asm.Mov(Reg.AL, Mem.Byte(this._valid).Cs());
    this._asm.And(Reg.AL, (byte)~(1 << st.Register.Index));
    this._asm.Mov(Mem.Byte(this._valid).Cs(), Reg.AL);
    this._asm.Pop(Reg.AX);
    return true;
  }

  private bool EmitRotateStack(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, bool incrementTop, out string? error) {
    if (!RequireNoOperands(operands, out error)) return false;
    if (incrementTop) {
      this.CopySlotToScratch(0, 0);
      for (var i = 0; i < 7; ++i) this.CopySlot(i + 1, i);
      this.CopyScratchToSlot(7, 0);
      this.EmitRotateValidityRight();
      this.EmitAdjustTop(+1);
    } else {
      this.CopySlotToScratch(7, 0);
      for (var i = 7; i > 0; --i) this.CopySlot(i - 1, i);
      this.CopyScratchToSlot(0, 0);
      this.EmitRotateValidityLeft();
      this.EmitAdjustTop(-1);
    }
    return true;
  }

  private bool EmitStoreStatus(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    error = null;
    if (operands.Count != 1) { error = "FSTSW/FNSTSW expects AX or word memory"; return false; }
    switch (operands[0]) {
      case TextAssembler.ParsedAsmRegister { Register: Reg.AX }:
        this._asm.Mov(Reg.AX, Mem.Word(this._status).Cs());
        return true;
      case TextAssembler.ParsedAsmMemory memory:
        this._asm.Push(Reg.AX);
        this._asm.Mov(Reg.AX, Mem.Word(this._status).Cs());
        this._asm.Mov(memory.Memory.WithSize(OperandSize.Word), Reg.AX);
        this._asm.Pop(Reg.AX);
        return true;
      default:
        error = "FSTSW/FNSTSW expects AX or word memory";
        return false;
    }
  }

  private bool EmitStoreControl(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    error = null;
    if (operands.Count != 1 || operands[0] is not TextAssembler.ParsedAsmMemory memory) { error = "FSTCW/FNSTCW expects word memory"; return false; }
    this._asm.Push(Reg.AX);
    this._asm.Mov(Reg.AX, Mem.Word(this._control).Cs());
    this._asm.Mov(memory.Memory.WithSize(OperandSize.Word), Reg.AX);
    this._asm.Pop(Reg.AX);
    return true;
  }

  private bool EmitLoadControl(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    error = null;
    if (operands.Count != 1 || operands[0] is not TextAssembler.ParsedAsmMemory memory) { error = "FLDCW expects word memory"; return false; }
    this._asm.Push(Reg.AX);
    this._asm.Mov(Reg.AX, memory.Memory.WithSize(OperandSize.Word));
    this._asm.Mov(Mem.Word(this._control).Cs(), Reg.AX);
    this._asm.Pop(Reg.AX);
    return true;
  }

  private void EmitPushEmpty() {
    for (var i = 7; i > 0; --i) this.CopySlot(i - 1, i);
    this._asm.Push(Reg.AX);
    this._asm.Mov(Reg.AL, Mem.Byte(this._valid).Cs());
    this._asm.Shl(Reg.AL, 1);
    this._asm.Or(Reg.AL, 1);
    this._asm.Mov(Mem.Byte(this._valid).Cs(), Reg.AL);
    this._asm.Pop(Reg.AX);
    this.EmitAdjustTop(-1);
  }

  private void EmitPop() {
    for (var i = 0; i < 7; ++i) this.CopySlot(i + 1, i);
    this._asm.Push(Reg.AX);
    this._asm.Mov(Reg.AL, Mem.Byte(this._valid).Cs());
    this._asm.Shr(Reg.AL, 1);
    this._asm.Mov(Mem.Byte(this._valid).Cs(), Reg.AL);
    this._asm.Pop(Reg.AX);
    this.EmitAdjustTop(+1);
  }

  private void EmitAdjustTop(int delta) {
    this._asm.Push(Reg.AX);
    this._asm.Mov(Reg.AX, Mem.Word(this._status).Cs());
    this._asm.Push(Reg.CX);
    this._asm.Mov(Reg.CX, Reg.AX);
    this._asm.Shr(Reg.CX, 11);
    this._asm.And(Reg.CX, 7);
    this._asm.Add(Reg.CX, delta);
    this._asm.And(Reg.CX, 7);
    this._asm.And(Reg.AX, 0xC7FF);
    this._asm.Shl(Reg.CX, 1); this._asm.Shl(Reg.CX, 1); this._asm.Shl(Reg.CX, 1);
    this._asm.Shl(Reg.CX, 1); this._asm.Shl(Reg.CX, 1); this._asm.Shl(Reg.CX, 1);
    this._asm.Shl(Reg.CX, 1); this._asm.Shl(Reg.CX, 1); this._asm.Shl(Reg.CX, 1);
    this._asm.Shl(Reg.CX, 1); this._asm.Shl(Reg.CX, 1);
    this._asm.Or(Reg.AX, Reg.CX);
    this._asm.Mov(Mem.Word(this._status).Cs(), Reg.AX);
    this._asm.Pop(Reg.CX);
    this._asm.Pop(Reg.AX);
  }

  private void CopySlot(int source, int destination) {
    if (source == destination) return;
    this._asm.Push(Reg.AX);
    for (var offset = 0; offset < SlotBytes; offset += 2) {
      this._asm.Mov(Reg.AX, this.Slot(source, offset, OperandSize.Word));
      this._asm.Mov(this.Slot(destination, offset, OperandSize.Word), Reg.AX);
    }
    this._asm.Pop(Reg.AX);
  }

  private void CopySlotToScratch(int source, int scratchOffset) {
    this._asm.Push(Reg.AX);
    for (var offset = 0; offset < SlotBytes; offset += 2) {
      this._asm.Mov(Reg.AX, this.Slot(source, offset, OperandSize.Word));
      this._asm.Mov(this.Scratch(scratchOffset + offset, OperandSize.Word), Reg.AX);
    }
    this._asm.Pop(Reg.AX);
  }

  private void CopyScratchToSlot(int destination, int scratchOffset) {
    this._asm.Push(Reg.AX);
    for (var offset = 0; offset < SlotBytes; offset += 2) {
      this._asm.Mov(Reg.AX, this.Scratch(scratchOffset + offset, OperandSize.Word));
      this._asm.Mov(this.Slot(destination, offset, OperandSize.Word), Reg.AX);
    }
    this._asm.Pop(Reg.AX);
  }

  private void CopyRawMemoryToScratch(Mem source, int count) {
    this._asm.Push(Reg.AX);
    for (var offset = 0; offset < count; offset += 2) {
      this._asm.Mov(Reg.AX, source.Offset(offset).WithSize(OperandSize.Word));
      this._asm.Mov(this.Scratch(offset, OperandSize.Word), Reg.AX);
    }
    this._asm.Pop(Reg.AX);
  }

  private void CopySlotToRawMemory(int source, Mem destination, int count) {
    this._asm.Push(Reg.AX);
    for (var offset = 0; offset < count; offset += 2) {
      this._asm.Mov(Reg.AX, this.Slot(source, offset, OperandSize.Word));
      this._asm.Mov(destination.Offset(offset).WithSize(OperandSize.Word), Reg.AX);
    }
    this._asm.Pop(Reg.AX);
  }

  private void EmitSwapValidity(int a, int b) {
    if (a == b) return;
    var same = this._asm.DefineLabel();
    var done = this._asm.DefineLabel();
    var mask = (byte)((1 << a) | (1 << b));
    this._asm.Push(Reg.AX);
    this._asm.Mov(Reg.AL, Mem.Byte(this._valid).Cs());
    this._asm.Mov(Reg.AH, Reg.AL);
    this._asm.And(Reg.AL, (byte)(1 << a));
    this._asm.And(Reg.AH, (byte)(1 << b));
    this._asm.Cmp(Reg.AL, 0);
    this._asm.J(Condition.Equal, same);
    this._asm.Cmp(Reg.AH, 0);
    this._asm.J(Condition.NotEqual, done);
    this._asm.Mov(Reg.AL, Mem.Byte(this._valid).Cs());
    this._asm.Xor(Reg.AL, mask);
    this._asm.Mov(Mem.Byte(this._valid).Cs(), Reg.AL);
    this._asm.Jmp(done);
    this._asm.MarkLabel(same);
    this._asm.Cmp(Reg.AH, 0);
    this._asm.J(Condition.Equal, done);
    this._asm.Mov(Reg.AL, Mem.Byte(this._valid).Cs());
    this._asm.Xor(Reg.AL, mask);
    this._asm.Mov(Mem.Byte(this._valid).Cs(), Reg.AL);
    this._asm.MarkLabel(done);
    this._asm.Pop(Reg.AX);
  }

  private void EmitRotateValidityRight() {
    this._asm.Push(Reg.AX);
    this._asm.Mov(Reg.AL, Mem.Byte(this._valid).Cs());
    this._asm.Ror(Reg.AL, 1);
    this._asm.Mov(Mem.Byte(this._valid).Cs(), Reg.AL);
    this._asm.Pop(Reg.AX);
  }

  private void EmitRotateValidityLeft() {
    this._asm.Push(Reg.AX);
    this._asm.Mov(Reg.AL, Mem.Byte(this._valid).Cs());
    this._asm.Rol(Reg.AL, 1);
    this._asm.Mov(Mem.Byte(this._valid).Cs(), Reg.AL);
    this._asm.Pop(Reg.AX);
  }
}
