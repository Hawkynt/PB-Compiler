namespace PowerBasic.Compiler.Backend;

/// <summary>
/// O0348/O0349 — conservative x87 expression-stack scheduling and value retention after instruction
/// selection. This pass keeps source evaluation order: it stackifies private TBYTE temporaries and
/// retains a completed left subtree while the right subtree executes when an explicit depth proof says
/// the eight-register x87 stack cannot overflow.
///
/// <para>
/// Selection deliberately begins from the simple form where every floating SSA result is materialized
/// in its own TBYTE frame slot. A TBYTE spill/reload preserves the x87 value, so removing a private
/// <c>FSTP tmp / FLD tmp</c> pair changes only its location. A SINGLE/DOUBLE store is a semantic
/// rounding point and is never removed.
/// </para>
/// <para>
/// Calls, inline assembly, terminators, clobbers and unmodelled x87 operations stop retention. The
/// pass therefore does not guess stack effects, synthesize FXCH-based reordering, or cross a required
/// precision boundary merely to save a temporary.
/// </para>
/// </summary>
public static class X87StackOptimizer {

  private const int _X87_DEPTH = 8;

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
  /// <c>left; FSTP A; right; FSTP B; FLD A; FLD B; FopP</c> by retaining A below the complete right
  /// subtree and B on top. The root arithmetic remains in place and sees ST(1)=left, ST(0)=right.
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
      if (leftWriter < 0 || !FitsWithOneResident(block, leftWriter + 1, root - 3))
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
      or MOpcode.Fsqrt or MOpcode.Fsin or MOpcode.Fcos => 0,
    _ => null,
  };

  private static bool IsPoppingBinary(MOpcode opcode)
    => opcode is MOpcode.Faddp or MOpcode.Fsubp or MOpcode.Fmulp or MOpcode.Fdivp;

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
