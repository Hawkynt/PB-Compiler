using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Backend;

/// <summary>
/// Which registers belong to the inline assembly rather than to the allocator, and where.
///
/// <para>
/// <b>The contract.</b> A <c>!</c> statement is not an island: PowerBASIC compiles one statement at a
/// time and keeps no BASIC value in a register between statements, so a register an <c>!</c> statement
/// loads is still there when the next <c>!</c> statement reads it - which is how
/// <c>!MOV CX,5 / n = n + 1 / !DEC CX</c> counts five times rather than once. The back end must
/// therefore treat a register as the assembly's, and refuse to allocate a value into it, at every
/// point that is <i>reachable from</i> an <c>!</c> statement using that register and <i>can reach</i>
/// an <c>!</c> statement using it again. Outside that span - before the first such statement, after
/// the last, on a path that never reaches one - the register is the allocator's as usual.
/// </para>
///
/// <para>
/// It is a reachability question and not a range of line numbers because a loop puts code between two
/// executions of the same statement: the increment of a <c>FOR</c> whose body ends in <c>!DEC CX</c>
/// runs between one <c>DEC</c> and the next while sitting <i>after</i> it in the instruction stream.
/// The two directions are computed over the CFG for exactly that reason.
/// </para>
///
/// <para>
/// <b>What it does not promise.</b> Registers a BASIC statement destroys by a FIXED convention - a
/// runtime call's argument and result registers, DX:AX around a divide, CL for a variable shift - are
/// not the allocator's choice and are not reserved. The direct emitter destroys the same registers in
/// the same places (a PRINT goes through AX in both), so this matches its behaviour rather than
/// exceeding it: assembly may not assume a register across a BASIC statement that architecturally
/// needs it. The other known gap is an <c>INT</c> whose OUTPUT register the text never names anywhere
/// - <c>INT 21h</c> answering in BX in a function that never writes BX - since a register is tracked
/// only from the point some statement names it.
/// </para>
///
/// <para>
/// In the other direction this is deliberately STRONGER than the direct emitter, which holds nothing
/// on purpose and merely happens to leave a register alone. <c>!MOV DX,22 / n = n * n + 1 / !MOV
/// s,DX</c> answers 22 here and 0 there, because the direct emitter's multiply goes through DX. Being
/// right where the older emitter is lucky is the intended direction of that difference, but it IS a
/// difference, and a corpus program written against the luck would show up as a differential
/// disagreement rather than as a failure here.
/// </para>
/// </summary>
public static class InlineAsmReservation {

  /// <summary>
  /// The registers reserved at each instruction index, in the same global numbering
  /// <see cref="LivenessAnalysis"/> uses, so an interval spanning an index sees them exactly as it
  /// sees a CALL's clobbers. Empty for a function with no inline assembly, which is nearly all of them.
  /// </summary>
  public static IReadOnlyDictionary<int, IReadOnlyList<Reg>> Compute(MFunction function) {
    ArgumentNullException.ThrowIfNull(function);
    var blocks = function.Blocks;
    var count = 0;
    var blockOf = new List<(int Start, int End)>(blocks.Count);
    foreach (var block in blocks) {
      blockOf.Add((count, count + block.Instructions.Count));
      count += block.Instructions.Count;
    }

    // which registers each inline-asm statement uses, by instruction index
    var uses = new Dictionary<int, IReadOnlyCollection<Reg>>();
    var mentioned = new HashSet<Reg>();
    var index = 0;
    foreach (var block in blocks)
      foreach (var instr in block.Instructions) {
        if (instr.Opcode == MOpcode.InlineAsm && instr.Operands.Count > 0
            && instr.Operands[0] is MOperand.InlineAsmText text) {
          var registers = TextAssembler.RegistersUsed(text.Text);
          uses[index] = registers;
          mentioned.UnionWith(registers);
        }
        ++index;
      }

    var result = new Dictionary<int, List<Reg>>();
    if (mentioned.Count > 0) {
      var successors = Successors(function);
      var predecessors = Invert(successors, blocks.Count);
      foreach (var register in mentioned) {
        var carries = new bool[blocks.Count];   // does some statement in this block use it?
        for (var b = 0; b < blocks.Count; ++b)
          for (var i = blockOf[b].Start; i < blockOf[b].End; ++i)
            if (uses.TryGetValue(i, out var registers) && registers.Contains(register))
              carries[b] = true;

        // forward along successors: a use somewhere before this block. Backward along predecessors: a
        // use somewhere after it. A register is only the assembly's where BOTH hold.
        var reachedFrom = Reachable(blocks.Count, carries, successors);
        var reaches = Reachable(blocks.Count, carries, predecessors);

        for (var b = 0; b < blocks.Count; ++b) {
          var (start, end) = blockOf[b];
          // walking the block twice gives each instruction the two halves of the answer: what has
          // already used the register on the way in, and what still will on the way out
          var after = new bool[end - start];
          var seen = reachedFrom[b];
          for (var i = start; i < end; ++i) {
            after[i - start] = seen;
            if (uses.TryGetValue(i, out var registers) && registers.Contains(register))
              seen = true;
          }

          var pending = reaches[b];
          for (var i = end - 1; i >= start; --i) {
            if (after[i - start] && pending)
              (result.TryGetValue(i, out var at) ? at : result[i] = []).Add(register);
            if (uses.TryGetValue(i, out var registers) && registers.Contains(register))
              pending = true;
          }
        }
      }
    }

    return result.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<Reg>)pair.Value);
  }

  /// <summary>Which blocks can reach a block that carries a use, following <paramref name="edges"/>.</summary>
  private static bool[] Reachable(int count, bool[] carries, List<int>[] edges) {
    var reached = new bool[count];
    var worklist = new Stack<int>();
    for (var b = 0; b < count; ++b)
      if (carries[b])
        foreach (var next in edges[b])
          if (!reached[next]) {
            reached[next] = true;
            worklist.Push(next);
          }

    while (worklist.Count > 0)
      foreach (var next in edges[worklist.Pop()])
        if (!reached[next]) {
          reached[next] = true;
          worklist.Push(next);
        }

    return reached;
  }

  private static List<int>[] Successors(MFunction function) {
    var labels = new Dictionary<string, int>(StringComparer.Ordinal);
    for (var b = 0; b < function.Blocks.Count; ++b)
      labels[function.Blocks[b].Label] = b;

    var result = new List<int>[function.Blocks.Count];
    for (var b = 0; b < function.Blocks.Count; ++b) {
      result[b] = [];
      foreach (var successor in function.Blocks[b].Successors)
        if (labels.TryGetValue(successor, out var target))
          result[b].Add(target);
    }
    return result;
  }

  private static List<int>[] Invert(List<int>[] edges, int count) {
    var result = new List<int>[count];
    for (var b = 0; b < count; ++b)
      result[b] = [];
    for (var b = 0; b < count; ++b)
      foreach (var target in edges[b])
        result[target].Add(b);
    return result;
  }
}
