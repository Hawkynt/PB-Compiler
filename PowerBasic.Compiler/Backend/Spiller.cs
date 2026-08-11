namespace PowerBasic.Compiler.Backend;

/// <summary>
/// The x86-16 back end's spilling (docs/X86-BACKEND.md): moving a value out of the register file and
/// into the frame when linear scan cannot place it - which on this target is usually not because six
/// registers ran out, but because a <c>CALL</c> destroys the whole caller-saved file and a value live
/// across it may not sit in any of them.
///
/// x86 is a memory-operand machine, so the cheapest spill needs no reload code: a spilled value
/// simply <b>is</b> its frame cell, and every legal instruction that touched the register names that
/// cell. Two kinds of cell exist, and the cheaper one is tried first:
///
/// - an incoming <b>parameter</b> is already in the frame where the caller pushed it, and an IR
///   argument is an SSA value nothing writes - so it costs literally nothing: the prologue copy is
///   dropped and the uses address <c>[BP+6]</c> directly;
/// - any other value gets a fresh stack slot, and its defining instruction writes there instead of
///   into a register.
///
/// Values that cannot become a memory operand are shortened instead: stable addresses are
/// rematerialized at each use, pointer parameters reload from their incoming cell, and an ordinary
/// value can be split through an explicit store/reload spill slot.
/// </summary>
internal static class Spiller {

  // where the emitter can take a memory operand: the Emit2 family accepts reg,mem / mem,reg / mem,imm
  private static readonly HashSet<MOpcode> _memoryCapable = [
    MOpcode.Mov, MOpcode.Add, MOpcode.Sub, MOpcode.And, MOpcode.Or, MOpcode.Xor,
    MOpcode.Adc, MOpcode.Sbb, MOpcode.Cmp,
  ];

  /// <summary>
  /// Shortens one value's live range by RECOMPUTING it at each use instead of keeping it in a
  /// register, and returns whether it moved anything.
  ///
  /// This is the answer for the case spilling cannot touch. A frame address - the <c>LEA</c> that puts
  /// an array's base where a GEP can index from it - is used as a memory BASE, and a base has to be in
  /// a register, so the spiller refuses it. If a call or an inline-asm block sits between the LEA and
  /// the use, the value is live across a clobber of the whole register file with nowhere to go, and
  /// the function selects but never routes.
  ///
  /// Recomputing is free of that problem because the LEA depends on nothing but BP: putting a fresh
  /// copy immediately before each use makes every live range one instruction long, so no clobber can
  /// fall inside one. A definition already adjacent to all of its uses is left alone because moving
  /// it again cannot shorten anything.
  ///
  /// <para>
  /// A <c>MOV reg, immediate</c> qualifies for the same reason and even more plainly - it depends on
  /// nothing whatever. It matters because the SCHEDULER runs first and is free to hoist every such
  /// move to the head of its block, all of them being ready at once: sixteen stores through a far
  /// pointer supply sixteen independent constant loads, and a block that needs at most two registers
  /// at a time then wants sixteen. Recomputing puts each back beside the one instruction that reads it.
  /// </para>
  /// </summary>
  internal static bool RematerializeOne(MFunction function) {
    if (TryReloadAddressArgument(function))
      return true;

    foreach (var block in function.Blocks)
      foreach (var instr in block.Instructions.ToList()) {
        if (instr.Operands is not [MOperand.Register { Reg: { IsVirtual: true } target }, { } source]
            || !IsRecomputable(instr.Opcode, source))
          continue;
        if (DefinitionCount(function, target.VirtualId) != 1 || UseCount(function, target.VirtualId) == 0)
          continue;
        if (AlreadyBesideItsUses(function, instr, target.VirtualId))
          continue;                              // nothing to gain, and re-doing it would never settle
        Rematerialize(function, instr, target.VirtualId);
        return true;
      }
    return false;
  }

  /// <summary>
  /// Whether a two-operand definition can simply be written again wherever its value is wanted: an
  /// address form, which recomputes from the frame, or a constant, which depends on nothing at all.
  /// </summary>
  private static bool IsRecomputable(MOpcode opcode, MOperand source) => opcode switch {
    MOpcode.Lea => source is MOperand.StackSlot or MOperand.DataOffset or MOperand.Memory,
    MOpcode.Mov => source is MOperand.Immediate,
    _ => false,
  };

  /// <summary>
  /// Shortens a read-only pointer parameter to one tiny live range per dereference. The pointer's
  /// original value remains in the caller-owned parameter cell for the whole call, so each use can
  /// reload it immediately before the instruction that needs an address register. This is the
  /// address equivalent of spilling an ordinary parameter directly to <see cref="MOperand.ParamCell"/>;
  /// the extra move exists only because an x86-16 memory base itself cannot be memory.
  /// </summary>
  private static bool TryReloadAddressArgument(MFunction function) {
    foreach (var load in function.ArgumentLoads.ToList()) {
      if (!IsAddressOnly(function, load.VirtualId))
        continue;

      foreach (var block in function.Blocks)
        for (var i = block.Instructions.Count - 1; i >= 0; --i) {
          var instruction = block.Instructions[i];
          if (!UsesAsAddress(instruction, load.VirtualId))
            continue;

          var fresh = MReg.Virtual(function.VirtualRegisterCount++, MRegSize.Word);
          var operands = instruction.Operands.Select(operand => operand is MOperand.Memory memory
            ? memory with {
              Base = Replace(memory.Base, load.VirtualId, fresh),
              Index = Replace(memory.Index, load.VirtualId, fresh),
              Segment = Replace(memory.Segment, load.VirtualId, fresh),
            }
            : operand).ToArray();
          block.Instructions[i] = new MInstr(instruction.Opcode, operands, instruction.Effect,
            instruction.Condition, instruction.Clobbers);
          block.Instructions.Insert(i, new MInstr(MOpcode.Mov, [new MOperand.Register(fresh),
              new MOperand.ParamCell(load.ArgumentIndex, load.ByteDelta, MRegSize.Word)],
            new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false,
              ReadsMemory: true, WritesMemory: false),
            condition: null, clobbers: WithPendingStaging(block, i, [])));
        }

      function.ArgumentLoads.Remove(load);
      return true;
    }
    return false;
  }

  /// <summary>True when every machine use is as an address base/index and nothing redefines it.</summary>
  private static bool IsAddressOnly(MFunction function, int virtualId) {
    var found = false;
    foreach (var instruction in function.AllInstructions) {
      var registers = LivenessAnalysis.RegistersOf(instruction);
      if (registers.Writes.Contains(virtualId))
        return false;
      foreach (var operand in instruction.Operands)
        switch (operand) {
          case MOperand.Register { Reg: { IsVirtual: true } register } when register.VirtualId == virtualId:
            return false;
          case MOperand.Memory memory when Is(memory.Base, virtualId) || Is(memory.Index, virtualId)
              || Is(memory.Segment, virtualId):
            found = true;
            break;
        }
    }
    return found;
  }

  private static bool UsesAsAddress(MInstr instruction, int virtualId)
    => instruction.Operands.OfType<MOperand.Memory>()
      .Any(memory => Is(memory.Base, virtualId) || Is(memory.Index, virtualId) || Is(memory.Segment, virtualId));

  private static bool Is(MReg? register, int virtualId)
    => register is { IsVirtual: true } value && value.VirtualId == virtualId;

  private static MReg? Replace(MReg? register, int virtualId, MReg replacement)
    => Is(register, virtualId) ? replacement : register;

  /// <summary>
  /// Whether every use is already immediately preceded by its definition. Fresh-id rematerialization
  /// leaves exactly this shape; admitting it again would replace one adjacent LEA with another forever.
  /// </summary>
  private static bool AlreadyBesideItsUses(MFunction function, MInstr definition, int virtualId) {
    foreach (var block in function.Blocks)
      for (var i = 0; i < block.Instructions.Count; ++i) {
        var instruction = block.Instructions[i];
        if (ReferenceEquals(instruction, definition) || !Mentions(instruction, virtualId))
          continue;
        if (LivenessAnalysis.RegistersOf(instruction).Writes.Contains(virtualId))
          continue;
        if (i == 0 || !ReferenceEquals(block.Instructions[i - 1], definition))
          return false;
      }
    return true;
  }

  private static int DefinitionCount(MFunction function, int virtualId)
    => function.AllInstructions.Count(i => LivenessAnalysis.RegistersOf(i).Writes.Contains(virtualId));

  private static int UseCount(MFunction function, int virtualId)
    => function.AllInstructions.Count(i => !LivenessAnalysis.RegistersOf(i).Writes.Contains(virtualId)
                                           && Mentions(i, virtualId));

  /// <summary>
  /// The physical registers a pending call's argument staging has already filled at this point in the
  /// block - empty everywhere except between a call's first staging move and the <c>CALL</c> itself.
  ///
  /// <para>
  /// Anything INSERTED there has to claim them. The selector attaches this prefix to each staging move
  /// so that a value live ACROSS the sequence avoids the registers already loaded (see
  /// <c>InstructionSelector.StagingDestinations</c>) - but a value defined and consumed entirely
  /// INSIDE the sequence is live across none of those moves, so nothing spoke for it. The spiller puts
  /// values exactly there: rematerializing a frame address places its <c>LEA</c> immediately before the
  /// use, and a use is a staging move.
  /// </para>
  /// <para>
  /// This is how <c>pts(2) = pts(1)</c> lost its record. The memcpy's staging is
  /// <c>MOV DI,dest / MOV BX,SS / MOV SI,src / MOV DX,SS</c>; rematerializing the source address put
  /// <c>LEA v,[BP-30]</c> between the second and third, its one-instruction interval saw no clobber,
  /// and BX - holding the destination SEGMENT - was the first register free. rt_memcpy then wrote ten
  /// bytes into a segment made out of a frame offset, and the read-back printed the zeroes the frame
  /// prologue had left. Every instruction was defensible on its own.
  /// </para>
  /// </summary>
  private static IReadOnlyList<Asm.Reg> StagingFilledAt(MBlock block, int index) {
    var filled = new List<Asm.Reg>();
    for (var j = index - 1; j >= 0; --j) {
      var instruction = block.Instructions[j];
      if (instruction.Opcode == MOpcode.Call)
        break;                                   // past the previous call: nothing is staged yet
      foreach (var register in instruction.Clobbers)
        if (!filled.Contains(register))
          filled.Add(register);
    }
    return filled;
  }

  /// <summary>The instruction's own clobbers plus whatever staging is pending where it is being placed.</summary>
  private static IReadOnlyList<Asm.Reg> WithPendingStaging(MBlock block, int index, IReadOnlyList<Asm.Reg> own) {
    var pending = StagingFilledAt(block, index);
    if (pending.Count == 0)
      return own;
    var all = new List<Asm.Reg>(own);
    foreach (var register in pending)
      if (!all.Contains(register))
        all.Add(register);
    return all;
  }

  /// <summary>Whether the instruction names the value anywhere - as an operand or inside a memory address.</summary>
  private static bool Mentions(MInstr instr, int virtualId) {
    foreach (var operand in instr.Operands)
      switch (operand) {
        case MOperand.Register { Reg: { IsVirtual: true } r } when r.VirtualId == virtualId:
        case MOperand.Memory { Base: { IsVirtual: true } b } when b.VirtualId == virtualId:
        case MOperand.Memory { Index: { IsVirtual: true } x } when x.VirtualId == virtualId:
        case MOperand.Memory { Segment: { IsVirtual: true } g } when g.VirtualId == virtualId:
          return true;
      }
    return false;
  }

  /// <summary>
  /// Rebuilds the definition into a fresh virtual register immediately before every use, then drops
  /// the original. A fresh id is essential: liveness has one interval per id, so copying the same
  /// definition with the same destination would still leave one interval spanning all calls.
  /// </summary>
  private static void Rematerialize(MFunction function, MInstr definition, int virtualId) {
    var target = ((MOperand.Register)definition.Operands[0]).Reg;
    foreach (var block in function.Blocks)
      for (var i = block.Instructions.Count - 1; i >= 0; --i) {
        var instr = block.Instructions[i];
        if (ReferenceEquals(instr, definition) || !Mentions(instr, virtualId))
          continue;
        if (LivenessAnalysis.RegistersOf(instr).Writes.Contains(virtualId))
          continue;

        var fresh = MReg.Virtual(function.VirtualRegisterCount++, target.Size);
        block.Instructions[i] = ReplaceMentions(instr, virtualId, fresh);
        var operands = definition.Operands.ToArray();
        operands[0] = new MOperand.Register(fresh);
        block.Instructions.Insert(i, new MInstr(definition.Opcode, operands, definition.Effect,
          definition.Condition, WithPendingStaging(block, i, definition.Clobbers)));
      }

    foreach (var block in function.Blocks)
      for (var i = 0; i < block.Instructions.Count; ++i)
        if (ReferenceEquals(block.Instructions[i], definition)) {
          block.Instructions.RemoveAt(i);          // the ORIGINAL, ahead of every copy inserted above
          return;
        }
  }

  /// <summary>Replaces one virtual value everywhere an instruction names it, including addresses.</summary>
  private static MInstr ReplaceMentions(MInstr instruction, int virtualId, MReg replacement) {
    var operands = instruction.Operands.Select(operand => operand switch {
      MOperand.Register { Reg: { IsVirtual: true } register } when register.VirtualId == virtualId
        => (MOperand)new MOperand.Register(replacement),
      MOperand.Memory memory => memory with {
        Base = Replace(memory.Base, virtualId, replacement),
        Index = Replace(memory.Index, virtualId, replacement),
        Segment = Replace(memory.Segment, virtualId, replacement),
      },
      _ => operand,
    }).ToArray();
    return new MInstr(instruction.Opcode, operands, instruction.Effect, instruction.Condition,
      instruction.Clobbers);
  }

  /// <summary>
  /// Shortens one value's live range when it cannot be rewritten as a memory operand directly. Every
  /// definition writes the value to one frame cell; every use reloads it into its own fresh,
  /// instruction-local virtual register. Multiple definitions occur after phi elimination, where each
  /// predecessor copies its incoming value into the same virtual destination. This also handles shapes
  /// such as <c>MOV value,[array-element]</c>: direct spilling would require an illegal memory-to-memory
  /// MOV, while the explicit store/reloads are ordinary register-memory instructions.
  /// </summary>
  internal static bool SplitOne(MFunction function) {
    var clobbers = GetClobberIndices(function);
    var intervals = LivenessAnalysis.Compute(function);

    // Values live across a full-register clobber first. That is the pressure this target creates all
    // by itself - one CALL destroys every allocatable register - and splitting one is self-limiting,
    // because the fresh ranges no longer cross the call.
    if (TrySplitLongest(function,
          intervals.Where(interval => clobbers.Any(at => interval.Start < at && at < interval.End))))
      return true;

    // Then plain pressure: no call anywhere near, simply more values wanted at once than there are
    // registers. Four LONG accumulators are eight words on a six-register machine, and none of them
    // can move to memory as it stands - a value loaded out of an array has a memory operand in its own
    // defining instruction, so making it one too would be a memory-to-memory MOV. An explicit
    // store/reload pair is two ordinary register-memory instructions and says the same thing.
    return TrySplitLongest(function,
      intervals.Where(interval => !function.SplitValues.Contains(interval.VirtualId)));
  }

  /// <summary>Splits the longest of the offered live ranges that can be split, if any can.</summary>
  private static bool TrySplitLongest(MFunction function,
      IEnumerable<LivenessAnalysis.LiveInterval> offered) {
    var candidates = offered
      .OrderByDescending(interval => interval.End - interval.Start)
      .ThenBy(interval => interval.VirtualId);

    foreach (var interval in candidates) {
      var argumentAt = function.ArgumentLoads.FindIndex(load => load.VirtualId == interval.VirtualId);
      if (argumentAt >= 0) {
        if (TrySplitArgument(function, interval.VirtualId, function.ArgumentLoads[argumentAt]))
          return true;
        continue;
      }
      if (!TryFindDefinitions(function, interval.VirtualId, out var definitions, out var target))
        continue;
      var definitionSet = new HashSet<MInstr>(definitions);

      var slot = function.StackSlots.Count;
      function.StackSlots.Add(StorageBytes(target.Size));
      var cell = new MOperand.StackSlot(slot, target.Size);

      foreach (var block in function.Blocks)
        for (var i = block.Instructions.Count - 1; i >= 0; --i) {
          var instruction = block.Instructions[i];
          if (definitionSet.Contains(instruction) || !Mentions(instruction, interval.VirtualId))
            continue;
          if (LivenessAnalysis.RegistersOf(instruction).Writes.Contains(interval.VirtualId))
            continue;

          var fresh = MReg.Virtual(function.VirtualRegisterCount++, target.Size);
          function.SplitValues.Add(fresh.VirtualId);
          block.Instructions[i] = ReplaceMentions(instruction, interval.VirtualId, fresh);
          block.Instructions.Insert(i, new MInstr(MOpcode.Mov,
            [new MOperand.Register(fresh), cell],
            new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false,
              ReadsMemory: true, WritesMemory: false),
            condition: null, clobbers: WithPendingStaging(block, i, [])));
        }

      var stored = 0;
      foreach (var block in function.Blocks)
        for (var i = block.Instructions.Count - 1; i >= 0; --i) {
          var instruction = block.Instructions[i];
          if (!definitionSet.Contains(instruction))
            continue;
          var readsOldValue = LivenessAnalysis.RegistersOf(instruction).Reads.Contains(interval.VirtualId);
          var fresh = MReg.Virtual(function.VirtualRegisterCount++, target.Size);
          function.SplitValues.Add(fresh.VirtualId);
          block.Instructions[i] = ReplaceMentions(instruction, interval.VirtualId, fresh);
          block.Instructions.Insert(i + 1, new MInstr(MOpcode.Mov,
            [cell, new MOperand.Register(fresh)],
            new MInstrEffect(WrittenRegs: [], ReadRegs: [1], ReadsFlags: false, WritesFlags: false,
              ReadsMemory: false, WritesMemory: true)));
          if (readsOldValue)
            block.Instructions.Insert(i, new MInstr(MOpcode.Mov,
              [new MOperand.Register(fresh), cell],
              new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false,
                ReadsMemory: true, WritesMemory: false),
              condition: null, clobbers: WithPendingStaging(block, i, [])));
          ++stored;
        }
      if (stored != definitions.Count)
        throw new InvalidOperationException("spill definition disappeared while splitting its live range");
      return true;
    }
    return false;
  }

  /// <summary>
  /// Reloads an immutable argument from its caller-owned cell immediately before every use. Direct
  /// parameter spilling cannot handle a use that already has a memory operand, but an explicit reload
  /// keeps the eventual instruction register-memory and gives every use its own short live range.
  /// </summary>
  private static bool TrySplitArgument(MFunction function, int virtualId,
      (int VirtualId, int ArgumentIndex, int ByteDelta) load) {
    if (function.AllInstructions.Any(instruction =>
          LivenessAnalysis.RegistersOf(instruction).Writes.Contains(virtualId)))
      return false;
    var size = FindVirtualSize(function, virtualId);
    if (size is null)
      return false;

    var found = false;
    foreach (var block in function.Blocks)
      for (var i = block.Instructions.Count - 1; i >= 0; --i) {
        var instruction = block.Instructions[i];
        if (!Mentions(instruction, virtualId))
          continue;
        var fresh = MReg.Virtual(function.VirtualRegisterCount++, size.Value);
        function.SplitValues.Add(fresh.VirtualId);
        block.Instructions[i] = ReplaceMentions(instruction, virtualId, fresh);
        block.Instructions.Insert(i, new MInstr(MOpcode.Mov,
          [new MOperand.Register(fresh), new MOperand.ParamCell(load.ArgumentIndex, load.ByteDelta, size.Value)],
          new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false,
            ReadsMemory: true, WritesMemory: false),
          condition: null, clobbers: WithPendingStaging(block, i, [])));
        found = true;
      }
    if (!found)
      return false;
    function.ArgumentLoads.Remove(load);
    return true;
  }

  private static MRegSize? FindVirtualSize(MFunction function, int virtualId) {
    MRegSize? result = null;
    foreach (var instruction in function.AllInstructions)
      foreach (var operand in instruction.Operands)
        switch (operand) {
          case MOperand.Register { Reg: { IsVirtual: true } register }
              when register.VirtualId == virtualId:
            result = Wider(result, register.Size);
            break;
          case MOperand.Memory memory when Is(memory.Base, virtualId):
            result = Wider(result, memory.Base!.Value.Size);
            break;
          case MOperand.Memory memory when Is(memory.Index, virtualId):
            result = Wider(result, memory.Index!.Value.Size);
            break;
          case MOperand.Memory memory when Is(memory.Segment, virtualId):
            result = Wider(result, memory.Segment!.Value.Size);
            break;
        }
    return result;
  }

  private static MRegSize Wider(MRegSize? left, MRegSize right)
    => left is null || StorageBytes(right) > StorageBytes(left.Value) ? right : left.Value;

  /// <summary>Global instruction indices carrying a physical-register clobber.</summary>
  private static List<int> GetClobberIndices(MFunction function) {
    var result = new List<int>();
    var index = 0;
    foreach (var instruction in function.AllInstructions) {
      if (instruction.Clobbers.Count > 0)
        result.Add(index);
      ++index;
    }
    return result;
  }

  /// <summary>Finds every compatible instruction defining a virtual value.</summary>
  private static bool TryFindDefinitions(MFunction function, int virtualId, out List<MInstr> definitions,
      out MReg target) {
    definitions = [];
    target = default;
    foreach (var instruction in function.AllInstructions) {
      var registers = LivenessAnalysis.RegistersOf(instruction);
      if (!registers.Writes.Contains(virtualId))
        continue;
      var written = WrittenVirtualRegister(instruction, virtualId);
      if (written is null || definitions.Count > 0 && written.Reg.Size != target.Size)
        return false;
      target = written.Reg;
      definitions.Add(instruction);
    }
    return definitions.Count > 0;
  }

  private static MOperand.Register? WrittenVirtualRegister(MInstr instruction, int virtualId)
    => instruction.Effect.WrittenRegs
      .Select(index => instruction.Operands[index])
      .OfType<MOperand.Register>()
      .FirstOrDefault(register => register.Reg.IsVirtual && register.Reg.VirtualId == virtualId);

  internal static bool SpillOne(MFunction function) {
    var length = new Dictionary<int, int>();
    foreach (var interval in LivenessAnalysis.Compute(function))
      length[interval.VirtualId] = interval.End - interval.Start;

    var arguments = function.ArgumentLoads.ToDictionary(a => a.VirtualId, a => a);
    var candidates = function.AllInstructions
      .SelectMany(i => i.Operands)
      .OfType<MOperand.Register>()
      .Where(r => r.Reg.IsVirtual)
      .Select(r => r.Reg.VirtualId)
      .Distinct()
      .OrderByDescending(v => arguments.ContainsKey(v))          // a parameter's cell costs nothing
      .ThenByDescending(v => length.GetValueOrDefault(v))
      .ThenBy(v => v);

    foreach (var virtualId in candidates) {
      if (!CanSpill(function, virtualId, arguments.ContainsKey(virtualId)))
        continue;
      if (arguments.TryGetValue(virtualId, out var load)) {
        var size = FindVirtualSize(function, virtualId) ?? MRegSize.Word;
        Rewrite(function, virtualId, new MOperand.ParamCell(load.ArgumentIndex, load.ByteDelta, size));
        function.ArgumentLoads.RemoveAll(a => a.VirtualId == virtualId);
      } else {
        var size = FindVirtualSize(function, virtualId) ?? MRegSize.Word;
        function.StackSlots.Add(StorageBytes(size));
        Rewrite(function, virtualId, new MOperand.StackSlot(function.StackSlots.Count - 1, size));
      }
      return true;
    }
    return false;
  }

  /// <summary>True when every reference to the value is one the emitter can satisfy from memory.</summary>
  private static bool CanSpill(MFunction function, int virtualId, bool isArgument) {
    foreach (var instr in function.AllInstructions) {
      // a value used as a memory base/index needs a real register wherever it appears
      foreach (var operand in instr.Operands)
        if (operand is MOperand.Memory mem
            && ((mem.Base is { IsVirtual: true } b && b.VirtualId == virtualId)
                || (mem.Index is { IsVirtual: true } x && x.VirtualId == virtualId)
                // the segment is moved into ES in front of the access, and MOV ES, r16 wants the
                // value in a register there and then - it cannot be reached from a frame cell
                || (mem.Segment is { IsVirtual: true } s && s.VirtualId == virtualId)))
          return false;

      var positions = Positions(instr, virtualId);
      if (positions.Count == 0)
        continue;
      if (positions.Count > 1)
        return false;                        // one instruction, one memory operand

      // a parameter cell is the CALLER's word - readable, never writable
      if (isArgument && LivenessAnalysis.RegistersOf(instr).Writes.Contains(virtualId))
        return false;

      var at = positions[0];
      var legal = instr.Opcode switch {
        var op when _memoryCapable.Contains(op) => true,
        MOpcode.Push => at == 0,
        MOpcode.Imul => at == 1,             // the destination of IMUL r16, r/m16 must be a register
        MOpcode.Idiv => at == 0,             // IDIV takes its divisor from memory as readily
        MOpcode.Shl or MOpcode.Shr or MOpcode.Sar => at == 0,   // shift a frame cell in place
        _ => false,
      };
      if (!legal || instr.Operands.Where((o, i) => i != at).Any(IsMemory))
        return false;
    }
    return true;
  }

  private static void Rewrite(MFunction function, int virtualId, MOperand cell) {
    foreach (var block in function.Blocks)
      for (var i = 0; i < block.Instructions.Count; ++i) {
        var instr = block.Instructions[i];
        var positions = Positions(instr, virtualId);
        if (positions.Count == 0)
          continue;
        var operands = instr.Operands.ToArray();
        var writes = LivenessAnalysis.RegistersOf(instr).Writes.Contains(virtualId);
        foreach (var at in positions) {
          var size = ((MOperand.Register)operands[at]).Reg.Size;
          operands[at] = WithSize(cell, size);
        }
        // the descriptor's read/write indices still describe the same operand positions; the operand
        // simply stops naming a register there, and the instruction now touches memory
        block.Instructions[i] = new MInstr(instr.Opcode, operands,
          instr.Effect with { ReadsMemory = true, WritesMemory = instr.Effect.WritesMemory || writes },
          instr.Condition, instr.Clobbers);
      }
  }

  private static List<int> Positions(MInstr instr, int virtualId) {
    var positions = new List<int>();
    for (var i = 0; i < instr.Operands.Count; ++i)
      if (instr.Operands[i] is MOperand.Register { Reg: { IsVirtual: true } r } && r.VirtualId == virtualId)
        positions.Add(i);
    return positions;
  }

  private static bool IsMemory(MOperand operand)
    => operand is MOperand.Memory or MOperand.StackSlot or MOperand.DataCell or MOperand.ParamCell;

  private static MOperand WithSize(MOperand cell, MRegSize size) => cell switch {
    MOperand.StackSlot stack => stack with { Size = size },
    MOperand.ParamCell parameter => parameter with { Size = size },
    _ => cell,
  };

  private static int StorageBytes(MRegSize size) => size switch {
    MRegSize.Byte => 1,
    MRegSize.Word => 2,
    MRegSize.Dword => 4,
    MRegSize.Qword => 8,
    MRegSize.Tbyte => 10,
    _ => throw new ArgumentOutOfRangeException(nameof(size)),
  };
}
