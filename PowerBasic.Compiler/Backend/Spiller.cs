namespace PowerBasic.Compiler.Backend;

/// <summary>
/// The x86-16 back end's spilling (docs/X86-BACKEND.md): moving a value out of the register file and
/// into the frame when linear scan cannot place it - which on this target is usually not because six
/// registers ran out, but because a <c>CALL</c> destroys the whole caller-saved file and a value live
/// across it may not sit in any of them.
///
/// x86 is a memory-operand machine, so the spill needs no reload code at all: a spilled value simply
/// <b>is</b> its frame cell, and every instruction that touched the register now names the cell. Two
/// kinds of cell exist, and the cheaper one is tried first:
///
/// - an incoming <b>parameter</b> is already in the frame where the caller pushed it, and an IR
///   argument is an SSA value nothing writes - so it costs literally nothing: the prologue copy is
///   dropped and the uses address <c>[BP+6]</c> directly;
/// - any other value gets a fresh stack slot, and its defining instruction writes there instead of
///   into a register.
///
/// It is conservative about where a memory operand is legal: only the forms the emitter really has
/// (the two-operand ALU family, <c>PUSH</c>, <c>IMUL</c>'s source, <c>IDIV</c>'s divisor, a shift's
/// destination), never two memory operands in one instruction, and never a value used as an address
/// base or index. A value it cannot move stays in a
/// register, and if that leaves no allocation the function declines - the back end is an opt-in path
/// that falls back.
/// </summary>
internal static class Spiller {

  // where the emitter can take a memory operand: the Emit2 family accepts reg,mem / mem,reg / mem,imm
  private static readonly HashSet<MOpcode> _memoryCapable = [
    MOpcode.Mov, MOpcode.Add, MOpcode.Sub, MOpcode.And, MOpcode.Or, MOpcode.Xor,
    MOpcode.Adc, MOpcode.Sbb, MOpcode.Cmp,
  ];

  /// <summary>
  /// Moves one value out of the register file, returning false when none can move. Parameters go
  /// first because their cell is free; among equals the longest live range goes first, being the one
  /// most likely to have blocked the allocation.
  /// </summary>
  /// <summary>
  /// Shortens one value's live range by RECOMPUTING it at each use instead of keeping it in a
  /// register, and returns whether it moved anything.
  ///
  /// This is the answer for the case spilling cannot touch. A frame address - the <c>LEA</c> that puts
  /// an array's base where a GEP can index from it - is used as a memory BASE, and a base has to be in
  /// a register, so the spiller refuses it. If a call or an inline-asm block sits between the LEA and
  /// the use, the value is live across a clobber of the whole register file with nowhere to go, and
  /// the function selects but never routes. It was 25 of the 37 such functions in the corpus.
  ///
  /// Recomputing is free of that problem because the LEA depends on nothing but BP: putting a fresh
  /// copy immediately before each use makes every live range one instruction long, so no clobber can
  /// fall inside one. It costs an instruction per use and saves nothing when there is only one use,
  /// which is why the single-use case is left alone.
  /// </summary>
  internal static bool RematerializeOne(MFunction function) {
    foreach (var block in function.Blocks)
      foreach (var instr in block.Instructions.ToList()) {
        if (instr.Opcode != MOpcode.Lea
            || instr.Operands is not [MOperand.Register { Reg: { IsVirtual: true } target }, MOperand.StackSlot or MOperand.DataOffset])
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
  /// Whether every use is already immediately preceded by the definition, so recomputing would produce
  /// the identical instruction list. Without this the allocator's retry loop would call this forever.
  /// </summary>
  private static bool AlreadyBesideItsUses(MFunction function, MInstr definition, int virtualId) {
    foreach (var block in function.Blocks)
      for (var i = 0; i < block.Instructions.Count; ++i) {
        var instr = block.Instructions[i];
        if (ReferenceEquals(instr, definition) || !Mentions(instr, virtualId))
          continue;
        if (LivenessAnalysis.RegistersOf(instr).Writes.Contains(virtualId))
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

  /// <summary>Whether the instruction names the value anywhere - as an operand or inside a memory address.</summary>
  private static bool Mentions(MInstr instr, int virtualId) {
    foreach (var operand in instr.Operands)
      switch (operand) {
        case MOperand.Register { Reg: { IsVirtual: true } r } when r.VirtualId == virtualId:
        case MOperand.Memory { Base: { IsVirtual: true } b } when b.VirtualId == virtualId:
        case MOperand.Memory { Index: { IsVirtual: true } x } when x.VirtualId == virtualId:
          return true;
      }
    return false;
  }

  /// <summary>Copies the defining instruction in front of every use, then drops the original.</summary>
  private static void Rematerialize(MFunction function, MInstr definition, int virtualId) {
    foreach (var block in function.Blocks)
      for (var i = block.Instructions.Count - 1; i >= 0; --i) {
        var instr = block.Instructions[i];
        if (ReferenceEquals(instr, definition) || !Mentions(instr, virtualId))
          continue;
        if (LivenessAnalysis.RegistersOf(instr).Writes.Contains(virtualId))
          continue;
        block.Instructions.Insert(i, definition);
      }

    foreach (var block in function.Blocks)
      for (var i = 0; i < block.Instructions.Count; ++i)
        if (ReferenceEquals(block.Instructions[i], definition)) {
          block.Instructions.RemoveAt(i);          // the ORIGINAL, ahead of every copy inserted above
          return;
        }
  }

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
        Rewrite(function, virtualId, new MOperand.ParamCell(load.ArgumentIndex, load.ByteDelta));
        function.ArgumentLoads.RemoveAll(a => a.VirtualId == virtualId);
      } else {
        function.StackSlots.Add(2);
        Rewrite(function, virtualId, new MOperand.StackSlot(function.StackSlots.Count - 1, MRegSize.Word));
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
                || (mem.Index is { IsVirtual: true } x && x.VirtualId == virtualId)))
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
        foreach (var at in positions)
          operands[at] = cell;
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
}
