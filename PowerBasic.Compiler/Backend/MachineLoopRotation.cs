namespace PowerBasic.Compiler.Backend;

/// <summary>
/// Rotates a canonical pre-tested machine loop under the SPEED objective. The header remains the
/// zero-trip entry guard; the latch repeats its CMP/Jcc/Jmp suffix and sends the taken loop edge
/// straight to the body, removing the unconditional trip back through the header.
/// </summary>
public static class MachineLoopRotation {

  /// <summary>Rotates every conservative single-latch match and returns the number changed.</summary>
  public static int Run(MFunction function) {
    ArgumentNullException.ThrowIfNull(function);
    if (function.AllInstructions.Any(instruction => instruction.Opcode == MOpcode.InlineAsm))
      return 0;
    var changed = 0;
    foreach (var header in function.Blocks.ToList()) {
      if (header.Instructions is not [var compare, var conditional, var fallback]
          || compare.Opcode != MOpcode.Cmp || conditional.Opcode != MOpcode.Jcc
          || fallback.Opcode != MOpcode.Jmp
          || header.Successors.Count != 2
          || conditional.Operands is not [MOperand.LabelRef conditionalTarget]
          || fallback.Operands is not [MOperand.LabelRef fallbackTarget]
          || conditional.Condition is null || conditionalTarget.Name == fallbackTarget.Name
          || !header.Successors.Contains(conditionalTarget.Name, StringComparer.Ordinal)
          || !header.Successors.Contains(fallbackTarget.Name, StringComparer.Ordinal))
        continue;

      var predecessors = function.Blocks
        .Where(block => block.Successors.Contains(header.Label, StringComparer.Ordinal))
        .ToList();
      var latches = predecessors.Where(block => EndsWithJumpTo(block, header.Label)
          && (Reaches(function, conditionalTarget.Name, block.Label, header.Label)
            || Reaches(function, fallbackTarget.Name, block.Label, header.Label)))
        .ToList();
      if (predecessors.Count != 2 || latches.Count != 1)
        continue;

      var latch = latches[0];
      if (latch.Successors.Count != 1 || latch.Successors[0] != header.Label)
        continue;

      var reachesConditional = Reaches(function, conditionalTarget.Name, latch.Label, header.Label);
      var reachesFallback = Reaches(function, fallbackTarget.Name, latch.Label, header.Label);
      if (reachesConditional == reachesFallback)
        continue;

      latch.Instructions.RemoveAt(latch.Instructions.Count - 1);
      latch.Instructions.Add(Clone(compare));
      latch.Instructions.Add(Clone(conditional));
      latch.Instructions.Add(Clone(fallback));
      latch.Successors.Clear();
      latch.Successors.AddRange(header.Successors);
      ++changed;
    }
    return changed;
  }

  private static bool EndsWithJumpTo(MBlock block, string target)
    => block.Instructions.LastOrDefault() is { Opcode: MOpcode.Jmp, Operands: [MOperand.LabelRef label] }
       && label.Name == target;

  /// <summary>Whether <paramref name="target"/> is reachable without passing through the loop header.</summary>
  private static bool Reaches(MFunction function, string start, string target, string header) {
    var blocks = function.Blocks.ToDictionary(block => block.Label, StringComparer.Ordinal);
    var seen = new HashSet<string>(StringComparer.Ordinal);
    var pending = new Queue<string>([start]);
    while (pending.TryDequeue(out var label)) {
      if (label == target)
        return true;
      if (label == header || !seen.Add(label) || !blocks.TryGetValue(label, out var block))
        continue;
      foreach (var successor in block.Successors)
        pending.Enqueue(successor);
    }
    return false;
  }

  private static MInstr Clone(MInstr instruction)
    => new(instruction.Opcode, instruction.Operands, instruction.Effect, instruction.Condition, instruction.Clobbers);
}
