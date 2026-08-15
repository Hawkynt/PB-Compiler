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
  /// How far along the spiller is, as three counts that a move must lower to be worth applying. It is
  /// the measure the allocator's loop is proved to terminate on - see
  /// <c>LinearScanAllocator.AdvanceSpiller</c>, which states the argument each component carries.
  /// </summary>
  /// <param name="Untouched">
  /// virtual registers still present that the spiller has not moved yet. Every move consumes its
  /// subject - the id is replaced everywhere by fresh ones, or by a memory cell - and every id a move
  /// mints is recorded in <see cref="MFunction.MovedValues"/> at birth, so a first move on a value
  /// lowers this by one and nothing can ever raise it.
  /// </param>
  /// <param name="Crossings">
  /// live ranges caught across an instruction that clobbers physical registers - the pressure this
  /// target makes all by itself, since one <c>CALL</c> destroys the whole allocatable file. Splitting a
  /// range is what removes one, so a split of a value the spiller has already moved has to show that it
  /// removed one to be worth another cell.
  /// </param>
  /// <param name="Unsettled">
  /// uses whose recomputable operand is still defined outside their preparation run. Rematerializing
  /// settles every use of one value and, inserting at the FRONT of the run, unsettles none - so the
  /// one move that may legitimately be repeated on a value it has already touched lowers this.
  /// </param>
  /// <param name="Present">
  /// virtual registers present at all. Only a direct spill lowers it (it mints nothing and its subject
  /// becomes a frame cell), which is what a repeat spill has to show for itself.
  /// </param>
  internal readonly record struct Progress(int Untouched, int Crossings, int Unsettled, int Present) {

    public static Progress Of(MFunction function) {
      var census = ValueCensus.Of(function);
      var present = new HashSet<int>();
      var mentioned = new List<int>();
      var unsettled = 0;
      foreach (var block in function.Blocks)
        foreach (var instruction in block.Instructions) {
          ValueCensus.Mentioned(instruction, mentioned);
          present.UnionWith(mentioned);
          if (instruction.Operands is [MOperand.Register { Reg: { IsVirtual: true } target }, { } source]
              && IsRecomputable(instruction.Opcode, source)
              && census.DefinitionsOf(target.VirtualId) == 1)
            unsettled += UnsettledUses(census, instruction, target.VirtualId);
        }
      var clobbers = GetClobberIndices(function);
      var crossings = LivenessAnalysis.Compute(function)
        .Sum(interval => clobbers.Count(at => interval.Start < at && at < interval.End));
      return new(present.Count(value => !function.MovedValues.Contains(value)), crossings, unsettled,
        present.Count);
    }

    /// <summary>Whether this state is strictly closer to an allocation than <paramref name="other"/>.</summary>
    public bool IsBelow(Progress other) => this.Untouched != other.Untouched ? this.Untouched < other.Untouched
      : this.Crossings != other.Crossings ? this.Crossings < other.Crossings
      : this.Unsettled != other.Unsettled ? this.Unsettled < other.Unsettled
      : this.Present < other.Present;
  }

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
  /// copy in front of each use makes every live range as short as the use's own operand setup, so no
  /// clobber can fall inside one. A definition already standing there is left alone because moving it
  /// again cannot shorten anything - <see cref="UnsettledUses"/> is what that means exactly, and the
  /// reason it is not simply "adjacent".
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

    var census = ValueCensus.Of(function);
    foreach (var block in function.Blocks)
      foreach (var instr in block.Instructions.ToList()) {
        if (instr.Operands is not [MOperand.Register { Reg: { IsVirtual: true } target }, { } source]
            || !IsRecomputable(instr.Opcode, source))
          continue;
        if (census.DefinitionsOf(target.VirtualId) != 1 || census.UsesOf(target.VirtualId).Count == 0)
          continue;
        if (UnsettledUses(census, instr, target.VirtualId) == 0)
          continue;                              // nothing to gain, and re-doing it would never settle
        Rematerialize(function, census, instr, target.VirtualId);
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
    var census = ValueCensus.Of(function);
    foreach (var load in function.ArgumentLoads.ToList()) {
      if (!IsAddressOnly(function, load.VirtualId))
        continue;

      foreach (var block in function.Blocks)
        for (var i = block.Instructions.Count - 1; i >= 0; --i) {
          var instruction = block.Instructions[i];
          if (!UsesAsAddress(instruction, load.VirtualId))
            continue;

          var fresh = MReg.Virtual(function.VirtualRegisterCount++, MRegSize.Word);
          function.MovedValues.Add(fresh.VirtualId);
          var operands = instruction.Operands.Select(operand => operand is MOperand.Memory memory
            ? memory with {
              Base = Replace(memory.Base, load.VirtualId, fresh),
              Index = Replace(memory.Index, load.VirtualId, fresh),
              Segment = Replace(memory.Segment, load.VirtualId, fresh),
            }
            : operand).ToArray();
          block.Instructions[i] = new MInstr(instruction.Opcode, operands, instruction.Effect,
            instruction.Condition, instruction.Clobbers);
          i = InsertPreparation(census, block, i, load.VirtualId, at => Reload(fresh,
            new MOperand.ParamCell(load.ArgumentIndex, load.ByteDelta, MRegSize.Word),
            WithPendingStaging(block, at, [])));
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
  /// Whether every use already has this value's definition inside its PREPARATION RUN - so
  /// rematerializing it again could not shorten anything.
  ///
  /// <para>
  /// The obvious test is physical adjacency ("is the definition the instruction immediately before the
  /// use"), and it is the reason the spill loop used to run for ever. <c>MOV [v_addr], v_const</c> reads
  /// a frame address and a constant, each is recomputable, and each can only be adjacent to the store
  /// if the other is not: putting either one beside it DISPLACES the other, which then looks unsettled
  /// and is rematerialized in its turn, one fresh virtual register per round and nothing ever
  /// converging. Adjacency was only ever a proxy for the property that matters - that the value's live
  /// range is as short as recomputing can make it - and it is the wrong proxy the moment an instruction
  /// has two recomputable operands.
  /// </para>
  /// <para>
  /// The run is the right one: an instruction that exists ONLY to prepare this use's operands does not
  /// lengthen anything the allocator cares about, so a definition standing anywhere inside it is
  /// settled. Two operands prepared for the same use are then BOTH settled and neither displaces the
  /// other - which is what turns the ping-pong into two rounds.
  /// </para>
  /// </summary>
  private static int UnsettledUses(ValueCensus census, MInstr definition, int virtualId) {
    if (census.PositionOf(definition) is not { } definedAt)
      return 0;
    var unsettled = 0;
    foreach (var use in census.UsesOf(virtualId)) {
      if (census.PositionOf(use) is not { } usedAt)
        continue;
      if (!ReferenceEquals(usedAt.Block, definedAt.Block) || definedAt.Index >= usedAt.Index
          || definedAt.Index < PreparationStart(census, usedAt.Block, usedAt.Index, keepBelow: -1))
        ++unsettled;
    }
    return unsettled;
  }

  /// <summary>
  /// Where the run of instructions that exist only to prepare the operands of the instruction at
  /// <paramref name="useIndex"/> begins - and so where the spiller inserts one more of them.
  ///
  /// <para>
  /// Inserting at the FRONT of the run rather than immediately before the use is the second half of the
  /// termination argument (<c>LinearScanAllocator.AdvanceSpiller</c>): nothing then lands between an
  /// already-settled definition and the use it was recomputed for, so settling one value never unsettles
  /// another and the count of unsettled values cannot go back up.
  /// </para>
  /// <para>
  /// <paramref name="keepBelow"/> is the value being moved, which the run may never be walked past: a
  /// preparation instruction is allowed to READ it (a chained <c>LEA</c> off an address that is itself
  /// being rematerialized), and inserting in front of that would put the new definition before a use
  /// the caller has not rewritten yet. -1 when there is nothing to keep below, which is the query form.
  /// </para>
  /// </summary>
  private static int PreparationStart(ValueCensus census, MBlock block, int useIndex, int keepBelow) {
    var use = block.Instructions[useIndex];
    var at = useIndex;
    while (at > 0 && census.PreparesOnly(block.Instructions[at - 1], use)
           && (keepBelow < 0 || !Mentions(block.Instructions[at - 1], keepBelow)))
      --at;
    return at;
  }

  /// <summary>
  /// Puts a freshly minted definition where it belongs for the use at <paramref name="useIndex"/> and
  /// answers the index it landed at, which the caller's descending scan resumes below.
  /// </summary>
  private static int InsertPreparation(ValueCensus census, MBlock block, int useIndex, int keepBelow,
      Func<int, MInstr> definition) {
    var at = PreparationStart(census, block, useIndex, keepBelow);
    block.Instructions.Insert(at, definition(at));   // the pending staging is the one AT the insertion point
    return at;
  }

  /// <summary>
  /// Where every value is defined and used, and where every instruction sits, taken once per spiller
  /// move. Both questions the settling rule asks - "does this instruction exist only to prepare that
  /// one's operands" and "is the definition inside the use's preparation run" - are otherwise a walk of
  /// the whole function EACH, which turns the measure into a quadratic on the function's size.
  ///
  /// <para>
  /// The positions are a snapshot and the definition/use counts are not: a move rewrites operands and
  /// inserts instructions as it goes, so <see cref="PositionOf"/> is only meaningful before one starts,
  /// while <see cref="PreparesOnly"/> stays true throughout - a move only ever adds fresh values, which
  /// the census does not know and therefore never mistakes for preparation.
  /// </para>
  /// </summary>
  private sealed class ValueCensus {

    private readonly Dictionary<int, (int Definitions, List<MInstr> Uses)> _values = [];
    private readonly Dictionary<MInstr, (MBlock Block, int Index)> _positions =
      new(ReferenceEqualityComparer.Instance);

    public static ValueCensus Of(MFunction function) {
      var census = new ValueCensus();
      var mentioned = new List<int>();
      foreach (var block in function.Blocks)
        for (var i = 0; i < block.Instructions.Count; ++i) {
          var instruction = block.Instructions[i];
          census._positions[instruction] = (block, i);
          var writes = LivenessAnalysis.RegistersOf(instruction).Writes;
          Mentioned(instruction, mentioned);
          foreach (var value in mentioned) {
            if (!census._values.TryGetValue(value, out var entry))
              census._values[value] = entry = (0, []);
            if (writes.Contains(value))
              census._values[value] = (entry.Definitions + 1, entry.Uses);
            else
              entry.Uses.Add(instruction);
          }
        }
      return census;
    }

    public (MBlock Block, int Index)? PositionOf(MInstr instruction)
      => this._positions.TryGetValue(instruction, out var position) ? position : null;

    public IReadOnlyList<MInstr> UsesOf(int value)
      => this._values.TryGetValue(value, out var entry) ? entry.Uses : [];

    public int DefinitionsOf(int value)
      => this._values.TryGetValue(value, out var entry) ? entry.Definitions : 0;

    /// <summary>
    /// Whether <paramref name="instruction"/> exists only to prepare an operand of <paramref name="use"/>:
    /// it defines exactly one virtual register, and that register is named nowhere else at all.
    /// </summary>
    public bool PreparesOnly(MInstr instruction, MInstr use) {
      if (instruction.Clobbers.Count > 0)
        return false;                            // it destroys registers, so standing in front of it costs something
      var writes = LivenessAnalysis.RegistersOf(instruction).Writes;
      return writes.Count == 1 && this._values.TryGetValue(writes[0], out var value)
        && value is { Definitions: 1, Uses.Count: 1 } && ReferenceEquals(value.Uses[0], use);
    }

    /// <summary>The virtual values the instruction names, each once - as an operand or inside an address.</summary>
    public static void Mentioned(MInstr instruction, List<int> into) {
      into.Clear();
      foreach (var operand in instruction.Operands)
        switch (operand) {
          case MOperand.Register { Reg: { IsVirtual: true } register }:
            Add(into, register.VirtualId);
            break;
          case MOperand.Memory memory:
            if (memory.Base is { IsVirtual: true } baseRegister)
              Add(into, baseRegister.VirtualId);
            if (memory.Index is { IsVirtual: true } indexRegister)
              Add(into, indexRegister.VirtualId);
            if (memory.Segment is { IsVirtual: true } segmentRegister)
              Add(into, segmentRegister.VirtualId);
            break;
        }
    }

    private static void Add(List<int> into, int value) {
      if (!into.Contains(value))
        into.Add(value);
    }
  }

  /// <summary>A reload of one value out of a memory cell, the shape three of the spiller's moves insert.</summary>
  private static MInstr Reload(MReg into, MOperand cell, IReadOnlyList<Asm.Reg> clobbers)
    => new(MOpcode.Mov, [new MOperand.Register(into), cell],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false,
        ReadsMemory: true, WritesMemory: false),
      condition: null, clobbers: clobbers);

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
  /// Rebuilds the definition into a fresh virtual register in front of every use, then drops the
  /// original. A fresh id is essential: liveness has one interval per id, so copying the same
  /// definition with the same destination would still leave one interval spanning all calls.
  /// </summary>
  private static void Rematerialize(MFunction function, ValueCensus census, MInstr definition, int virtualId) {
    var target = ((MOperand.Register)definition.Operands[0]).Reg;
    foreach (var block in function.Blocks)
      for (var i = block.Instructions.Count - 1; i >= 0; --i) {
        var instr = block.Instructions[i];
        if (ReferenceEquals(instr, definition) || !Mentions(instr, virtualId))
          continue;
        if (LivenessAnalysis.RegistersOf(instr).Writes.Contains(virtualId))
          continue;

        var fresh = MReg.Virtual(function.VirtualRegisterCount++, target.Size);
        function.MovedValues.Add(fresh.VirtualId);
        block.Instructions[i] = ReplaceMentions(instr, virtualId, fresh);
        var operands = definition.Operands.ToArray();
        operands[0] = new MOperand.Register(fresh);
        i = InsertPreparation(census, block, i, virtualId, at => new MInstr(definition.Opcode, operands,
          definition.Effect, definition.Condition, WithPendingStaging(block, at, definition.Clobbers)));
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
  /// Splits a range caught across a full-register clobber. That is the pressure this target creates all
  /// by itself - one <c>CALL</c> destroys every allocatable register - and the fresh ranges a split
  /// leaves usually no longer cross the call.
  ///
  /// <para>
  /// "Usually" is the whole difficulty, and it is why the offer is NOT restricted to values the spiller
  /// has not moved yet, as <see cref="SplitPressureOne"/> is: a definition that carries clobbers of its
  /// own leaves its store and its reload straddling one, so the fresh range crosses a clobber too and is
  /// offered again - and sometimes that second split is the one that lands, because the range it takes
  /// apart is a different range. Which of the two it is cannot be told from here, so it is told from the
  /// RESULT: <c>LinearScanAllocator.AdvanceSpiller</c> keeps a split of an already-moved value only when
  /// it removed a crossing, and discards a re-split that settled nothing - which is also why this is a
  /// separate move from the one below rather than its first half. A discarded move must leave the next
  /// KIND to be tried, and a caller cannot try what it cannot name.
  /// </para>
  /// </summary>
  internal static bool SplitCrossingOne(MFunction function) {
    var clobbers = GetClobberIndices(function);
    return TrySplitLongest(function, ValueCensus.Of(function), LivenessAnalysis.Compute(function)
      .Where(interval => clobbers.Any(at => interval.Start < at && at < interval.End)));
  }

  /// <summary>
  /// Splits a range under plain pressure: no call anywhere near, simply more values wanted at once than
  /// there are registers. Four LONG accumulators are eight words on a six-register machine, and none of
  /// them can move to memory as it stands - a value loaded out of an array has a memory operand in its
  /// own defining instruction, so making it one too would be a memory-to-memory MOV. An explicit
  /// store/reload pair is two ordinary register-memory instructions and says the same thing.
  ///
  /// <para>
  /// Only values the spiller has not moved yet are offered. Pressure has no self-limiting shape at all -
  /// a range already taken apart is already as short as this move can make it - so a second attempt
  /// could only add another cell and another pair of moves.
  /// </para>
  /// </summary>
  internal static bool SplitPressureOne(MFunction function)
    => TrySplitLongest(function, ValueCensus.Of(function), LivenessAnalysis.Compute(function)
      .Where(interval => !function.MovedValues.Contains(interval.VirtualId)));

  /// <summary>
  /// Splits the longest of the offered live ranges that can be split, if any can - the shared half of
  /// both splitting moves. Every definition writes the value to one frame cell; every use reloads it
  /// into its own fresh, instruction-local virtual register. Multiple definitions occur after phi
  /// elimination, where each predecessor copies its incoming value into the same virtual destination.
  /// This also handles shapes such as <c>MOV value,[array-element]</c>: direct spilling would require an
  /// illegal memory-to-memory MOV, while the explicit store/reloads are ordinary register-memory
  /// instructions.
  /// </summary>
  private static bool TrySplitLongest(MFunction function, ValueCensus census,
      IEnumerable<LivenessAnalysis.LiveInterval> offered) {
    // A value the spiller has not moved yet is offered before one it has, whatever their lengths: taking
    // a range apart a second time is the move that may settle nothing at all, and the longest range in
    // the function is exactly the one most likely to have been taken apart already.
    var candidates = offered
      .OrderBy(interval => function.MovedValues.Contains(interval.VirtualId) ? 1 : 0)
      .ThenByDescending(interval => interval.End - interval.Start)
      .ThenBy(interval => interval.VirtualId);

    foreach (var interval in candidates) {
      var argumentAt = function.ArgumentLoads.FindIndex(load => load.VirtualId == interval.VirtualId);
      if (argumentAt >= 0) {
        if (TrySplitArgument(function, census, interval.VirtualId, function.ArgumentLoads[argumentAt]))
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
          function.MovedValues.Add(fresh.VirtualId);
          block.Instructions[i] = ReplaceMentions(instruction, interval.VirtualId, fresh);
          i = InsertPreparation(census, block, i, interval.VirtualId,
            at => Reload(fresh, cell, WithPendingStaging(block, at, [])));
        }

      var stored = 0;
      foreach (var block in function.Blocks)
        for (var i = block.Instructions.Count - 1; i >= 0; --i) {
          var instruction = block.Instructions[i];
          if (!definitionSet.Contains(instruction))
            continue;
          var readsOldValue = LivenessAnalysis.RegistersOf(instruction).Reads.Contains(interval.VirtualId);
          var fresh = MReg.Virtual(function.VirtualRegisterCount++, target.Size);
          function.MovedValues.Add(fresh.VirtualId);
          block.Instructions[i] = ReplaceMentions(instruction, interval.VirtualId, fresh);
          block.Instructions.Insert(i + 1, new MInstr(MOpcode.Mov,
            [cell, new MOperand.Register(fresh)],
            new MInstrEffect(WrittenRegs: [], ReadRegs: [1], ReadsFlags: false, WritesFlags: false,
              ReadsMemory: false, WritesMemory: true)));
          if (readsOldValue)
            i = InsertPreparation(census, block, i, interval.VirtualId,
              at => Reload(fresh, cell, WithPendingStaging(block, at, [])));
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
  private static bool TrySplitArgument(MFunction function, ValueCensus census, int virtualId,
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
        function.MovedValues.Add(fresh.VirtualId);
        block.Instructions[i] = ReplaceMentions(instruction, virtualId, fresh);
        i = InsertPreparation(census, block, i, virtualId, at => Reload(fresh,
          new MOperand.ParamCell(load.ArgumentIndex, load.ByteDelta, size.Value),
          WithPendingStaging(block, at, [])));
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
