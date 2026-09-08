using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Backend;

/// <summary>
/// O0348/O0349 — conservative x87 expression-stack scheduling and value retention after instruction
/// selection. The pass keeps source evaluation order, removes private TBYTE spill/reload pairs, and
/// may keep one or more non-overlapping private values resident across basic-block edges when every
/// path admits the same x87 stack state.
///
/// <para>
/// Selection deliberately begins from the simple form where every floating SSA result is materialized
/// in its own TBYTE frame slot. A TBYTE spill/reload preserves the x87 value, so removing a private
/// <c>FSTP tmp / FLD tmp</c> pair changes only its location. A SINGLE/DOUBLE store is a semantic
/// rounding point and is never removed.
/// </para>
/// <para>
/// Multi-use retention removes the defining TBYTE store and turns each later reload into the existing
/// assembler's <c>FLD ST(i)</c> form. The resident value stays below ordinary transient expression
/// values, block edges are crossed only with those transients drained, and the final region exits pop
/// the resident copy. Calls, inline assembly, clobbers, inconsistent joins and unmodelled x87 effects
/// veto a candidate rather than guessing.
/// </para>
/// </summary>
public static class X87StackOptimizer {

  private const int _X87_DEPTH = 8;

  private static readonly AsmRegisterEffect _NO_REGISTER_EFFECT = new(
    new HashSet<Reg>(), new HashSet<Reg>(), new HashSet<Reg>(),
    ReadsFlags: false, WritesFlags: false, IsOpaque: false);

  private readonly record struct X87Effect(int Required, int Delta);

  private sealed record Candidate(
    MOperand.StackSlot Slot,
    MBlock WriterBlock,
    MInstr Writer,
    IReadOnlyList<(MBlock Block, MInstr Instruction)> Readers);

  private sealed record ResidencyPlan(
    HashSet<MBlock> Blocks,
    Dictionary<MInstr, MInstr?> Rewrites,
    HashSet<MBlock> FlushBlocks);

  /// <summary>Stackifies eligible x87 temporaries; returns the number of spill/reload groups removed.</summary>
  public static int Run(MFunction function) {
    ArgumentNullException.ThrowIfNull(function);

    var total = RetainMultiUseValues(function, out var residentBlocks);
    for (var round = 0; round < 16; ++round) {
      var uses = SlotUses(function);
      var made = 0;
      foreach (var block in function.Blocks) {
        if (residentBlocks.Contains(block))
          continue;
        made += RetainTreeValues(block, uses);
        made += RemoveImmediateReloads(block, uses);
      }
      total += made;
      if (made == 0)
        break;
    }
    return total;
  }

  private static int RetainMultiUseValues(MFunction function, out HashSet<MBlock> residentBlocks) {
    residentBlocks = [];
    if (function.Blocks.Count == 0)
      return 0;

    var graph = FlowGraph.Build(function);
    var made = 0;
    foreach (var candidate in MultiUseCandidates(function).OrderByDescending(c => c.Readers.Count)) {
      if (!TryPlanResidency(graph, candidate, residentBlocks, out var plan))
        continue;

      Apply(plan);
      residentBlocks.UnionWith(plan.Blocks);
      ++made;
    }
    return made;
  }

  private static List<Candidate> MultiUseCandidates(MFunction function) {
    var refs = new Dictionary<int, List<(MBlock Block, MInstr Instruction, MOperand.StackSlot Slot)>>();
    foreach (var block in function.Blocks)
      foreach (var instruction in block.Instructions)
        foreach (var slot in instruction.Operands.OfType<MOperand.StackSlot>())
          (refs.TryGetValue(slot.Index, out var found) ? found : refs[slot.Index] = []).Add((block, instruction, slot));

    var result = new List<Candidate>();
    foreach (var accesses in refs.Values) {
      if (accesses.Any(access => access.Slot is not { Size: MRegSize.Tbyte, Disp: 0 }))
        continue;

      var writers = accesses.Where(access => IsStoreOf(access.Instruction, access.Slot)).ToList();
      var readers = accesses.Where(access => IsLoadOf(access.Instruction, access.Slot)).ToList();
      if (writers.Count != 1 || readers.Count == 0 || writers.Count + readers.Count != accesses.Count)
        continue;

      var writer = writers[0];
      if (readers.Count == 1 && ReferenceEquals(readers[0].Block, writer.Block))
        continue; // single-block single-use shapes are cheaper in the existing local stackifier
      result.Add(new Candidate(writer.Slot, writer.Block, writer.Instruction,
        readers.Select(reader => (reader.Block, reader.Instruction)).ToList()));
    }
    return result;
  }

  private static bool TryPlanResidency(FlowGraph graph, Candidate candidate, HashSet<MBlock> occupied,
      out ResidencyPlan plan) {
    plan = null!;
    var writerIndex = candidate.WriterBlock.Instructions.IndexOf(candidate.Writer);
    if (writerIndex < 0 || !graph.Reachable.Contains(candidate.WriterBlock))
      return false;
    if (candidate.Readers.Any(reader => !graph.Reachable.Contains(reader.Block)
        || !graph.Dominates(candidate.WriterBlock, reader.Block)
        || ReferenceEquals(reader.Block, candidate.WriterBlock)
          && reader.Block.Instructions.IndexOf(reader.Instruction) < writerIndex))
      return false;

    var region = RegionBetween(graph, candidate.WriterBlock, candidate.Readers.Select(reader => reader.Block));
    if (!CloseRegion(graph, candidate.WriterBlock, region) || region.Overlaps(occupied))
      return false;

    var rewrites = new Dictionary<MInstr, MInstr?>(ReferenceEqualityComparer.Instance) {
      [candidate.Writer] = null,
    };
    var flushBlocks = new HashSet<MBlock>();
    if (!WriterStartsFromEmpty(candidate, writerIndex))
      return false;
    foreach (var block in region)
      if (!PlanActiveBlock(graph, candidate, block, region, writerIndex, rewrites, flushBlocks))
        return false;

    plan = new ResidencyPlan(region, rewrites, flushBlocks);
    return true;
  }

  private static HashSet<MBlock> RegionBetween(FlowGraph graph, MBlock writer, IEnumerable<MBlock> readers) {
    var forward = graph.ForwardFrom(writer);
    var backward = graph.BackwardFrom(readers);
    forward.IntersectWith(backward);
    forward.Add(writer);
    return forward;
  }

  private static bool CloseRegion(FlowGraph graph, MBlock writer, HashSet<MBlock> region) {
    bool changed;
    do {
      changed = false;
      foreach (var block in region.ToArray()) {
        if (!graph.Dominates(writer, block))
          return false;
        if (block != writer)
          foreach (var predecessor in graph.Predecessors[block].Where(graph.Reachable.Contains))
            if (!region.Contains(predecessor)) {
              if (!graph.Dominates(writer, predecessor))
                return false;
              region.Add(predecessor);
              changed = true;
            }

        var successors = graph.Successors[block];
        if (!successors.Any(region.Contains) || successors.All(region.Contains))
          continue;
        foreach (var successor in successors) {
          if (!graph.Dominates(writer, successor))
            return false;
          changed |= region.Add(successor);
        }
      }
      if (graph.Predecessors[writer].Any(region.Contains))
        return false;
    } while (changed);
    return true;
  }

  private static bool WriterStartsFromEmpty(Candidate candidate, int writerIndex) {
    var depth = 0;
    for (var i = 0; i < writerIndex; ++i) {
      var instruction = candidate.WriterBlock.Instructions[i];
      if (instruction.Opcode is MOpcode.Call or MOpcode.InlineAsm)
        return false;
      if (!MOpcodes.UsesX87(instruction.Opcode))
        continue;
      if (StackEffect(instruction) is not { } effect || effect.Required > depth)
        return false;
      depth += effect.Delta;
      if (depth is < 0 or > _X87_DEPTH)
        return false;
    }
    return depth == 1;
  }

  private static bool PlanActiveBlock(FlowGraph graph, Candidate candidate, MBlock block, HashSet<MBlock> region,
      int writerIndex, Dictionary<MInstr, MInstr?> rewrites, HashSet<MBlock> flushBlocks) {
    var depth = 0;
    var start = ReferenceEquals(block, candidate.WriterBlock) ? writerIndex + 1 : 0;
    for (var i = start; i < block.Instructions.Count; ++i) {
      var instruction = block.Instructions[i];
      if (IsLoadOf(instruction, candidate.Slot)) {
        if (depth + 2 > _X87_DEPTH)
          return false;
        rewrites[instruction] = DuplicateResident(depth);
        ++depth;
        continue;
      }
      if (instruction.Opcode is MOpcode.Call or MOpcode.InlineAsm || instruction.Clobbers.Count > 0)
        return false;
      if (!MOpcodes.UsesX87(instruction.Opcode))
        continue;
      if (StackEffect(instruction) is not { } effect || effect.Required > depth)
        return false;
      depth += effect.Delta;
      if (depth is < 0 or >= _X87_DEPTH)
        return false;
    }
    if (depth != 0)
      return false;

    if (!graph.Successors[block].Any(region.Contains))
      flushBlocks.Add(block);
    return true;
  }

  private static MInstr DuplicateResident(int transientDepth) {
    var text = $"FLD ST({transientDepth})";
    return new MInstr(MOpcode.InlineAsm,
      [new MOperand.InlineAsmText(text, [], _NO_REGISTER_EFFECT)], MInstrEffect.None);
  }

  private static void Apply(ResidencyPlan plan) {
    foreach (var block in plan.Blocks) {
      var old = block.Instructions.ToArray();
      block.Instructions.Clear();
      var flushed = false;
      foreach (var instruction in old) {
        if (!flushed && plan.FlushBlocks.Contains(block) && instruction.IsTerminator) {
          block.Instructions.Add(Op(MOpcode.FstpSt0));
          flushed = true;
        }
        if (!plan.Rewrites.TryGetValue(instruction, out var replacement))
          block.Instructions.Add(instruction);
        else if (replacement is not null)
          block.Instructions.Add(replacement);
      }
      if (plan.FlushBlocks.Contains(block) && !flushed)
        block.Instructions.Add(Op(MOpcode.FstpSt0));
    }
  }

  private static MInstr Op(MOpcode opcode) => new(opcode, [], MInstrEffect.None);

  private static Dictionary<int, int> SlotUses(MFunction function) {
    var uses = new Dictionary<int, int>();
    foreach (var instruction in function.AllInstructions)
      foreach (var operand in instruction.Operands)
        if (operand is MOperand.StackSlot slot)
          uses[slot.Index] = uses.GetValueOrDefault(slot.Index) + 1;
    return uses;
  }

  private static bool SingleUse(MOperand.StackSlot slot, IReadOnlyDictionary<int, int> uses)
    => slot is { Size: MRegSize.Tbyte, Disp: 0 } && uses.GetValueOrDefault(slot.Index) == 2;

  private static int RemoveImmediateReloads(MBlock block, IReadOnlyDictionary<int, int> uses) {
    var made = 0;
    for (var i = 0; i + 1 < block.Instructions.Count; ++i) {
      if (!TryStore(block.Instructions[i], out var stored) || !SingleUse(stored, uses)
          || !TryLoad(block.Instructions[i + 1], out var loaded) || !stored.Equals(loaded))
        continue;
      block.Instructions.RemoveRange(i, 2);
      --i;
      ++made;
    }
    return made;
  }

  /// <summary>
  /// Rewrites the selector shape
  /// <c>left; FSTP A; right; FSTP B; FLD A; FLD B; FopP</c> by retaining A below the complete right
  /// subtree and B on top. The root arithmetic remains in place and sees ST(1)=left, ST(0)=right.
  /// </summary>
  private static int RetainTreeValues(MBlock block, IReadOnlyDictionary<int, int> uses) {
    var made = 0;
    for (var root = 3; root < block.Instructions.Count; ++root) {
      if (!IsPoppingBinary(block.Instructions[root].Opcode)
          || !TryStore(block.Instructions[root - 3], out var right) || !SingleUse(right, uses)
          || !TryLoad(block.Instructions[root - 2], out var loadedLeft)
          || !TryLoad(block.Instructions[root - 1], out var loadedRight)
          || !right.Equals(loadedRight) || !SingleUse(loadedLeft, uses))
        continue;

      var leftWriter = FindWriter(block, loadedLeft, root - 4);
      if (leftWriter < 0 || !FitsWithOneResident(block, leftWriter + 1, root - 3))
        continue;

      block.Instructions.RemoveAt(root - 1);
      block.Instructions.RemoveAt(root - 2);
      block.Instructions.RemoveAt(root - 3);
      block.Instructions.RemoveAt(leftWriter);
      root -= 4;
      ++made;
    }
    return made;
  }

  private static int FindWriter(MBlock block, MOperand.StackSlot slot, int from) {
    for (var i = from; i >= 0; --i) {
      if (TryStore(block.Instructions[i], out var stored) && stored.Equals(slot))
        return i;
      if (block.Instructions[i].Opcode is MOpcode.Call or MOpcode.InlineAsm || block.Instructions[i].IsTerminator)
        return -1;
      if (MOpcodes.UsesX87(block.Instructions[i].Opcode) && StackEffect(block.Instructions[i]) is null)
        return -1;
    }
    return -1;
  }

  private static bool FitsWithOneResident(MBlock block, int from, int closingStore) {
    var depth = 0;
    var maximum = 0;
    for (var i = from; i < closingStore; ++i) {
      var instruction = block.Instructions[i];
      if (instruction.Opcode is MOpcode.Call or MOpcode.InlineAsm || instruction.IsTerminator
          || instruction.Clobbers.Count > 0)
        return false;
      if (!MOpcodes.UsesX87(instruction.Opcode))
        continue;
      if (StackEffect(instruction) is not { } effect || effect.Required > depth)
        return false;
      depth += effect.Delta;
      maximum = Math.Max(maximum, depth);
      if (maximum + 1 > _X87_DEPTH)
        return false;
    }
    return depth == 1;
  }

  /// <summary>
  /// Stack effects that are safe with an unrelated resident value below the transient expression.
  /// Narrow stores remain deliberately unmodelled because they are semantic rounding boundaries.
  /// </summary>
  private static X87Effect? StackEffect(MInstr instruction) => instruction.Opcode switch {
    MOpcode.Fld or MOpcode.Fild
      or MOpcode.Fld1 or MOpcode.Fldln2 or MOpcode.Fldlg2 or MOpcode.Fldl2e or MOpcode.Fldl2t
      => new(0, +1),
    MOpcode.Fstp when instruction.Operands is [MOperand.StackSlot { Size: MRegSize.Tbyte }]
      => new(1, -1),
    MOpcode.Faddp or MOpcode.Fsubp or MOpcode.Fmulp or MOpcode.Fdivp
      => new(2, -1),
    MOpcode.Fadd or MOpcode.Fsub or MOpcode.Fmul or MOpcode.Fdiv
      or MOpcode.Fiadd or MOpcode.Fisub or MOpcode.Fimul or MOpcode.Fidiv
      or MOpcode.Fsqrt or MOpcode.Fsin or MOpcode.Fcos
      => new(1, 0),
    _ => null,
  };

  private static bool IsPoppingBinary(MOpcode opcode)
    => opcode is MOpcode.Faddp or MOpcode.Fsubp or MOpcode.Fmulp or MOpcode.Fdivp;

  private static bool IsStoreOf(MInstr instruction, MOperand.StackSlot slot)
    => TryStore(instruction, out var stored) && stored.Equals(slot);

  private static bool IsLoadOf(MInstr instruction, MOperand.StackSlot slot)
    => TryLoad(instruction, out var loaded) && loaded.Equals(slot);

  private static bool TryStore(MInstr instruction, out MOperand.StackSlot slot) {
    if (instruction is { Opcode: MOpcode.Fstp, Operands: [MOperand.StackSlot candidate] }
        && candidate is { Size: MRegSize.Tbyte, Disp: 0 }) {
      slot = candidate;
      return true;
    }
    slot = null!;
    return false;
  }

  private static bool TryLoad(MInstr instruction, out MOperand.StackSlot slot) {
    if (instruction is { Opcode: MOpcode.Fld, Operands: [MOperand.StackSlot candidate] }
        && candidate is { Size: MRegSize.Tbyte, Disp: 0 }) {
      slot = candidate;
      return true;
    }
    slot = null!;
    return false;
  }

  private sealed class FlowGraph {

    private readonly Dictionary<MBlock, HashSet<MBlock>> _dominators;

    public IReadOnlyDictionary<MBlock, List<MBlock>> Successors { get; }
    public IReadOnlyDictionary<MBlock, List<MBlock>> Predecessors { get; }
    public HashSet<MBlock> Reachable { get; }

    private FlowGraph(Dictionary<MBlock, List<MBlock>> successors,
        Dictionary<MBlock, List<MBlock>> predecessors, HashSet<MBlock> reachable,
        Dictionary<MBlock, HashSet<MBlock>> dominators) {
      this.Successors = successors;
      this.Predecessors = predecessors;
      this.Reachable = reachable;
      this._dominators = dominators;
    }

    public static FlowGraph Build(MFunction function) {
      var byLabel = function.Blocks.ToDictionary(block => block.Label, StringComparer.Ordinal);
      var successors = function.Blocks.ToDictionary(block => block, _ => new List<MBlock>());
      var predecessors = function.Blocks.ToDictionary(block => block, _ => new List<MBlock>());
      foreach (var block in function.Blocks)
        foreach (var label in block.SuccessorsWithAsmJumps().Distinct(StringComparer.Ordinal))
          if (byLabel.TryGetValue(label, out var successor)) {
            successors[block].Add(successor);
            predecessors[successor].Add(block);
          }

      var reachable = Traverse([function.Blocks[0]], successors);
      var dominators = Dominators(function.Blocks[0], reachable, predecessors);
      return new FlowGraph(successors, predecessors, reachable, dominators);
    }

    public bool Dominates(MBlock dominator, MBlock block)
      => this._dominators.TryGetValue(block, out var set) && set.Contains(dominator);

    public HashSet<MBlock> ForwardFrom(MBlock block) => Traverse([block], this.Successors);

    public HashSet<MBlock> BackwardFrom(IEnumerable<MBlock> blocks) => Traverse(blocks, this.Predecessors);

    private static HashSet<MBlock> Traverse(IEnumerable<MBlock> starts,
        IReadOnlyDictionary<MBlock, List<MBlock>> edges) {
      var result = new HashSet<MBlock>();
      var work = new Queue<MBlock>(starts);
      while (work.TryDequeue(out var block)) {
        if (!result.Add(block))
          continue;
        foreach (var next in edges[block])
          work.Enqueue(next);
      }
      return result;
    }

    private static Dictionary<MBlock, HashSet<MBlock>> Dominators(MBlock entry, HashSet<MBlock> reachable,
        IReadOnlyDictionary<MBlock, List<MBlock>> predecessors) {
      var result = new Dictionary<MBlock, HashSet<MBlock>>();
      foreach (var block in reachable)
        result[block] = ReferenceEquals(block, entry) ? new HashSet<MBlock> { entry } : new HashSet<MBlock>(reachable);

      bool changed;
      do {
        changed = false;
        foreach (var block in reachable) {
          if (ReferenceEquals(block, entry))
            continue;
          var incoming = predecessors[block].Where(reachable.Contains).ToList();
          if (incoming.Count == 0)
            continue;
          var next = new HashSet<MBlock>(result[incoming[0]]);
          foreach (var predecessor in incoming.Skip(1))
            next.IntersectWith(result[predecessor]);
          next.Add(block);
          if (result[block].SetEquals(next))
            continue;
          result[block] = next;
          changed = true;
        }
      } while (changed);
      return result;
    }
  }
}
