namespace PowerBasic.Compiler.Asm;

public sealed partial class Assembler {

  #region MOV

  public void Mov(Reg destination, Reg source) {
    var start = this.Position;
    if (destination.IsSegment()) {
      if (destination == Reg.CS)
        throw new ArgumentException("MOV CS, r is not encodable.", nameof(destination));
      if (!source.IsWord())
        throw new ArgumentException($"MOV {destination}, {source}: source must be a 16-bit register.", nameof(source));

      this.EmitByte(0x8E);
      this.EmitModRmRegister(destination.Index(), source);
      return;
    }

    if (source.IsSegment()) {
      if (!destination.IsWord())
        throw new ArgumentException($"MOV {destination}, {source}: destination must be a 16-bit register.", nameof(destination));

      this.EmitByte(0x8C);
      this.EmitModRmRegister(source.Index(), destination);
      return;
    }

    RequireSameSize(destination, source);
    this.EmitOperandSizePrefixIf(destination.IsDword());
    this.EmitByte(destination.IsByte() ? (byte)0x88 : (byte)0x89);
    this.EmitModRmRegister(source.Index(), destination);
    if (destination.IsWord() && source.IsWord())
      this.RecordPeep(PeepKind.MovRegReg, start, destination, source, this.Position - 1);
    this.RecordSchedReg(start, RegBit(source), RegBit(destination), false, false);
  }

  public void Mov(Reg destination, Mem source) {
    var start = this.Position;
    this.EmitSegmentPrefix(source);
    if (destination.IsSegment()) {
      if (destination == Reg.CS)
        throw new ArgumentException("MOV CS, m is not encodable.", nameof(destination));

      this.EmitByte(0x8E);
      this.EmitModRmMemory(destination.Index(), source);
      return;
    }

    RequireMatchingSize(destination, source);
    this.EmitOperandSizePrefixIf(destination.IsDword());
    this.EmitByte(destination.IsByte() ? (byte)0x8A : (byte)0x8B);
    var modrmAt = this.Position;
    this.EmitModRmMemory(destination.Index(), source);
    if (destination.IsWord())
      this.RecordPeep(PeepKind.MovRegMem, start, destination, default, modrmAt);
    this.RecordSchedMem(start, 0, RegBit(destination), false, false, memRead: true, memWrite: false, source);
  }

  public void Mov(Mem destination, Reg source) {
    var start = this.Position;
    this.EmitSegmentPrefix(destination);
    if (source.IsSegment()) {
      this.EmitByte(0x8C);
      this.EmitModRmMemory(source.Index(), destination);
      return;
    }

    RequireMatchingSize(source, destination);
    this.EmitOperandSizePrefixIf(source.IsDword());
    // accumulator short form: MOV [addr], AL/AX/EAX -> A2/A3 moffs (one byte shorter than 88/89 mod=00
    // rm=110). Stores are not peephole-recorded, so this is unconditionally safe; the scheduler permutes
    // whole instruction blocks, so the shorter encoding rides along unchanged.
    if (source.Index() == 0 && destination is { Base: null, Index: null }) {
      this.EmitByte(source.IsByte() ? (byte)0xA2 : (byte)0xA3);
      this.EmitDisp16(destination);
      this.RecordSchedMem(start, RegBit(source), 0, false, false, memRead: false, memWrite: true, destination);
      return;
    }
    this.EmitByte(source.IsByte() ? (byte)0x88 : (byte)0x89);
    this.EmitModRmMemory(source.Index(), destination);
    this.RecordSchedMem(start, RegBit(source), 0, false, false, memRead: false, memWrite: true, destination);
  }

  public void Mov(Reg destination, Imm immediate) {
    var start = this.Position;
    RequireGeneralPurpose(destination, nameof(destination));
    this.EmitOperandSizePrefixIf(destination.IsDword());
    this.EmitByte((byte)((destination.IsByte() ? 0xB0 : 0xB8) + destination.Index()));
    this.EmitImmediate(destination.Size(), immediate);
    if (destination.IsWord())
      this.RecordPeep(PeepKind.MovRegImm, start, destination);
    this.RecordSchedReg(start, 0, RegBit(destination), false, false);
  }

  public void Mov(Mem destination, Imm immediate) {
    var start = this.Position;
    var size = RequireSized(destination);
    this.EmitSegmentPrefix(destination);
    this.EmitOperandSizePrefixIf(size == OperandSize.Dword);
    this.EmitByte(size == OperandSize.Byte ? (byte)0xC6 : (byte)0xC7);
    this.EmitModRmMemory(0, destination);
    this.EmitImmediate(size, immediate);
    this.RecordSchedMem(start, 0, 0, false, false, memRead: false, memWrite: true, destination);
  }

  #endregion

  #region XCHG / LEA / LDS / LES / XLAT

  public void Xchg(Reg first, Reg second) {
    RequireGeneralPurpose(first, nameof(first));
    RequireGeneralPurpose(second, nameof(second));
    RequireSameSize(first, second);

    if (!first.IsByte() && (first.Index() == 0 || second.Index() == 0)) {
      var other = first.Index() == 0 ? second : first;
      this.EmitOperandSizePrefixIf(first.IsDword());
      this.EmitByte((byte)(0x90 + other.Index()));
      return;
    }

    this.EmitOperandSizePrefixIf(first.IsDword());
    this.EmitByte(first.IsByte() ? (byte)0x86 : (byte)0x87);
    this.EmitModRmRegister(second.Index(), first);
  }

  public void Xchg(Reg register, Mem memory) {
    RequireGeneralPurpose(register, nameof(register));
    RequireMatchingSize(register, memory);
    this.EmitSegmentPrefix(memory);
    this.EmitOperandSizePrefixIf(register.IsDword());
    this.EmitByte(register.IsByte() ? (byte)0x86 : (byte)0x87);
    this.EmitModRmMemory(register.Index(), memory);
  }

  public void Xchg(Mem memory, Reg register) => this.Xchg(register, memory);

  public void Lea(Reg destination, Mem source) {
    var start = this.Position;
    RequireWordOrDword(destination, nameof(destination));
    this.EmitSegmentPrefix(source);
    this.EmitOperandSizePrefixIf(destination.IsDword());
    this.EmitByte(0x8D);
    this.EmitModRmMemory(destination.Index(), source);
    // LEA computes an address only - it reads the address registers (added by RecordSchedMem) and
    // writes the destination, but does NOT access memory (memRead/memWrite false) and sets no flags.
    this.RecordSchedMem(start, 0, RegBit(destination), false, false, false, false, source);
  }

  public void Lds(Reg destination, Mem source) => this.LoadFarPointer(0xC5, destination, source);
  public void Les(Reg destination, Mem source) => this.LoadFarPointer(0xC4, destination, source);

  private void LoadFarPointer(byte opcode, Reg destination, Mem source) {
    if (!destination.IsWord())
      throw new ArgumentException($"{destination} must be a 16-bit register.", nameof(destination));

    this.EmitSegmentPrefix(source);
    this.EmitByte(opcode);
    this.EmitModRmMemory(destination.Index(), source);
  }

  public void Xlat() => this.EmitByte(0xD7);

  #endregion

  #region PUSH / POP

  public void Push(Reg register) {
    switch (register) {
      case Reg.ES: this.EmitByte(0x06); return;
      case Reg.CS: this.EmitByte(0x0E); return;
      case Reg.SS: this.EmitByte(0x16); return;
      case Reg.DS: this.EmitByte(0x1E); return;
      case Reg.FS: this.EmitByte(0x0F); this.EmitByte(0xA0); return;
      case Reg.GS: this.EmitByte(0x0F); this.EmitByte(0xA8); return;
    }

    RequireWordOrDword(register, nameof(register));
    this.EmitOperandSizePrefixIf(register.IsDword());
    this.EmitByte((byte)(0x50 + register.Index()));
  }

  public void Pop(Reg register) {
    var start = this.Position;
    switch (register) {
      case Reg.ES: this.EmitByte(0x07); return;
      case Reg.CS: throw new ArgumentException("POP CS is not encodable.", nameof(register));
      case Reg.SS: this.EmitByte(0x17); return;
      case Reg.DS: this.EmitByte(0x1F); return;
      case Reg.FS: this.EmitByte(0x0F); this.EmitByte(0xA1); return;
      case Reg.GS: this.EmitByte(0x0F); this.EmitByte(0xA9); return;
    }

    RequireWordOrDword(register, nameof(register));
    this.EmitOperandSizePrefixIf(register.IsDword());
    this.EmitByte((byte)(0x58 + register.Index()));
    if (register.IsWord())
      this.RecordPeep(PeepKind.PopReg, start, register);
  }

  public void Push(Mem memory) {
    var size = RequireSized(memory);
    if (size is not (OperandSize.Word or OperandSize.Dword))
      throw new ArgumentException($"PUSH {memory}: only word or dword operands can be pushed.", nameof(memory));

    this.EmitSegmentPrefix(memory);
    this.EmitOperandSizePrefixIf(size == OperandSize.Dword);
    this.EmitByte(0xFF);
    this.EmitModRmMemory(6, memory);
  }

  public void Pop(Mem memory) {
    var size = RequireSized(memory);
    if (size is not (OperandSize.Word or OperandSize.Dword))
      throw new ArgumentException($"POP {memory}: only word or dword operands can be popped.", nameof(memory));

    this.EmitSegmentPrefix(memory);
    this.EmitOperandSizePrefixIf(size == OperandSize.Dword);
    this.EmitByte(0x8F);
    this.EmitModRmMemory(0, memory);
  }

  /// <summary>186+ PUSH imm: 6A for signed bytes, 68 otherwise.</summary>
  public void Push(Imm immediate) {
    if (immediate.Label is null && !immediate.IsSegmentReference && FitsSByte(immediate.Value)) {
      this.EmitByte(0x6A);
      this.EmitImmediate(OperandSize.Byte, immediate);
      return;
    }

    this.EmitByte(0x68);
    this.EmitImmediate(OperandSize.Word, immediate);
  }

  public void Pusha() => this.EmitByte(0x60);
  public void Popa() => this.EmitByte(0x61);
  public void Pushf() => this.EmitByte(0x9C);
  public void Popf() => this.EmitByte(0x9D);

  #endregion

  #region ALU group (ADD/OR/ADC/SBB/AND/SUB/XOR/CMP)

  public void Add(Reg destination, Reg source) => this.Alu(0, destination, source);
  public void Add(Reg destination, Mem source) => this.Alu(0, destination, source);
  public void Add(Mem destination, Reg source) => this.Alu(0, destination, source);
  public void Add(Reg destination, Imm immediate) => this.Alu(0, destination, immediate);
  public void Add(Mem destination, Imm immediate) => this.Alu(0, destination, immediate);

  public void Or(Reg destination, Reg source) => this.Alu(1, destination, source);
  public void Or(Reg destination, Mem source) => this.Alu(1, destination, source);
  public void Or(Mem destination, Reg source) => this.Alu(1, destination, source);
  public void Or(Reg destination, Imm immediate) => this.Alu(1, destination, immediate);
  public void Or(Mem destination, Imm immediate) => this.Alu(1, destination, immediate);

  public void Adc(Reg destination, Reg source) => this.Alu(2, destination, source);
  public void Adc(Reg destination, Mem source) => this.Alu(2, destination, source);
  public void Adc(Mem destination, Reg source) => this.Alu(2, destination, source);
  public void Adc(Reg destination, Imm immediate) => this.Alu(2, destination, immediate);
  public void Adc(Mem destination, Imm immediate) => this.Alu(2, destination, immediate);

  public void Sbb(Reg destination, Reg source) => this.Alu(3, destination, source);
  public void Sbb(Reg destination, Mem source) => this.Alu(3, destination, source);
  public void Sbb(Mem destination, Reg source) => this.Alu(3, destination, source);
  public void Sbb(Reg destination, Imm immediate) => this.Alu(3, destination, immediate);
  public void Sbb(Mem destination, Imm immediate) => this.Alu(3, destination, immediate);

  public void And(Reg destination, Reg source) => this.Alu(4, destination, source);
  public void And(Reg destination, Mem source) => this.Alu(4, destination, source);
  public void And(Mem destination, Reg source) => this.Alu(4, destination, source);
  public void And(Reg destination, Imm immediate) => this.Alu(4, destination, immediate);
  public void And(Mem destination, Imm immediate) => this.Alu(4, destination, immediate);

  public void Sub(Reg destination, Reg source) => this.Alu(5, destination, source);
  public void Sub(Reg destination, Mem source) => this.Alu(5, destination, source);
  public void Sub(Mem destination, Reg source) => this.Alu(5, destination, source);
  public void Sub(Reg destination, Imm immediate) => this.Alu(5, destination, immediate);
  public void Sub(Mem destination, Imm immediate) => this.Alu(5, destination, immediate);

  public void Xor(Reg destination, Reg source) => this.Alu(6, destination, source);
  public void Xor(Reg destination, Mem source) => this.Alu(6, destination, source);
  public void Xor(Mem destination, Reg source) => this.Alu(6, destination, source);
  public void Xor(Reg destination, Imm immediate) => this.Alu(6, destination, immediate);
  public void Xor(Mem destination, Imm immediate) => this.Alu(6, destination, immediate);

  public void Cmp(Reg destination, Reg source) => this.Alu(7, destination, source);
  public void Cmp(Reg destination, Mem source) => this.Alu(7, destination, source);
  public void Cmp(Mem destination, Reg source) => this.Alu(7, destination, source);
  public void Cmp(Reg destination, Imm immediate) {
    var start = this.Position;
    this.Alu(7, destination, immediate);
    if (immediate is { Label: null, IsSegmentReference: false, Value: 0 } && (destination.IsWord() || destination.IsByte()))
      this.RecordPeep(PeepKind.CmpRegZero, start, destination);
  }
  public void Cmp(Mem destination, Imm immediate) => this.Alu(7, destination, immediate);

  // operation 7 is CMP - it reads but does not write its destination; 2/3 are ADC/SBB which read the carry flag
  private static bool AluWritesDest(int operation) => operation != 7;
  private static bool AluReadsFlags(int operation) => operation is 2 or 3;

  private void Alu(int operation, Reg destination, Reg source) {
    var start = this.Position;
    RequireGeneralPurpose(destination, nameof(destination));
    RequireGeneralPurpose(source, nameof(source));
    RequireSameSize(destination, source);
    this.EmitOperandSizePrefixIf(destination.IsDword());
    this.EmitByte((byte)(operation << 3 | (destination.IsByte() ? 0x00 : 0x01)));
    this.EmitModRmRegister(source.Index(), destination);
    this.RecordSchedReg(start, (ushort)(RegBit(destination) | RegBit(source)),
      (ushort)(AluWritesDest(operation) ? RegBit(destination) : 0), AluReadsFlags(operation), true);
  }

  private void Alu(int operation, Reg destination, Mem source) {
    var start = this.Position;
    RequireGeneralPurpose(destination, nameof(destination));
    RequireMatchingSize(destination, source);
    this.EmitSegmentPrefix(source);
    this.EmitOperandSizePrefixIf(destination.IsDword());
    this.EmitByte((byte)(operation << 3 | (destination.IsByte() ? 0x02 : 0x03)));
    this.EmitModRmMemory(destination.Index(), source);
    this.RecordSchedMem(start, RegBit(destination), (ushort)(AluWritesDest(operation) ? RegBit(destination) : 0),
      AluReadsFlags(operation), true, memRead: true, memWrite: false, source);
  }

  private void Alu(int operation, Mem destination, Reg source) {
    var start = this.Position;
    RequireGeneralPurpose(source, nameof(source));
    RequireMatchingSize(source, destination);
    this.EmitSegmentPrefix(destination);
    this.EmitOperandSizePrefixIf(source.IsDword());
    this.EmitByte((byte)(operation << 3 | (source.IsByte() ? 0x00 : 0x01)));
    this.EmitModRmMemory(source.Index(), destination);
    this.RecordSchedMem(start, RegBit(source), 0, AluReadsFlags(operation), true,
      memRead: true, memWrite: AluWritesDest(operation), destination);
  }

  private void Alu(int operation, Reg destination, Imm immediate) {
    var start = this.Position;
    RequireGeneralPurpose(destination, nameof(destination));
    var size = destination.Size();
    var isPlainValue = immediate.Label is null && !immediate.IsSegmentReference;

    if (size != OperandSize.Byte && isPlainValue && FitsSByte(immediate.Value)) {
      // sign-extended imm8 form (83 /op)
      this.EmitOperandSizePrefixIf(destination.IsDword());
      this.EmitByte(0x83);
      this.EmitModRmRegister(operation, destination);
      this.EmitImmediate(OperandSize.Byte, immediate);
    } else if (destination.Index() == 0) {
      // accumulator short form
      this.EmitOperandSizePrefixIf(destination.IsDword());
      this.EmitByte((byte)(operation << 3 | (size == OperandSize.Byte ? 0x04 : 0x05)));
      this.EmitImmediate(size, immediate);
    } else {
      this.EmitOperandSizePrefixIf(destination.IsDword());
      this.EmitByte(size == OperandSize.Byte ? (byte)0x80 : (byte)0x81);
      this.EmitModRmRegister(operation, destination);
      this.EmitImmediate(size, immediate);
    }
    this.RecordSchedReg(start, RegBit(destination),
      (ushort)(AluWritesDest(operation) ? RegBit(destination) : 0), AluReadsFlags(operation), true);
  }

  private void Alu(int operation, Mem destination, Imm immediate) {
    var start = this.Position;
    var size = RequireSized(destination);
    var isPlainValue = immediate.Label is null && !immediate.IsSegmentReference;

    this.EmitSegmentPrefix(destination);
    this.EmitOperandSizePrefixIf(size == OperandSize.Dword);
    if (size != OperandSize.Byte && isPlainValue && FitsSByte(immediate.Value)) {
      this.EmitByte(0x83);
      this.EmitModRmMemory(operation, destination);
      this.EmitImmediate(OperandSize.Byte, immediate);
    } else {
      this.EmitByte(size == OperandSize.Byte ? (byte)0x80 : (byte)0x81);
      this.EmitModRmMemory(operation, destination);
      this.EmitImmediate(size, immediate);
    }
    this.RecordSchedMem(start, 0, 0, AluReadsFlags(operation), true,
      memRead: true, memWrite: AluWritesDest(operation), destination);
  }

  #endregion

  #region TEST / NOT / NEG

  public void Test(Reg first, Reg second) {
    var start = this.Position;
    RequireGeneralPurpose(first, nameof(first));
    RequireGeneralPurpose(second, nameof(second));
    RequireSameSize(first, second);
    this.EmitOperandSizePrefixIf(first.IsDword());
    this.EmitByte(first.IsByte() ? (byte)0x84 : (byte)0x85);
    this.EmitModRmRegister(second.Index(), first);
    // TEST reads both operands and writes flags; it writes no register and touches no memory.
    this.RecordSchedReg(start, (ushort)(RegBit(first) | RegBit(second)), 0, false, true);
  }

  public void Test(Mem memory, Reg register) {
    var start = this.Position;
    RequireGeneralPurpose(register, nameof(register));
    RequireMatchingSize(register, memory);
    this.EmitSegmentPrefix(memory);
    this.EmitOperandSizePrefixIf(register.IsDword());
    this.EmitByte(register.IsByte() ? (byte)0x84 : (byte)0x85);
    this.EmitModRmMemory(register.Index(), memory);
    // reads the register and the memory cell, writes flags
    this.RecordSchedMem(start, RegBit(register), 0, false, true, true, false, memory);
  }

  public void Test(Reg register, Mem memory) => this.Test(memory, register);

  public void Test(Reg register, Imm immediate) {
    var start = this.Position;
    RequireGeneralPurpose(register, nameof(register));
    var size = register.Size();
    this.EmitOperandSizePrefixIf(register.IsDword());
    if (register.Index() == 0) {
      this.EmitByte(size == OperandSize.Byte ? (byte)0xA8 : (byte)0xA9);
      this.EmitImmediate(size, immediate);
    } else {
      this.EmitByte(size == OperandSize.Byte ? (byte)0xF6 : (byte)0xF7);
      this.EmitModRmRegister(0, register);
      this.EmitImmediate(size, immediate);
    }

    this.RecordSchedReg(start, RegBit(register), 0, false, true);
  }

  public void Test(Mem memory, Imm immediate) {
    var start = this.Position;
    var size = RequireSized(memory);
    this.EmitSegmentPrefix(memory);
    this.EmitOperandSizePrefixIf(size == OperandSize.Dword);
    this.EmitByte(size == OperandSize.Byte ? (byte)0xF6 : (byte)0xF7);
    this.EmitModRmMemory(0, memory);
    this.EmitImmediate(size, immediate);
    this.RecordSchedMem(start, 0, 0, false, true, true, false, memory);
  }

  public void Not(Reg register) {
    var start = this.Position;
    this.UnaryF6(2, register);
    // NOT reads and writes its operand and sets no flags
    this.RecordSchedReg(start, RegBit(register), RegBit(register), false, false);
  }

  public void Not(Mem memory) => this.UnaryF6(2, memory);

  public void Neg(Reg register) {
    var start = this.Position;
    this.UnaryF6(3, register);
    // NEG reads and writes its operand and writes flags
    this.RecordSchedReg(start, RegBit(register), RegBit(register), false, true);
  }

  public void Neg(Mem memory) => this.UnaryF6(3, memory);

  private void UnaryF6(int operation, Reg register) {
    RequireGeneralPurpose(register, nameof(register));
    this.EmitOperandSizePrefixIf(register.IsDword());
    this.EmitByte(register.IsByte() ? (byte)0xF6 : (byte)0xF7);
    this.EmitModRmRegister(operation, register);
  }

  private void UnaryF6(int operation, Mem memory) {
    var size = RequireSized(memory);
    this.EmitSegmentPrefix(memory);
    this.EmitOperandSizePrefixIf(size == OperandSize.Dword);
    this.EmitByte(size == OperandSize.Byte ? (byte)0xF6 : (byte)0xF7);
    this.EmitModRmMemory(operation, memory);
  }

  #endregion

  #region INC / DEC

  public void Inc(Reg register) {
    var start = this.Position;
    RequireGeneralPurpose(register, nameof(register));
    if (register.IsByte()) {
      this.EmitByte(0xFE);
      this.EmitModRmRegister(0, register);
    } else {
      this.EmitOperandSizePrefixIf(register.IsDword());
      this.EmitByte((byte)(0x40 + register.Index()));
    }

    // INC reads and writes its operand and writes flags (CF preserved, but conservatively a flag write)
    this.RecordSchedReg(start, RegBit(register), RegBit(register), false, true);
  }

  public void Dec(Reg register) {
    var start = this.Position;
    RequireGeneralPurpose(register, nameof(register));
    if (register.IsByte()) {
      this.EmitByte(0xFE);
      this.EmitModRmRegister(1, register);
    } else {
      this.EmitOperandSizePrefixIf(register.IsDword());
      this.EmitByte((byte)(0x48 + register.Index()));
    }

    this.RecordSchedReg(start, RegBit(register), RegBit(register), false, true);
  }

  public void Inc(Mem memory) => this.IncDec(0, memory);
  public void Dec(Mem memory) => this.IncDec(1, memory);

  private void IncDec(int operation, Mem memory) {
    var start = this.Position;
    var size = RequireSized(memory);
    this.EmitSegmentPrefix(memory);
    this.EmitOperandSizePrefixIf(size == OperandSize.Dword);
    this.EmitByte(size == OperandSize.Byte ? (byte)0xFE : (byte)0xFF);
    this.EmitModRmMemory(operation, memory);
    // a memory INC/DEC reads and writes the cell and writes flags
    this.RecordSchedMem(start, 0, 0, false, true, true, true, memory);
  }

  #endregion

  #region MUL / IMUL / DIV / IDIV / sign extension

  public void Mul(Reg register) => this.UnaryF6(4, register);
  public void Mul(Mem memory) => this.UnaryF6(4, memory);
  public void Imul(Reg register) => this.UnaryF6(5, register);
  public void Imul(Mem memory) => this.UnaryF6(5, memory);
  public void Div(Reg register) => this.UnaryF6(6, register);
  public void Div(Mem memory) => this.UnaryF6(6, memory);
  public void Idiv(Reg register) => this.UnaryF6(7, register);
  public void Idiv(Mem memory) => this.UnaryF6(7, memory);

  /// <summary>386 IMUL r16/32, r/m16/32 (0F AF).</summary>
  public void Imul(Reg destination, Reg source) {
    var start = this.Position;
    RequireWordOrDword(destination, nameof(destination));
    RequireSameSize(destination, source);
    this.EmitOperandSizePrefixIf(destination.IsDword());
    this.EmitByte(0x0F);
    this.EmitByte(0xAF);
    this.EmitModRmRegister(destination.Index(), source);
    // the two-operand IMUL reads both operands, writes the destination and writes flags
    this.RecordSchedReg(start, (ushort)(RegBit(destination) | RegBit(source)), RegBit(destination), false, true);
  }

  public void Imul(Reg destination, Mem source) {
    var start = this.Position;
    RequireWordOrDword(destination, nameof(destination));
    RequireMatchingSize(destination, source);
    this.EmitSegmentPrefix(source);
    this.EmitOperandSizePrefixIf(destination.IsDword());
    this.EmitByte(0x0F);
    this.EmitByte(0xAF);
    this.EmitModRmMemory(destination.Index(), source);
    this.RecordSchedMem(start, RegBit(destination), RegBit(destination), false, true, true, false, source);
  }

  /// <summary>186 IMUL r16/32, r/m16/32, imm (6B sign-extended byte / 69 full width).</summary>
  public void Imul(Reg destination, Reg source, Imm immediate) {
    RequireWordOrDword(destination, nameof(destination));
    RequireSameSize(destination, source);
    this.EmitOperandSizePrefixIf(destination.IsDword());
    var fitsByte = immediate.Label is null && !immediate.IsSegmentReference && FitsSByte(immediate.Value);
    this.EmitByte(fitsByte ? (byte)0x6B : (byte)0x69);
    this.EmitModRmRegister(destination.Index(), source);
    this.EmitImmediate(fitsByte ? OperandSize.Byte : destination.Size(), immediate);
  }

  public void Imul(Reg destination, Mem source, Imm immediate) {
    RequireWordOrDword(destination, nameof(destination));
    RequireMatchingSize(destination, source);
    this.EmitSegmentPrefix(source);
    this.EmitOperandSizePrefixIf(destination.IsDword());
    var fitsByte = immediate.Label is null && !immediate.IsSegmentReference && FitsSByte(immediate.Value);
    this.EmitByte(fitsByte ? (byte)0x6B : (byte)0x69);
    this.EmitModRmMemory(destination.Index(), source);
    this.EmitImmediate(fitsByte ? OperandSize.Byte : destination.Size(), immediate);
  }

  public void Imul(Reg destination, Imm immediate) => this.Imul(destination, destination, immediate);

  /// <summary>386 SHLD r/m16/32, r16/32, imm8 (0F A4): destination shifted left by count, vacated low bits filled from the high bits of source.</summary>
  public void Shld(Reg destination, Reg source, int count) => this.DoubleShift(0xA4, destination, source, count);

  /// <summary>386 SHRD r/m16/32, r16/32, imm8 (0F AC): destination shifted right by count, vacated high bits filled from the low bits of source.</summary>
  public void Shrd(Reg destination, Reg source, int count) => this.DoubleShift(0xAC, destination, source, count);

  private void DoubleShift(byte opcode, Reg destination, Reg source, int count) {
    RequireWordOrDword(destination, nameof(destination));
    RequireSameSize(destination, source);
    if (count is < 1 or > 31)
      throw new ArgumentOutOfRangeException(nameof(count), count, "Double-shift count must be 1..31.");
    this.EmitOperandSizePrefixIf(destination.IsDword());
    this.EmitByte(0x0F);
    this.EmitByte(opcode);
    this.EmitModRmRegister(source.Index(), destination);   // reg field = source, r/m = destination
    this.EmitByte((byte)count);
  }

  public void Cbw() => this.EmitByte(0x98);
  public void Cwd() => this.EmitByte(0x99);

  public void Cwde() {
    this.EmitByte(0x66);
    this.EmitByte(0x98);
  }

  /// <summary>386+: sets an 8-bit register to 1/0 from the condition flags (0F 90+cc).</summary>
  public void Setcc(Condition condition, Reg destination) {
    RequireGeneralPurpose(destination, nameof(destination));
    if (!destination.IsByte())
      throw new ArgumentException("SETcc takes an 8-bit register.", nameof(destination));
    this.EmitByte(0x0F);
    this.EmitByte((byte)(0x90 + (byte)condition));
    this.EmitModRmRegister(0, destination);
  }

  /// <summary>
  /// 686+ (Pentium Pro) conditional move: <c>destination = source</c> when <paramref name="condition"/>
  /// holds, otherwise unchanged - branchless (<c>0F 40+cc /r</c>; 0x66-prefixed for a 32-bit operand).
  /// </summary>
  public void Cmovcc(Condition condition, Reg destination, Reg source) {
    if (destination.IsByte() || source.IsByte())
      throw new ArgumentException("CMOVcc takes 16- or 32-bit operands.", nameof(destination));
    if (destination.IsDword())
      this.EmitByte(0x66);
    this.EmitByte(0x0F);
    this.EmitByte((byte)(0x40 + (byte)condition));
    this.EmitModRmRegister(destination.Index(), source);
  }

  public void Cmovcc(Condition condition, Reg destination, Mem source) {
    if (destination.IsByte())
      throw new ArgumentException("CMOVcc takes 16- or 32-bit operands.", nameof(destination));
    if (destination.IsDword())
      this.EmitByte(0x66);
    this.EmitByte(0x0F);
    this.EmitByte((byte)(0x40 + (byte)condition));
    this.EmitModRmMemory(destination.Index(), source);
  }

  public void Cdq() {
    this.EmitByte(0x66);
    this.EmitByte(0x99);
  }

  #endregion

  #region shifts and rotates

  public void Rol(Reg destination, int count) => this.Shift(0, destination, count);
  public void Rol(Reg destination, Reg count) => this.Shift(0, destination, count);
  public void Rol(Mem destination, int count) => this.Shift(0, destination, count);
  public void Rol(Mem destination, Reg count) => this.Shift(0, destination, count);

  public void Ror(Reg destination, int count) => this.Shift(1, destination, count);
  public void Ror(Reg destination, Reg count) => this.Shift(1, destination, count);
  public void Ror(Mem destination, int count) => this.Shift(1, destination, count);
  public void Ror(Mem destination, Reg count) => this.Shift(1, destination, count);

  public void Rcl(Reg destination, int count) => this.Shift(2, destination, count);
  public void Rcl(Reg destination, Reg count) => this.Shift(2, destination, count);
  public void Rcl(Mem destination, int count) => this.Shift(2, destination, count);
  public void Rcl(Mem destination, Reg count) => this.Shift(2, destination, count);

  public void Rcr(Reg destination, int count) => this.Shift(3, destination, count);
  public void Rcr(Reg destination, Reg count) => this.Shift(3, destination, count);
  public void Rcr(Mem destination, int count) => this.Shift(3, destination, count);
  public void Rcr(Mem destination, Reg count) => this.Shift(3, destination, count);

  public void Shl(Reg destination, int count) => this.Shift(4, destination, count);
  public void Shl(Reg destination, Reg count) => this.Shift(4, destination, count);
  public void Shl(Mem destination, int count) => this.Shift(4, destination, count);
  public void Shl(Mem destination, Reg count) => this.Shift(4, destination, count);

  public void Shr(Reg destination, int count) => this.Shift(5, destination, count);
  public void Shr(Reg destination, Reg count) => this.Shift(5, destination, count);
  public void Shr(Mem destination, int count) => this.Shift(5, destination, count);
  public void Shr(Mem destination, Reg count) => this.Shift(5, destination, count);

  public void Sar(Reg destination, int count) => this.Shift(7, destination, count);
  public void Sar(Reg destination, Reg count) => this.Shift(7, destination, count);
  public void Sar(Mem destination, int count) => this.Shift(7, destination, count);
  public void Sar(Mem destination, Reg count) => this.Shift(7, destination, count);

  private void Shift(int operation, Reg destination, int count) {
    var start = this.Position;
    RequireGeneralPurpose(destination, nameof(destination));
    if (count is < 1 or > 31)
      throw new ArgumentOutOfRangeException(nameof(count), count, "Shift count must be 1..31.");

    this.EmitOperandSizePrefixIf(destination.IsDword());
    if (count == 1) {
      this.EmitByte(destination.IsByte() ? (byte)0xD0 : (byte)0xD1);
      this.EmitModRmRegister(operation, destination);
    } else {
      this.EmitByte(destination.IsByte() ? (byte)0xC0 : (byte)0xC1);
      this.EmitModRmRegister(operation, destination);
      this.EmitByte((byte)count);
    }

    // an immediate-count shift reads and writes the destination and writes flags
    this.RecordSchedReg(start, RegBit(destination), RegBit(destination), false, true);
  }

  private void Shift(int operation, Reg destination, Reg count) {
    var start = this.Position;
    RequireGeneralPurpose(destination, nameof(destination));
    if (count != Reg.CL)
      throw new ArgumentException("Variable shift counts must be in CL.", nameof(count));

    this.EmitOperandSizePrefixIf(destination.IsDword());
    this.EmitByte(destination.IsByte() ? (byte)0xD2 : (byte)0xD3);
    this.EmitModRmRegister(operation, destination);
    // a CL-count shift additionally reads CL (count register)
    this.RecordSchedReg(start, (ushort)(RegBit(destination) | RegBit(count)), RegBit(destination), false, true);
  }

  private void Shift(int operation, Mem destination, int count) {
    var start = this.Position;
    var size = RequireSized(destination);
    if (count is < 1 or > 31)
      throw new ArgumentOutOfRangeException(nameof(count), count, "Shift count must be 1..31.");

    this.EmitSegmentPrefix(destination);
    this.EmitOperandSizePrefixIf(size == OperandSize.Dword);
    if (count == 1) {
      this.EmitByte(size == OperandSize.Byte ? (byte)0xD0 : (byte)0xD1);
      this.EmitModRmMemory(operation, destination);
    } else {
      this.EmitByte(size == OperandSize.Byte ? (byte)0xC0 : (byte)0xC1);
      this.EmitModRmMemory(operation, destination);
      this.EmitByte((byte)count);
    }

    // a memory shift reads and writes the cell and writes flags
    this.RecordSchedMem(start, 0, 0, false, true, true, true, destination);
  }

  private void Shift(int operation, Mem destination, Reg count) {
    var start = this.Position;
    var size = RequireSized(destination);
    if (count != Reg.CL)
      throw new ArgumentException("Variable shift counts must be in CL.", nameof(count));

    this.EmitSegmentPrefix(destination);
    this.EmitOperandSizePrefixIf(size == OperandSize.Dword);
    this.EmitByte(size == OperandSize.Byte ? (byte)0xD2 : (byte)0xD3);
    this.EmitModRmMemory(operation, destination);
    // additionally reads CL (count register)
    this.RecordSchedMem(start, RegBit(count), 0, false, true, true, true, destination);
  }

  #endregion

  #region MOVZX / MOVSX (386)

  public void Movzx(Reg destination, Reg source) => this.ExtendedMove(0xB6, destination, source);
  public void Movsx(Reg destination, Reg source) => this.ExtendedMove(0xBE, destination, source);

  public void Movzx(Reg destination, Mem source) => this.ExtendedMove(0xB6, destination, source);
  public void Movsx(Reg destination, Mem source) => this.ExtendedMove(0xBE, destination, source);

  private void ExtendedMove(byte opcodeBase, Reg destination, Reg source) {
    RequireWordOrDword(destination, nameof(destination));
    RequireGeneralPurpose(source, nameof(source));
    if (source.Size() >= destination.Size())
      throw new ArgumentException($"Source {source} must be narrower than destination {destination}.", nameof(source));

    this.EmitOperandSizePrefixIf(destination.IsDword());
    this.EmitByte(0x0F);
    this.EmitByte((byte)(opcodeBase + (source.IsByte() ? 0 : 1)));
    this.EmitModRmRegister(destination.Index(), source);
  }

  private void ExtendedMove(byte opcodeBase, Reg destination, Mem source) {
    RequireWordOrDword(destination, nameof(destination));
    var sourceSize = RequireSized(source);
    if (sourceSize is not (OperandSize.Byte or OperandSize.Word) || sourceSize >= destination.Size())
      throw new ArgumentException($"Source {source} must be a byte or word narrower than {destination}.", nameof(source));

    this.EmitSegmentPrefix(source);
    this.EmitOperandSizePrefixIf(destination.IsDword());
    this.EmitByte(0x0F);
    this.EmitByte((byte)(opcodeBase + (sourceSize == OperandSize.Byte ? 0 : 1)));
    this.EmitModRmMemory(destination.Index(), source);
  }

  #endregion

  #region conditional jumps

  /// <summary>Jcc: short form when the bound target is in range, 386 near form otherwise.</summary>
  public void J(Condition condition, Label target) {
    ArgumentNullException.ThrowIfNull(target);
    if (target.IsBound && FitsSByte(target.Position - (this.Position + 2))) {
      this.JShort(condition, target);
      return;
    }

    var start = this.Position;
    if (this.Allow386Jcc) {
      this.EmitByte(0x0F);
      this.EmitByte((byte)(0x80 + (int)condition));
      this.EmitRel16(target);
      this.RecordSchedJump(start);
      return;
    }

    // 8086: no near Jcc exists, so the condition inverts and hops over a near JMP that does the
    // work. Five bytes against four - the relaxation folds it back to two wherever the target turns
    // out to be reachable in a byte, so only the jumps that had no choice pay for it.
    this.EmitByte((byte)(0x70 + ((int)condition ^ 1)));
    this.EmitByte(0x03);
    this.EmitByte(0xE9);
    this.EmitRel16Pair(target);
    this.RecordSchedJump(start);
  }

  /// <summary>Forced short Jcc (8086-safe); throws at <see cref="ToArray"/> when out of range.</summary>
  public void JShort(Condition condition, Label target) {
    ArgumentNullException.ThrowIfNull(target);
    var start = this.Position;
    this.EmitByte((byte)(0x70 + (int)condition));
    this.EmitRel8(target);
    this.RecordSchedJump(start);
  }

  public void Jo(Label target) => this.J(Condition.Overflow, target);
  public void Jno(Label target) => this.J(Condition.NotOverflow, target);
  public void Jb(Label target) => this.J(Condition.Below, target);
  public void Jc(Label target) => this.J(Condition.Carry, target);
  public void Jae(Label target) => this.J(Condition.AboveOrEqual, target);
  public void Jnc(Label target) => this.J(Condition.NotCarry, target);
  public void Je(Label target) => this.J(Condition.Equal, target);
  public void Jz(Label target) => this.J(Condition.Zero, target);
  public void Jne(Label target) => this.J(Condition.NotEqual, target);
  public void Jnz(Label target) => this.J(Condition.NotZero, target);
  public void Jbe(Label target) => this.J(Condition.BelowOrEqual, target);
  public void Ja(Label target) => this.J(Condition.Above, target);
  public void Js(Label target) => this.J(Condition.Sign, target);
  public void Jns(Label target) => this.J(Condition.NotSign, target);
  public void Jp(Label target) => this.J(Condition.Parity, target);
  public void Jnp(Label target) => this.J(Condition.NotParity, target);
  public void Jl(Label target) => this.J(Condition.Less, target);
  public void Jge(Label target) => this.J(Condition.GreaterOrEqual, target);
  public void Jle(Label target) => this.J(Condition.LessOrEqual, target);
  public void Jg(Label target) => this.J(Condition.Greater, target);

  #endregion

  #region JMP / CALL / RET / LOOP

  /// <summary>JMP: short form when the bound target is in range, near form otherwise.</summary>
  public void Jmp(Label target) {
    ArgumentNullException.ThrowIfNull(target);
    if (target.IsBound && FitsSByte(target.Position - (this.Position + 2))) {
      this.JmpShort(target);
      return;
    }

    this.JmpNear(target);
  }

  public void JmpShort(Label target) {
    ArgumentNullException.ThrowIfNull(target);
    this.EmitByte(0xEB);
    this.EmitRel8(target);
  }

  public void JmpNear(Label target) {
    ArgumentNullException.ThrowIfNull(target);
    this.EmitByte(0xE9);
    this.EmitRel16(target);
  }

  /// <summary>Indirect near JMP through a 16-bit register.</summary>
  public void Jmp(Reg target) {
    if (!target.IsWord())
      throw new ArgumentException($"JMP {target}: indirect jumps need a 16-bit register.", nameof(target));

    this.EmitByte(0xFF);
    this.EmitModRmRegister(4, target);
  }

  /// <summary>Indirect near JMP through a word in memory.</summary>
  public void Jmp(Mem target) {
    this.EmitSegmentPrefix(target);
    this.EmitByte(0xFF);
    this.EmitModRmMemory(4, target);
  }

  /// <summary>Direct far JMP to an absolute segment:offset (no loader relocation).</summary>
  public void JmpFar(ushort segment, ushort offset) {
    this.EmitByte(0xEA);
    this.EmitWord(offset);
    this.EmitWord(segment);
  }

  /// <summary>Direct far JMP into this image; the segment word is recorded as an MZ relocation.</summary>
  public void JmpFar(Label target, ushort segment = 0) {
    ArgumentNullException.ThrowIfNull(target);
    this.EmitByte(0xEA);
    this._fixups.Add(new(this.Position, FixupKind.Abs16, target, 0));
    this.EmitWord(0);
    this.DwSegment(segment);
  }

  /// <summary>Indirect far JMP through a dword (offset:segment) in memory.</summary>
  public void JmpFar(Mem target) {
    this.EmitSegmentPrefix(target);
    this.EmitByte(0xFF);
    this.EmitModRmMemory(5, target);
  }

  public void Call(Label target) {
    ArgumentNullException.ThrowIfNull(target);
    this.EmitByte(0xE8);
    this.EmitRel16(target);
  }

  public void Call(Reg target) {
    if (!target.IsWord())
      throw new ArgumentException($"CALL {target}: indirect calls need a 16-bit register.", nameof(target));

    this.EmitByte(0xFF);
    this.EmitModRmRegister(2, target);
  }

  public void Call(Mem target) {
    this.EmitSegmentPrefix(target);
    this.EmitByte(0xFF);
    this.EmitModRmMemory(2, target);
  }

  /// <summary>Direct far CALL to an absolute segment:offset (no loader relocation).</summary>
  public void CallFar(ushort segment, ushort offset) {
    this.EmitByte(0x9A);
    this.EmitWord(offset);
    this.EmitWord(segment);
  }

  /// <summary>Direct far CALL into this image; the segment word is recorded as an MZ relocation.</summary>
  public void CallFar(Label target, ushort segment = 0) {
    ArgumentNullException.ThrowIfNull(target);
    this.EmitByte(0x9A);
    this._fixups.Add(new(this.Position, FixupKind.Abs16, target, 0));
    this.EmitWord(0);
    this.DwSegment(segment);
  }

  /// <summary>Indirect far CALL through a dword (offset:segment) in memory.</summary>
  public void CallFar(Mem target) {
    this.EmitSegmentPrefix(target);
    this.EmitByte(0xFF);
    this.EmitModRmMemory(3, target);
  }

  public void Ret() => this.EmitByte(0xC3);

  public void Ret(ushort bytesToPop) {
    this.EmitByte(0xC2);
    this.EmitWord(bytesToPop);
  }

  public void Retf() => this.EmitByte(0xCB);

  public void Retf(ushort bytesToPop) {
    this.EmitByte(0xCA);
    this.EmitWord(bytesToPop);
  }

  public void Loop(Label target) => this.ShortBranch(0xE2, target);
  public void Loope(Label target) => this.ShortBranch(0xE1, target);
  public void Loopne(Label target) => this.ShortBranch(0xE0, target);
  public void Jcxz(Label target) => this.ShortBranch(0xE3, target);

  /// <summary>
  /// LOOP/LOOPE/LOOPNE/JCXZ exist only with a signed-byte displacement - there is no near form to
  /// relax into. When the body has outgrown that range the branch bounces through a near JMP:
  /// the short branch takes a two-byte hop to the trampoline, the fall-through skips over it.
  /// Emitted only for a bound target that genuinely cannot be reached, so code that already fit
  /// keeps exactly the encoding it had.
  /// </summary>
  private void ShortBranch(byte opcode, Label target) {
    ArgumentNullException.ThrowIfNull(target);
    if (target.IsBound && !FitsSByte(target.Position - (this.Position + 2))) {
      var trampoline = this.DefineLabel();
      var past = this.DefineLabel();
      this.EmitByte(opcode);
      this.EmitRel8(trampoline);
      this.JmpShort(past);
      this.MarkLabel(trampoline);
      this.JmpNear(target);
      this.MarkLabel(past);
      return;
    }

    this.EmitByte(opcode);
    this.EmitRel8(target);
  }

  #endregion

  #region INT / flags / misc

  public void Int(byte vector) {
    this.EmitByte(0xCD);
    this.EmitByte(vector);
  }

  public void Int3() => this.EmitByte(0xCC);
  public void Into() => this.EmitByte(0xCE);
  public void Iret() => this.EmitByte(0xCF);

  public void Clc() => this.EmitByte(0xF8);
  public void Stc() => this.EmitByte(0xF9);
  public void Cmc() => this.EmitByte(0xF5);
  public void Cld() => this.EmitByte(0xFC);
  public void Std() => this.EmitByte(0xFD);
  public void Cli() => this.EmitByte(0xFA);
  public void Sti() => this.EmitByte(0xFB);

  public void Lahf() => this.EmitByte(0x9F);
  public void Sahf() => this.EmitByte(0x9E);

  public void Nop() => this.EmitByte(0x90);
  public void Hlt() => this.EmitByte(0xF4);

  /// <summary>486+ byte-order reverse of a 32-bit register (0F C8+reg, 0x66-prefixed in 16-bit mode).</summary>
  public void Bswap(Reg register) {
    if (!register.IsDword())
      throw new ArgumentException("BSWAP takes a 32-bit register.", nameof(register));
    this.EmitByte(0x66);
    this.EmitByte(0x0F);
    this.EmitByte((byte)(0xC8 + register.Index()));
  }

  /// <summary>Pads with NOPs (0x90) to the next <paramref name="alignment"/>-byte boundary - safe to fall through or jump over.</summary>
  public void AlignCode(int alignment) {
    while (this.Position % alignment != 0)
      this.EmitByte(0x90);
  }

  #endregion

  #region string instructions

  public void Movsb() => this.EmitByte(0xA4);
  public void Movsw() => this.EmitByte(0xA5);
  public void Movsd() { this.EmitByte(0x66); this.EmitByte(0xA5); }
  public void Cmpsb() => this.EmitByte(0xA6);
  public void Cmpsw() => this.EmitByte(0xA7);
  public void Cmpsd() { this.EmitByte(0x66); this.EmitByte(0xA7); }
  public void Stosb() => this.EmitByte(0xAA);
  public void Stosw() => this.EmitByte(0xAB);
  public void Stosd() { this.EmitByte(0x66); this.EmitByte(0xAB); }
  public void Lodsb() => this.EmitByte(0xAC);
  public void Lodsw() => this.EmitByte(0xAD);
  public void Lodsd() { this.EmitByte(0x66); this.EmitByte(0xAD); }
  public void Scasb() => this.EmitByte(0xAE);
  public void Scasw() => this.EmitByte(0xAF);
  public void Scasd() { this.EmitByte(0x66); this.EmitByte(0xAF); }

  /// <summary>REP/REPE prefix for the following string instruction.</summary>
  public void Rep() => this.EmitByte(0xF3);

  public void Repe() => this.EmitByte(0xF3);
  public void Repne() => this.EmitByte(0xF2);

  #endregion

  #region IN / OUT

  public void In(Reg accumulator, byte port) {
    RequireAccumulator(accumulator);
    this.EmitOperandSizePrefixIf(accumulator.IsDword());
    this.EmitByte(accumulator.IsByte() ? (byte)0xE4 : (byte)0xE5);
    this.EmitByte(port);
  }

  public void In(Reg accumulator, Reg port) {
    RequireAccumulator(accumulator);
    if (port != Reg.DX)
      throw new ArgumentException("Variable port numbers must be in DX.", nameof(port));

    this.EmitOperandSizePrefixIf(accumulator.IsDword());
    this.EmitByte(accumulator.IsByte() ? (byte)0xEC : (byte)0xED);
  }

  public void Out(byte port, Reg accumulator) {
    RequireAccumulator(accumulator);
    this.EmitOperandSizePrefixIf(accumulator.IsDword());
    this.EmitByte(accumulator.IsByte() ? (byte)0xE6 : (byte)0xE7);
    this.EmitByte(port);
  }

  public void Out(Reg port, Reg accumulator) {
    RequireAccumulator(accumulator);
    if (port != Reg.DX)
      throw new ArgumentException("Variable port numbers must be in DX.", nameof(port));

    this.EmitOperandSizePrefixIf(accumulator.IsDword());
    this.EmitByte(accumulator.IsByte() ? (byte)0xEE : (byte)0xEF);
  }

  private static void RequireAccumulator(Reg register) {
    if (register is not (Reg.AL or Reg.AX or Reg.EAX))
      throw new ArgumentException($"{register}: IN/OUT only transfer through AL/AX/EAX.", nameof(register));
  }

  #endregion
}
