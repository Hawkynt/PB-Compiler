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

  /// <summary>
  /// <c>[Base + Index*Scale + Disp]</c>; <see cref="Base"/>/<see cref="Index"/> are registers, either
  /// may be null.
  ///
  /// <para>
  /// <see cref="Segment"/>, when set, is a general register HOLDING a segment value, and the access
  /// goes through that segment instead of the default one - the machine form of <see cref="Ir.IrFarPtr"/>.
  /// It is part of the OPERAND rather than a preceding instruction on purpose: x86-16 reaches a
  /// non-default segment through a segment register, so the value has to be moved into <c>ES</c>
  /// first, and any gap between that move and the access is a window for a later pass - scheduling, a
  /// spill reload, a rematerialized address - to put something in. Carrying it here makes the pair
  /// indivisible: the emitter writes <c>MOV ES, reg</c> immediately in front of the instruction it
  /// belongs to, and nothing upstream can separate what it never saw as two things.
  /// </para>
  ///
  /// <para>
  /// <see cref="SegmentCell"/> is the same idea arrived at from the other end: a runtime WORD holding
  /// the segment, for memory whose segment the program never computes because the runtime owns it -
  /// the far array heap at <c>rt_arrseg</c>, written once at startup. <see cref="Segment"/> is a
  /// value the program made and so needs a register to have been put in; a cell needs none, and
  /// <c>MOV ES, [rt_arrseg]</c> is one instruction where a register would cost a move as well. At
  /// most one of the two is set. Null for both means the default segment, which is every other
  /// operand in the back end.
  /// </para>
  /// </summary>
  public sealed record Memory(MReg? Base, MReg? Index, int Scale, int Disp, MRegSize Size,
    MReg? Segment = null, string? SegmentCell = null) : MOperand;

  /// <summary>A code label (branch target) or a data/global symbol address.</summary>
  public sealed record LabelRef(string Name) : MOperand;

  /// <summary>
  /// A frame stack slot - allocas and register spills resolve to <c>[BP + Offset]</c> at emission.
  /// <paramref name="Disp"/> reaches INSIDE a multi-byte slot: the words of a qword staged for FILD are
  /// the same slot at 0, 2, 4 and 6.
  /// </summary>
  public sealed record StackSlot(int Index, MRegSize Size, int Disp = 0) : MOperand;

  /// <summary>
  /// A source variable's data cell, named as the IR names it (<c>g.total</c>, <c>static.Tick.c</c>).
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
  /// An inline-assembly block: the source text plus the BASIC names it refers to. The instruction's
  /// remaining operands are those names' machine locations in the SAME order, which is what lets the
  /// emitter build a resolver that answers from the ROUTED frame rather than the direct emitter's.
  ///
  /// <para>
  /// <paramref name="Effect"/> is what the text does to the register file, read out of it by the
  /// assembler at selection. The instruction's <see cref="MInstr.Clobbers"/> says only that nothing of
  /// OURS survives the block; this says which registers are THEIRS, and for how long - see
  /// <c>LinearScanAllocator.AsmHeldByIndex</c>, which is what keeps a countdown in <c>CX</c> alive
  /// across the BASIC statement between the two <c>!</c> lines that set it and read it.
  /// </para>
  /// </summary>
  public sealed record InlineAsmText(string Text, IReadOnlyList<string> Names, AsmRegisterEffect Effect) : MOperand;

  /// <summary>
  /// The OFFSET of a basic block's own label - the machine form of the IR's <c>blockaddress</c>.
  /// PB needs it for exactly one thing: <c>ON ERROR GOTO</c> writes a code address into a runtime
  /// cell, and a fault anywhere afterwards jumps through it. No other operand names a point in this
  /// function's own code, because every other transfer of control IS an instruction.
  /// </summary>
  public sealed record BlockOffset(string Block) : MOperand;

  /// <summary>
  /// A table of BLOCK ADDRESSES, assembled as DATA into the code stream immediately behind the
  /// <see cref="MOpcode.JmpIndexed"/> that reads it. It is the one operand here that names several
  /// points in this function's code at once, and the only reason a dispatch can be O(1) rather than a
  /// compare per case.
  ///
  /// <para>
  /// The table lives inside the code because that is the only place a near jump can reach it in one
  /// instruction: <c>JMP word [BX + table]</c> takes a 16-bit displacement in the current segment, and
  /// the segment the code is in is the one the assembler is filling. Nothing falls into it - the jump
  /// in front is unconditional - and the block it sits in is closed by that jump, so no instruction can
  /// be scheduled after the data.
  /// </para>
  ///
  /// <para>
  /// Three forms, which are the three shapes worth emitting on this target:
  /// </para>
  /// <list type="bullet">
  /// <item><b>Plain.</b> <see cref="Blocks"/> alone: entry <c>i</c> is where index <c>i</c> jumps.
  ///   Two bytes per index.</item>
  /// <item><b>Byte-indexed</b> (<see cref="ByteIndex"/> set, the <c>$OPTIMIZE SIZE</c> form). Entry
  ///   <c>i</c> of the byte table names a SLOT of <see cref="Blocks"/>, so a wide span with few
  ///   distinct arms costs <c>span + 2*slots</c> bytes instead of <c>2*span</c>. It costs one extra
  ///   load per dispatch, which is why it is not the default.</item>
  /// <item><b>Key-verified</b> (<see cref="Keys"/> set, the perfect-hash form). The index is
  ///   <c>subject AND <see cref="KeyMask"/></c>, which is collision-free on the case values and on
  ///   nothing else - so the value keyed at the slot is compared against the subject first and a
  ///   mismatch takes the default arm. An empty slot keys 0 and points at the default anyway.</item>
  /// </list>
  ///
  /// <para>
  /// A block this table names must still exist under its own label when emission gets there. That is
  /// the machine-level twin of <see cref="Ir.IrFunction.AddressTakenBlocks"/>, and it holds here for a
  /// different reason: the IR keeps a real CFG edge to every arm of an <see cref="Ir.IrSwitch"/>, so no
  /// IR rewrite can drop one, and nothing after selection merges machine blocks.
  /// </para>
  /// </summary>
  public sealed record BlockAddressTable(IReadOnlyList<string> Blocks,
    IReadOnlyList<byte>? ByteIndex = null, IReadOnlyList<ushort>? Keys = null, int KeyMask = 0) : MOperand;

  /// <summary>
  /// An incoming argument read straight out of the cell the caller pushed it into - <c>[BP+6]</c>.
  /// This is where a spilled parameter lives: it is already in the frame, it is never written (an IR
  /// argument is an SSA value), so the cheapest possible spill is to stop copying it into a register
  /// at all and address the caller's word instead. The emitter resolves the index through the same
  /// parameter offsets the prologue uses.
  /// </summary>
  public sealed record ParamCell(int ArgumentIndex, int ByteDelta, MRegSize Size = MRegSize.Word) : MOperand;
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

  public bool IsTerminator => this.Opcode is MOpcode.Jmp or MOpcode.Jcc or MOpcode.Ret
    or MOpcode.JmpIndirect or MOpcode.JmpIndexed;

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
  /// The indexed indirect jump through a <see cref="MOperand.BlockAddressTable"/>, and the table
  /// itself: <c>JMP word [BX + table]</c> followed by the table's bytes.
  ///
  /// <para>
  /// Operand 0 is the register holding the index (or, for the key-verified form, the subject itself);
  /// operand 1 is the table; operand 2 is the default arm, which only the key-verified form reads -
  /// there the index is a hash and a mismatch has to go somewhere.
  /// </para>
  ///
  /// <para>
  /// The whole sequence is one instruction rather than a run of them because it is indivisible in a way
  /// the def/use model cannot express: the address register is fixed at <c>BX</c> (16-bit addressing has
  /// no other general base a displacement may join), the data follows the jump with no label of its own
  /// until emission, and anything scheduled between the scaling and the jump would be scheduled into a
  /// table. It carries its clobbers for the same reason every ABI-pinned sequence here does.
  /// </para>
  /// </summary>
  JmpIndexed,
  /// <summary>
  /// x87. Floating point is computed on a stack, not in the register file, so these carry at most one
  /// MEMORY operand and the arithmetic forms carry none at all - they consume the two values the
  /// preceding loads pushed. Selection keeps every float value in a frame cell and brackets each
  /// operation with FLD/FSTP, so the stack is empty again at every instruction boundary and nothing
  /// the allocator models is involved.
  /// </summary>
  Fld, Fstp, Fild, Fistp,
  Faddp, Fsubp, Fmulp, Fdivp,

  /// <summary>
  /// The MEMORY forms of the same four operations, plus the compare: <c>ST(0) op= [cell]</c>, with
  /// nothing pushed and nothing popped. They exist because the pair form needs the second operand on
  /// the stack and getting it there is an <c>FLD</c> - so <c>FLD a; FLD b; FADDP</c> and
  /// <c>FLD a; FADD b</c> compute the same value, and the second is one instruction and one stack slot
  /// shorter. The operand may only be a dword or a qword (an <c>FADD</c> has no tbyte form), which is
  /// why an 80-bit intermediate still goes through the pair.
  /// </summary>
  Fadd, Fsub, Fmul, Fdiv, Fcomp,

  /// <summary>
  /// And the INTEGER memory forms - <c>ST(0) op= (real)[cell]</c> for a word or dword integer cell.
  /// The x87 converts as it reads, so an integer operand of a floating expression needs neither a
  /// <c>FILD</c> of its own nor the 80-bit temporary that would hold the converted value.
  /// </summary>
  Fiadd, Fisub, Fimul, Fidiv,

  /// <summary>
  /// Compare ST(0) with ST(1) and pop both, copy the x87 status word into AX, then copy AH's
  /// condition bits into the integer flags. The explicit AX operand on the latter two instructions
  /// makes their hidden dependency visible to scheduling and allocation.
  /// </summary>
  Fcompp, FstswAx, Sahf,

  /// <summary>Square root of ST(0), in place - no operand, because the x87 answers where it was asked.</summary>
  Fsqrt,

  // The transcendental family: each is a bare x87 instruction the direct emitter also writes inline.
  // They take no operand for the same reason FSQRT does not - the stack is where the answer goes.
  Fsin, Fcos, Fptan, Fpatan, Fyl2x, Fxch, FstpSt0,
  Fld1, Fldln2, Fldlg2, Fldl2e, Fldl2t,

  /// <summary>A block of inline assembly, assembled verbatim at emission (see <see cref="MOperand.InlineAsmText"/>).</summary>
  InlineAsm,
}

/// <summary>Facts about opcodes that the scheduler and the selector both need to agree on.</summary>
public static class MOpcodes {

  /// <summary>
  /// Whether the opcode operates on the x87 stack.
  ///
  /// This exists because the machine IR models registers and memory and has NO name for the x87
  /// stack, so nothing an x87 instruction does to it appears in its effect descriptor. Two of them in
  /// a row therefore look independent, and a scheduler is free to swap them - which it did, twice:
  /// an FSQRT moved past the FSTP that captured its answer, and later a FADDP moved out from between
  /// the FLDs that set up its operands, so a DOUBLE accumulated round a loop printed the addend
  /// instead of the sum.
  ///
  /// Both were patched by claiming the instruction touched memory. That worked and was a lie, and it
  /// over-ordered as well: it pinned unrelated integer loads and stores against every x87 operation.
  /// Naming the real resource is both truthful and narrower - x87 instructions are ordered against
  /// each OTHER and against nothing else.
  /// </summary>
  public static bool UsesX87(MOpcode opcode) => opcode is
    MOpcode.Fld or MOpcode.Fstp or MOpcode.Fild or MOpcode.Fistp
    or MOpcode.Faddp or MOpcode.Fsubp or MOpcode.Fmulp or MOpcode.Fdivp
    or MOpcode.Fadd or MOpcode.Fsub or MOpcode.Fmul or MOpcode.Fdiv or MOpcode.Fcomp
    or MOpcode.Fiadd or MOpcode.Fisub or MOpcode.Fimul or MOpcode.Fidiv
    or MOpcode.Fcompp or MOpcode.FstswAx
    or MOpcode.Fsqrt
    or MOpcode.Fsin or MOpcode.Fcos or MOpcode.Fptan or MOpcode.Fpatan or MOpcode.Fyl2x
    or MOpcode.Fxch or MOpcode.FstpSt0
    or MOpcode.Fld1 or MOpcode.Fldln2 or MOpcode.Fldlg2 or MOpcode.Fldl2e or MOpcode.Fldl2t;
}

/// <summary>A machine basic block: a label, its instructions in order, and its successor labels.</summary>
public sealed class MBlock(string label) {
  public string Label { get; } = label;
  public List<MInstr> Instructions { get; } = [];
  public List<string> Successors { get; } = [];

  /// <summary>
  /// Every label control can leave this block for: its CFG successors PLUS the BASIC labels an
  /// inline-assembly block jumps to.
  ///
  /// <c>!JNZ AddLoop</c> is a transfer of control no graph here draws - the target is address-taken
  /// and nothing else - so an analysis reading <see cref="Successors"/> alone does not see the loop it
  /// closes, and would end a value's life at the last point the LAYOUT mentions it.
  /// </summary>
  public IEnumerable<string> SuccessorsWithAsmJumps() {
    foreach (var successor in this.Successors)
      yield return successor;
    foreach (var instr in this.Instructions) {
      if (instr.Opcode != MOpcode.InlineAsm)
        continue;
      foreach (var operand in instr.Operands)
        if (operand is MOperand.BlockOffset target)
          yield return target.Block;
    }
  }
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

  /// <summary>
  /// The virtual registers the spiller minted while splitting a live range - each one a reload or a
  /// store standing beside the single instruction that wants the value.
  ///
  /// It exists so splitting terminates. Splitting a value replaces it with fresh ids whose ranges are
  /// one instruction long, so re-splitting one can only add another store and another reload without
  /// shortening anything; a live range crossing a CALL states that on its own (the fresh range no
  /// longer crosses one), but plain register pressure has no such self-limiting shape and needs the
  /// spiller to remember what it has already taken apart.
  /// </summary>
  public HashSet<int> SplitValues { get; } = [];

  /// <summary>
  /// A copy that can be transformed and thrown away. An <see cref="MInstr"/> is immutable, so only the
  /// LISTS need duplicating - a pass that rewrites an instruction replaces the entry rather than
  /// editing it. It exists for the one caller that has to be able to change its mind: the speed
  /// objective's coalescing may cost an allocation the un-coalesced function had, and a decline is not
  /// an acceptable price for a code-quality transform.
  /// </summary>
  public MFunction Clone() {
    var copy = new MFunction(this.Name) {
      VirtualRegisterCount = this.VirtualRegisterCount,
      HasArgumentPlan = this.HasArgumentPlan,
    };
    copy.StackSlots.AddRange(this.StackSlots);
    copy.ArgumentLoads.AddRange(this.ArgumentLoads);
    copy.SplitValues.UnionWith(this.SplitValues);
    foreach (var block in this.Blocks) {
      var cloned = new MBlock(block.Label);
      cloned.Instructions.AddRange(block.Instructions);
      cloned.Successors.AddRange(block.Successors);
      copy.Blocks.Add(cloned);
    }
    return copy;
  }

  /// <summary>Takes over another function's blocks and frame - how a discarded-or-kept transform commits.</summary>
  public void Adopt(MFunction other) {
    ArgumentNullException.ThrowIfNull(other);
    this.VirtualRegisterCount = other.VirtualRegisterCount;
    this.HasArgumentPlan = other.HasArgumentPlan;
    this.StackSlots.Clear();
    this.StackSlots.AddRange(other.StackSlots);
    this.ArgumentLoads.Clear();
    this.ArgumentLoads.AddRange(other.ArgumentLoads);
    this.SplitValues.Clear();
    this.SplitValues.UnionWith(other.SplitValues);
    this.Blocks.Clear();
    this.Blocks.AddRange(other.Blocks);
  }

  public IEnumerable<MInstr> AllInstructions {
    get {
      foreach (var block in this.Blocks)
        foreach (var instr in block.Instructions)
          yield return instr;
    }
  }
}
