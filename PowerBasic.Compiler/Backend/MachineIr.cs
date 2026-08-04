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
/// <summary>
/// An operand's width. <c>Qword</c> never names a register on this target - x86-16 has none - but it
/// does name a memory cell: a DOUBLE, and the qword form of an x87 load or store.
/// </summary>
/// <summary>
/// The width of an operand. The three wide ones never name a REGISTER on a 16-bit machine - they
/// only ever describe a memory reference an x87 instruction reaches: a SINGLE is a dword, a DOUBLE a
/// qword, and an EXTENDED a tbyte. Writing one through a narrower reference stores part of a value.
/// </summary>
public enum MRegSize { Byte, Word, Dword, Qword, Tbyte }

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

  /// <summary>
  /// The <b>address</b> of a data object rather than its contents - <c>MOV SI, OFFSET .str0</c>, the
  /// form the DOS runtime's string entries take their argument in. Same naming and same resolution as
  /// <see cref="DataCell"/>: the codegen owns the layout, so this becomes an <c>Imm.OffsetOf</c> of the
  /// very label the direct emitter would have used.
  /// </summary>
  public sealed record DataOffset(string Name, int Disp) : MOperand;

  /// <summary>
  /// The OFFSET of a basic block's own label - the machine form of the IR's <c>blockaddress</c>.
  /// PB needs it for exactly one thing: <c>ON ERROR GOTO</c> writes a code address into a runtime
  /// cell, and a fault anywhere afterwards jumps through it. No other operand names a point in this
  /// function's own code, because every other transfer of control IS an instruction.
  /// </summary>
  public sealed record BlockOffset(string Block) : MOperand;

  /// <summary>
  /// An incoming argument read straight out of the cell the caller pushed it into - <c>[BP+6]</c>.
  /// This is where a spilled parameter lives: it is already in the frame, it is never written (an IR
  /// argument is an SSA value), so the cheapest possible spill is to stop copying it into a register
  /// at all and address the caller's word instead. The emitter resolves the index through the same
  /// parameter offsets the prologue uses.
  /// </summary>
  public sealed record ParamCell(int ArgumentIndex, int ByteDelta) : MOperand;
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

  public bool IsTerminator => this.Opcode is MOpcode.Jmp or MOpcode.Jcc or MOpcode.Ret or MOpcode.JmpIndirect;

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
  /// <summary>Sign-extend AX into DX:AX - the dividend a 16-bit IDIV consumes.</summary>
  Cwd,
  Shl, Shr, Sar,
  /// <summary>Rotate-through-carry, the second half of a 32-bit shift on a register pair.</summary>
  Rcl, Rcr,
  Push, Pop,
  Jmp, Jcc, Call, Ret,
  /// <summary>
  /// A jump THROUGH a memory cell - the only indirect transfer this back end emits. RESUME and
  /// RESUME NEXT go back to a statement the FAULT chose, so the destination is a value the runtime
  /// latched rather than a label anything here can name.
  /// </summary>
  JmpIndirect,
  /// <summary>
  /// x87. Floating point is computed on a stack, not in the register file, so these carry at most one
  /// MEMORY operand and the arithmetic forms carry none at all - they consume the two values the
  /// preceding loads pushed. Selection keeps every float value in a frame cell and brackets each
  /// operation with FLD/FSTP, so the stack is empty again at every instruction boundary and nothing
  /// the allocator models is involved.
  /// </summary>
  Fld, Fstp, Fild, Fistp,
  Faddp, Fsubp, Fmulp, Fdivp,

  /// <summary>Square root of ST(0), in place - no operand, because the x87 answers where it was asked.</summary>
  Fsqrt,
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

  /// <summary>
  /// Whether <see cref="ArgumentLoads"/> is the authoritative plan. Selection always builds one, so an
  /// EMPTY table means every parameter was spilled into its own incoming cell and the prologue loads
  /// nothing - which is not the same as a hand-built function that never had a table, where the
  /// emitter falls back to "argument i is virtual register i".
  /// </summary>
  public bool HasArgumentPlan { get; set; }

  public IEnumerable<MInstr> AllInstructions {
    get {
      foreach (var block in this.Blocks)
        foreach (var instr in block.Instructions)
          yield return instr;
    }
  }
}
