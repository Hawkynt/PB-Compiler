using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>Regression coverage for O0358 physical-register alias invalidation.</summary>
[TestFixture]
public sealed class O0358RegisterAliasTests {

  [Test]
  public void LateLoadStore_GivenByteSpillHeldInAxAndAlIsOverwritten_ThenReloadStaysInMemory() {
    var (function, block, slot) = SpillFunction(MRegSize.Byte);
    block.Instructions.Add(Store(slot, V(0, MRegSize.Byte)));
    block.Instructions.Add(WriteFixed(Reg.AL, MRegSize.Byte, 0x7F));
    block.Instructions.Add(Load(1, slot, MRegSize.Byte));
    var allocation = new Dictionary<int, Reg> { [0] = Reg.AX, [1] = Reg.BX };

    Assert.That(LateLoadStoreOptimization.Run(function, allocation), Is.Zero);
    Assert.That(block.Instructions[2].Effect.ReadsMemory, Is.True,
      "a byte virtual allocated AX is emitted through AL, so an AL write kills the forwarded value");
  }

  [Test]
  public void LateLoadStore_GivenByteSpillHeldInAlAndAhIsOverwritten_ThenReloadStillForwards() {
    var (function, block, slot) = SpillFunction(MRegSize.Byte);
    block.Instructions.Add(Store(slot, V(0, MRegSize.Byte)));
    block.Instructions.Add(WriteFixed(Reg.AH, MRegSize.Byte, 0x7F));
    block.Instructions.Add(Load(1, slot, MRegSize.Byte));
    var allocation = new Dictionary<int, Reg> { [0] = Reg.AX, [1] = Reg.BX };

    Assert.That(LateLoadStoreOptimization.Run(function, allocation), Is.EqualTo(1));
    Assert.Multiple(() => {
      Assert.That(block.Instructions[2].Effect.ReadsMemory, Is.False,
        "AL and AH are disjoint byte slices and should not be treated as the same value lane");
      Assert.That(block.Instructions[2].Operands[1],
        Is.EqualTo(new MOperand.Register(MReg.Physical_(Reg.AL, MRegSize.Byte))));
    });
  }

  [Test]
  public void LateLoadStore_GivenWordSpillHeldInAxAndAhIsOverwritten_ThenReloadStaysInMemory() {
    var (function, block, slot) = SpillFunction(MRegSize.Word);
    block.Instructions.Add(Store(slot, V(0)));
    block.Instructions.Add(WriteFixed(Reg.AH, MRegSize.Byte, 0x7F));
    block.Instructions.Add(Load(1, slot));
    var allocation = new Dictionary<int, Reg> { [0] = Reg.AX, [1] = Reg.BX };

    Assert.That(LateLoadStoreOptimization.Run(function, allocation), Is.Zero);
    Assert.That(block.Instructions[2].Effect.ReadsMemory, Is.True,
      "AH overwrites bits 8..15 of AX, so the word-sized forwarded value no longer exists");
  }

  [Test]
  public void LateLoadStore_GivenDwordSpillHeldInEaxAndAxIsOverwritten_ThenReloadStaysInMemory() {
    var (function, block, slot) = SpillFunction(MRegSize.Dword);
    block.Instructions.Add(Store(slot, V(0, MRegSize.Dword)));
    block.Instructions.Add(WriteFixed(Reg.AX, MRegSize.Word, 0x1234));
    block.Instructions.Add(Load(1, slot, MRegSize.Dword));
    var allocation = new Dictionary<int, Reg> { [0] = Reg.EAX, [1] = Reg.EBX };

    Assert.That(LateLoadStoreOptimization.Run(function, allocation), Is.Zero);
    Assert.That(block.Instructions[2].Effect.ReadsMemory, Is.True,
      "AX overwrites the low half of EAX, so a dword spill fact backed by EAX is invalid");
  }

  private static (MFunction Function, MBlock Block, MOperand.StackSlot Slot) SpillFunction(MRegSize size) {
    var function = new MFunction("f") { VirtualRegisterCount = 4 };
    var block = new MBlock("entry");
    function.Blocks.Add(block);
    Peephole.Run(function);                         // selected frame boundary: no source-owned slots
    function.StackSlots.Add(Bytes(size));          // allocator/spiller appends spill slot zero
    return (function, block, new MOperand.StackSlot(0, size));
  }

  private static MOperand.Register V(int id, MRegSize size = MRegSize.Word)
    => new(MReg.Virtual(id, size));

  private static MInstr Store(MOperand.StackSlot slot, MOperand source)
    => new(MOpcode.Mov, [slot, source],
      new MInstrEffect(WrittenRegs: [], ReadRegs: source is MOperand.Register ? [1] : [],
        ReadsFlags: false, WritesFlags: false, ReadsMemory: false, WritesMemory: true));

  private static MInstr Load(int destination, MOperand.StackSlot slot, MRegSize size = MRegSize.Word)
    => new(MOpcode.Mov, [V(destination, size), slot],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false,
        ReadsMemory: true, WritesMemory: false));

  private static MInstr WriteFixed(Reg destination, MRegSize size, long value)
    => new(MOpcode.Mov,
      [new MOperand.Register(MReg.Physical_(destination, size)), new MOperand.Immediate(value)],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false,
        ReadsMemory: false, WritesMemory: false));

  private static int Bytes(MRegSize size) => size switch {
    MRegSize.Byte => 1,
    MRegSize.Word => 2,
    MRegSize.Dword => 4,
    MRegSize.Qword => 8,
    MRegSize.Tbyte => 10,
    _ => throw new ArgumentOutOfRangeException(nameof(size)),
  };
}
