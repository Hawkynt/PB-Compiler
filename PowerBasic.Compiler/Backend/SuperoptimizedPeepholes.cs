namespace PowerBasic.Compiler.Backend;

/// <summary>
/// A bounded superoptimizer for small one-register x86-16 peepholes. At startup it searches a tiny
/// target-instruction vocabulary, proves candidate value semantics over all 65,536 word inputs, and
/// keeps only strictly cheaper replacements. The hot path is therefore table lookup/matching rather
/// than search or SMT, and no external solver becomes a compiler dependency.
/// </summary>
public static class SuperoptimizedPeepholes {

  private static readonly IReadOnlyDictionary<SourcePattern, Candidate> _catalog = DiscoverCatalog();

  /// <summary>Applies exhaustively verified replacements whose flag differences are unobservable.</summary>
  public static int Run(MFunction function) {
    ArgumentNullException.ThrowIfNull(function);
    var changed = 0;
    foreach (var block in function.Blocks)
      for (var i = 0; i < block.Instructions.Count; ++i) {
        var instruction = block.Instructions[i];
        if (!FlagsDeadAfter(block, i) || instruction.Condition is not null || instruction.Clobbers.Count != 0)
          continue;
        if (Match(instruction) is not { } pattern || !_catalog.TryGetValue(pattern, out var candidate))
          continue;
        block.Instructions[i] = Build(candidate, ((MOperand.Register)instruction.Operands[0]).Reg);
        ++changed;
      }
    return changed;
  }

  private static SourcePattern? Match(MInstr instruction) {
    if (instruction.Operands is not [MOperand.Register { Reg: { Size: MRegSize.Word } destination }, var source])
      return null;
    if (source is MOperand.Immediate immediate)
      return instruction.Opcode switch {
        MOpcode.Add when unchecked((ushort)immediate.Value) == 1 => SourcePattern.AddOne,
        MOpcode.Sub when unchecked((ushort)immediate.Value) == 1 => SourcePattern.SubOne,
        MOpcode.Xor when unchecked((ushort)immediate.Value) == ushort.MaxValue => SourcePattern.XorAllOnes,
        MOpcode.And when unchecked((ushort)immediate.Value) == 0 => SourcePattern.AndZero,
        _ => null,
      };
    if (instruction.Opcode == MOpcode.Add
        && source is MOperand.Register { Reg: var other } && other.Equals(destination))
      return SourcePattern.AddSelf;
    return null;
  }

  private static MInstr Build(Candidate candidate, MReg register) => candidate switch {
    Candidate.Inc => Unary(MOpcode.Inc, register, writesFlags: true),
    Candidate.Dec => Unary(MOpcode.Dec, register, writesFlags: true),
    Candidate.Not => Unary(MOpcode.Not, register, writesFlags: false),
    Candidate.ShlOne => new MInstr(MOpcode.Shl,
      [new MOperand.Register(register), new MOperand.Immediate(1)],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [0], ReadsFlags: false, WritesFlags: true,
        ReadsMemory: false, WritesMemory: false)),
    Candidate.XorSelf => new MInstr(MOpcode.Xor,
      [new MOperand.Register(register), new MOperand.Register(register)],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: true,
        ReadsMemory: false, WritesMemory: false)),
    _ => throw new ArgumentOutOfRangeException(nameof(candidate)),
  };

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
    return false;                                  // a successor may consume the flags
  }

  private static IReadOnlyDictionary<SourcePattern, Candidate> DiscoverCatalog() {
    var result = new Dictionary<SourcePattern, Candidate>();
    foreach (var source in Enum.GetValues<SourcePattern>()) {
      Candidate? best = null;
      var bestCost = SourceCost(source);
      foreach (var candidate in Enum.GetValues<Candidate>()) {
        var cost = CandidateCost(candidate);
        if (cost >= bestCost || !Equivalent(source, candidate))
          continue;
        best = candidate;
        bestCost = cost;
      }
      if (best is { } replacement)
        result[source] = replacement;
    }
    return result;
  }

  /// <summary>Complete 16-bit truth-table proof for one source/candidate pair.</summary>
  private static bool Equivalent(SourcePattern source, Candidate candidate) {
    for (var raw = 0; raw <= ushort.MaxValue; ++raw) {
      var value = (ushort)raw;
      if (Evaluate(source, value) != Evaluate(candidate, value))
        return false;
    }
    return true;
  }

  private static ushort Evaluate(SourcePattern pattern, ushort value) => pattern switch {
    SourcePattern.AddOne => unchecked((ushort)(value + 1)),
    SourcePattern.SubOne => unchecked((ushort)(value - 1)),
    SourcePattern.XorAllOnes => (ushort)(value ^ ushort.MaxValue),
    SourcePattern.AddSelf => unchecked((ushort)(value + value)),
    SourcePattern.AndZero => 0,
    _ => throw new ArgumentOutOfRangeException(nameof(pattern)),
  };

  private static ushort Evaluate(Candidate candidate, ushort value) => candidate switch {
    Candidate.Inc => value == ushort.MaxValue ? (ushort)0 : (ushort)(value + 1),
    Candidate.Dec => value == 0 ? ushort.MaxValue : (ushort)(value - 1),
    Candidate.Not => unchecked((ushort)~value),
    Candidate.ShlOne => unchecked((ushort)(value << 1)),
    Candidate.XorSelf => 0,
    _ => throw new ArgumentOutOfRangeException(nameof(candidate)),
  };

  // Conservative generic-register encoding sizes on 8086. A replacement is admitted only when this
  // upper-level model says it is strictly shorter; register-special accumulator encodings can only
  // make a source cheaper, never make an admitted replacement incorrect.
  private static int SourceCost(SourcePattern pattern) => pattern switch {
    SourcePattern.AddOne or SourcePattern.SubOne => 3,
    SourcePattern.XorAllOnes or SourcePattern.AndZero => 4,
    SourcePattern.AddSelf => 2,
    _ => int.MaxValue,
  };

  private static int CandidateCost(Candidate candidate) => candidate switch {
    Candidate.Inc or Candidate.Dec or Candidate.Not or Candidate.ShlOne or Candidate.XorSelf => 2,
    _ => int.MaxValue,
  };

  private enum SourcePattern { AddOne, SubOne, XorAllOnes, AddSelf, AndZero }
  private enum Candidate { Inc, Dec, Not, ShlOne, XorSelf }
}
