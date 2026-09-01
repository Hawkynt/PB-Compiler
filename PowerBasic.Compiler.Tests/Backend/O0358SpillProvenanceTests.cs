using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;

namespace PowerBasic.Compiler.Tests.Backend;

[TestFixture]
public sealed class O0358SpillProvenanceTests {

  [Test]
  public void LateLoadStore_GivenSelectorSlotAndLaterSpillSlot_ThenOnlyAllocatorOwnedSlotIsForwarded() {
    var function = new MFunction("f") { VirtualRegisterCount = 4 };
    function.StackSlots.Add(2);                    // selected/source-owned slot 0
    var block = new MBlock("entry");
    function.Blocks.Add(block);

    Peephole.Run(function);                        // records the selected frame boundary at one slot
    function.StackSlots.Add(2);                    // allocator/spiller-owned slot 1

    var sourceSlot = new MOperand.StackSlot(0, MRegSize.Word);
    var spillSlot = new MOperand.StackSlot(1, MRegSize.Word);
    block.Instructions.Add(Store(sourceSlot, 0));
    block.Instructions.Add(Load(1, sourceSlot));
    block.Instructions.Add(Store(spillSlot, 2));
    block.Instructions.Add(Load(3, spillSlot));
    var allocation = new Dictionary<int, Reg> {
      [0] = Reg.AX, [1] = Reg.BX, [2] = Reg.CX, [3] = Reg.DX,
    };

    Assert.That(LateLoadStoreOptimization.Run(function, allocation), Is.EqualTo(1));
    Assert.Multiple(() => {
      Assert.That(block.Instructions[1].Effect.ReadsMemory, Is.True,
        "selector-owned frame storage is not an O0358 spill fact");
      Assert.That(block.Instructions[3].Effect.ReadsMemory, Is.False,
        "the post-selection allocator slot is compiler-private and can forward");
      Assert.That(block.Instructions[3].Operands[1],
        Is.EqualTo(new MOperand.Register(MReg.Physical_(Reg.CX))));
    });
  }

  private static MInstr Store(MOperand.StackSlot slot, int source) => new(MOpcode.Mov,
    [slot, new MOperand.Register(MReg.Virtual(source))],
    new MInstrEffect(WrittenRegs: [], ReadRegs: [1], ReadsFlags: false, WritesFlags: false,
      ReadsMemory: false, WritesMemory: true));

  private static MInstr Load(int destination, MOperand.StackSlot slot) => new(MOpcode.Mov,
    [new MOperand.Register(MReg.Virtual(destination)), slot],
    new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false,
      ReadsMemory: true, WritesMemory: false));
}
