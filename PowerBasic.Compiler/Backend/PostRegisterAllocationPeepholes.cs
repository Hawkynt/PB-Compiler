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
    foreach (var block in function.Blocks) {
      // O0064 belongs here rather than in the pre-allocation combiner: a 32-bit LEA is useful only
      // once the allocator has proved that every address input really lives in a 386 register. That
      // keeps the ordinary 8086 address constraints untouched and also lets us reject ESP as a SIB
      // index using the register assignment that will actually be emitted.
      changed += FuseLeaArithmetic(block, allocation);

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
    }
    return changed;
  }

  /// <summary>
  /// O0064 - folds dword address arithmetic into the 80386 LEA/SIB encoding after allocation.
  ///
  /// <para>
  /// <c>MOV d,x; ADD d,y</c> becomes <c>LEA d,[x+y]</c>, and
  /// <c>MOV d,x; SHL d,k; ADD d,y</c> becomes <c>LEA d,[y+x*2^k]</c> for k=1..3. The latter also
  /// covers the useful x*3/x*5/x*9 forms when y is x. An immediate ADD/SUB becomes the displacement
  /// of a base-only LEA. Every rewrite is flags-free, so the flags written by the final arithmetic
  /// must be dead before another instruction reads them.
  /// </para>
  ///
  /// <para>
  /// The physical allocation is the CPU gate: dword virtuals are allocatable only on the 386+ native
  /// dword path. We additionally validate the resolved registers here because SIB reserves index=4
  /// for "no index" and therefore cannot scale ESP.
  /// </para>
  /// </summary>
  private static int FuseLeaArithmetic(MBlock block, IReadOnlyDictionary<int, Reg> allocation) {
    var changed = 0;
    for (var i = 0; i < block.Instructions.Count; ++i) {
      if (i + 2 < block.Instructions.Count
          && TryScaledLea(block, i, allocation, out var scaled)) {
        block.Instructions[i + 2] = scaled;
        block.Instructions.RemoveAt(i + 1);
        block.Instructions.RemoveAt(i);
        ++changed;
        --i;
        continue;
      }

      if (i + 1 < block.Instructions.Count
          && TrySimpleLea(block, i, allocation, out var simple)) {
        block.Instructions[i + 1] = simple;
        block.Instructions.RemoveAt(i);
        ++changed;
        --i;
      }
    }
    return changed;
  }

  private static bool TryScaledLea(MBlock block, int index, IReadOnlyDictionary<int, Reg> allocation,
      out MInstr replacement) {
    replacement = null!;
    var copy = block.Instructions[index];
    var shift = block.Instructions[index + 1];
    var add = block.Instructions[index + 2];
    if (!Plain(copy) || !Plain(shift) || !Plain(add)
        || copy.Opcode != MOpcode.Mov
        || copy.Operands is not [MOperand.Register { Reg: var destination }, MOperand.Register { Reg: var scaledSource }]
        || destination.Size != MRegSize.Dword || scaledSource.Size != MRegSize.Dword
        || shift.Opcode != MOpcode.Shl
        || shift.Operands is not [MOperand.Register { Reg: var shifted }, MOperand.Immediate { Value: >= 1 and <= 3 } count]
        || !shifted.Equals(destination)
        || add.Opcode != MOpcode.Add
        || add.Operands is not [MOperand.Register { Reg: var written }, MOperand.Register { Reg: var baseSource }]
        || !written.Equals(destination) || baseSource.Size != MRegSize.Dword
        || !FlagsDeadAfter(block, index + 2))
      return false;

    var destinationPhysical = Resolve(destination, allocation);
    var scaledPhysical = Resolve(scaledSource, allocation);
    var basePhysical = Resolve(baseSource, allocation);
    if (destinationPhysical?.IsDword() != true || scaledPhysical?.IsDword() != true || basePhysical?.IsDword() != true
        || scaledPhysical == Reg.ESP
        // If ADD reads the shifted destination itself, it is doubling the current value, not adding
        // an independently preserved base. Treating that as [base + scaled-old-value] would be wrong.
        || SamePhysical(destination, baseSource, allocation))
      return false;

    var scale = 1 << (int)count.Value;
    replacement = Lea(destination, new MOperand.Memory(baseSource, scaledSource, scale, 0, MRegSize.Dword));
    return true;
  }

  private static bool TrySimpleLea(MBlock block, int index, IReadOnlyDictionary<int, Reg> allocation,
      out MInstr replacement) {
    replacement = null!;
    var copy = block.Instructions[index];
    var arithmetic = block.Instructions[index + 1];
    if (!Plain(copy) || !Plain(arithmetic)
        || copy.Opcode != MOpcode.Mov
        || copy.Operands is not [MOperand.Register { Reg: var destination }, MOperand.Register { Reg: var source }]
        || destination.Size != MRegSize.Dword || source.Size != MRegSize.Dword
        || arithmetic.Opcode is not (MOpcode.Add or MOpcode.Sub)
        || arithmetic.Operands.Count != 2
        || arithmetic.Operands[0] is not MOperand.Register { Reg: var written }
        || !written.Equals(destination) || !FlagsDeadAfter(block, index + 1))
      return false;

    if (Resolve(destination, allocation)?.IsDword() != true || Resolve(source, allocation)?.IsDword() != true)
      return false;

    switch (arithmetic.Operands[1]) {
      case MOperand.Immediate immediate: {
        if (immediate.Value == long.MinValue)
          return false;
        var displacement = arithmetic.Opcode == MOpcode.Sub ? -immediate.Value : immediate.Value;
        if (displacement is < int.MinValue or > int.MaxValue)
          return false;
        replacement = Lea(destination,
          new MOperand.Memory(source, null, 1, (int)displacement, MRegSize.Dword));
        return true;
      }

      case MOperand.Register { Reg: var other } when arithmetic.Opcode == MOpcode.Add && other.Size == MRegSize.Dword: {
        var sourcePhysical = Resolve(source, allocation);
        var otherPhysical = Resolve(other, allocation);
        if (sourcePhysical?.IsDword() != true || otherPhysical?.IsDword() != true)
          return false;

        // For scale=1 base and index are interchangeable, so move ESP to the base side if necessary.
        var @base = source;
        var scaled = other;
        if (otherPhysical == Reg.ESP) {
          if (sourcePhysical == Reg.ESP)
            return false;
          (@base, scaled) = (other, source);
        }
        replacement = Lea(destination, new MOperand.Memory(@base, scaled, 1, 0, MRegSize.Dword));
        return true;
      }

      default:
        return false;
    }
  }

  private static MInstr Lea(MReg destination, MOperand.Memory address) => new(MOpcode.Lea,
    [new MOperand.Register(destination), address],
    new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false,
      ReadsMemory: false, WritesMemory: false));

  private static bool Plain(MInstr instruction)
    => instruction.Condition is null && instruction.Clobbers.Count == 0;

  private static bool FlagsDeadAfter(MBlock block, int index) {
    for (var i = index + 1; i < block.Instructions.Count; ++i) {
      var effect = block.Instructions[i].Effect;
      if (effect.ReadsFlags)
        return false;
      if (effect.WritesFlags)
        return true;
    }
    // Flags may flow into a successor block, and machine IR has no cross-block flag liveness fact.
    return false;
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

    // Do not erase an overwritten MEMORY read here. Ordinary compiler memory is non-volatile, but PB
    // can address absolute/far hardware locations as well; O0357 is a register-allocation peephole, not
    // a dead-I/O-access pass. Register/immediate staging is the intended post-RA case.
    if (firstPhysical == secondPhysical && firstSource is MOperand.Register or MOperand.Immediate
        && secondSource is MOperand.Register or MOperand.Immediate
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

  /// <summary>
  /// Resolves the register exactly as <see cref="MachineEmitter"/> will emit it. The allocator stores a
  /// byte virtual as its containing word register (AX/CX/DX/BX), but emission names the addressable low
  /// byte. Comparing the container here would miss self-copies such as <c>v:byte(AX) &lt;- AL</c> and,
  /// worse, could let the overwritten-copy rule delete the definition feeding that apparent copy.
  /// </summary>
  private static Reg? Resolve(MReg register, IReadOnlyDictionary<int, Reg> allocation) {
    Reg? physical = register.IsVirtual
      ? allocation.TryGetValue(register.VirtualId, out var allocated) ? allocated : null
      : register.Physical;
    if (physical is not { } resolved || register.Size != MRegSize.Byte || resolved.IsByte())
      return physical;
    return resolved switch {
      Reg.AX => Reg.AL,
      Reg.CX => Reg.CL,
      Reg.DX => Reg.DL,
      Reg.BX => Reg.BL,
      _ => null,
    };
  }
}
