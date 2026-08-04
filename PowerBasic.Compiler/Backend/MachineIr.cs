using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Backend;

/// <summary>
/// The target-level machine IR the x86-16 back end selects the SSA IR into (docs/X86-BACKEND.md).
/// It is a list of <see cref="MInstr"/> over <see cref="MReg"/> virtual registers; register
/// allocation rewrites the virtuals to physical <see cref="Reg"/>s and the emitter then lowers each
/// instruction through the existing <see cref="Assembler"/> (so encoding, length and fixups are
/// handled there - the byte-level renaming walls never arise). This file is just the data model;
/// instruction selection, liveness, linear-scan allocation, emission and scheduling build on it.
/// </summary>
public enum MRegSize { Byte, Word, Dword }

/// <summary>A register operand: a virtual id until allocation binds it to a physical register.</summary>
public readonly record struct MReg(int VirtualId, Reg Physical, MRegSize Size, bool IsVirtual) {

  /// <summary>A fresh virtual register of the given size (bound to a physical register by allocation).</summary>
  public static MReg Virtual(int id, MRegSize size = MRegSize.Word) => new(id, default, size, true);

  /// <summary>A fixed physical register (for ABI-pinned spots: the <c>AX</c>/<c>DX</c> of MUL/DIV, <c>CL</c> shift counts, return values).</summary>
  public static MReg Physical_(Reg reg, MRegSize size = MRegSize.Word) => new(-1, reg, size, false);

  public override string ToString() => this.IsVirtual ? $"v{this.VirtualId}:{this.Size}" : this.Physical.ToString();
}

/// <summary>An instruction operand: a register, an immediate, a memory reference, a code/data label or a spill/stack slot.</summary>
public abstract record MOperand {

  public sealed record Register(MReg Reg) : MOperand;

  public sealed record Immediate(long Value) : MOperand;

  /// <summary><c>[Base + Index*Scale + Disp]</c>; <see cref="Base"/>/<see cref="Index"/> are registers, either may be null.</summary>
  public sealed record Memory(MReg? Base, MReg? Index, int Scale, int Disp, MRegSize Size) : MOperand;

  /// <summary>A code label (branch target) or a data/global symbol address.</summary>
  public sealed record LabelRef(string Name) : MOperand;

  /// <summary>A frame stack slot - allocas and register spills resolve to <c>[BP + Offset]</c> at emission.</summary>
  public sealed record StackSlot(int Index, MRegSize Size) : MOperand;

  /// <summary>
  /// A module-level variable's data cell, named as the IR names it (<c>g.total</c>, <c>static.c</c>).
  /// The back end does not lay data out - the whole-program codegen does - so this resolves at
  /// emission to exactly the <c>Mem</c> the direct emitter uses for that symbol, which is what keeps
  /// the two paths addressing the same storage.
  /// </summary>
  public sealed record DataCell(string Name, int Disp, MRegSize Size) : MOperand;
}

/// <summary>
/// A machine instruction: an opcode, its operands, and a conservative def/use descriptor so that one
/// model drives liveness, allocation and scheduling. Branch/return targets are carried as
/// <see cref="MOperand.LabelRef"/> operands; the descriptor names which register operands it reads and
/// writes plus whether it touches flags and memory.
/// </summary>
public sealed class MInstr(MOpcode opcode, IReadOnlyList<MOperand> operands, MInstrEffect effect,
    Condition? condition = null, IReadOnlyList<Reg>? clobbers = null) {
  public MOpcode Opcode { get; } = opcode;
  public IReadOnlyList<MOperand> Operands { get; } = operands;
  public MInstrEffect Effect { get; } = effect;

  /// <summary>The branch condition for a <see cref="MOpcode.Jcc"/> (null for every other opcode).</summary>
  public Condition? Condition { get; } = condition;

  /// <summary>Physical registers this instruction destroys (a CALL's caller-saved set); a value live across it must avoid them.</summary>
  public IReadOnlyList<Reg> Clobbers { get; } = clobbers ?? [];

  public bool IsTerminator => this.Opcode is MOpcode.Jmp or MOpcode.Jcc or MOpcode.Ret;

  public override string ToString() => $"{this.Opcode} {string.Join(", ", this.Operands)}";
}

/// <summary>What an <see cref="MInstr"/> reads and writes, in terms of operand positions (so allocation can rewrite virtuals).</summary>
/// <param name="WrittenRegs">operand indices whose register this instruction defines</param>
/// <param name="ReadRegs">operand indices whose register this instruction uses</param>
public readonly record struct MInstrEffect(
  IReadOnlyList<int> WrittenRegs,
  IReadOnlyList<int> ReadRegs,
  bool ReadsFlags,
  bool WritesFlags,
  bool ReadsMemory,
  bool WritesMemory) {

  public static MInstrEffect None { get; } = new([], [], false, false, false, false);
}

/// <summary>The x86-16 opcodes the selector targets; each maps to an <see cref="Assembler"/> method at emission.</summary>
public enum MOpcode {
  Mov, Lea,
  Add, Sub, And, Or, Xor, Cmp, Test,
  /// <summary>The carry-chain halves of 32-bit add/subtract: a LONG lives in a register pair on this target.</summary>
  Adc, Sbb,
  Imul, Mul, Idiv, Div,
  Neg, Not, Inc, Dec,
  Shl, Shr, Sar,
  /// <summary>Rotate-through-carry, the second half of a 32-bit shift on a register pair.</summary>
  Rcl, Rcr,
  Push, Pop,
  Jmp, Jcc, Call, Ret,
}

/// <summary>A machine basic block: a label, its instructions in order, and its successor labels.</summary>
public sealed class MBlock(string label) {
  public string Label { get; } = label;
  public List<MInstr> Instructions { get; } = [];
  public List<string> Successors { get; } = [];
}

/// <summary>A machine function: its blocks, the number of virtual registers selection minted, and the stack-slot table.</summary>
public sealed class MFunction(string name) {
  public string Name { get; } = name;
  public List<MBlock> Blocks { get; } = [];
  public int VirtualRegisterCount { get; set; }

  /// <summary>The frame stack slots (allocas + register spills), as byte sizes; frame offsets are assigned at emission.</summary>
  public List<int> StackSlots { get; } = [];

  /// <summary>
  /// How the prologue loads the incoming arguments: which virtual register takes which word of which
  /// argument. A 16-bit argument contributes one entry, a 32-bit one contributes two (its low word at
  /// the parameter's own offset and its high word at +2) - which is why this is a table rather than
  /// the positional "argument i is virtual register i" the emitter used to assume.
  /// </summary>
  public List<(int VirtualId, int ArgumentIndex, int ByteDelta)> ArgumentLoads { get; } = [];

  public IEnumerable<MInstr> AllInstructions {
    get {
      foreach (var block in this.Blocks)
        foreach (var instr in block.Instructions)
          yield return instr;
    }
  }
}
