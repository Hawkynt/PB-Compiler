namespace PowerBasic.Compiler.Backend.Targets;

/// <summary>Width-specific general-purpose register file for the hosted x86 targets.</summary>
public sealed class X86TargetRegisterFile {
  public X86TargetRegisterFile(X86Mode mode) {
    this.Mode = mode;
    this.Registers = mode switch {
      X86Mode.Bit64 => X86RegisterFile.Gpr64,
      X86Mode.Bit32 => X86RegisterFile.Gpr32,
      _ => X86RegisterFile.Gpr16,
    };
    this.Words = mode == X86Mode.Bit64 ? X86RegisterFile.Gpr64Words : X86RegisterFile.Gpr16;
    this.Dwords = mode == X86Mode.Bit64 ? X86RegisterFile.Gpr64Dwords : X86RegisterFile.Gpr32;
    this.LowBytes = mode == X86Mode.Bit64 ? X86RegisterFile.Gpr64LowBytes : X86RegisterFile.LowBytes;
    this.HighBytes = X86RegisterFile.HighBytes;
  }

  public X86Mode Mode { get; }
  public IReadOnlyList<MachineRegister> Registers { get; }
  public IReadOnlyList<MachineRegister> Words { get; }
  public IReadOnlyList<MachineRegister> Dwords { get; }
  public IReadOnlyList<MachineRegister> LowBytes { get; }
  public IReadOnlyList<MachineRegister> HighBytes { get; }
  public IEnumerable<MachineRegister> All {
    get {
      var aliases = this.Registers.Concat(this.Words).Concat(this.Dwords).Concat(this.LowBytes);
      return this.Mode == X86Mode.Bit64 ? aliases.Distinct() : aliases.Concat(this.HighBytes).Distinct();
    }
  }
  public MachineRegister StackPointer => this.Registers[4];
  public MachineRegister FramePointer => this.Registers[5];
  public MachineRegister ReturnValue => this.Registers[0];

  public MachineRegister RegisterFor(MReg register) {
    var index = (int)register.Physical & 0x0F;
    if ((uint)index >= (uint)this.Registers.Count)
      throw new InvalidOperationException("physical register is not in the target register file");
    if (register.Size is MRegSize.Qword or MRegSize.Tbyte)
      return this.Registers[index];
    if (register.Size == MRegSize.Byte) {
      // Reg.AH..BH intentionally retain their hardware encodings (4..7); they are
      // not interchangeable with AL..BL and cannot be encoded with a REX prefix.
      if (((int)register.Physical & 0xF0) == 0 && index >= 4) {
        if (this.Mode == X86Mode.Bit64)
          throw new InvalidOperationException("AH/CH/DH/BH are not encodable in x86-64 mode");
        return this.HighBytes[index - 4];
      }
      if ((uint)index >= (uint)this.LowBytes.Count)
        throw new InvalidOperationException("physical byte register is not in the target register file");
      return this.LowBytes[index];
    }
    return register.Size switch {
      MRegSize.Word => this.Words[index],
      MRegSize.Dword => this.Dwords[index],
      _ => this.Registers[index],
    };
  }
}

/// <summary>Explicit SIMD register classes.  They are not aliases for the scalar allocator.</summary>
public enum X86VectorRegisterClass { Mmx64, Xmm128, Ymm256, Zmm512 }

public sealed class X86VectorRegisterFile(X86VectorRegisterClass registerClass) {
  public X86VectorRegisterClass Class { get; } = registerClass;
  public int WidthBits => registerClass switch {
    X86VectorRegisterClass.Mmx64 => 64,
    X86VectorRegisterClass.Xmm128 => 128,
    X86VectorRegisterClass.Ymm256 => 256,
    _ => 512,
  };
  public IReadOnlyList<MachineRegister> Registers { get; } = Enumerable.Range(0,
    registerClass == X86VectorRegisterClass.Mmx64 ? 8 : registerClass == X86VectorRegisterClass.Zmm512 ? 32 : 16)
    .Select(index => new MachineRegister(
      registerClass == X86VectorRegisterClass.Mmx64 ? $"mm{index}" :
      registerClass == X86VectorRegisterClass.Xmm128 ? $"xmm{index}" :
      registerClass == X86VectorRegisterClass.Ymm256 ? $"ymm{index}" : $"zmm{index}", index,
      registerClass switch {
        X86VectorRegisterClass.Mmx64 => 64,
        X86VectorRegisterClass.Xmm128 => 128,
        X86VectorRegisterClass.Ymm256 => 256,
        _ => 512,
      })).ToArray();
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
    IReadOnlyList<X86TargetOperand>? Operands = null,
    X86VectorOpcode? VectorOperation = null);

/// <summary>Explicit target operand kinds; register-only instructions use the compact fields above.</summary>
public abstract record X86TargetOperand {
  public sealed record Register(MachineRegister Value) : X86TargetOperand;
  public sealed record Immediate(long Value) : X86TargetOperand;
  public sealed record Memory(X86TargetAddress Value) : X86TargetOperand;
  public sealed record Symbol(string Name) : X86TargetOperand;
  public sealed record Table(IReadOnlyList<string> Labels, IReadOnlyList<ushort>? Keys = null) : X86TargetOperand;
}

public enum X86TargetOpcode {
  Nop, Popcnt, Bsf, Bsr, Bextr, Andn, Blsi, Blsr, Bzhi, Pext, Pdep, Mulx,
  AesEnc, AesDec, AesImc, Pclmul, VectorBinary, Mov, Xchg, Lea,
  MoveMemoryImmediate, MoveMemorySymbol,
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
    IReadOnlyDictionary<string, int>? labelInstructionIndices = null,
    int frameSizeBytes = 0,
    IReadOnlyList<(int Argument, IReadOnlyList<MachineRegister> Registers, int StackOffset)>? argumentLocations = null) {
  public X86Mode Mode { get; } = mode;
  public X86TargetAbi Abi { get; } = abi;
  public IReadOnlyList<X86TargetInstruction> Instructions { get; } = instructions;
  public IReadOnlyDictionary<string, int> LabelInstructionIndices { get; }
    = labelInstructionIndices ?? new Dictionary<string, int>(StringComparer.Ordinal);
  public int FrameSizeBytes { get; } = frameSizeBytes;
  public IReadOnlyList<(int Argument, IReadOnlyList<MachineRegister> Registers, int StackOffset)> ArgumentLocations { get; }
    = argumentLocations ?? [];
}
