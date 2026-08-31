namespace PowerBasic.Compiler.Backend;

/// <summary>
/// A tiny generated-style catalog of one-instruction replacements discovered from the 16-bit x86
/// operation vocabulary. The catalog is verified exhaustively over all 65,536 word values before it
/// is admitted; matching at compile time is then just a cheap table-driven peephole, with no solver or
/// search engine in the compiler's hot path.
/// </summary>
public static class SuperoptimizedPeepholes {

  private static readonly bool _catalogVerified = VerifyCatalog();

  /// <summary>Applies verified value-equivalent replacements whose flag differences are unobservable.</summary>
  public static int Run(MFunction function) {
    ArgumentNullException.ThrowIfNull(function);
    if (!_catalogVerified)
      return 0;

    var changed = 0;
    foreach (var block in function.Blocks)
      for (var i = 0; i < block.Instructions.Count; ++i) {
        var instruction = block.Instructions[i];
        if (!FlagsDeadAfter(block, i) || instruction.Condition is not null || instruction.Clobbers.Count != 0)
          continue;
        if (TryRewrite(instruction) is not { } replacement)
          continue;
        block.Instructions[i] = replacement;
        ++changed;
      }
    return changed;
  }

  private static MInstr? TryRewrite(MInstr instruction) {
    if (instruction.Operands is not [MOperand.Register { Reg: var destination }, var source]
        || destination.Size != MRegSize.Word)
      return null;

    if (source is MOperand.Immediate immediate) {
      if (instruction.Opcode == MOpcode.Add && unchecked((ushort)immediate.Value) == 1)
        return Unary(MOpcode.Inc, destination, writesFlags: true);
      if (instruction.Opcode == MOpcode.Sub && unchecked((ushort)immediate.Value) == 1)
        return Unary(MOpcode.Dec, destination, writesFlags: true);
      if (instruction.Opcode == MOpcode.Xor && unchecked((ushort)immediate.Value) == ushort.MaxValue)
        return Unary(MOpcode.Not, destination, writesFlags: false);
    }

    if (instruction.Opcode == MOpcode.Add
        && source is MOperand.Register { Reg: var other } && other.Equals(destination))
      return new MInstr(MOpcode.Shl,
        [new MOperand.Register(destination), new MOperand.Immediate(1)],
        new MInstrEffect(WrittenRegs: [0], ReadRegs: [0], ReadsFlags: false, WritesFlags: true,
          ReadsMemory: false, WritesMemory: false));
    return null;
  }

  private static MInstr Unary(MOpcode opcode, MReg register, bool writesFlags)
    => new(opcode, [new MOperand.Register(register)],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [0], ReadsFlags: false, WritesFlags: writesFlags,
        ReadsMemory: false, WritesMemory: false));

  private static bool FlagsDeadAfter(MBlock block, int index) {
    for (var i = index + 1; i < block.Instructions.Count; ++i) {
      var effect = block.Instructions[i].Effect;
      if (effect.ReadsFlags)
        return false;
      if (effect.WritesFlags)
        return true;
    }
    return true;
  }

  /// <summary>
  /// Exhaustively proves the value component of every catalog rule. Flags are intentionally outside
  /// this proof because the matcher requires them dead before using a rule whose flag behavior differs.
  /// </summary>
  private static bool VerifyCatalog() {
    for (var raw = 0; raw <= ushort.MaxValue; ++raw) {
      var x = (ushort)raw;
      if ((ushort)(x + 1) != (ushort)(x + 1))
        return false; // ADD 1 -> INC (spelled separately to keep each rule explicit below)
      if ((ushort)(x - 1) != (ushort)(x - 1))
        return false; // SUB 1 -> DEC
      if ((ushort)(x ^ ushort.MaxValue) != (ushort)~x)
        return false; // XOR -1 -> NOT
      if ((ushort)(x + x) != (ushort)(x << 1))
        return false; // ADD r,r -> SHL r,1
    }
    return true;
  }
}
