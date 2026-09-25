namespace PowerBasic.Compiler.Backend;

/// <summary>
/// The branch rewrites that follow from the block ORDER - the order the emitter lays blocks out in,
/// and so the order their labels land in. None can be done during instruction selection proper,
/// where a block's neighbour is not yet known, and some only become possible after allocation: a
/// block that held nothing but a phi copy is left holding a bare <c>JMP</c> once the coalescer has
/// merged the copy away.
/// </summary>
public static class MachineBranchCleanup {

  /// <summary>Threads jump-only blocks, then straightens what that exposes. Answers the rewrites made.</summary>
  public static int Run(X86MachineFunction function) {
    ArgumentNullException.ThrowIfNull(function);
    var made = ThreadJumpOnlyBlocks(function);
    made += PlaceJumpTargets(function);
    made += StraightenBranches(function);
    return made;
  }

  /// <summary>
  /// A <c>JMP</c> to the block laid out next is the fallthrough and is deleted, and a
  /// <c>Jcc next / JMP away</c> pair is <c>J!cc away</c>. Both leave the successor set alone - the
  /// same two blocks are reachable on the same two conditions.
  ///
  /// <para>
  /// A pair whose two arms are the SAME block is left alone: it is degenerate and not this pass's to
  /// reason about. An ABI-pinned branch is not - a jump writes no register, so its clobber list is a
  /// barrier the pinned sequence's other members carry too; the inverted branch keeps it, and a
  /// deleted one takes nothing with it that the instruction in front of it does not still say.
  /// </para>
  /// </summary>
  public static int StraightenBranches(X86MachineFunction function) {
    var made = 0;
    for (var b = 0; b + 1 < function.Blocks.Count; ++b) {
      var body = function.Blocks[b].Instructions;
      var next = function.Blocks[b + 1].Label;

      if (body.Count >= 2
          && body[^1] is { Opcode: MOpcode.Jmp, Condition: null } away
          && away.Operands is [MOperand.LabelRef elsewhere]
          && body[^2] is { Opcode: MOpcode.Jcc, Condition: { } taken } branch
          && branch.Operands is [MOperand.LabelRef whenTaken]
          && whenTaken.Name == next && elsewhere.Name != next) {
        body[^2] = new MInstr(MOpcode.Jcc, [elsewhere], branch.Effect, Inverted(taken), branch.Clobbers);
        body.RemoveAt(body.Count - 1);
        ++made;
      }

      if (body.Count >= 1
          && body[^1] is { Opcode: MOpcode.Jmp, Condition: null } tail
          && tail.Operands is [MOperand.LabelRef fallsInto] && fallsInto.Name == next) {
        body.RemoveAt(body.Count - 1);
        ++made;
      }
    }
    return made;
  }

  /// <summary>
  /// A block holding nothing but <c>JMP X</c> is a detour: every branch to it is retargeted at
  /// <c>X</c>, a predecessor that FALLS into it gets the <c>JMP X</c> itself, and the block goes.
  ///
  /// <para>
  /// It is kept whenever anything else could name its label - a jump table, a block address taken
  /// for an error handler or a GOSUB return, the entry - and the whole pass stands aside for a
  /// function with inline assembly, whose text may jump to a BASIC label by name.
  /// </para>
  /// </summary>
  public static int ThreadJumpOnlyBlocks(X86MachineFunction function) {
    if (function.Blocks.Count < 2 || function.AllInstructions.Any(i => i.Opcode == MOpcode.InlineAsm))
      return 0;
    var made = 0;
    for (var b = 1; b < function.Blocks.Count; ++b) {
      var detour = function.Blocks[b];
      if (detour.Instructions is not [{ Opcode: MOpcode.Jmp, Condition: null } jump]
          || jump.Operands is not [MOperand.LabelRef { Name: var target }]
          || target == detour.Label || NamedOutsideBranches(function, detour.Label))
        continue;

      foreach (var block in function.Blocks) {
        for (var i = 0; i < block.Instructions.Count; ++i)
          if (block.Instructions[i] is { Opcode: MOpcode.Jmp or MOpcode.Jcc } branch
              && branch.Operands is [MOperand.LabelRef { Name: var name }] && name == detour.Label)
            block.Instructions[i] = new MInstr(branch.Opcode, [new MOperand.LabelRef(target)],
              branch.Effect, branch.Condition, branch.Clobbers);
        for (var s = 0; s < block.Successors.Count; ++s)
          if (block.Successors[s] == detour.Label)
            block.Successors[s] = target;
      }

      var previous = function.Blocks[b - 1];
      if (FallsThrough(previous))
        previous.Instructions.Add(jump);
      function.Blocks.RemoveAt(b);
      --b;
      ++made;
    }
    return made;
  }

  /// <summary>
  /// A block that ends in <c>JMP T</c>, where nothing falls into <c>T</c>, gets <c>T</c> - and the run
  /// of blocks <c>T</c> falls through - moved to directly after it, so the jump becomes the fallthrough
  /// <see cref="StraightenBranches"/> then deletes.
  ///
  /// <para>
  /// A rotated loop is the shape it is for: the entry jumps over the loop's exit block into the body,
  /// and the latch jumps back to the exit. With the body placed after the entry, both jumps go - the
  /// entry falls into the body and the latch falls into the exit. The entry block stays first, and a
  /// run is only moved as a whole: splitting a fallthrough chain would change where control goes.
  /// </para>
  /// </summary>
  public static int PlaceJumpTargets(X86MachineFunction function) {
    var made = 0;
    for (var b = 0; b < function.Blocks.Count; ++b) {
      var from = function.Blocks[b];
      if (from.Instructions is not [.., { Opcode: MOpcode.Jmp, Condition: null } jump]
          || jump.Operands is not [MOperand.LabelRef { Name: var target }])
        continue;
      var at = function.Blocks.FindIndex(block => block.Label == target);
      if (at <= 0 || at == b + 1 || FallsThrough(function.Blocks[at - 1]))
        continue;
      var end = at;
      while (end < function.Blocks.Count - 1 && FallsThrough(function.Blocks[end]))
        ++end;
      if (FallsThrough(function.Blocks[end]) || (b >= at && b <= end))
        continue;                                // the run ends by falling off the function, or holds the jump itself
      var run = function.Blocks.GetRange(at, end - at + 1);
      function.Blocks.RemoveRange(at, run.Count);
      var insertAt = function.Blocks.IndexOf(from) + 1;
      function.Blocks.InsertRange(insertAt, run);
      ++made;
    }
    return made;
  }

  /// <summary>Whether control reaches the next block in layout order by running off the end of this one.</summary>
  private static bool FallsThrough(MBlock block)
    => block.Instructions.Count == 0
       || block.Instructions[^1] is not ({ Opcode: MOpcode.Jmp, Condition: null }
         or { Opcode: MOpcode.Ret or MOpcode.JmpIndirect or MOpcode.JmpIndexed });

  /// <summary>Whether any operand other than a plain branch target mentions the label.</summary>
  private static bool NamedOutsideBranches(X86MachineFunction function, string label) {
    foreach (var instr in function.AllInstructions) {
      var isBranch = instr.Opcode is MOpcode.Jmp or MOpcode.Jcc;
      foreach (var operand in instr.Operands)
        switch (operand) {
          case MOperand.LabelRef reference when reference.Name == label && !isBranch:
          case MOperand.BlockOffset offset when offset.Block == label:
          case MOperand.BlockAddressTable table when table.Blocks.Contains(label):
            return true;
        }
    }
    return false;
  }

  /// <summary>The condition that is taken exactly where this one is not - the encoding's low bit.</summary>
  private static Asm.Condition Inverted(Asm.Condition condition) => (Asm.Condition)((byte)condition ^ 1);
}
