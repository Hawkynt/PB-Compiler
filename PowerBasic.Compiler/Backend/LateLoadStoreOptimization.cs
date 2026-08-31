using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Backend;

/// <summary>
/// Local stack-slot forwarding after spilling and allocation. This is intentionally conservative:
/// facts never cross a basic-block boundary, call, inline assembly, or unknown memory write, and a
/// register-backed fact dies as soon as that physical register is overwritten.
/// </summary>
public static class LateLoadStoreOptimization {

  public static int Run(MFunction function, IReadOnlyDictionary<int, Reg> allocation) {
    ArgumentNullException.ThrowIfNull(function);
    ArgumentNullException.ThrowIfNull(allocation);
    var changed = 0;
    foreach (var block in function.Blocks)
      changed += RunBlock(block, allocation);
    return changed;
  }

  private static int RunBlock(MBlock block, IReadOnlyDictionary<int, Reg> allocation) {
    var known = new Dictionary<SlotKey, KnownValue>();
    var output = new List<MInstr?>();
    var changed = 0;

    foreach (var original in block.Instructions) {
      var emitted = original;
      var omit = false;

      if (TryLoad(original, out var loadDestination, out var loadSlot)) {
        var key = SlotKey.Of(loadSlot);
        if (known.TryGetValue(key, out var value)) {
          var destination = Resolve(loadDestination, allocation);
          if (value.Source is MOperand.Register { Reg: var held } && destination is { } d && held.Physical == d) {
            omit = true;
          } else {
            emitted = new MInstr(MOpcode.Mov,
              [new MOperand.Register(loadDestination), value.Source],
              new MInstrEffect(WrittenRegs: [0], ReadRegs: value.Source is MOperand.Register ? [1] : [],
                ReadsFlags: false, WritesFlags: false, ReadsMemory: false, WritesMemory: false));
          }
          ++changed;
        } else if (known.TryGetValue(key, out var readValue)) {
          known[key] = readValue with { ReadSinceStore = true };
        }
      } else if (TryStore(original, allocation, out var storeSlot, out var source)) {
        var key = SlotKey.Of(storeSlot);
        if (known.TryGetValue(key, out var previous) && SameValue(previous.Source, source)) {
          omit = true;
          ++changed;
        } else {
          if (known.TryGetValue(key, out previous) && !previous.ReadSinceStore && previous.StoreOutputIndex >= 0
              && previous.StoreOutputIndex < output.Count && output[previous.StoreOutputIndex] is not null) {
            output[previous.StoreOutputIndex] = null;
            ++changed;
          }
          known[key] = new(source, output.Count, ReadSinceStore: false);
        }
      } else {
        foreach (var slot in original.Operands.OfType<MOperand.StackSlot>())
          if (known.TryGetValue(SlotKey.Of(slot), out var value))
            known[SlotKey.Of(slot)] = value with { ReadSinceStore = true };

        if (original.Opcode is MOpcode.Call or MOpcode.InlineAsm || original.IsTerminator)
          known.Clear();
        else if (original.Effect.WritesMemory)
          known.Clear();
      }

      if (!omit)
        output.Add(emitted);

      if (!omit)
        InvalidateWrittenRegisters(emitted, known, allocation);
    }

    if (changed == 0)
      return 0;
    block.Instructions.Clear();
    block.Instructions.AddRange(output.OfType<MInstr>());
    return changed;
  }

  private static bool TryLoad(MInstr instruction, out MReg destination, out MOperand.StackSlot slot) {
    if (instruction.Opcode == MOpcode.Mov
        && instruction.Operands is [MOperand.Register { Reg: var register }, MOperand.StackSlot stack]) {
      destination = register;
      slot = stack;
      return true;
    }
    destination = default;
    slot = null!;
    return false;
  }

  private static bool TryStore(MInstr instruction, IReadOnlyDictionary<int, Reg> allocation,
      out MOperand.StackSlot slot, out MOperand source) {
    if (instruction.Opcode == MOpcode.Mov
        && instruction.Operands is [MOperand.StackSlot stack, MOperand.Immediate immediate]) {
      slot = stack;
      source = immediate;
      return true;
    }
    if (instruction.Opcode == MOpcode.Mov
        && instruction.Operands is [MOperand.StackSlot stack, MOperand.Register { Reg: var register }]
        && Resolve(register, allocation) is { } physical) {
      slot = stack;
      source = new MOperand.Register(MReg.Physical_(physical, register.Size));
      return true;
    }
    slot = null!;
    source = null!;
    return false;
  }

  private static void InvalidateWrittenRegisters(MInstr instruction, Dictionary<SlotKey, KnownValue> known,
      IReadOnlyDictionary<int, Reg> allocation) {
    var written = new HashSet<Reg>(instruction.Clobbers);
    foreach (var operandIndex in instruction.Effect.WrittenRegs)
      if (operandIndex >= 0 && operandIndex < instruction.Operands.Count
          && instruction.Operands[operandIndex] is MOperand.Register { Reg: var register }
          && Resolve(register, allocation) is { } physical)
        written.Add(physical);
    if (written.Count == 0)
      return;

    foreach (var key in known.Where(pair => pair.Value.Source is MOperand.Register { Reg: var register }
        && written.Contains(register.Physical)).Select(pair => pair.Key).ToArray())
      known.Remove(key);
  }

  private static bool SameValue(MOperand left, MOperand right) => (left, right) switch {
    (MOperand.Immediate a, MOperand.Immediate b) => a.Value == b.Value,
    (MOperand.Register a, MOperand.Register b) => a.Reg.Physical == b.Reg.Physical && a.Reg.Size == b.Reg.Size,
    _ => false,
  };

  private static Reg? Resolve(MReg register, IReadOnlyDictionary<int, Reg> allocation)
    => register.IsVirtual ? allocation.GetValueOrDefault(register.VirtualId) : register.Physical;

  private readonly record struct SlotKey(int Index, int Disp, MRegSize Size) {
    public static SlotKey Of(MOperand.StackSlot slot) => new(slot.Index, slot.Disp, slot.Size);
  }

  private readonly record struct KnownValue(MOperand Source, int StoreOutputIndex, bool ReadSinceStore);
}
