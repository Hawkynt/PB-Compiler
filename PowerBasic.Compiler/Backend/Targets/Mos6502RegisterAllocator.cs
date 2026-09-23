namespace PowerBasic.Compiler.Backend.Targets;

public sealed record Mos6502FramePlan(IReadOnlyList<int> SavedRegisters, int LocalBytes, int StackBytes);
public sealed record Mos6502Allocation(IReadOnlyDictionary<int, MachineRegister> Registers, Mos6502FramePlan Frame);

public sealed class Mos6502RegisterAllocator {
  public IReadOnlyDictionary<int, MachineRegister> Allocate(IEnumerable<int> virtualRegisters) {
    var result = new Dictionary<int, MachineRegister>();
    var registers = Mos6502RegisterFile.ZeroPage.Take(10).ToArray();
    foreach (var (id, index) in virtualRegisters.Distinct().Select((id, index) => (id, index))) {
      if (index >= registers.Length)
        throw new InvalidOperationException("6502 zero-page register pressure exceeds RS0-RS9");
      result[id] = registers[index];
    }
    return result;
  }

  public Mos6502FramePlan FramePlan(int localBytes, IEnumerable<int> savedRegisters)
    => new(savedRegisters.Distinct().Where(index => index is >= 10 and <= 15).ToArray(),
      Math.Max(0, localBytes), 0);
}
