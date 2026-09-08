using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Backend;

/// <summary>
/// Target-level combining after instruction selection. These patterns deliberately depend on x86
/// encodings and register roles, so expressing them in the target-independent IR would be the wrong
/// abstraction even when the source expression looked similar.
/// </summary>
public static class MachineCombiner {

  /// <summary>Combines selected x86 instruction windows without increasing virtual-register pressure.</summary>
  public static int Run(MFunction function) {
    ArgumentNullException.ThrowIfNull(function);
    var addressValues = AddressConstrainedValues(function);
    var blocksByLabel = function.Blocks.ToDictionary(block => block.Label, StringComparer.Ordinal);
    var changed = 0;
    foreach (var block in function.Blocks) {
      changed += CombineCompareZero(block, blocksByLabel);
      changed += CombineAddressArithmetic(block, addressValues);
    }
    return changed;
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
