using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>O0273 — profile-weighted spill and split selection in the machine register allocator.</summary>
[TestFixture]
public sealed class O0273ProfileGuidedRegisterAllocationTests {

  private const int _cold = 1;

  [Test]
  public void Allocate_GivenCompleteProfile_ThenDirectlySpillsTheColdestValue() {
    var function = PressureFunction(definitionsFromMemory: false, profile: Profile.Complete);

    var allocation = LinearScanAllocator.Allocate(function,
      new SelectionTarget(Optimize: true, OptimizeSpeed: true), out var reason);

    Assert.That(allocation, Is.Not.Null, reason);
    Assert.That(Mentions(function, _cold), Is.False, "the cold value should become its spill cell");
    Assert.That(Mentions(function, 0), Is.True, "the structurally longest hot value should stay resident");
  }

  [Test]
  public void Allocate_GivenCompleteProfileAndMemoryDefinitions_ThenSplitsTheColdestValue() {
    var function = PressureFunction(definitionsFromMemory: true, profile: Profile.Complete);

    var allocation = LinearScanAllocator.Allocate(function,
      new SelectionTarget(Optimize: true, OptimizeSpeed: true), out var reason);

    Assert.That(allocation, Is.Not.Null, reason);
    Assert.That(Mentions(function, _cold), Is.False, "the cold non-spillable range should be split");
    Assert.That(Mentions(function, 0), Is.True, "the longer hot range should not win the split tie-break");
  }

  [Test]
  public void Allocate_GivenNoProfile_ThenKeepsTheExistingStructuralSpillOrder() {
    var function = PressureFunction(definitionsFromMemory: false, profile: Profile.None);

    var allocation = LinearScanAllocator.Allocate(function, out var reason);

    Assert.That(allocation, Is.Not.Null, reason);
    Assert.That(Mentions(function, 0), Is.False, "without profile data the longest range remains first");
    Assert.That(Mentions(function, _cold), Is.True);
  }

  [Test]
  public void Allocate_GivenPartialProfile_ThenDoesNotTreatMissingCountsAsCold() {
    var function = PressureFunction(definitionsFromMemory: false, profile: Profile.Partial);

    var allocation = LinearScanAllocator.Allocate(function, out var reason);

    Assert.That(allocation, Is.Not.Null, reason);
    Assert.That(Mentions(function, 0), Is.False, "a partial profile must fall back to structural ordering");
    Assert.That(Mentions(function, _cold), Is.True);
  }

  private enum Profile { None, Partial, Complete }

  private static MFunction PressureFunction(bool definitionsFromMemory, Profile profile) {
    const int values = 7;
    var function = new MFunction("profile_pressure") { VirtualRegisterCount = values };
    if (definitionsFromMemory)
      function.StackSlots.AddRange(Enumerable.Repeat(2, values));

    var entry = new MBlock("entry");
    var hot = new MBlock("hot");
    var tail = new MBlock("tail");

    for (var value = 0; value < values; ++value)
      entry.Instructions.Add(definitionsFromMemory ? DefineFromMemory(value) : DefineFromRegister(value));
    for (var value = 0; value < values; ++value)
      if (value != _cold)
        hot.Instructions.Add(Touch(value));
    for (var value = 0; value < values; ++value)
      tail.Instructions.Add(Touch(value));
    tail.Instructions.Add(new MInstr(MOpcode.Ret, [], MInstrEffect.None));

    entry.Successors.Add(hot.Label);
    hot.Successors.Add(tail.Label);

    if (profile is Profile.Partial or Profile.Complete) {
      entry.ExecutionCount = 1;
      hot.ExecutionCount = 1_000;
      if (profile is Profile.Complete)
        tail.ExecutionCount = 1;
    }

    function.Blocks.AddRange([entry, hot, tail]);
    return function;

    MInstr DefineFromMemory(int value) => new(MOpcode.Mov,
      [new MOperand.Register(MReg.Virtual(value)), new MOperand.StackSlot(value, MRegSize.Word)],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false,
        ReadsMemory: true, WritesMemory: false));
  }

  private static MInstr DefineFromRegister(int value) => new(MOpcode.Mov,
    [new MOperand.Register(MReg.Virtual(value)), new MOperand.Register(MReg.Physical_(Reg.BP))],
    new MInstrEffect(WrittenRegs: [0], ReadRegs: [1], ReadsFlags: false, WritesFlags: false,
      ReadsMemory: false, WritesMemory: false));

  private static MInstr Touch(int value) => new(MOpcode.Add,
    [new MOperand.Register(MReg.Virtual(value)), new MOperand.Immediate(1)],
    new MInstrEffect(WrittenRegs: [0], ReadRegs: [0], ReadsFlags: false, WritesFlags: true,
      ReadsMemory: false, WritesMemory: false));

  private static bool Mentions(MFunction function, int value) => function.AllInstructions
    .Select(LivenessAnalysis.RegistersOf)
    .Any(registers => registers.Reads.Contains(value) || registers.Writes.Contains(value));
}
