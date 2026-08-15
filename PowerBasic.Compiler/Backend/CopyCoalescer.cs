namespace PowerBasic.Compiler.Backend;

/// <summary>
/// Merges a register-to-register <c>MOV</c>'s two virtual registers into one, so the move disappears
/// and the value keeps a single register across it. It runs between scheduling and allocation, on the
/// speed objective only (see <see cref="LinearScanAllocator"/>).
///
/// <para>
/// <b>What it is for.</b> Out-of-SSA is what makes this necessary rather than merely nice. A loop's
/// counter is a phi, so selection gives it one virtual register for the value entering the loop and
/// another for the value computed in the body, joined by a copy on the back edge - and a two-address
/// machine adds a second copy in front of the increment, because <c>ADD</c> writes one of the
/// registers it reads. The counter of <c>FOR i = 1 TO n</c> therefore comes out as
/// <c>MOV t, i / ADD t, 1 / MOV i, t</c> over two registers where the hardware wants <c>ADD i, 1</c>
/// over one. No preference can fix that: the two ranges genuinely overlap, so the allocator is right
/// to keep them apart, and residency without this pass buys a register the loop then copies out of
/// and back into on every iteration.
/// </para>
///
/// <para>
/// <b>Why merging those two is sound.</b> Overlapping is not the same as conflicting. What actually
/// stops two values sharing a register is a DEFINITION of one at a point where the other is still
/// wanted, so that is the whole test: for every instruction that writes <c>a</c>, <c>b</c> must be
/// dead immediately after it, and the other way round. The copies between them are exempt, because
/// after <c>a := b</c> the two hold the same bits and the merge turns the instruction into the no-op
/// it wants to be. Two values may therefore be live together for a whole loop and still merge, which
/// is exactly the case that matters - the counter entering the loop and the counter leaving it are
/// live together from the back edge onwards and are the same number.
/// </para>
///
/// <para>
/// An earlier version proved the weaker property "equal wherever both are live" by forward dataflow
/// and it MISCOMPILED, which is worth recording because the two read alike. Equality before an
/// instruction says nothing about what the instruction then does: in <c>MOV a, b / MOV b, c / use a</c>
/// the two are provably equal at the second instruction, and merging them makes that instruction
/// overwrite the value <c>a</c> is about to be read for. The property has to be about definitions, not
/// about points.
/// </para>
///
/// <para>
/// <b>What it deliberately refuses.</b> A value the prologue loads from an argument cell is never
/// merged: that definition is not an instruction, so the analysis cannot see it and would prove an
/// equality that the prologue then breaks. Nor is a copy with clobbers, a byte/word mismatch, or a
/// move that is not a plain register-to-register <c>MOV</c>. And the pass does not decide whether its
/// result is kept - merging unions two live ranges, so the merged value must avoid the clobbers of
/// BOTH, which can cost an allocation that two separate registers had. The caller allocates a COPY of
/// the function and keeps the coalesced form only when it allocates.
/// </para>
/// </summary>
internal static class CopyCoalescer {

  /// <summary>Merges what it can prove, and answers how many copies it removed.</summary>
  internal static int Run(MFunction function) {
    var removed = 0;
    // one merge invalidates every liveness fact, so each round re-measures; the bound is the number of
    // copies, since each round removes at least one
    for (var again = true; again;) {
      again = false;
      var pinned = ArgumentRegisters(function);
      var liveness = LivenessAnalysis.Analyze(function);
      foreach (var (_, instr) in Numbered(function)) {
        if (!IsPlainCopy(instr, out var destination, out var source))
          continue;
        if (destination.VirtualId == source.VirtualId
            || pinned.Contains(destination.VirtualId) || pinned.Contains(source.VirtualId))
          continue;
        if (!CanMerge(function, liveness, destination.VirtualId, source.VirtualId))
          continue;
        removed += Merge(function, destination.VirtualId, source.VirtualId);
        again = true;
        break;
      }
    }
    return removed;
  }

  /// <summary>A register-to-register <c>MOV</c> between two virtual registers of the same width, with nothing pinned to it.</summary>
  private static bool IsPlainCopy(MInstr instr, out MReg destination, out MReg source) {
    destination = default;
    source = default;
    if (instr.Opcode != MOpcode.Mov || instr.Clobbers.Count > 0)
      return false;
    if (instr.Operands is not [MOperand.Register { Reg: { IsVirtual: true } d }, MOperand.Register { Reg: { IsVirtual: true } s }])
      return false;
    // the descriptor has to say what the shape implies - a hand-built MOV whose effect claims
    // something else is not a copy, and reading it as one would rewrite the wrong register
    if (instr.Effect.WrittenRegs is not [0] || instr.Effect.ReadRegs is not [1])
      return false;
    if (d.Size != s.Size)
      return false;
    (destination, source) = (d, s);
    return true;
  }

  /// <summary>
  /// The virtual registers the PROLOGUE writes - each one an argument word the emitter loads before
  /// the first instruction. That definition is not an instruction, so the rule below cannot see it and
  /// would merge across a write it does not know happens.
  /// </summary>
  private static HashSet<int> ArgumentRegisters(MFunction function) {
    var pinned = new HashSet<int>();
    foreach (var (virtualId, _, _) in function.ArgumentLoads)
      pinned.Add(virtualId);
    return pinned;
  }

  /// <summary>
  /// Whether <paramref name="a"/> and <paramref name="b"/> may become one register: no definition of
  /// either lands where the other is still live, the copies between them excepted.
  /// </summary>
  private static bool CanMerge(MFunction function, LivenessAnalysis.Liveness liveness, int a, int b) {
    var after = liveness.LiveAfter;
    foreach (var (index, instr) in Numbered(function)) {
      // an inline-asm block's operands ARE its BASIC names' machine locations, so two names sharing a
      // register would resolve to one place; and it declares no def/use the rule could read anyway
      if (instr.Opcode == MOpcode.InlineAsm && Mentions(instr, a, b))
        return false;
      if (IsCopyBetween(instr, a, b))
        continue;
      var (_, writes) = LivenessAnalysis.RegistersOf(instr);
      if (index >= after.Count)
        continue;
      foreach (var written in writes)
        if ((written == a && after[index].Contains(b)) || (written == b && after[index].Contains(a)))
          return false;
    }
    return true;
  }

  /// <summary>A copy from one of the pair to the other - the instruction the merge is trying to delete.</summary>
  private static bool IsCopyBetween(MInstr instr, int a, int b)
    => IsPlainCopy(instr, out var destination, out var source)
       && ((destination.VirtualId == a && source.VirtualId == b)
           || (destination.VirtualId == b && source.VirtualId == a));

  private static bool Mentions(MInstr instr, int a, int b) {
    foreach (var operand in instr.Operands)
      switch (operand) {
        case MOperand.Register { Reg: { IsVirtual: true, VirtualId: var v } } when v == a || v == b:
          return true;
        case MOperand.Memory memory when Names(memory.Base, a, b) || Names(memory.Index, a, b) || Names(memory.Segment, a, b):
          return true;
      }
    return false;
  }

  private static bool Names(MReg? register, int a, int b)
    => register is { IsVirtual: true, VirtualId: var v } && (v == a || v == b);

  /// <summary>Rewrites every mention of <paramref name="from"/> as <paramref name="to"/> and drops the copies that became no-ops.</summary>
  private static int Merge(MFunction function, int from, int to) {
    var removed = 0;
    foreach (var block in function.Blocks) {
      for (var i = block.Instructions.Count - 1; i >= 0; --i) {
        var rewritten = Rewrite(block.Instructions[i], from, to);
        if (IsPlainCopy(rewritten, out var destination, out var source)
            && destination.VirtualId == source.VirtualId && destination.Size == source.Size) {
          block.Instructions.RemoveAt(i);
          ++removed;
          continue;
        }
        block.Instructions[i] = rewritten;
      }
    }
    // ArgumentLoads needs no rewriting: a value the prologue loads is never a merge candidate
    if (function.MovedValues.Remove(from))
      function.MovedValues.Add(to);
    return removed;
  }

  private static MInstr Rewrite(MInstr instr, int from, int to) {
    List<MOperand>? rewritten = null;
    for (var i = 0; i < instr.Operands.Count; ++i) {
      var replacement = RewriteOperand(instr.Operands[i], from, to);
      if (ReferenceEquals(replacement, instr.Operands[i]))
        continue;
      rewritten ??= [.. instr.Operands];
      rewritten[i] = replacement;
    }
    return rewritten is null ? instr
      : new(instr.Opcode, rewritten, instr.Effect, instr.Condition, instr.Clobbers);
  }

  private static MOperand RewriteOperand(MOperand operand, int from, int to) => operand switch {
    MOperand.Register register when register.Reg is { IsVirtual: true, VirtualId: var v } && v == from
      => register with { Reg = register.Reg with { VirtualId = to } },
    MOperand.Memory memory => memory with {
      Base = RewriteRegister(memory.Base, from, to),
      Index = RewriteRegister(memory.Index, from, to),
      Segment = RewriteRegister(memory.Segment, from, to),
    },
    _ => operand,
  };

  private static MReg? RewriteRegister(MReg? register, int from, int to)
    => register is { IsVirtual: true, VirtualId: var v } && v == from ? register.Value with { VirtualId = to } : register;

  private static IEnumerable<(int Index, MInstr Instr)> Numbered(MFunction function) {
    var index = 0;
    foreach (var block in function.Blocks)
      foreach (var instr in block.Instructions)
        yield return (index++, instr);
  }
}
