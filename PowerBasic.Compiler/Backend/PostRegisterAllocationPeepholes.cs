using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Backend;

/// <summary>Local machine-IR simplifications that require the final virtual-to-physical allocation.</summary>
public static class PostRegisterAllocationPeepholes {

  public static int Run(MFunction function, IReadOnlyDictionary<int, Reg> allocation) {
    ArgumentNullException.ThrowIfNull(function);
    ArgumentNullException.ThrowIfNull(allocation);
    if (!MachineOptimizationState.IsMarked(function))
      return 0;

    var changed = 0;
    foreach (var block in function.Blocks)
      for (var i = 0; i < block.Instructions.Count;) {
        if (IsSelfCopy(block.Instructions[i], allocation)) {
          block.Instructions.RemoveAt(i);
          ++changed;
          continue;
        }
        if (i + 1 < block.Instructions.Count
            && TryPair(block.Instructions[i], block.Instructions[i + 1], allocation,
              out var removeFirst, out var removeSecond)) {
          if (removeSecond)
            block.Instructions.RemoveAt(i + 1);
          if (removeFirst)
            block.Instructions.RemoveAt(i);
          ++changed;
          if (i > 0)
            --i;
          continue;
        }
        ++i;
      }
    return changed;
  }

  private static bool TryPair(MInstr first, MInstr second, IReadOnlyDictionary<int, Reg> allocation,
      out bool removeFirst, out bool removeSecond) {
    removeFirst = removeSecond = false;
    if (first.Condition is not null || second.Condition is not null
        || first.Clobbers.Count != 0 || second.Clobbers.Count != 0)
      return false;

    if (first.Opcode == MOpcode.Push && second.Opcode == MOpcode.Pop
        && first.Operands is [MOperand.Register { Reg: var pushed }]
        && second.Operands is [MOperand.Register { Reg: var popped }]
        && SamePhysical(pushed, popped, allocation)) {
      removeFirst = removeSecond = true;
      return true;
    }

    if (first.Opcode != MOpcode.Mov || second.Opcode != MOpcode.Mov
        || first.Operands is not [MOperand.Register { Reg: var firstDest }, var firstSource]
        || second.Operands is not [MOperand.Register { Reg: var secondDest }, var secondSource])
      return false;

    var firstPhysical = Resolve(firstDest, allocation);
    var secondPhysical = Resolve(secondDest, allocation);
    if (firstPhysical is null || secondPhysical is null)
      return false;

    if (firstSource is MOperand.Register { Reg: var firstFrom }
        && secondSource is MOperand.Register { Reg: var secondFrom }
        && SamePhysical(firstDest, secondFrom, allocation)
        && SamePhysical(firstFrom, secondDest, allocation)) {
      removeSecond = true;
      return true;
    }

    if (firstPhysical == secondPhysical && SameSource(firstSource, secondSource, allocation)) {
      removeSecond = true;
      return true;
    }

    if (firstPhysical == secondPhysical && secondSource is MOperand.Register or MOperand.Immediate
        && (secondSource is not MOperand.Register { Reg: var read } || Resolve(read, allocation) != firstPhysical)) {
      removeFirst = true;
      return true;
    }
    return false;
  }

  private static bool IsSelfCopy(MInstr instruction, IReadOnlyDictionary<int, Reg> allocation)
    => instruction.Opcode == MOpcode.Mov && instruction.Condition is null && instruction.Clobbers.Count == 0
      && instruction.Operands is [MOperand.Register { Reg: var destination }, MOperand.Register { Reg: var source }]
      && SamePhysical(destination, source, allocation);

  private static bool SameSource(MOperand left, MOperand right, IReadOnlyDictionary<int, Reg> allocation)
    => (left, right) switch {
      (MOperand.Immediate a, MOperand.Immediate b) => a.Value == b.Value,
      (MOperand.Register a, MOperand.Register b) => SamePhysical(a.Reg, b.Reg, allocation),
      _ => false,
    };

  private static bool SamePhysical(MReg left, MReg right, IReadOnlyDictionary<int, Reg> allocation)
    => Resolve(left, allocation) is { } a && Resolve(right, allocation) is { } b && a == b && left.Size == right.Size;

  private static Reg? Resolve(MReg register, IReadOnlyDictionary<int, Reg> allocation) {
    if (!register.IsVirtual)
      return register.Physical;
    return allocation.TryGetValue(register.VirtualId, out var physical) ? physical : null;
  }
}
