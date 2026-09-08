namespace PowerBasic.Compiler.Backend;

/// <summary>
/// O0348/O0349 — conservative x87 expression-stack scheduling and value retention after instruction
/// selection. Private TBYTE temporaries are stackified whenever the eight-register depth can be
/// proven safe. For adjacent pure x87 subtrees, O0348 also chooses the lower-pressure evaluation order
/// and inserts FXCH when a non-commutative root needs the original operand order restored.
///
/// <para>
/// Selection deliberately begins from the simple form where every floating SSA result is materialized
/// in its own TBYTE frame slot. A TBYTE spill/reload preserves the x87 value, so removing a private
/// <c>FSTP tmp / FLD tmp</c> pair changes only its location. A SINGLE/DOUBLE store is a semantic
/// rounding point and is never removed.
/// </para>
/// <para>
/// Reordering is deliberately narrower than retention: both subtrees must be contiguous, fully-modelled
/// x87 code with no stores, calls, inline assembly, terminators or physical clobbers. That proves moving
/// the right subtree before the left cannot move a side effect. Unknown x87 stack effects remain a
/// hard barrier.
/// </para>
/// </summary>
public static class X87StackOptimizer {

  private const int _X87_DEPTH = 8;

  private readonly record struct Subtree(int Start, int End, int Peak);

  /// <summary>Stackifies eligible x87 temporaries; returns the number of spill/reload groups removed.</summary>
  public static int Run(MFunction function) {
    ArgumentNullException.ThrowIfNull(function);
    var total = 0;
    for (var round = 0; round < 16; ++round) {
      var uses = SlotUses(function);
      var made = 0;
      foreach (var block in function.Blocks) {
        made += RetainTreeValues(block, uses);
        made += RemoveImmediateReloads(block, uses);
      }
      total += made;
      if (made == 0)
        break;
    }
    return total;
  }

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
  /// <c>left; FSTP A; right; FSTP B; FLD A; FLD B; FopP</c>. The normal form retains A below the
  /// right subtree. If both subtrees are pure and evaluating right first strictly lowers the measured
  /// peak depth, their order is exchanged; FSUBP/FDIVP then receive one FXCH so ST(1)=left and
  /// ST(0)=right at the root.
  /// </summary>
  private static int RetainTreeValues(MBlock block, IReadOnlyDictionary<int, int> uses) {
    var made = 0;
    for (var root = 3; root < block.Instructions.Count; ++root) {
      if (!IsPoppingBinary(block.Instructions[root].Opcode))
        continue;
      if (!TryStore(block.Instructions[root - 3], out var right) || !SingleUse(right, uses)
          || !TryLoad(block.Instructions[root - 2], out var loadedLeft)
          || !TryLoad(block.Instructions[root - 1], out var loadedRight)
          || !right.Equals(loadedRight) || !SingleUse(loadedLeft, uses))
        continue;

      var leftWriter = FindWriter(block, loadedLeft, root - 4);
      if (leftWriter < 0)
        continue;

      var keepLeftFirst = FitsWithOneResident(block, leftWriter + 1, root - 3);
      if (TryProfileSubtree(block, leftWriter, out var left)
          && TryProfileSubtree(block, root - 3, out var profiledRight)
          && profiledRight.Start == leftWriter + 1) {
        var leftFirstPeak = Math.Max(left.Peak, 1 + profiledRight.Peak);
        var rightFirstPeak = Math.Max(profiledRight.Peak, 1 + left.Peak);
        if (rightFirstPeak <= _X87_DEPTH && rightFirstPeak < leftFirstPeak) {
          root = ScheduleRightFirst(block, root, left, profiledRight);
          ++made;
          continue;
        }
      }

      if (!keepLeftFirst)
        continue;

      block.Instructions.RemoveAt(root - 1);   // FLD right
      block.Instructions.RemoveAt(root - 2);   // FLD left
      block.Instructions.RemoveAt(root - 3);   // FSTP right
      block.Instructions.RemoveAt(leftWriter); // FSTP left
      root -= 4;
      ++made;
    }
    return made;
  }

  private static int ScheduleRightFirst(MBlock block, int root, Subtree left, Subtree right) {
    var rootInstruction = block.Instructions[root];
    var replacement = new List<MInstr>(
      (right.End - right.Start) + (left.End - left.Start) + (NeedsExchange(rootInstruction.Opcode) ? 2 : 1));
    replacement.AddRange(block.Instructions.GetRange(right.Start, right.End - right.Start));
    replacement.AddRange(block.Instructions.GetRange(left.Start, left.End - left.Start));
    if (NeedsExchange(rootInstruction.Opcode))
      replacement.Add(new MInstr(MOpcode.Fxch, [], MInstrEffect.None));
    replacement.Add(rootInstruction);

    block.Instructions.RemoveRange(left.Start, root - left.Start + 1);
    block.Instructions.InsertRange(left.Start, replacement);
    return left.Start + replacement.Count - 1;
  }

  private static bool TryProfileSubtree(MBlock block, int closingStore, out Subtree subtree) {
    subtree = default;
    var needed = 1;
    var start = -1;

    for (var i = closingStore - 1; i >= 0; --i) {
      var instruction = block.Instructions[i];
      if (!CanReorder(instruction) || StackDelta(instruction) is not { } delta)
        return false;
      needed -= delta;
      if (needed < 0)
        return false;
      if (needed == 0) {
        start = i;
        break;
      }
    }

    if (start < 0)
      return false;

    var depth = 0;
    var maximum = 0;
    for (var i = start; i < closingStore; ++i) {
      var instruction = block.Instructions[i];
      if (!CanReorder(instruction) || StackDelta(instruction) is not { } delta
          || delta < 0 && depth < -delta)
        return false;
      depth += delta;
      maximum = Math.Max(maximum, depth);
      if (maximum > _X87_DEPTH)
        return false;
    }

    if (depth != 1)
      return false;

    subtree = new Subtree(start, closingStore, maximum);
    return true;
  }

  private static bool CanReorder(MInstr instruction)
    => instruction.Opcode is not (MOpcode.Call or MOpcode.InlineAsm)
      && !instruction.IsTerminator
      && instruction.Clobbers.Count == 0
      && !instruction.Effect.WritesMemory
      && MOpcodes.UsesX87(instruction.Opcode);

  private static int FindWriter(MBlock block, MOperand.StackSlot slot, int from) {
    for (var i = from; i >= 0; --i) {
      if (TryStore(block.Instructions[i], out var stored) && stored.Equals(slot))
        return i;
      if (block.Instructions[i].Opcode is MOpcode.Call or MOpcode.InlineAsm || block.Instructions[i].IsTerminator)
        return -1;
      if (MOpcodes.UsesX87(block.Instructions[i].Opcode) && StackDelta(block.Instructions[i]) is null)
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
      if (StackDelta(instruction) is not { } delta || delta < 0 && depth < -delta)
        return false;
      depth += delta;
      maximum = Math.Max(maximum, depth);
      if (maximum + 1 > _X87_DEPTH)
        return false;
    }
    return depth == 1;
  }

  /// <summary>
  /// Stack effects safe with an unrelated resident value below them. Operations with explicit ST(i)
  /// addressing or more complicated pop conventions remain intentionally unmodelled.
  /// </summary>
  private static int? StackDelta(MInstr instruction) => instruction.Opcode switch {
    MOpcode.Fld or MOpcode.Fild
      or MOpcode.Fld1 or MOpcode.Fldln2 or MOpcode.Fldlg2 or MOpcode.Fldl2e or MOpcode.Fldl2t => +1,
    MOpcode.Fstp when instruction.Operands is [MOperand.StackSlot { Size: MRegSize.Tbyte }] => -1,
    MOpcode.Faddp or MOpcode.Fsubp or MOpcode.Fmulp or MOpcode.Fdivp => -1,
    MOpcode.Fadd or MOpcode.Fsub or MOpcode.Fmul or MOpcode.Fdiv
      or MOpcode.Fiadd or MOpcode.Fisub or MOpcode.Fimul or MOpcode.Fidiv
      or MOpcode.Fsqrt or MOpcode.Fsin or MOpcode.Fcos or MOpcode.Fxch => 0,
    _ => null,
  };

  private static bool IsPoppingBinary(MOpcode opcode)
    => opcode is MOpcode.Faddp or MOpcode.Fsubp or MOpcode.Fmulp or MOpcode.Fdivp;

  private static bool NeedsExchange(MOpcode opcode)
    => opcode is MOpcode.Fsubp or MOpcode.Fdivp;

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
}
