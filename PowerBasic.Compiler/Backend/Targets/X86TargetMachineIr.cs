namespace PowerBasic.Compiler.Backend.Targets;

/// <summary>Width-specific general-purpose register file for the hosted x86 targets.</summary>
public sealed class X86TargetRegisterFile {
  public X86TargetRegisterFile(X86Mode mode) {
    this.Mode = mode;
    var names = mode == X86Mode.Bit64
      ? new[] { "rax", "rcx", "rdx", "rbx", "rsp", "rbp", "rsi", "rdi", "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15" }
      : mode == X86Mode.Bit32
        ? new[] { "eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi" }
        : new[] { "ax", "cx", "dx", "bx", "sp", "bp", "si", "di" };
    this.Registers = names.Select((name, index) =>
      new MachineRegister(name, index, mode == X86Mode.Bit64 ? 64 : mode == X86Mode.Bit32 ? 32 : 16)).ToArray();
  }

  public X86Mode Mode { get; }
  public IReadOnlyList<MachineRegister> Registers { get; }
  public MachineRegister StackPointer => this.Registers[4];
  public MachineRegister FramePointer => this.Registers[5];
  public MachineRegister ReturnValue => this.Registers[0];
}

/// <summary>Target-specific memory address; no segment-register or 16-bit addressing assumptions.</summary>
public readonly record struct X86TargetAddress(
    MachineRegister? Base,
    MachineRegister? Index,
    byte Scale,
    int Displacement,
    int WidthBits = 0,
    string? Symbol = null);

/// <summary>Explicit ABI facts consumed by lowering and emission.</summary>
public sealed record X86TargetAbi(
    X86Mode Mode,
    string Name,
    int StackAlignment,
    int ShadowSpaceBytes,
    IReadOnlyList<MachineRegister> ArgumentRegisters,
    MachineRegister ReturnRegister,
    IReadOnlySet<MachineRegister> CalleeSavedRegisters);

/// <summary>Target machine instruction independent of the legacy DOS assembler.</summary>
public sealed record X86TargetInstruction(
    X86TargetOpcode Opcode,
    IReadOnlyList<MachineRegister> Registers,
    long Immediate = 0,
    X86TargetAddress? Address = null,
    string? Symbol = null,
    IReadOnlyList<X86TargetOperand>? Operands = null);

/// <summary>Explicit target operand kinds; register-only instructions use the compact fields above.</summary>
public abstract record X86TargetOperand {
  public sealed record Register(MachineRegister Value) : X86TargetOperand;
  public sealed record Immediate(long Value) : X86TargetOperand;
  public sealed record Memory(X86TargetAddress Value) : X86TargetOperand;
  public sealed record Symbol(string Name) : X86TargetOperand;
}

public enum X86TargetOpcode {
  Mov, Xchg, Lea,
  Add, Sub, And, Or, Xor, Cmp, Test, Adc, Sbb,
  Imul, Mul, Idiv, Div, Neg, Not, Inc, Dec, Cwd, Cbw,
  Shl, Shr, Sar, Shld, Shrd, Rcl, Rcr,
  Push, Pop, Jmp, Jcc, Call, Ret, CallFar, JmpIndirect, JmpIndexed,
  Fld, Fstp, Fild, Fistp, Faddp, Fsubp, Fmulp, Fdivp,
  Fadd, Fsub, Fmul, Fdiv, Fcomp, Fiadd, Fisub, Fimul, Fidiv,
  Fcompp, FstswAx, Sahf, Fsqrt,
  Fsin, Fcos, Fptan, Fpatan, Fyl2x, Fxch, FstpSt0,
  Fld1, Fldln2, Fldlg2, Fldl2e, Fldl2t,
  InlineAsm,

  // Compatibility spellings for hosted target tests and clients while they migrate to the
  // complete target opcode vocabulary.
  MoveImmediate = Mov,
  MoveRegister = Mov,
  AddImmediate = Add,
  SubImmediate = Sub,
  AndImmediate = And,
  OrImmediate = Or,
  XorImmediate = Xor,
  CompareImmediate = Cmp,
  PushRegister = Push,
  PopRegister = Pop,
  PushImmediate = Push,
  CallRelative = Call,
  Return = Ret,
}

/// <summary>Independent target machine function used by hosted x86 emitters.</summary>
public sealed class X86TargetMachineFunction(
    X86Mode mode,
    X86TargetAbi abi,
    IReadOnlyList<X86TargetInstruction> instructions,
    IReadOnlyDictionary<string, int>? labelInstructionIndices = null) {
  public X86Mode Mode { get; } = mode;
  public X86TargetAbi Abi { get; } = abi;
  public IReadOnlyList<X86TargetInstruction> Instructions { get; } = instructions;
  public IReadOnlyDictionary<string, int> LabelInstructionIndices { get; }
    = labelInstructionIndices ?? new Dictionary<string, int>(StringComparer.Ordinal);
}
