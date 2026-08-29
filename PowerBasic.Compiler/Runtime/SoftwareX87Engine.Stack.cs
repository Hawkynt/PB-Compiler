using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

public sealed partial class SoftwareX87Engine {
  #region inline wrappers

  private bool EmitInlineInit(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    if (!RequireNoOperands(operands, out error)) return false;
    return this.EmitInit();
  }

  private bool EmitInlineClear(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    if (!RequireNoOperands(operands, out error)) return false;
    return this.EmitClearExceptions();
  }

  private bool EmitInlineWait(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) =>
    RequireNoOperands(operands, out error);

  private bool EmitInlineConstant(string mnemonic, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    if (!RequireNoOperands(operands, out error)) return false;
    return this.EmitConstant(mnemonic);
  }

  private bool EmitInlineSign(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, bool absolute, out string? error) {
    if (!RequireNoOperands(operands, out error)) return false;
    return this.EmitChangeSign(absolute);
  }

  private bool EmitInlineFxch(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    error = null;
    var index = operands.Count switch {
      0 => 1,
      1 when operands[0] is TextAssembler.ParsedAsmSt st => st.Register.Index,
      _ => -1,
    };
    if (index is < 0 or > 7) { error = "FXCH expects optional ST(i)"; return false; }
    return this.EmitExchange(index);
  }

  private bool EmitInlineFfree(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    error = null;
    if (operands.Count != 1 || operands[0] is not TextAssembler.ParsedAsmSt st) { error = "FFREE expects ST(i)"; return false; }
    return this.EmitFree(st.Register.Index);
  }

  private bool EmitInlineRotateStack(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, int delta, out string? error) {
    if (!RequireNoOperands(operands, out error)) return false;
    return this.EmitRotateLogicalStack(delta);
  }

  private bool EmitInlineStatus(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    error = null;
    if (operands.Count != 1) { error = "FSTSW/FNSTSW expects AX or word memory"; return false; }
    return operands[0] switch {
      TextAssembler.ParsedAsmRegister { Register: Reg.AX } => this.EmitStatusToAx(),
      TextAssembler.ParsedAsmMemory m => this.EmitStoreStatus(m.Memory),
      _ => Fail("FSTSW/FNSTSW expects AX or word memory", out error),
    };
  }

  private bool EmitInlineStoreControl(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    error = null;
    if (operands.Count != 1 || operands[0] is not TextAssembler.ParsedAsmMemory m)
      return Fail("FSTCW/FNSTCW expects word memory", out error);
    return this.EmitStoreControl(m.Memory);
  }

  private bool EmitInlineLoadControl(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    error = null;
    if (operands.Count != 1 || operands[0] is not TextAssembler.ParsedAsmMemory m)
      return Fail("FLDCW expects word memory", out error);
    return this.EmitLoadControl(m.Memory);
  }

  private bool EmitInlineFld(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    error = null;
    if (operands.Count != 1) return Fail("FLD expects one operand", out error);
    return operands[0] switch {
      TextAssembler.ParsedAsmSt st => this.EmitLoadStack(st.Register.Index),
      TextAssembler.ParsedAsmMemory m when m.Memory.Size == OperandSize.Dword => this.EmitLoadReal(m.Memory, 32),
      TextAssembler.ParsedAsmMemory m when m.Memory.Size == OperandSize.Qword => this.EmitLoadReal(m.Memory, 64),
      TextAssembler.ParsedAsmMemory m when m.Memory.Size == OperandSize.Tbyte => this.EmitLoadReal(m.Memory, 80),
      _ => Fail("FLD requires ST(i) or dword/qword/tbyte real memory", out error),
    };
  }

  private bool EmitInlineFst(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, bool pop, out string? error) {
    error = null;
    if (operands.Count != 1) return Fail(pop ? "FSTP expects one operand" : "FST expects one operand", out error);
    return operands[0] switch {
      TextAssembler.ParsedAsmSt st => this.EmitStoreStack(st.Register.Index, pop),
      TextAssembler.ParsedAsmMemory m when m.Memory.Size == OperandSize.Dword => this.EmitStoreReal(m.Memory, 32, pop),
      TextAssembler.ParsedAsmMemory m when m.Memory.Size == OperandSize.Qword => this.EmitStoreReal(m.Memory, 64, pop),
      TextAssembler.ParsedAsmMemory m when pop && m.Memory.Size == OperandSize.Tbyte => this.EmitStoreReal(m.Memory, 80, pop),
      _ => Fail("FST/FSTP requires ST(i) or appropriately-sized real memory", out error),
    };
  }

  private bool EmitInlineFild(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    error = null;
    if (operands.Count != 1 || operands[0] is not TextAssembler.ParsedAsmMemory m)
      return Fail("FILD expects integer memory", out error);
    return m.Memory.Size switch {
      OperandSize.Word => this.EmitLoadInteger(m.Memory, 16),
      OperandSize.Dword => this.EmitLoadInteger(m.Memory, 32),
      OperandSize.Qword => this.EmitLoadInteger(m.Memory, 64),
      _ => Fail("FILD integer memory must be word/dword/qword", out error),
    };
  }

  private bool EmitInlineFist(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, bool pop, out string? error) {
    error = null;
    if (operands.Count != 1 || operands[0] is not TextAssembler.ParsedAsmMemory m)
      return Fail("FIST/FISTP expects integer memory", out error);
    return m.Memory.Size switch {
      OperandSize.Word => this.EmitStoreInteger(m.Memory, 16, pop),
      OperandSize.Dword => this.EmitStoreInteger(m.Memory, 32, pop),
      OperandSize.Qword when pop => this.EmitStoreInteger(m.Memory, 64, pop),
      _ => Fail("FIST/FISTP integer memory width is invalid", out error),
    };
  }

  private static bool Fail(string text, out string? error) { error = text; return false; }

  #endregion

  #region control/status

  private bool EmitInit() {
    this._asm.Push(Reg.AX);
    this._asm.Mov(Reg.AX, 0x037F); this._asm.Mov(this.Control, Reg.AX);
    this._asm.Xor(Reg.AX, Reg.AX); this._asm.Mov(this.Status, Reg.AX); this._asm.Mov(this.Valid, Reg.AL);
    this._asm.Pop(Reg.AX);
    return true;
  }

  private bool EmitClearExceptions() {
    this._asm.Push(Reg.AX);
    this._asm.Mov(Reg.AX, this.Status);
    this._asm.And(Reg.AX, 0x7F00); // preserve C0/C1/C2/TOP/C3; clear exception flags, SF, ES, B
    this._asm.Mov(this.Status, Reg.AX);
    this._asm.Pop(Reg.AX);
    return true;
  }

  private bool EmitStatusToAx() { this._asm.Mov(Reg.AX, this.Status); return true; }

  private bool EmitStoreStatus(Mem destination) {
    this._asm.Push(Reg.AX);
    this._asm.Mov(Reg.AX, this.Status); this._asm.Mov(destination.WithSize(OperandSize.Word), Reg.AX);
    this._asm.Pop(Reg.AX);
    return true;
  }

  private bool EmitStoreControl(Mem destination) {
    this._asm.Push(Reg.AX);
    this._asm.Mov(Reg.AX, this.Control); this._asm.Mov(destination.WithSize(OperandSize.Word), Reg.AX);
    this._asm.Pop(Reg.AX);
    return true;
  }

  private bool EmitLoadControl(Mem source) {
    this._asm.Push(Reg.AX);
    this._asm.Mov(Reg.AX, source.WithSize(OperandSize.Word)); this._asm.Mov(this.Control, Reg.AX);
    this._asm.Pop(Reg.AX);
    return true;
  }

  internal void SetConditionCodes(bool c0, bool c2, bool c3) {
    this._asm.Push(Reg.AX);
    this._asm.Mov(Reg.AX, this.Status);
    this._asm.And(Reg.AX, 0xBAFF); // clear C0(8), C2(10), C3(14); preserve C1 and TOP
    if (c0) this._asm.Or(Reg.AX, 0x0100);
    if (c2) this._asm.Or(Reg.AX, 0x0400);
    if (c3) this._asm.Or(Reg.AX, 0x4000);
    this._asm.Mov(this.Status, Reg.AX);
    this._asm.Pop(Reg.AX);
  }

  internal void SetStatusBits(ushort bits) {
    this._asm.Push(Reg.AX);
    this._asm.Mov(Reg.AX, this.Status); this._asm.Or(Reg.AX, bits); this._asm.Mov(this.Status, Reg.AX);
    this._asm.Pop(Reg.AX);
  }

  #endregion

  #region stack operations

  private bool EmitChangeSign(bool absolute) {
    this._asm.Push(Reg.AX);
    this._asm.Mov(Reg.AX, this.Slot(0, Meta, OperandSize.Word));
    if (absolute) this._asm.And(Reg.AX, 0xFFFE); else this._asm.Xor(Reg.AX, SignMask);
    this._asm.Mov(this.Slot(0, Meta, OperandSize.Word), Reg.AX);
    this._asm.Pop(Reg.AX);
    return true;
  }

  private bool EmitConstant(string mnemonic) {
    this.EmitPushFromConstant(mnemonic);
    return true;
  }

  private void EmitPushFromConstant(string mnemonic) {
    this.EmitPushEmpty();
    (short exp, ushort meta, ushort w0, ushort w1, ushort w2, ushort w3) = mnemonic switch {
      "FLDZ" => (0, ClassZero, 0, 0, 0, 0),
      "FLD1" => (0, ClassFinite, 0, 0, 0, 0x8000),
      "FLDPI" => (1, ClassFinite, 0xC235, 0x2168, 0xDAA2, 0xC90F),
      "FLDL2E" => (0, ClassFinite, 0xF0BC, 0x5C17, 0x3B29, 0xB8AA),
      "FLDL2T" => (1, ClassFinite, 0x8AFE, 0xCD1B, 0x784B, 0xD49A),
      "FLDLG2" => (-2, ClassFinite, 0xF799, 0xFBCF, 0x9A84, 0x9A20),
      "FLDLN2" => (-1, ClassFinite, 0x79AC, 0xD1CF, 0x17F7, 0xB172),
      _ => throw new ArgumentOutOfRangeException(nameof(mnemonic)),
    };
    this._asm.Mov(this.Slot(0, Sig0, OperandSize.Word), w0);
    this._asm.Mov(this.Slot(0, Sig1, OperandSize.Word), w1);
    this._asm.Mov(this.Slot(0, Sig2, OperandSize.Word), w2);
    this._asm.Mov(this.Slot(0, Sig3, OperandSize.Word), w3);
    this._asm.Mov(this.Slot(0, Exponent, OperandSize.Word), exp);
    this._asm.Mov(this.Slot(0, Meta, OperandSize.Word), meta);
  }

  private bool EmitLoadStack(int index) {
    this.CopySlotToScratch(index, ScratchC);
    this.EmitPushEmpty();
    this.CopyScratchToSlot(ScratchC, 0);
    return true;
  }

  private bool EmitStoreStack(int index, bool pop) {
    this.CopySlot(0, index);
    this.CopyValidity(0, index);
    if (pop) this.EmitPop();
    return true;
  }

  private bool EmitExchange(int index) {
    if (index == 0) return true;
    this.CopySlotToScratch(0, ScratchC);
    this.CopySlot(index, 0);
    this.CopyScratchToSlot(ScratchC, index);
    this.SwapValidity(0, index);
    return true;
  }

  private bool EmitFree(int index) {
    this._asm.Push(Reg.AX);
    this._asm.Mov(Reg.AL, this.Valid);
    this._asm.And(Reg.AL, (byte)~(1 << index));
    this._asm.Mov(this.Valid, Reg.AL);
    this._asm.Pop(Reg.AX);
    return true;
  }

  private bool EmitRotateLogicalStack(int delta) {
    if (delta > 0) {
      this.CopySlotToScratch(0, ScratchC);
      for (var i = 0; i < 7; ++i) this.CopySlot(i + 1, i);
      this.CopyScratchToSlot(ScratchC, 7);
      this._asm.Push(Reg.AX); this._asm.Mov(Reg.AL, this.Valid); this._asm.Ror(Reg.AL, 1); this._asm.Mov(this.Valid, Reg.AL); this._asm.Pop(Reg.AX);
      this.AdjustTop(+1);
    } else {
      this.CopySlotToScratch(7, ScratchC);
      for (var i = 7; i > 0; --i) this.CopySlot(i - 1, i);
      this.CopyScratchToSlot(ScratchC, 0);
      this._asm.Push(Reg.AX); this._asm.Mov(Reg.AL, this.Valid); this._asm.Rol(Reg.AL, 1); this._asm.Mov(this.Valid, Reg.AL); this._asm.Pop(Reg.AX);
      this.AdjustTop(-1);
    }
    return true;
  }

  internal void EmitPushEmpty() {
    for (var i = 7; i > 0; --i) this.CopySlot(i - 1, i);
    this._asm.Push(Reg.AX);
    this._asm.Mov(Reg.AL, this.Valid); this._asm.Shl(Reg.AL, 1); this._asm.Or(Reg.AL, 1); this._asm.Mov(this.Valid, Reg.AL);
    this._asm.Pop(Reg.AX);
    this.AdjustTop(-1);
  }

  internal void EmitPop() {
    for (var i = 0; i < 7; ++i) this.CopySlot(i + 1, i);
    this._asm.Push(Reg.AX);
    this._asm.Mov(Reg.AL, this.Valid); this._asm.Shr(Reg.AL, 1); this._asm.Mov(this.Valid, Reg.AL);
    this._asm.Pop(Reg.AX);
    this.AdjustTop(+1);
  }

  private void AdjustTop(int delta) {
    this._asm.Push(Reg.AX); this._asm.Push(Reg.CX);
    this._asm.Mov(Reg.AX, this.Status);
    this._asm.Mov(Reg.CX, Reg.AX); this._asm.Shr(Reg.CX, 11); this._asm.And(Reg.CX, 7);
    this._asm.Add(Reg.CX, delta); this._asm.And(Reg.CX, 7);
    this._asm.And(Reg.AX, 0xC7FF);
    for (var i = 0; i < 11; ++i) this._asm.Shl(Reg.CX, 1);
    this._asm.Or(Reg.AX, Reg.CX); this._asm.Mov(this.Status, Reg.AX);
    this._asm.Pop(Reg.CX); this._asm.Pop(Reg.AX);
  }

  internal void CopySlot(int source, int destination) {
    if (source == destination) return;
    this._asm.Push(Reg.AX);
    for (var offset = 0; offset < SlotBytes; offset += 2) {
      this._asm.Mov(Reg.AX, this.Slot(source, offset, OperandSize.Word));
      this._asm.Mov(this.Slot(destination, offset, OperandSize.Word), Reg.AX);
    }
    this._asm.Pop(Reg.AX);
  }

  internal void CopySlotToScratch(int source, int scratchOffset) {
    this._asm.Push(Reg.AX);
    for (var offset = 0; offset < SlotBytes; offset += 2) {
      this._asm.Mov(Reg.AX, this.Slot(source, offset, OperandSize.Word));
      this._asm.Mov(this.Scratch(scratchOffset + offset, OperandSize.Word), Reg.AX);
    }
    this._asm.Pop(Reg.AX);
  }

  internal void CopyScratchToSlot(int scratchOffset, int destination) {
    this._asm.Push(Reg.AX);
    for (var offset = 0; offset < SlotBytes; offset += 2) {
      this._asm.Mov(Reg.AX, this.Scratch(scratchOffset + offset, OperandSize.Word));
      this._asm.Mov(this.Slot(destination, offset, OperandSize.Word), Reg.AX);
    }
    this._asm.Pop(Reg.AX);
  }

  internal void CopyScratch(int source, int destination) {
    this._asm.Push(Reg.AX);
    for (var offset = 0; offset < SlotBytes; offset += 2) {
      this._asm.Mov(Reg.AX, this.Scratch(source + offset, OperandSize.Word));
      this._asm.Mov(this.Scratch(destination + offset, OperandSize.Word), Reg.AX);
    }
    this._asm.Pop(Reg.AX);
  }

  private void CopyValidity(int source, int destination) {
    if (source == destination) return;
    var sourceSet = this._asm.DefineLabel();
    var done = this._asm.DefineLabel();
    this._asm.Push(Reg.AX);
    this._asm.Mov(Reg.AL, this.Valid); this._asm.Test(Reg.AL, (byte)(1 << source));
    this._asm.J(Condition.NotEqual, sourceSet);
    this._asm.And(Reg.AL, (byte)~(1 << destination)); this._asm.Jmp(done);
    this._asm.MarkLabel(sourceSet);
    this._asm.Or(Reg.AL, (byte)(1 << destination));
    this._asm.MarkLabel(done);
    this._asm.Mov(this.Valid, Reg.AL); this._asm.Pop(Reg.AX);
  }

  private void SwapValidity(int a, int b) {
    if (a == b) return;
    var same = this._asm.DefineLabel();
    var done = this._asm.DefineLabel();
    var mask = (byte)((1 << a) | (1 << b));
    this._asm.Push(Reg.AX);
    this._asm.Mov(Reg.AL, this.Valid); this._asm.Mov(Reg.AH, Reg.AL);
    this._asm.And(Reg.AL, (byte)(1 << a)); this._asm.And(Reg.AH, (byte)(1 << b));
    this._asm.Cmp(Reg.AL, 0); this._asm.J(Condition.Equal, same);
    this._asm.Cmp(Reg.AH, 0); this._asm.J(Condition.NotEqual, done);
    this._asm.Mov(Reg.AL, this.Valid); this._asm.Xor(Reg.AL, mask); this._asm.Mov(this.Valid, Reg.AL); this._asm.Jmp(done);
    this._asm.MarkLabel(same);
    this._asm.Cmp(Reg.AH, 0); this._asm.J(Condition.Equal, done);
    this._asm.Mov(Reg.AL, this.Valid); this._asm.Xor(Reg.AL, mask); this._asm.Mov(this.Valid, Reg.AL);
    this._asm.MarkLabel(done); this._asm.Pop(Reg.AX);
  }

  #endregion
}
