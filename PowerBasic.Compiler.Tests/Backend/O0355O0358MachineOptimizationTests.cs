using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>Target-machine regression coverage for O0355-O0358.</summary>
[TestFixture]
public sealed class O0355O0358MachineOptimizationTests {

  private static MOperand.Register V(int id, MRegSize size = MRegSize.Word) => new(MReg.Virtual(id, size));
  private static MOperand.StackSlot Slot(int index, MRegSize size = MRegSize.Word, int disp = 0) => new(index, size, disp);

  private static MInstr Move(int destination, MOperand source, MRegSize size = MRegSize.Word)
    => new(MOpcode.Mov, [V(destination, size), source],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: source is MOperand.Register ? [1] : [],
        ReadsFlags: false, WritesFlags: false, ReadsMemory: source.IsMemoryAccess(), WritesMemory: false));

  private static MInstr Store(MOperand.StackSlot destination, MOperand source)
    => new(MOpcode.Mov, [destination, source],
      new MInstrEffect(WrittenRegs: [], ReadRegs: source is MOperand.Register ? [1] : [],
        ReadsFlags: false, WritesFlags: false, ReadsMemory: false, WritesMemory: true));

  private static MInstr Add(int destination, long immediate)
    => new(MOpcode.Add, [V(destination), new MOperand.Immediate(immediate)],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [0], ReadsFlags: false, WritesFlags: true,
        ReadsMemory: false, WritesMemory: false));

  private static MInstr Compare(int value, long immediate)
    => new(MOpcode.Cmp, [V(value), new MOperand.Immediate(immediate)],
      new MInstrEffect(WrittenRegs: [], ReadRegs: [0], ReadsFlags: false, WritesFlags: true,
        ReadsMemory: false, WritesMemory: false));

  private static MInstr Call()
    => new(MOpcode.Call, [new MOperand.LabelRef("rt_x")],
      new MInstrEffect([], [], ReadsFlags: false, WritesFlags: true, ReadsMemory: true, WritesMemory: true),
      clobbers: [Reg.AX, Reg.CX, Reg.DX]);

  private static MFunction OneBlock(params MInstr[] instructions) {
    var function = new MFunction("f") { VirtualRegisterCount = 16 };
    var block = new MBlock("entry");
    block.Instructions.AddRange(instructions);
    function.Blocks.Add(block);
    return function;
  }

  private static void MarkOptimized(MFunction function) => Peephole.Run(function);

  [Test]
  public void Superoptimizer_GivenAddOneAndLaterFlagOverwrite_WhenRun_ThenSearchDiscoveredIncIsUsed() {
    var function = OneBlock(Add(0, 1), Compare(1, 7));

    Assert.That(SuperoptimizedPeepholes.Run(function), Is.EqualTo(1));

    Assert.That(function.Blocks[0].Instructions[0].Opcode, Is.EqualTo(MOpcode.Inc));
  }

  [Test]
  public void Superoptimizer_GivenAddOneAtBlockEnd_WhenRun_ThenUnknownSuccessorFlagUseKeepsAdd() {
    var function = OneBlock(Add(0, 1));

    Assert.That(SuperoptimizedPeepholes.Run(function), Is.Zero);
    Assert.That(function.Blocks[0].Instructions[0].Opcode, Is.EqualTo(MOpcode.Add));
  }

  [Test]
  public void Superoptimizer_GivenAndZeroAndLaterFlagOverwrite_WhenRun_ThenItUsesZeroIdiom() {
    var andZero = new MInstr(MOpcode.And, [V(0), new MOperand.Immediate(0)],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [0], ReadsFlags: false, WritesFlags: true,
        ReadsMemory: false, WritesMemory: false));
    var function = OneBlock(andZero, Compare(1, 7));

    Assert.That(SuperoptimizedPeepholes.Run(function), Is.EqualTo(1));

    Assert.That(function.Blocks[0].Instructions[0].Opcode, Is.EqualTo(MOpcode.Xor));
    Assert.That(function.Blocks[0].Instructions[0].Effect.ReadRegs, Is.Empty,
      "xor r,r is a zero definition and must not lengthen the old value's live range");
  }

  [Test]
  public void MachineCombiner_GivenCompareAgainstZero_WhenRun_ThenTestCarriesTheFlags() {
    var function = OneBlock(Compare(0, 0));

    Assert.That(MachineCombiner.Run(function), Is.EqualTo(1));

    var test = function.Blocks[0].Instructions.Single();
    Assert.That(test.Opcode, Is.EqualTo(MOpcode.Test));
    Assert.That(test.Operands[0], Is.EqualTo(test.Operands[1]));
  }

  [Test]
  public void MachineCombiner_GivenAddressValueCopyAddAndDeadFlags_WhenRun_ThenLeaReplacesThePair() {
    var source = MReg.Virtual(0);
    var indirect = new MOperand.Memory(source, null, 1, 0, MRegSize.Word);
    var useAddress = new MInstr(MOpcode.Mov, [V(3), indirect],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false,
        ReadsMemory: true, WritesMemory: false));
    var function = OneBlock(
      Move(1, V(0)),
      Add(1, 6),
      Compare(2, 7),
      useAddress);

    Assert.That(MachineCombiner.Run(function), Is.EqualTo(1));

    var lea = function.Blocks[0].Instructions[0];
    Assert.That(lea.Opcode, Is.EqualTo(MOpcode.Lea));
    Assert.That(((MOperand.Memory)lea.Operands[1]).Disp, Is.EqualTo(6));
  }

  [Test]
  public void MachineCombiner_GivenCopyAddAtBlockEnd_WhenRun_ThenFlagsAreNotAssumedDead() {
    var source = MReg.Physical_(Reg.BX);
    var function = OneBlock(
      new MInstr(MOpcode.Mov, [V(1), new MOperand.Register(source)],
        new MInstrEffect([0], [1], false, false, false, false)),
      Add(1, 6));

    Assert.That(MachineCombiner.Run(function), Is.Zero);
    Assert.That(function.Blocks[0].Instructions.Select(i => i.Opcode), Is.EqualTo(new[] { MOpcode.Mov, MOpcode.Add }));
  }

  [Test]
  public void PostRa_GivenTwoVirtualsAllocatedToSamePhysicalRegister_WhenOptimized_ThenSelfCopyDisappears() {
    var function = OneBlock(Move(0, V(1)));
    var allocation = new Dictionary<int, Reg> { [0] = Reg.AX, [1] = Reg.AX };

    Assert.That(PostRegisterAllocationPeepholes.Run(function, allocation), Is.Zero,
      "unoptimized machine functions must remain faithful");
    MarkOptimized(function);
    Assert.That(PostRegisterAllocationPeepholes.Run(function, allocation), Is.EqualTo(1));
    Assert.That(function.Blocks[0].Instructions, Is.Empty);
  }

  [Test]
  public void PostRa_GivenMemoryReadThenPhysicalOverwrite_WhenRun_ThenItDoesNotDeleteTheMemoryRead() {
    var function = OneBlock(Move(0, Slot(0)), Move(1, new MOperand.Immediate(42)));
    var allocation = new Dictionary<int, Reg> { [0] = Reg.AX, [1] = Reg.AX };
    MarkOptimized(function);

    Assert.That(PostRegisterAllocationPeepholes.Run(function, allocation), Is.Zero);
    Assert.That(function.Blocks[0].Instructions, Has.Count.EqualTo(2));
    Assert.That(function.Blocks[0].Instructions[0].Effect.ReadsMemory, Is.True);
  }

  [Test]
  public void LateLoadStore_GivenStoreThenReload_WhenOptimized_ThenReloadForwardsFromPhysicalRegister() {
    var slot = Slot(0);
    var function = OneBlock(Store(slot, V(0)), Move(1, slot));
    var allocation = new Dictionary<int, Reg> { [0] = Reg.AX, [1] = Reg.BX };
    MarkOptimized(function);

    Assert.That(LateLoadStoreOptimization.Run(function, allocation), Is.EqualTo(1));

    var reload = function.Blocks[0].Instructions[1];
    Assert.That(reload.Effect.ReadsMemory, Is.False);
    Assert.That(reload.Operands[1], Is.EqualTo(new MOperand.Register(MReg.Physical_(Reg.AX))));
  }

  [Test]
  public void LateLoadStore_GivenCallBetweenStoreAndReload_WhenRun_ThenCallInvalidatesTheFact() {
    var slot = Slot(0);
    var function = OneBlock(Store(slot, V(0)), Call(), Move(1, slot));
    var allocation = new Dictionary<int, Reg> { [0] = Reg.AX, [1] = Reg.BX };
    MarkOptimized(function);

    Assert.That(LateLoadStoreOptimization.Run(function, allocation), Is.Zero);
    Assert.That(function.Blocks[0].Instructions[2].Effect.ReadsMemory, Is.True);
  }

  [Test]
  public void LateLoadStore_GivenUnreadStoreOverwritten_WhenRun_ThenFirstStoreIsRemoved() {
    var slot = Slot(0, MRegSize.Dword);
    var function = OneBlock(
      Store(slot, new MOperand.Immediate(0x11223344)),
      Store(slot, new MOperand.Immediate(0x55667788)));
    MarkOptimized(function);

    Assert.That(LateLoadStoreOptimization.Run(function, new Dictionary<int, Reg>()), Is.EqualTo(1));
    Assert.That(function.Blocks[0].Instructions, Has.Count.EqualTo(1));
    Assert.That(((MOperand.Immediate)function.Blocks[0].Instructions[0].Operands[1]).Value, Is.EqualTo(0x55667788));
  }

  [Test]
  public void LateLoadStore_GivenPartialReadBeforeOverwrite_WhenRun_ThenOverlappingFirstStoreStays() {
    var whole = Slot(0, MRegSize.Dword);
    var highWord = Slot(0, MRegSize.Word, disp: 2);
    var function = OneBlock(
      Store(whole, new MOperand.Immediate(0x11223344)),
      Move(0, highWord),
      Store(whole, new MOperand.Immediate(0x55667788)));
    var allocation = new Dictionary<int, Reg> { [0] = Reg.AX };
    MarkOptimized(function);

    Assert.That(LateLoadStoreOptimization.Run(function, allocation), Is.Zero);
    Assert.That(function.Blocks[0].Instructions, Has.Count.EqualTo(3));
  }
}
