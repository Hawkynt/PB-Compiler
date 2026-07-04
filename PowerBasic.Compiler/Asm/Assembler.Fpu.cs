namespace PowerBasic.Compiler.Asm;

public sealed partial class Assembler {

  #region load / store

  public void Fld(Mem source) {
    switch (RequireSized(source)) {
      case OperandSize.Dword: this.FpuMemory(0xD9, 0, source); return;
      case OperandSize.Qword: this.FpuMemory(0xDD, 0, source); return;
      case OperandSize.Tbyte: this.FpuMemory(0xDB, 5, source); return;
      default: throw new ArgumentException($"FLD {source}: reals are dword, qword or tbyte.", nameof(source));
    }
  }

  public void Fld(St source) => this.FpuStack(0xD9, 0xC0, source);

  public void Fst(Mem destination) {
    switch (RequireSized(destination)) {
      case OperandSize.Dword: this.FpuMemory(0xD9, 2, destination); return;
      case OperandSize.Qword: this.FpuMemory(0xDD, 2, destination); return;
      default: throw new ArgumentException($"FST {destination}: only dword or qword reals can be stored without popping.", nameof(destination));
    }
  }

  public void Fst(St destination) => this.FpuStack(0xDD, 0xD0, destination);

  public void Fstp(Mem destination) {
    switch (RequireSized(destination)) {
      case OperandSize.Dword: this.FpuMemory(0xD9, 3, destination); return;
      case OperandSize.Qword: this.FpuMemory(0xDD, 3, destination); return;
      case OperandSize.Tbyte: this.FpuMemory(0xDB, 7, destination); return;
      default: throw new ArgumentException($"FSTP {destination}: reals are dword, qword or tbyte.", nameof(destination));
    }
  }

  public void Fstp(St destination) => this.FpuStack(0xDD, 0xD8, destination);

  public void Fild(Mem source) {
    switch (RequireSized(source)) {
      case OperandSize.Word: this.FpuMemory(0xDF, 0, source); return;
      case OperandSize.Dword: this.FpuMemory(0xDB, 0, source); return;
      case OperandSize.Qword: this.FpuMemory(0xDF, 5, source); return;
      default: throw new ArgumentException($"FILD {source}: integers are word, dword or qword.", nameof(source));
    }
  }

  public void Fist(Mem destination) {
    switch (RequireSized(destination)) {
      case OperandSize.Word: this.FpuMemory(0xDF, 2, destination); return;
      case OperandSize.Dword: this.FpuMemory(0xDB, 2, destination); return;
      default: throw new ArgumentException($"FIST {destination}: only word or dword integers can be stored without popping.", nameof(destination));
    }
  }

  public void Fistp(Mem destination) {
    switch (RequireSized(destination)) {
      case OperandSize.Word: this.FpuMemory(0xDF, 3, destination); return;
      case OperandSize.Dword: this.FpuMemory(0xDB, 3, destination); return;
      case OperandSize.Qword: this.FpuMemory(0xDF, 7, destination); return;
      default: throw new ArgumentException($"FISTP {destination}: integers are word, dword or qword.", nameof(destination));
    }
  }

  public void Fxch() => this.Fxch(St.St1);
  public void Fxch(St other) => this.FpuStack(0xD9, 0xC8, other);

  public void Ffree(St target) => this.FpuStack(0xDD, 0xC0, target);

  #endregion

  #region arithmetic

  // /digit for the D8/DC memory and register forms
  private const int _FPU_ADD = 0;
  private const int _FPU_MUL = 1;
  private const int _FPU_COM = 2;
  private const int _FPU_COMP = 3;
  private const int _FPU_SUB = 4;
  private const int _FPU_SUBR = 5;
  private const int _FPU_DIV = 6;
  private const int _FPU_DIVR = 7;

  public void Fadd(Mem source) => this.FpuArithmeticMemory(_FPU_ADD, source);
  public void Fadd(St destination, St source) => this.FpuArithmetic(_FPU_ADD, destination, source);
  public void Faddp() => this.Faddp(St.St1);
  public void Faddp(St destination) => this.FpuStack(0xDE, 0xC0, destination);
  public void Fiadd(Mem source) => this.FpuIntegerArithmetic(_FPU_ADD, source);

  public void Fmul(Mem source) => this.FpuArithmeticMemory(_FPU_MUL, source);
  public void Fmul(St destination, St source) => this.FpuArithmetic(_FPU_MUL, destination, source);
  public void Fmulp() => this.Fmulp(St.St1);
  public void Fmulp(St destination) => this.FpuStack(0xDE, 0xC8, destination);
  public void Fimul(Mem source) => this.FpuIntegerArithmetic(_FPU_MUL, source);

  public void Fsub(Mem source) => this.FpuArithmeticMemory(_FPU_SUB, source);
  public void Fsub(St destination, St source) => this.FpuArithmetic(_FPU_SUB, destination, source);
  public void Fsubp() => this.Fsubp(St.St1);
  public void Fsubp(St destination) => this.FpuStack(0xDE, 0xE8, destination);
  public void Fisub(Mem source) => this.FpuIntegerArithmetic(_FPU_SUB, source);

  public void Fsubr(Mem source) => this.FpuArithmeticMemory(_FPU_SUBR, source);
  public void Fsubr(St destination, St source) => this.FpuArithmetic(_FPU_SUBR, destination, source);
  public void Fsubrp() => this.Fsubrp(St.St1);
  public void Fsubrp(St destination) => this.FpuStack(0xDE, 0xE0, destination);
  public void Fisubr(Mem source) => this.FpuIntegerArithmetic(_FPU_SUBR, source);

  public void Fdiv(Mem source) => this.FpuArithmeticMemory(_FPU_DIV, source);
  public void Fdiv(St destination, St source) => this.FpuArithmetic(_FPU_DIV, destination, source);
  public void Fdivp() => this.Fdivp(St.St1);
  public void Fdivp(St destination) => this.FpuStack(0xDE, 0xF8, destination);
  public void Fidiv(Mem source) => this.FpuIntegerArithmetic(_FPU_DIV, source);

  public void Fdivr(Mem source) => this.FpuArithmeticMemory(_FPU_DIVR, source);
  public void Fdivr(St destination, St source) => this.FpuArithmetic(_FPU_DIVR, destination, source);
  public void Fdivrp() => this.Fdivrp(St.St1);
  public void Fdivrp(St destination) => this.FpuStack(0xDE, 0xF0, destination);
  public void Fidivr(Mem source) => this.FpuIntegerArithmetic(_FPU_DIVR, source);

  private void FpuArithmeticMemory(int operation, Mem source) {
    switch (RequireSized(source)) {
      case OperandSize.Dword: this.FpuMemory(0xD8, operation, source); return;
      case OperandSize.Qword: this.FpuMemory(0xDC, operation, source); return;
      default: throw new ArgumentException($"FPU arithmetic on {source}: reals are dword or qword.", nameof(source));
    }
  }

  private void FpuIntegerArithmetic(int operation, Mem source) {
    switch (RequireSized(source)) {
      case OperandSize.Word: this.FpuMemory(0xDE, operation, source); return;
      case OperandSize.Dword: this.FpuMemory(0xDA, operation, source); return;
      default: throw new ArgumentException($"FPU integer arithmetic on {source}: integers are word or dword.", nameof(source));
    }
  }

  private void FpuArithmetic(int operation, St destination, St source) {
    if (destination.Index == 0) {
      this.EmitByte(0xD8);
      this.EmitByte((byte)(0xC0 | operation << 3 | source.Index));
      return;
    }

    if (source.Index != 0)
      throw new ArgumentException($"FPU arithmetic needs ST(0) as one operand, got ST({destination.Index}), ST({source.Index}).", nameof(source));

    // the DC forms swap the reversed-operation opcode slots for SUB/DIV
    var dcOperation = operation switch {
      _FPU_SUB => _FPU_SUBR,
      _FPU_SUBR => _FPU_SUB,
      _FPU_DIV => _FPU_DIVR,
      _FPU_DIVR => _FPU_DIV,
      _ => operation,
    };
    if (dcOperation is _FPU_COM or _FPU_COMP)
      throw new ArgumentException("FCOM/FCOMP have no ST(i), ST(0) form.", nameof(destination));

    this.EmitByte(0xDC);
    this.EmitByte((byte)(0xC0 | dcOperation << 3 | destination.Index));
  }

  #endregion

  #region comparisons

  public void Fcom(Mem source) {
    switch (RequireSized(source)) {
      case OperandSize.Dword: this.FpuMemory(0xD8, _FPU_COM, source); return;
      case OperandSize.Qword: this.FpuMemory(0xDC, _FPU_COM, source); return;
      default: throw new ArgumentException($"FCOM {source}: reals are dword or qword.", nameof(source));
    }
  }

  public void Fcom() => this.Fcom(St.St1);
  public void Fcom(St other) => this.FpuStack(0xD8, 0xD0, other);

  public void Fcomp(Mem source) {
    switch (RequireSized(source)) {
      case OperandSize.Dword: this.FpuMemory(0xD8, _FPU_COMP, source); return;
      case OperandSize.Qword: this.FpuMemory(0xDC, _FPU_COMP, source); return;
      default: throw new ArgumentException($"FCOMP {source}: reals are dword or qword.", nameof(source));
    }
  }

  public void Fcomp() => this.Fcomp(St.St1);
  public void Fcomp(St other) => this.FpuStack(0xD8, 0xD8, other);

  public void Fcompp() { this.EmitByte(0xDE); this.EmitByte(0xD9); }

  public void Ficom(Mem source) => this.FpuIntegerArithmetic(_FPU_COM, source);
  public void Ficomp(Mem source) => this.FpuIntegerArithmetic(_FPU_COMP, source);

  public void Fucom() => this.Fucom(St.St1);
  public void Fucom(St other) => this.FpuStack(0xDD, 0xE0, other);
  public void Fucomp() => this.Fucomp(St.St1);
  public void Fucomp(St other) => this.FpuStack(0xDD, 0xE8, other);
  public void Fucompp() { this.EmitByte(0xDA); this.EmitByte(0xE9); }

  public void Ftst() { this.EmitByte(0xD9); this.EmitByte(0xE4); }

  #endregion

  #region transcendental and unary operations

  public void Fchs() { this.EmitByte(0xD9); this.EmitByte(0xE0); }
  public void Fabs() { this.EmitByte(0xD9); this.EmitByte(0xE1); }
  public void Fsqrt() { this.EmitByte(0xD9); this.EmitByte(0xFA); }
  public void Frndint() { this.EmitByte(0xD9); this.EmitByte(0xFC); }
  public void Fscale() { this.EmitByte(0xD9); this.EmitByte(0xFD); }
  public void Fprem() { this.EmitByte(0xD9); this.EmitByte(0xF8); }
  public void Fprem1() { this.EmitByte(0xD9); this.EmitByte(0xF5); }
  public void Fptan() { this.EmitByte(0xD9); this.EmitByte(0xF2); }
  public void Fpatan() { this.EmitByte(0xD9); this.EmitByte(0xF3); }
  public void F2xm1() { this.EmitByte(0xD9); this.EmitByte(0xF0); }
  public void Fyl2x() { this.EmitByte(0xD9); this.EmitByte(0xF1); }
  public void Fyl2xp1() { this.EmitByte(0xD9); this.EmitByte(0xF9); }
  public void Fsin() { this.EmitByte(0xD9); this.EmitByte(0xFE); }
  public void Fcos() { this.EmitByte(0xD9); this.EmitByte(0xFF); }
  public void Fsincos() { this.EmitByte(0xD9); this.EmitByte(0xFB); }

  #endregion

  #region constants

  public void Fldz() { this.EmitByte(0xD9); this.EmitByte(0xEE); }
  public void Fld1() { this.EmitByte(0xD9); this.EmitByte(0xE8); }
  public void Fldpi() { this.EmitByte(0xD9); this.EmitByte(0xEB); }
  public void Fldl2e() { this.EmitByte(0xD9); this.EmitByte(0xEA); }
  public void Fldl2t() { this.EmitByte(0xD9); this.EmitByte(0xE9); }
  public void Fldlg2() { this.EmitByte(0xD9); this.EmitByte(0xEC); }
  public void Fldln2() { this.EmitByte(0xD9); this.EmitByte(0xED); }

  #endregion

  #region control

  public void Finit() { this.EmitByte(0x9B); this.Fninit(); }
  public void Fninit() { this.EmitByte(0xDB); this.EmitByte(0xE3); }

  public void Fclex() { this.EmitByte(0x9B); this.Fnclex(); }
  public void Fnclex() { this.EmitByte(0xDB); this.EmitByte(0xE2); }

  public void FstswAx() { this.EmitByte(0x9B); this.FnstswAx(); }
  public void FnstswAx() { this.EmitByte(0xDF); this.EmitByte(0xE0); }

  public void Fstsw(Mem destination) { this.EmitByte(0x9B); this.Fnstsw(destination); }
  public void Fnstsw(Mem destination) => this.FpuWordControl(0xDD, 7, destination, "FSTSW");

  public void Fstcw(Mem destination) { this.EmitByte(0x9B); this.Fnstcw(destination); }
  public void Fnstcw(Mem destination) => this.FpuWordControl(0xD9, 7, destination, "FSTCW");
  public void Fldcw(Mem source) => this.FpuWordControl(0xD9, 5, source, "FLDCW");

  private void FpuWordControl(byte opcode, int regField, Mem memory, string mnemonic) {
    if (memory.Size is not (OperandSize.None or OperandSize.Word))
      throw new ArgumentException($"{mnemonic} {memory}: operand is a word.", nameof(memory));

    this.FpuMemory(opcode, regField, memory);
  }

  public void Fincstp() { this.EmitByte(0xD9); this.EmitByte(0xF7); }
  public void Fdecstp() { this.EmitByte(0xD9); this.EmitByte(0xF6); }
  public void Fwait() => this.EmitByte(0x9B);

  #endregion

  #region encoding helpers

  private void FpuMemory(byte opcode, int regField, Mem memory) {
    var start = this.Position;
    this.EmitSegmentPrefix(memory);
    this.EmitByte(opcode);
    this.EmitModRmMemory(regField, memory);
    // C3 x87 scheduling: record with the FPU-stack pseudo-resource so all FPU instructions
    // keep their exact relative order (RAW+WAW on the pseudo-slot) while independent integer
    // work schedules around them. The memory cell is recorded conservatively as read AND
    // written (covers FLD reads, FST/FSTP/FIST writes and read-modify arithmetic alike).
    // Segment-overridden operands stay unrecorded - they remain scheduling barriers.
    if (memory.Segment is null)
      this.RecordSchedMem(start, _FPUSTACK, _FPUSTACK, false, false, memRead: true, memWrite: true, memory);
  }

  private void FpuStack(byte opcode, byte modRmBase, St register) {
    var start = this.Position;
    this.EmitByte(opcode);
    this.EmitByte((byte)(modRmBase + register.Index));
    // pure stack manipulation: ordered against every other FPU op, transparent to integers
    this.RecordSchedReg(start, _FPUSTACK, _FPUSTACK, false, false);
  }

  #endregion
}
