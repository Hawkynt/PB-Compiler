namespace PowerBasic.Compiler.Backend;

/// <summary>
/// The idiom pass over the selected machine IR (docs/X86-BACKEND.md): three rewrites that are about
/// x86 ENCODINGS rather than about the program, which is why none of them belongs in the IR.
///
/// <list type="bullet">
/// <item><b>A memory operand instead of a staged register.</b> <c>MOV v,[n] / ADD d,v</c> is
///   <c>ADD d,[n]</c> - the 8086's ALU takes one operand from memory, so the load was never an
///   instruction, it was an addressing mode.</item>
/// <item><b>Read-modify-write in place.</b> <c>MOV v,[a] / ADD v,1 / MOV [a],v</c> is <c>INC [a]</c>,
///   and the same shape with any other constant or register is <c>ADD [a],imm</c>.</item>
/// <item><b>A bit test.</b> <c>MOV v,x / AND v,mask / CMP v,0</c> is <c>TEST x,mask</c> - the flags
///   <c>TEST</c> writes are bit for bit the flags that sequence wrote, and the masked value nobody
///   else reads never has to exist.</item>
/// </list>
///
/// <para>
/// <b>Why it is a pass over the machine IR and not a selection pattern.</b> Each of these spans
/// instructions that came from SEPARATE IR instructions - a load and the binary that consumes it, a
/// load/modify/store trio the SSA form has already taken apart into three. Selection walks one IR
/// instruction at a time and mints a virtual register for every result, so the shapes only exist once
/// selection is over. It runs BEFORE scheduling and allocation deliberately: every rewrite here
/// removes an instruction and a value, so it can only lower register pressure, and doing it after
/// allocation would mean undoing an assignment.
/// </para>
///
/// <para>
/// <b>Why each is sound.</b> All three rest on the same two facts, read from a census of the WHOLE
/// function rather than from the block: the value being eliminated is defined only by the instructions
/// being rewritten, and read only by them - so no other block can observe it. Between the instructions
/// of a pattern nothing may write memory, clobber the register file (a CALL, an ABI-pinned sequence,
/// an inline-asm block), or write a register the folded address is formed from; the bit test
/// additionally refuses anything that READS the flags in between, because those flags were the
/// <c>AND</c>'s and the rewrite does not produce them until later. The flags a folded instruction
/// writes are otherwise identical to the ones it replaces - <c>CMP r,0</c> after <c>AND r,mask</c>
/// leaves exactly what <c>TEST r,mask</c> leaves - except for <c>INC</c>/<c>DEC</c>, which preserve
/// the carry where <c>ADD</c>/<c>SUB</c> write it, so those two are taken only where the flags are
/// provably dead.
/// </para>
///
/// <para>
/// <b>The addressing rule, which is the one that costs coverage if it is got wrong.</b> A value used
/// as a memory BASE or INDEX may only live in <c>BX</c>/<c>SI</c>/<c>DI</c> and cannot itself move to
/// memory (<c>LinearScanAllocator.LegalFor</c>), so lengthening such a value's live range is the move
/// that makes a function fail to allocate. A cell with no register in it - a frame slot, a parameter
/// word, a module variable - lengthens nothing and folds at any distance. A register-formed address
/// folds only into the instruction IMMEDIATELY following the load, where the last read moves by one
/// slot and nothing can be placed in between; the read-modify-write form has no such limit in the
/// other direction, because it deletes two of the three accesses and the address register's range
/// SHRINKS.
/// </para>
///
/// <para>
/// <b>What it deliberately refuses.</b> A value with a second reader is left alone even when one of
/// its readers matches - materializing it is what the second reader wants, and duplicating the load
/// would trade one instruction for two memory accesses. A memory-to-memory pattern is refused because
/// the machine has none. And the whole pass is gated on the optimizer being ON
/// (<see cref="SelectionTarget.Optimize"/>): with it off, selection must write what it would have
/// written.
/// </para>
/// </summary>
public static class Peephole {

  /// <summary>The two-address ALU opcodes whose SOURCE operand the 8086 can take straight from memory.</summary>
  private static bool FoldsMemorySource(MOpcode opcode) => opcode is
    MOpcode.Add or MOpcode.Sub or MOpcode.And or MOpcode.Or or MOpcode.Xor
    or MOpcode.Adc or MOpcode.Sbb or MOpcode.Cmp or MOpcode.Test;

  /// <summary>The two-address ALU opcodes that can also take their DESTINATION in memory.</summary>
  private static bool FoldsMemoryDestination(MOpcode opcode) => opcode is
    MOpcode.Add or MOpcode.Sub or MOpcode.And or MOpcode.Or or MOpcode.Xor;

  /// <summary>Rewrites the idioms above in place; the number of rewrites made.</summary>
  public static int Run(MFunction function) {
    ArgumentNullException.ThrowIfNull(function);
    var total = 0;
    // Each rewrite removes a value, so the census changes and a pattern may only now be visible: a
    // load folded into an ADD can leave that ADD the middle of a read-modify-write. The bound is a
    // belt-and-braces guard - every round strictly shrinks the function, so it cannot spin.
    for (var round = 0; round < 8; ++round) {
      var census = Census.Of(function);
      var made = 0;
      foreach (var block in function.Blocks) {
        made += FoldBitTests(block, census);
        made += FoldReadModifyWrites(block, census);
        made += FoldMemorySources(block, census);
      }
      total += made;
      if (made == 0)
        break;
    }
    return total;
  }

  /// <summary>
  /// How many times each virtual register is defined and read over the WHOLE function, which is what
  /// makes "nobody else can see this value" a fact rather than a block-local guess. A memory operand's
  /// base, index and segment count as reads, exactly as <see cref="LivenessAnalysis.RegistersOf"/>
  /// counts them.
  /// </summary>
  private sealed record Census(Dictionary<int, int> Defs, Dictionary<int, int> Uses) {

    public static Census Of(MFunction function) {
      var defs = new Dictionary<int, int>();
      var uses = new Dictionary<int, int>();
      foreach (var instr in function.AllInstructions) {
        var (reads, writes) = LivenessAnalysis.RegistersOf(instr);
        foreach (var read in reads)
          uses[read] = uses.GetValueOrDefault(read) + 1;
        foreach (var write in writes)
          defs[write] = defs.GetValueOrDefault(write) + 1;
      }
      return new(defs, uses);
    }

    /// <summary>Whether the value is virtual and mentioned exactly this many times, and no more.</summary>
    public bool Exactly(MReg register, int definitions, int readers)
      => register.IsVirtual
         && this.Defs.GetValueOrDefault(register.VirtualId) == definitions
         && this.Uses.GetValueOrDefault(register.VirtualId) == readers;
  }

  private static bool IsMemory(MOperand operand) => operand
    is MOperand.Memory or MOperand.StackSlot or MOperand.DataCell or MOperand.ParamCell;

  /// <summary>The registers a memory operand's effective address is formed from (empty for a direct cell).</summary>
  private static List<MReg> AddressRegisters(MOperand cell) {
    var registers = new List<MReg>();
    if (cell is not MOperand.Memory memory)
      return registers;
    if (memory.Base is { } baseRegister)
      registers.Add(baseRegister);
    if (memory.Index is { } index)
      registers.Add(index);
    if (memory.Segment is { } segment)
      registers.Add(segment);
    return registers;
  }

  /// <summary>
  /// <c>MOV v,[n]</c> whose only reader is a following ALU instruction becomes that instruction's
  /// memory operand.
  /// </summary>
  private static int FoldMemorySources(MBlock block, Census census) {
    var made = 0;
    for (var i = 0; i < block.Instructions.Count; ++i) {
      var load = block.Instructions[i];
      if (load.Opcode != MOpcode.Mov || load.Operands.Count != 2
          || load.Operands[0] is not MOperand.Register { Reg: { IsVirtual: true } value }
          || !IsMemory(load.Operands[1]))
        continue;
      if (!census.Exactly(value, definitions: 1, readers: 1))
        continue;
      var cell = load.Operands[1];
      var address = AddressRegisters(cell);
      if (address.Contains(value))
        continue;                                // the load's own address: not a value being staged

      var consumer = FindSingleReader(block, i + 1, value, address);
      if (consumer < 0 || (address.Count > 0 && consumer != i + 1))
        continue;                                // see the addressing rule in the type remarks
      var user = block.Instructions[consumer];
      // the value must be the SOURCE of a two-address ALU op whose destination is a register: the
      // machine has no memory-to-memory form, and the destination is where the result goes
      if (!FoldsMemorySource(user.Opcode) || user.Operands.Count != 2
          || user.Operands[0] is not MOperand.Register
          || user.Operands[1] is not MOperand.Register { Reg: var read } || !read.Equals(value))
        continue;

      block.Instructions[consumer] = new MInstr(user.Opcode, [user.Operands[0], cell],
        new MInstrEffect(WrittenRegs: user.Effect.WrittenRegs, ReadRegs: [0],
          ReadsFlags: user.Effect.ReadsFlags, WritesFlags: user.Effect.WritesFlags,
          ReadsMemory: true, WritesMemory: user.Effect.WritesMemory),
        user.Condition, user.Clobbers);
      block.Instructions.RemoveAt(i);
      --i;
      ++made;
    }
    return made;
  }

  /// <summary>
  /// The instruction that reads <paramref name="value"/>, or -1 when something between here and there
  /// makes moving the access unsafe: a write to memory, a clobber of the register file, or a write to
  /// a register the folded address is formed from.
  /// </summary>
  private static int FindSingleReader(MBlock block, int from, MReg value, IReadOnlyCollection<MReg> address) {
    for (var i = from; i < block.Instructions.Count; ++i) {
      var instr = block.Instructions[i];
      var (reads, _) = LivenessAnalysis.RegistersOf(instr);
      if (reads.Contains(value.VirtualId))
        return i;
      if (Disturbs(instr, address))
        return -1;
    }
    return -1;                                   // the reader is in another block: not ours to move
  }

  /// <summary>Whether an instruction in between invalidates a memory operand that is being moved past it.</summary>
  private static bool Disturbs(MInstr instr, IReadOnlyCollection<MReg> address) {
    if (instr.Effect.WritesMemory || instr.Clobbers.Count > 0)
      return true;
    if (instr.Opcode is MOpcode.Call or MOpcode.InlineAsm || instr.IsTerminator)
      return true;
    if (address.Count == 0)
      return false;
    foreach (var index in instr.Effect.WrittenRegs)
      if (index < instr.Operands.Count && instr.Operands[index] is MOperand.Register written
          && address.Contains(written.Reg))
        return true;
    return false;
  }

  /// <summary>
  /// <c>MOV v,[a] / OP v,src / MOV [a],v</c> - a cell read, changed and written back with nothing else
  /// looking at the intermediate - becomes <c>OP [a],src</c>, and the <c>+/-1</c> cases become
  /// <c>INC</c>/<c>DEC</c> where the carry they leave alone is dead.
  /// </summary>
  private static int FoldReadModifyWrites(MBlock block, Census census) {
    var made = 0;
    for (var i = 0; i + 2 < block.Instructions.Count; ++i) {
      var load = block.Instructions[i];
      var modify = block.Instructions[i + 1];
      var store = block.Instructions[i + 2];
      if (load.Opcode != MOpcode.Mov || load.Operands.Count != 2
          || load.Operands[0] is not MOperand.Register { Reg: { IsVirtual: true } value }
          || !IsMemory(load.Operands[1]))
        continue;
      var cell = load.Operands[1];
      if (AddressRegisters(cell).Contains(value))
        continue;
      if (!FoldsMemoryDestination(modify.Opcode) || modify.Operands.Count != 2
          || modify.Operands[0] is not MOperand.Register first || !first.Reg.Equals(value)
          || modify.Operands[1] is not (MOperand.Immediate or MOperand.Register))
        continue;
      if (modify.Operands[1] is MOperand.Register source && source.Reg.Equals(value))
        continue;                                // OP v,v is not a read-modify-write of the cell
      if (store.Opcode != MOpcode.Mov || store.Operands.Count != 2
          || !store.Operands[0].Equals(cell)
          || store.Operands[1] is not MOperand.Register written || !written.Reg.Equals(value))
        continue;
      // the intermediate is written by the load and the modify, and read by the modify and the store:
      // any other mention means somebody else can see it
      if (!census.Exactly(value, definitions: 2, readers: 2))
        continue;

      var rhs = modify.Operands[1];
      block.Instructions[i] = rhs is MOperand.Immediate { Value: 1 }
          && modify.Opcode is MOpcode.Add or MOpcode.Sub && FlagsDeadAfter(block, i + 3)
        ? new MInstr(modify.Opcode == MOpcode.Add ? MOpcode.Inc : MOpcode.Dec, [cell],
          new MInstrEffect(WrittenRegs: [], ReadRegs: [], ReadsFlags: false, WritesFlags: true,
            ReadsMemory: true, WritesMemory: true))
        : new MInstr(modify.Opcode, [cell, rhs],
          new MInstrEffect(WrittenRegs: [], ReadRegs: rhs is MOperand.Register ? [1] : [],
            ReadsFlags: modify.Effect.ReadsFlags, WritesFlags: true,
            ReadsMemory: true, WritesMemory: true));
      block.Instructions.RemoveRange(i + 1, 2);
      ++made;
    }
    return made;
  }

  /// <summary>
  /// Whether nothing from <paramref name="from"/> on reads the flags before something overwrites them.
  /// Reaching the end of the block is NOT proof - a successor may branch on them - so it answers no.
  /// </summary>
  private static bool FlagsDeadAfter(MBlock block, int from) {
    for (var i = from; i < block.Instructions.Count; ++i) {
      if (block.Instructions[i].Effect.ReadsFlags)
        return false;
      if (block.Instructions[i].Effect.WritesFlags)
        return true;
    }
    return false;
  }

  /// <summary>
  /// <c>MOV v,x / AND v,mask / CMP v,0</c> becomes <c>TEST x,mask</c>: the masked value is never
  /// needed, only the flags it would have been compared for, and those are the flags <c>TEST</c>
  /// already writes.
  /// </summary>
  private static int FoldBitTests(MBlock block, Census census) {
    var made = 0;
    for (var i = 0; i + 1 < block.Instructions.Count; ++i) {
      var load = block.Instructions[i];
      var mask = block.Instructions[i + 1];
      if (load.Opcode != MOpcode.Mov || load.Operands.Count != 2
          || load.Operands[0] is not MOperand.Register { Reg: { IsVirtual: true } value })
        continue;
      var subject = load.Operands[1];
      if (subject is MOperand.Register { Reg: var same } && same.Equals(value))
        continue;
      var address = subject switch {
        MOperand.Register register => [register.Reg],
        _ when IsMemory(subject) => AddressRegisters(subject),
        _ => null,
      };
      if (address is null || address.Contains(value))
        continue;
      if (mask.Opcode != MOpcode.And || mask.Operands.Count != 2
          || mask.Operands[0] is not MOperand.Register masked || !masked.Reg.Equals(value)
          || mask.Operands[1] is not MOperand.Immediate immediate)
        continue;

      var test = FindZeroTest(block, i + 2, value, address);
      if (test < 0)
        continue;
      // TEST v,v names the value twice; CMP v,0 once. Either way nothing else may.
      var readers = block.Instructions[test].Opcode == MOpcode.Test ? 3 : 2;
      if (!census.Exactly(value, definitions: 2, readers: readers))
        continue;

      block.Instructions[test] = new MInstr(MOpcode.Test, [subject, immediate],
        new MInstrEffect(WrittenRegs: [], ReadRegs: subject is MOperand.Register ? [0] : [],
          ReadsFlags: false, WritesFlags: true, ReadsMemory: IsMemory(subject), WritesMemory: false));
      block.Instructions.RemoveRange(i, 2);
      --i;
      ++made;
    }
    return made;
  }

  /// <summary>
  /// The <c>CMP v,0</c> or <c>TEST v,v</c> that asks whether the masked value is zero, or -1 when
  /// something in between destroys the subject, writes memory, or READS the flags the <c>AND</c> left -
  /// the one thing the rewrite cannot reproduce, since it produces those flags later instead.
  /// </summary>
  private static int FindZeroTest(MBlock block, int from, MReg value, IReadOnlyCollection<MReg> address) {
    for (var i = from; i < block.Instructions.Count; ++i) {
      var instr = block.Instructions[i];
      if (instr.Operands.Count == 2
          && instr.Operands[0] is MOperand.Register { Reg: var tested } && tested.Equals(value)
          && (instr.Opcode == MOpcode.Cmp && instr.Operands[1] is MOperand.Immediate { Value: 0 }
              || instr.Opcode == MOpcode.Test && instr.Operands[1] is MOperand.Register { Reg: var other }
                 && other.Equals(value)))
        return i;
      if (instr.Effect.ReadsFlags || Disturbs(instr, address))
        return -1;
    }
    return -1;
  }
}
