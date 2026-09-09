using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Backend;

/// <summary>
/// Target-level combining after instruction selection. These patterns deliberately depend on x86
/// encodings and register roles, so expressing them in the target-independent IR would be the wrong
/// abstraction even when the source expression looked similar.
/// </summary>
public static class MachineCombiner {

  /// <summary>Combines selected x86 instruction windows for the conservative baseline target.</summary>
  public static int Run(MFunction function) => Run(function, SelectionTarget.Baseline);

  /// <summary>Combines selected x86 instruction windows without increasing virtual-register pressure.</summary>
  public static int Run(MFunction function, SelectionTarget target) {
    ArgumentNullException.ThrowIfNull(function);
    var addressValues = AddressConstrainedValues(function);
    var blocksByLabel = function.Blocks.ToDictionary(block => block.Label, StringComparer.Ordinal);
    var (reads, writes) = RegisterCensus(function);
    var changed = 0;
    foreach (var block in function.Blocks) {
      if (target is { Optimize: true, Cpu386OrLater: true }) {
        changed += CombineDwordCopies(function, block, reads, writes);
        changed += CombineDwordConstantStores(block);
      }
      changed += CombineCompareZero(block, blocksByLabel);
      changed += CombineAddressArithmetic(block, addressValues);
    }
    return changed;
  }

  /// <summary>
  /// Rejoins the exact word-pair shape produced by selecting a scalar i32 load immediately consumed by
  /// its store: two word loads followed by the matching two word stores become one native 386 dword
  /// load/store through a fresh dword virtual register. The two source values must have no other reads
  /// or definitions anywhere in the function, so this cannot silently widen an ordinary LONG value
  /// that the rest of the backend still represents as a pair.
  /// </summary>
  private static int CombineDwordCopies(MFunction function, MBlock block,
      IReadOnlyDictionary<int, int> reads, IReadOnlyDictionary<int, int> writes) {
    var changed = 0;
    for (var i = 0; i + 3 < block.Instructions.Count; ++i) {
      var loadLow = block.Instructions[i];
      var loadHigh = block.Instructions[i + 1];
      var storeLow = block.Instructions[i + 2];
      var storeHigh = block.Instructions[i + 3];
      if (!PlainMov(loadLow) || !PlainMov(loadHigh) || !PlainMov(storeLow) || !PlainMov(storeHigh)
          || loadLow.Operands is not [MOperand.Register { Reg: var low }, var sourceLow]
          || loadHigh.Operands is not [MOperand.Register { Reg: var high }, var sourceHigh]
          || storeLow.Operands is not [var targetLow, MOperand.Register { Reg: var storedLow }]
          || storeHigh.Operands is not [var targetHigh, MOperand.Register { Reg: var storedHigh }]
          || !low.IsVirtual || !high.IsVirtual || low.Size != MRegSize.Word || high.Size != MRegSize.Word
          || !storedLow.Equals(low) || !storedHigh.Equals(high)
          || reads.GetValueOrDefault(low.VirtualId) != 1 || writes.GetValueOrDefault(low.VirtualId) != 1
          || reads.GetValueOrDefault(high.VirtualId) != 1 || writes.GetValueOrDefault(high.VirtualId) != 1
          || !TryDwordView(sourceLow, sourceHigh, out var source)
          || !TryDwordView(targetLow, targetHigh, out var target))
        continue;

      var value = MReg.Virtual(function.VirtualRegisterCount++, MRegSize.Dword);
      var valueOperand = new MOperand.Register(value);
      block.Instructions[i] = new MInstr(MOpcode.Mov, [valueOperand, source],
        new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false,
          ReadsMemory: true, WritesMemory: false));
      block.Instructions[i + 1] = new MInstr(MOpcode.Mov, [target, valueOperand],
        new MInstrEffect(WrittenRegs: [], ReadRegs: [1], ReadsFlags: false, WritesFlags: false,
          ReadsMemory: false, WritesMemory: true));
      block.Instructions.RemoveAt(i + 2);
      block.Instructions.RemoveAt(i + 2);
      ++changed;
      ++i;
    }
    return changed;
  }

  /// <summary>
  /// A constant-byte memset expanded through the baseline LONG representation becomes two adjacent
  /// word-immediate stores with the same repeated word. On a 386 those two stores are one legal dword
  /// immediate store even when the objective is BALANCED or SIZE (the selector's resident-dword policy
  /// is deliberately SPEED-only and therefore cannot be used as the legality test here).
  /// </summary>
  private static int CombineDwordConstantStores(MBlock block) {
    var changed = 0;
    for (var i = 0; i + 1 < block.Instructions.Count; ++i) {
      var low = block.Instructions[i];
      var high = block.Instructions[i + 1];
      if (!PlainMov(low) || !PlainMov(high)
          || low.Operands is not [var lowTarget, MOperand.Immediate lowValue]
          || high.Operands is not [var highTarget, MOperand.Immediate highValue]
          || unchecked((ushort)lowValue.Value) != unchecked((ushort)highValue.Value)
          || !TryDwordView(lowTarget, highTarget, out var target))
        continue;

      var word = unchecked((uint)(ushort)lowValue.Value);
      var value = word | word << 16;
      block.Instructions[i] = new MInstr(MOpcode.Mov, [target, new MOperand.Immediate(value)],
        new MInstrEffect(WrittenRegs: [], ReadRegs: [], ReadsFlags: false, WritesFlags: false,
          ReadsMemory: false, WritesMemory: true));
      block.Instructions.RemoveAt(i + 1);
      ++changed;
    }
    return changed;
  }

  private static bool PlainMov(MInstr instruction)
    => instruction.Opcode == MOpcode.Mov && instruction.Condition is null && instruction.Clobbers.Count == 0;

  /// <summary>Answers the dword view when <paramref name="high"/> is exactly the next word of the same cell.</summary>
  private static bool TryDwordView(MOperand low, MOperand high, out MOperand dword) {
    dword = null!;
    switch (low, high) {
      case (MOperand.Memory a, MOperand.Memory b)
          when a.Size == MRegSize.Word && b.Size == MRegSize.Word
               && Nullable.Equals(a.Base, b.Base) && Nullable.Equals(a.Index, b.Index)
               && a.Scale == b.Scale && b.Disp == a.Disp + 2
               && Nullable.Equals(a.Segment, b.Segment) && a.SegmentCell == b.SegmentCell:
        dword = a with { Size = MRegSize.Dword };
        return true;
      case (MOperand.StackSlot a, MOperand.StackSlot b)
          when a.Size == MRegSize.Word && b.Size == MRegSize.Word
               && a.Index == b.Index && b.Disp == a.Disp + 2:
        dword = a with { Size = MRegSize.Dword };
        return true;
      case (MOperand.DataCell a, MOperand.DataCell b)
          when a.Size == MRegSize.Word && b.Size == MRegSize.Word
               && a.Name == b.Name && b.Disp == a.Disp + 2:
        dword = a with { Size = MRegSize.Dword };
        return true;
      case (MOperand.ParamCell a, MOperand.ParamCell b)
          when a.Size == MRegSize.Word && b.Size == MRegSize.Word
               && a.ArgumentIndex == b.ArgumentIndex && b.ByteDelta == a.ByteDelta + 2:
        dword = a with { Size = MRegSize.Dword };
        return true;
      default:
        return false;
    }
  }

  private static (Dictionary<int, int> Reads, Dictionary<int, int> Writes) RegisterCensus(MFunction function) {
    var reads = new Dictionary<int, int>();
    var writes = new Dictionary<int, int>();
    foreach (var instruction in function.AllInstructions) {
      var (instructionReads, instructionWrites) = LivenessAnalysis.RegistersOf(instruction);
      foreach (var register in instructionReads)
        reads[register] = reads.GetValueOrDefault(register) + 1;
      foreach (var register in instructionWrites)
        writes[register] = writes.GetValueOrDefault(register) + 1;
    }
    return (reads, writes);
  }

  private static int CombineCompareZero(MBlock block, IReadOnlyDictionary<string, MBlock> blocksByLabel) {
    var changed = 0;
    for (var i = 0; i < block.Instructions.Count; ++i) {
      var instruction = block.Instructions[i];
      if (instruction.Opcode != MOpcode.Cmp || instruction.Condition is not null
          || instruction.Clobbers.Count != 0
          || instruction.Operands is not [MOperand.Register { Reg: var value }, MOperand.Immediate { Value: 0 }]
          || !AuxiliaryFlagUnobservableAfter(block, i, blocksByLabel))
        continue;

      var register = new MOperand.Register(value);
      block.Instructions[i] = new MInstr(MOpcode.Test, [register, register],
        new MInstrEffect(WrittenRegs: [], ReadRegs: [0, 1], ReadsFlags: false, WritesFlags: true,
          ReadsMemory: false, WritesMemory: false));
      ++changed;
    }
    return changed;
  }

  private static int CombineAddressArithmetic(MBlock block, HashSet<int> addressValues) {
    var changed = 0;
    for (var i = 0; i + 1 < block.Instructions.Count; ++i) {
      var copy = block.Instructions[i];
      var arithmetic = block.Instructions[i + 1];
      if (copy.Opcode != MOpcode.Mov || copy.Condition is not null || copy.Clobbers.Count != 0
          || copy.Operands is not [MOperand.Register { Reg: var destination }, MOperand.Register { Reg: var source }]
          || destination.Size != MRegSize.Word || source.Size != MRegSize.Word
          || arithmetic.Opcode is not (MOpcode.Add or MOpcode.Sub)
          || arithmetic.Condition is not null || arithmetic.Clobbers.Count != 0
          || arithmetic.Operands is not [MOperand.Register { Reg: var written }, MOperand.Immediate displacement]
          || !written.Equals(destination) || !FlagsDeadAfter(block, i + 1)
          || !CanAddress(source, addressValues))
        continue;

      var signedDisplacement = arithmetic.Opcode == MOpcode.Sub ? unchecked(-displacement.Value) : displacement.Value;
      if (signedDisplacement is < short.MinValue or > short.MaxValue)
        continue;
      var address = new MOperand.Memory(source, null, 1, (int)signedDisplacement, MRegSize.Word);
      block.Instructions[i + 1] = new MInstr(MOpcode.Lea,
        [new MOperand.Register(destination), address],
        new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false,
          ReadsMemory: false, WritesMemory: false));
      block.Instructions.RemoveAt(i);
      --i;
      ++changed;
    }
    return changed;
  }

  private static HashSet<int> AddressConstrainedValues(MFunction function) {
    var values = new HashSet<int>();
    foreach (var instruction in function.AllInstructions)
      foreach (var memory in instruction.Operands.OfType<MOperand.Memory>()) {
        Add(memory.Base);
        Add(memory.Index);
        Add(memory.Segment);
      }
    return values;

    void Add(MReg? register) {
      if (register is { IsVirtual: true } value)
        values.Add(value.VirtualId);
    }
  }

  private static bool CanAddress(MReg register, HashSet<int> addressValues)
    => register.IsVirtual
      ? addressValues.Contains(register.VirtualId)
      : register.Physical is Reg.BX or Reg.BP or Reg.SI or Reg.DI;

  /// <summary>
  /// <c>CMP r,0</c> and <c>TEST r,r</c> agree on every flag consumed by Jcc and carry-chain
  /// instructions, but TEST leaves AF undefined while CMP against zero clears it. Inline assembly can
  /// observe AF through LAHF/PUSHF, including in a successor block, so only replace the compare when
  /// every reachable flag read before AF is definitely overwritten is one of the equivalent consumers.
  /// </summary>
  private static bool AuxiliaryFlagUnobservableAfter(MBlock block, int index,
      IReadOnlyDictionary<string, MBlock> blocksByLabel) {
    var visited = new HashSet<string>(StringComparer.Ordinal);
    return Scan(block, index + 1);

    bool Scan(MBlock current, int start) {
      if (start == 0 && !visited.Add(current.Label))
        return true;

      for (var i = start; i < current.Instructions.Count; ++i) {
        var instruction = current.Instructions[i];
        if (instruction.Effect.ReadsFlags && !ReadsOnlyCompareTestEquivalentFlags(instruction))
          return false;
        if (OverwritesAuxiliaryFlag(instruction))
          return true;
      }

      foreach (var successor in current.SuccessorsWithAsmJumps()) {
        if (!blocksByLabel.TryGetValue(successor, out var successorBlock) || !Scan(successorBlock, 0))
          return false;
      }
      return true;
    }
  }

  private static bool ReadsOnlyCompareTestEquivalentFlags(MInstr instruction)
    => instruction.Opcode is MOpcode.Jcc or MOpcode.Adc or MOpcode.Sbb or MOpcode.Rcl or MOpcode.Rcr;

  private static bool OverwritesAuxiliaryFlag(MInstr instruction)
    => instruction.Opcode is MOpcode.Add or MOpcode.Adc or MOpcode.Sub or MOpcode.Sbb or MOpcode.Cmp
      or MOpcode.Neg or MOpcode.Inc or MOpcode.Dec or MOpcode.Sahf;

  private static bool FlagsDeadAfter(MBlock block, int index) {
    for (var i = index + 1; i < block.Instructions.Count; ++i) {
      var effect = block.Instructions[i].Effect;
      if (effect.ReadsFlags)
        return false;
      if (effect.WritesFlags)
        return true;
    }
    // A successor may consume the flags. The machine IR has no flag liveness across blocks, so the
    // same conservative rule as Peephole applies: falling out of the block proves nothing.
    return false;
  }
}
