using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// The entry moves of a register calling convention: every argument register is read before any is
/// overwritten, whatever permutation the allocator asked for. Checked by simulating the register file
/// over every assignment of four arguments to four registers.
/// </summary>
[TestFixture]
public sealed class ParallelMoveTests {

  private static readonly Reg[] _registers = [Reg.AX, Reg.DX, Reg.BX, Reg.CX];

  private static IEnumerable<Reg[]> Permutations(Reg[] items) {
    if (items.Length <= 1) {
      yield return items;
      yield break;
    }
    for (var i = 0; i < items.Length; ++i)
      foreach (var rest in Permutations([.. items.Take(i), .. items.Skip(i + 1)]))
        yield return [items[i], .. rest];
  }

  [Test]
  public void Sequence_GivenEveryPermutationOfFourRegisters_ThenEachValueEndsWhereItWasSent() {
    var permutations = Permutations(_registers).ToList();
    Assert.That(permutations, Has.Count.EqualTo(24));
    foreach (var targets in permutations) {
      var moves = _registers.Select((source, i) => (Destination: targets[i], Source: source)).ToList();
      var file = _registers.ToDictionary(register => register, register => (int)register);
      foreach (var (exchange, destination, source) in MachineEmitter.SequenceParallelMoves(moves))
        if (exchange)
          (file[destination], file[source]) = (file[source], file[destination]);
        else
          file[destination] = file[source];
      foreach (var (destination, source) in moves)
        Assert.That(file[destination], Is.EqualTo((int)source), $"{source} -> {destination} in {string.Join(",", targets)}");
    }
  }

  [Test]
  public void Sequence_GivenAFanOutOfOneRegister_ThenBothDestinationsGetIt() {
    // AX -> DX while DX -> BX: DX has to be read before it is written
    var steps = MachineEmitter.SequenceParallelMoves([(Reg.DX, Reg.AX), (Reg.BX, Reg.DX)]);
    Assert.That(steps, Is.EqualTo(new[] { (false, Reg.BX, Reg.DX), (false, Reg.DX, Reg.AX) }));
  }
}
