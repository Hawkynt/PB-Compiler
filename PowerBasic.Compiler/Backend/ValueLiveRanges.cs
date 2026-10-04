using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Backend;

/// <summary>
/// The lives of a function's SSA values, for a back end that keeps every value in a frame cell and
/// wants values that are never alive together to share one: the 6502's (O0408) and x86-32/x64's.
/// </summary>
public static class ValueLiveRanges {

  /// <summary>
  /// Each value's life as one span of instruction positions, numbered through the blocks in order: from
  /// where it is written to where it is last read, widened to every block it is live into or out of.
  /// A phi is written at the end of each predecessor, where its incoming value is read. The span is the
  /// hull of the true life, so two values whose spans do not meet are never alive together; a value and
  /// the operands of the instruction that writes it always meet, so a result never overwrites what it is
  /// computed from. A division's partner is written by the division (<see cref="IrDivRem"/>), so its life
  /// starts there.
  /// </summary>
  public static Dictionary<IrInstruction, (int Start, int End)> Of(IrFunction function, IReadOnlyList<IrInstruction> values) {
    var tracked = new HashSet<IrValue>(values, ReferenceEqualityComparer.Instance);
    var position = new Dictionary<IrInstruction, int>(ReferenceEqualityComparer.Instance);
    var first = new Dictionary<IrBasicBlock, int>();
    var last = new Dictionary<IrBasicBlock, int>();
    var counter = 0;
    foreach (var block in function.Blocks) {
      first[block] = counter;
      foreach (var instruction in block.Instructions)
        position[instruction] = counter++;
      last[block] = counter - 1;
    }
    var ranges = new Dictionary<IrInstruction, (int Start, int End)>(ReferenceEqualityComparer.Instance);
    void Cover(IrInstruction value, int at) {
      ranges[value] = ranges.TryGetValue(value, out var range) ? (Math.Min(range.Start, at), Math.Max(range.End, at)) : (at, at);
    }
    // what each block reads before writing it, and what it writes
    var upward = function.Blocks.ToDictionary(block => block, _ => new HashSet<IrInstruction>(ReferenceEqualityComparer.Instance));
    var defined = function.Blocks.ToDictionary(block => block, _ => new HashSet<IrInstruction>(ReferenceEqualityComparer.Instance));
    var incoming = function.Blocks.ToDictionary(block => block, _ => new HashSet<IrInstruction>(ReferenceEqualityComparer.Instance));
    foreach (var block in function.Blocks)
      foreach (var instruction in block.Instructions) {
        if (instruction is IrPhi phi) {
          for (var k = 0; k < phi.Operands.Count; ++k)
            if (phi.Operands[k] is IrInstruction from && tracked.Contains(from)) {
              incoming[phi.IncomingBlocks[k]].Add(from);
              Cover(from, last[phi.IncomingBlocks[k]]);
            }
          if (tracked.Contains(phi)) {
            foreach (var predecessor in phi.IncomingBlocks)
              Cover(phi, last[predecessor]);
            Cover(phi, first[block]);
          }
          continue;
        }
        foreach (var operand in instruction.Operands)
          if (operand is IrInstruction used && tracked.Contains(used)) {
            Cover(used, position[instruction]);
            if (!defined[block].Contains(used))
              upward[block].Add(used);
          }
        if (tracked.Contains(instruction)) {
          Cover(instruction, position[instruction]);
          defined[block].Add(instruction);
        }
      }
    // live in and out of each block, to a fixed point
    var liveIn = function.Blocks.ToDictionary(block => block, _ => new HashSet<IrInstruction>(ReferenceEqualityComparer.Instance));
    var liveOut = function.Blocks.ToDictionary(block => block, _ => new HashSet<IrInstruction>(ReferenceEqualityComparer.Instance));
    for (var changed = true; changed;) {
      changed = false;
      foreach (var block in function.Blocks.AsEnumerable().Reverse()) {
        var output = new HashSet<IrInstruction>(incoming[block], ReferenceEqualityComparer.Instance);
        foreach (var successor in block.Successors)
          foreach (var value in liveIn[successor])
            if (value is not IrPhi phi || phi.Parent != successor)
              output.Add(value);
        var input = new HashSet<IrInstruction>(upward[block], ReferenceEqualityComparer.Instance);
        foreach (var value in output)
          if (!defined[block].Contains(value))
            input.Add(value);
        foreach (var phi in block.Phis)
          if (tracked.Contains(phi))
            input.Add(phi);
        if (!output.SetEquals(liveOut[block]) || !input.SetEquals(liveIn[block])) {
          (liveOut[block], liveIn[block], changed) = (output, input, true);
        }
      }
    }
    foreach (var block in function.Blocks) {
      foreach (var value in liveIn[block])
        Cover(value, first[block]);
      foreach (var value in liveOut[block])
        Cover(value, last[block]);
    }
    foreach (var value in values) {
      if (!ranges.ContainsKey(value))
        Cover(value, position[value]);
      if (value is IrBinary division && IrDivRem.PartnerOf(division) is { } partner && ranges.ContainsKey(partner)) {
        var start = Math.Min(ranges[division].Start, ranges[partner].Start);
        ranges[division] = (start, ranges[division].End);
        ranges[partner] = (start, ranges[partner].End);
      }
    }
    return ranges;
  }
}
