namespace PowerBasic.Compiler.Backend;

/// <summary>
/// The idiom pass over the selected machine IR (docs/X86-BACKEND.md): rewrites that are about
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
/// <item><b>A copy chain.</b> <c>MOV v,src / MOV w,v</c> with nothing else naming <c>v</c> is
///   <c>MOV w,src</c> - the intermediate register was a staging post nobody arrives at.</item>
/// <item><b>The branch layout.</b> A <c>JMP</c> to the block laid out next is the fallthrough, and a
///   <c>Jcc next / JMP away</c> pair is <c>J!cc away</c>: the same two successors reached by one
///   instruction instead of two.</item>
/// <item><b>The zero idiom.</b> <c>MOV r,0</c> is <c>XOR r,r</c> where the flags it dirties are
///   dead - one byte shorter on a word register, three on a dword one.</item>
/// <item><b>A scalar exchange.</b> Two loads followed by the crossed stores produced by <c>SWAP</c>
///   become one load, <c>XCHG reg,[right]</c>, and the remaining store.</item>
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
/// <b>Why each is sound.</b> They rest on the same two facts, read from a census of the WHOLE
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
  public static int Run(X86MachineFunction function) {
    ArgumentNullException.ThrowIfNull(function);
    MachineOptimizationState.Mark(function);
    var total = 0;
    // Each rewrite removes a value, so the census changes and a pattern may only now be visible: a
    // load folded into an ADD can leave that ADD the middle of a read-modify-write. The bound is a
    // belt-and-braces guard - every round strictly shrinks the function, so it cannot spin.
    for (var round = 0; round < 8; ++round) {
      var census = Census.Of(function);
      var made = 0;
      foreach (var block in function.Blocks) {
        made += FoldSwaps(block, census);
        made += FoldBitTests(block, census);
        made += FoldReadModifyWrites(block, census);
        made += FoldMemorySources(block, census);
        made += FoldCopyChains(block, census);
      }
      total += made;
      if (made == 0)
        break;
    }
    // Neither of these removes a VALUE, so neither can expose a pattern for the rewrites above and
    // neither belongs in their fixpoint. Straightening goes first because it deletes instructions the
    // zero idiom's flag question then does not have to look past.
    total += FoldConstantBranches(function);
    total += MachineBranchCleanup.StraightenBranches(function);
    foreach (var block in function.Blocks)
      total += FoldZeroConstants(function, block);
    return total;
  }

  /// <summary>
  /// How many times each virtual register is defined and read over the WHOLE function, which is what
  /// makes "nobody else can see this value" a fact rather than a block-local guess. A memory operand's
  /// base, index and segment count as reads, exactly as <see cref="LivenessAnalysis.RegistersOf"/>
  /// counts them.
  /// </summary>
  private sealed record Census(Dictionary<int, int> Defs, Dictionary<int, int> Uses) {

    public static Census Of(X86MachineFunction function) {
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

  private static bool IsMemory(MOperand operand) => operand.IsMemoryAccess();

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
      // the one-operand multiply and divide read their operand from anywhere: DX:AX = AX * [n]
      if (user.Opcode is MOpcode.Imul or MOpcode.Idiv && user.Operands is [MOperand.Register { Reg: var factor }]
          && factor.Equals(value)) {
        block.Instructions[consumer] = new MInstr(user.Opcode, [cell],
          new MInstrEffect(WrittenRegs: [], ReadRegs: [],
            ReadsFlags: user.Effect.ReadsFlags, WritesFlags: user.Effect.WritesFlags,
            ReadsMemory: true, WritesMemory: user.Effect.WritesMemory),
          user.Condition, user.Clobbers);
        block.Instructions.RemoveAt(i);
        --i;
        ++made;
        continue;
      }
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
  /// <c>MOV v,src / MOV w,v</c> - a value staged into a register only to be copied straight on - is
  /// <c>MOV w,src</c>. The source may be an immediate, which depends on nothing at all, a register,
  /// which the barrier scan requires nobody to write in between, or a memory cell, which it requires
  /// nobody to write either - and whose address registers, as in <see cref="FoldMemorySources"/>, are
  /// only trusted across an adjacent pair.
  ///
  /// <para>
  /// Memory used to be excluded on the grounds that <see cref="FoldMemorySources"/> already buys what
  /// matters. It does not when the copy's target is a PINNED register: <c>x% * z%</c> loaded
  /// <c>x%</c> into a virtual and then copied it into AX for the IMUL, where one load into AX does both.
  /// </para>
  /// </summary>
  private static int FoldCopyChains(MBlock block, Census census) {
    var made = 0;
    for (var i = 0; i < block.Instructions.Count; ++i) {
      var stage = block.Instructions[i];
      if (stage.Opcode != MOpcode.Mov || stage.Condition is not null || stage.Clobbers.Count > 0
          || stage.Operands is not [MOperand.Register { Reg: { IsVirtual: true } value }, var source])
        continue;
      if (source is not (MOperand.Immediate or MOperand.Register) && !IsMemory(source))
        continue;
      if (source is MOperand.Register { Reg: var from }
          && (from.Equals(value) || from.Size != value.Size))
        continue;                                // MOV v,v is no chain, and a resize is not a copy
      if (!census.Exactly(value, definitions: 1, readers: 1))
        continue;

      var address = source is MOperand.Register register ? new List<MReg> { register.Reg } : AddressRegisters(source);
      if (address.Contains(value))
        continue;                                // the load's own address: not a value being staged
      var consumer = FindSingleReader(block, i + 1, value, address);
      if (consumer < 0 || (IsMemory(source) && address.Count > 0 && consumer != i + 1))
        continue;
      var user = block.Instructions[consumer];
      if (user.Opcode != MOpcode.Mov || user.Condition is not null
          || user.Operands is not [MOperand.Register { Reg: var target }, MOperand.Register { Reg: var read }]
          || !read.Equals(value) || target.Size != value.Size)
        continue;
      // MOV w,v where w IS the source register is a copy back to where the value came from: the
      // register already holds it, so both instructions go and nothing takes their place.
      var identity = source is MOperand.Register { Reg: var origin } && origin.Equals(target);
      // ...but only when the register is VIRTUAL. "Nothing takes their place" is the whole claim, and
      // for a physical register nothing is exactly what says it is occupied. A virtual keeps its
      // identity - the allocator gives it an interval spanning every mention, and a value it still
      // reads later still interferes. A physical register has no id and no interval: it is protected
      // only over the window between the instruction that fills it and the instruction that reads it
      // out (LinearScanAllocator.InFlightByIndex), and deleting both ends of the copy deletes the
      // window. `PassW% = a \ 2` is the shape: rt_ldiv answers in DX:AX, the pair is copied out and
      // the low half copied back into AX for the RET, and folding those two away left AX mentioned
      // NOWHERE between the call and the return - so the allocator handed AX to the (dead) high half
      // and `MOV AX, DX` overwrote the result on its way out. The function returned 0 for every input.
      if (identity && (!target.IsVirtual || user.Clobbers.Count > 0))
        continue;                                // ...nor when deleting the copy deletes the window it opens

      if (identity)
        block.Instructions.RemoveAt(consumer);
      else
        block.Instructions[consumer] = new MInstr(MOpcode.Mov, [user.Operands[0], source],
          new MInstrEffect(WrittenRegs: [0], ReadRegs: source is MOperand.Register ? [1] : [],
            ReadsFlags: false, WritesFlags: false, ReadsMemory: IsMemory(source), WritesMemory: false),
          condition: null, user.Clobbers);           // a pinned copy keeps the window it opens
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
  /// <remarks>
  /// A CLOBBER only disturbs an operand that names a clobbered PHYSICAL register. Moving the use of a
  /// virtual later lengthens its live range across the clobber, and keeping a live value out of a
  /// clobbered register is exactly what the allocator does with clobbers - so it is pressure, never a
  /// wrong answer. Treating every clobber as a wall kept a load from folding into the IMUL whose
  /// pinned accumulator load sits between them.
  /// </remarks>
  private static bool Disturbs(MInstr instr, IReadOnlyCollection<MReg> address) {
    if (instr.Effect.WritesMemory)
      return true;
    if (instr.Opcode is MOpcode.Call or MOpcode.CallFar or MOpcode.InlineAsm || instr.IsTerminator)
      return true;
    if (address.Any(register => !register.IsVirtual && instr.Clobbers.Contains(register.Physical)))
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
  /// <c>MOV a,[left] / MOV b,[right] / MOV [left],b / MOV [right],a</c> becomes
  /// <c>MOV a,[left] / XCHG a,[right] / MOV [left],a</c>. Both temporaries must belong only to the
  /// exchange, and register-formed addresses may not depend on either value being replaced.
  /// </summary>
  private static int FoldSwaps(MBlock block, Census census) {
    var made = 0;
    for (var i = 0; i + 3 < block.Instructions.Count; ++i) {
      var loadLeft = block.Instructions[i];
      var loadRight = block.Instructions[i + 1];
      var storeLeft = block.Instructions[i + 2];
      var storeRight = block.Instructions[i + 3];
      if (loadLeft.Opcode != MOpcode.Mov || loadLeft.Operands is not
          [MOperand.Register { Reg: { IsVirtual: true } leftValue }, var leftCell]
          || !IsMemory(leftCell)
          || loadRight.Opcode != MOpcode.Mov || loadRight.Operands is not
          [MOperand.Register { Reg: { IsVirtual: true } rightValue }, var rightCell]
          || !IsMemory(rightCell) || leftCell.Equals(rightCell))
        continue;
      if (storeLeft.Opcode != MOpcode.Mov || storeLeft.Operands is not
          [var writtenLeft, MOperand.Register { Reg: var fromRight }]
          || !writtenLeft.Equals(leftCell) || !fromRight.Equals(rightValue)
          || storeRight.Opcode != MOpcode.Mov || storeRight.Operands is not
          [var writtenRight, MOperand.Register { Reg: var fromLeft }]
          || !writtenRight.Equals(rightCell) || !fromLeft.Equals(leftValue))
        continue;
      if (!census.Exactly(leftValue, definitions: 1, readers: 1)
          || !census.Exactly(rightValue, definitions: 1, readers: 1))
        continue;
      var addresses = AddressRegisters(leftCell).Concat(AddressRegisters(rightCell)).ToHashSet();
      if (addresses.Contains(leftValue) || addresses.Contains(rightValue))
        continue;

      var held = new MOperand.Register(leftValue);
      block.Instructions[i + 1] = new MInstr(MOpcode.Xchg, [held, rightCell],
        new MInstrEffect(WrittenRegs: [0], ReadRegs: [0], ReadsFlags: false, WritesFlags: false,
          ReadsMemory: true, WritesMemory: true));
      block.Instructions[i + 2] = new MInstr(MOpcode.Mov, [leftCell, held],
        new MInstrEffect(WrittenRegs: [], ReadRegs: [1], ReadsFlags: false, WritesFlags: false,
          ReadsMemory: false, WritesMemory: true));
      block.Instructions.RemoveAt(i + 3);
      ++made;
    }
    return made;
  }

  /// <summary>
  /// <c>MOV v,[a] / OP v,src / MOV [a],v</c> - a cell read, changed and written back with nothing else
  /// looking at the intermediate - becomes <c>OP [a],src</c>, and the <c>+/-1</c> cases become
  /// <c>INC</c>/<c>DEC</c> where the carry they leave alone is dead.
  ///
  /// <para>
  /// The selector usually writes it STAGED - <c>MOV t,[a] / MOV v,t / OP v,src / MOV [a],v</c> - because
  /// a two-address ADD overwrites the register it reads, so the loaded value is copied first. The copy
  /// is only removed later, by the allocator's coalescer, long after this pass has looked; matching
  /// only the unstaged shape meant <c>g% = g% + 6</c> on a SHARED variable never folded. A staging copy
  /// whose source has no other reader is part of the pattern.
  /// </para>
  /// </summary>
  private static int FoldReadModifyWrites(MBlock block, Census census) {
    var made = 0;
    for (var i = 0; i + 2 < block.Instructions.Count; ++i) {
      var load = block.Instructions[i];
      if (load.Opcode != MOpcode.Mov || load.Operands.Count != 2
          || load.Operands[0] is not MOperand.Register { Reg: { IsVirtual: true } loaded }
          || !IsMemory(load.Operands[1]))
        continue;
      var cell = load.Operands[1];
      var staged = IsStagingCopy(block.Instructions[i + 1], loaded, census, out var value);
      if (!staged)
        value = loaded;
      var modifyAt = staged ? i + 2 : i + 1;
      if (modifyAt + 1 >= block.Instructions.Count)
        continue;
      var modify = block.Instructions[modifyAt];
      var store = block.Instructions[modifyAt + 1];
      if (AddressRegisters(cell).Contains(value) || AddressRegisters(cell).Contains(loaded))
        continue;
      if (!FoldsMemoryDestination(modify.Opcode) || modify.Operands.Count != 2
          || modify.Operands[0] is not MOperand.Register first || !first.Reg.Equals(value)
          || modify.Operands[1] is not (MOperand.Immediate or MOperand.Register))
        continue;
      if (modify.Operands[1] is MOperand.Register source && (source.Reg.Equals(value) || source.Reg.Equals(loaded)))
        continue;                                // OP v,v is not a read-modify-write of the cell
      if (store.Opcode != MOpcode.Mov || store.Operands.Count != 2
          || !store.Operands[0].Equals(cell)
          || store.Operands[1] is not MOperand.Register written || !written.Reg.Equals(value))
        continue;
      // the intermediate is written by the load (or the staging copy) and the modify, and read by the
      // modify and the store: any other mention means somebody else can see it
      if (!census.Exactly(value, definitions: 2, readers: 2))
        continue;

      var rhs = modify.Operands[1];
      block.Instructions[i] = UnitStep(modify.Opcode, rhs) is { } step && MachineFlags.DeadAfter(block, modifyAt + 1)
        ? new MInstr(step, [cell],
          new MInstrEffect(WrittenRegs: [], ReadRegs: [], ReadsFlags: false, WritesFlags: true,
            ReadsMemory: true, WritesMemory: true))
        : new MInstr(modify.Opcode, [cell, rhs],
          new MInstrEffect(WrittenRegs: [], ReadRegs: rhs is MOperand.Register ? [1] : [],
            ReadsFlags: modify.Effect.ReadsFlags, WritesFlags: true,
            ReadsMemory: true, WritesMemory: true));
      block.Instructions.RemoveRange(i + 1, modifyAt + 1 - i);
      ++made;
    }
    return made;
  }

  /// <summary>
  /// <c>MOV v,t</c> where <c>t</c> is the value just loaded and read nowhere else - the two-address
  /// staging copy, which <see cref="FoldReadModifyWrites"/> treats as part of its pattern.
  /// </summary>
  private static bool IsStagingCopy(MInstr copy, MReg loaded, Census census, out MReg value) {
    value = default;
    if (copy.Opcode != MOpcode.Mov || copy.Condition is not null || copy.Clobbers.Count != 0
        || copy.Operands is not [MOperand.Register { Reg: { IsVirtual: true } destination }, MOperand.Register { Reg: var source }]
        || !source.Equals(loaded) || destination.Size != loaded.Size
        || !census.Exactly(loaded, definitions: 1, readers: 1))
      return false;
    value = destination;
    return true;
  }

  /// <summary>INC or DEC for an add or subtract of one, in either sign; null for anything else.</summary>
  private static MOpcode? UnitStep(MOpcode opcode, MOperand amount) => (opcode, amount) switch {
    (MOpcode.Add, MOperand.Immediate { Value: 1 }) or (MOpcode.Sub, MOperand.Immediate { Value: -1 }) => MOpcode.Inc,
    (MOpcode.Sub, MOperand.Immediate { Value: 1 }) or (MOpcode.Add, MOperand.Immediate { Value: -1 }) => MOpcode.Dec,
    _ => null,
  };

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


  /// <summary>
  /// <c>MOV v,a / ... / CMP v,b / Jcc L</c> with no other write to <c>v</c> in between: the branch is
  /// decided before the program runs, so it becomes <c>JMP L</c> or disappears, and the compare goes
  /// with it once nothing reads its flags.
  ///
  /// <para>
  /// Loop rotation is what makes these. It copies a counted loop's header test into the block in front
  /// of the loop, where the counter has just been given its first value - <c>FOR i% = 1 TO 10</c>
  /// left <c>MOV AX,1 / CMP AX,10 / JLE body</c> at the top of every rotated loop, a test of two
  /// constants. Only a write in the SAME block counts: a value arriving from a predecessor is not
  /// known here, and nothing is guessed.
  /// </para>
  /// </summary>
  private static int FoldConstantBranches(X86MachineFunction function) {
    var made = 0;
    foreach (var block in function.Blocks)
      for (var j = 1; j < block.Instructions.Count; ++j) {
        if (block.Instructions[j] is not { Opcode: MOpcode.Jcc, Condition: { } condition } branch
            || branch.Operands is not [MOperand.LabelRef target]
            || block.Instructions[j - 1] is not { Opcode: MOpcode.Cmp, Condition: null } compare
            || compare.Operands is not [MOperand.Register { Reg: { IsVirtual: true } subject }, MOperand.Immediate { Value: var right }]
            || ConstantBefore(function, block, j - 1, subject) is not { } left
            || MachineFlags.CompareHolds(condition, left, right, BitsOf(subject.Size)) is not { } taken)
          continue;

        if (taken) {
          block.Instructions[j] = new MInstr(MOpcode.Jmp, [target], MInstrEffect.None, condition: null, branch.Clobbers);
          block.Instructions.RemoveRange(j + 1, block.Instructions.Count - j - 1);
          block.Successors.Clear();
          block.Successors.Add(target.Name);
        } else {
          block.Instructions.RemoveAt(j);
          if (!block.Instructions.Any(i => i.Operands is [MOperand.LabelRef { Name: var name }] && name == target.Name))
            block.Successors.Remove(target.Name);
        }
        if (MachineFlags.DeadAfter(function, block, j - 1)) {
          block.Instructions.RemoveAt(j - 1);
          --j;
        }
        ++made;
      }
    return made;
  }

  /// <summary>
  /// The immediate <paramref name="register"/> was last set to before <paramref name="index"/>, if that
  /// is what it was. The search runs back through the block and then into its predecessor while there is
  /// exactly ONE - a block with two ways in can receive two different values.
  /// </summary>
  private static long? ConstantBefore(X86MachineFunction function, MBlock block, int index, MReg register) {
    var visited = new HashSet<string>(StringComparer.Ordinal);
    for (var from = index; visited.Add(block.Label);) {
      for (var k = from - 1; k >= 0; --k) {
        var instr = block.Instructions[k];
        if (!LivenessAnalysis.RegistersOf(instr).Writes.Contains(register.VirtualId))
          continue;
        return instr is { Opcode: MOpcode.Mov, Condition: null }
            && instr.Operands is [MOperand.Register { Reg: var written }, MOperand.Immediate { Value: var value }]
            && written.Equals(register)
          ? value
          : null;
      }
      var predecessors = function.Blocks.Where(candidate => candidate.Successors.Contains(block.Label)).ToList();
      if (predecessors.Count != 1 || ReferenceEquals(block, function.Blocks[0]))
        return null;
      (block, from) = (predecessors[0], predecessors[0].Instructions.Count);
    }
    return null;
  }

  private static int BitsOf(MRegSize size) => size switch {
    MRegSize.Byte => 8,
    MRegSize.Dword => 32,
    MRegSize.Qword => 64,
    _ => 16,
  };

  /// <summary>
  /// <c>MOV r,0</c> is <c>XOR r,r</c> - a byte shorter on a word register and three on a dword one,
  /// and the same value either way. It is taken only where the flags <c>XOR</c> dirties are provably
  /// dead, and only on a word or larger: the byte forms are both two bytes, so the rewrite would be
  /// churn.
  ///
  /// <para>
  /// The rewritten instruction names the register twice but declares no READ of it, which is the
  /// truth - <c>XOR r,r</c> depends on nothing - and is what keeps the value's live range starting
  /// here rather than reaching back for a definition that does not exist. Naming it twice does make
  /// <see cref="Spiller"/> decline the value, and that is the right answer too: there is no
  /// memory-to-memory <c>XOR</c>, so a spilled one would have to become the <c>MOV</c> again.
  /// </para>
  /// </summary>
  private static int FoldZeroConstants(X86MachineFunction function, MBlock block) {
    var made = 0;
    for (var i = 0; i < block.Instructions.Count; ++i) {
      var instr = block.Instructions[i];
      if (instr.Opcode != MOpcode.Mov || instr.Condition is not null
          || instr.Operands is not [MOperand.Register zero, MOperand.Immediate { Value: 0 }]
          || zero.Reg.Size is not (MRegSize.Word or MRegSize.Dword)
          || !MachineFlags.DeadAfter(function, block, i))
        continue;
      block.Instructions[i] = new MInstr(MOpcode.Xor, [zero, zero],
        new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: true,
          ReadsMemory: false, WritesMemory: false), condition: null, instr.Clobbers);
      ++made;
    }
    return made;
  }
}
