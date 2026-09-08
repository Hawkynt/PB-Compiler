using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>Regression coverage for O0357 post-register-allocation register-view matching.</summary>
[TestFixture]
public sealed class O0357PostRegisterAllocationPeepholeTests {

  private static MOperand.Register V(int id, MRegSize size = MRegSize.Word) => new(MReg.Virtual(id, size));

  private static MInstr Move(int destination, MOperand source, MRegSize size = MRegSize.Word)
    => new(MOpcode.Mov, [V(destination, size), source],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: source is MOperand.Register ? [1] : [],
        ReadsFlags: false, WritesFlags: false, ReadsMemory: source.IsMemoryAccess(), WritesMemory: false));

  private static MFunction OneBlock(params MInstr[] instructions) {
    var function = new MFunction("f") { VirtualRegisterCount = 4 };
    var block = new MBlock("entry");
    block.Instructions.AddRange(instructions);
    function.Blocks.Add(block);
    Peephole.Run(function);
    return function;
  }

  [TestCase(Reg.AX, Reg.AL)]
  [TestCase(Reg.CX, Reg.CL)]
  [TestCase(Reg.DX, Reg.DL)]
  [TestCase(Reg.BX, Reg.BL)]
  public void PostRa_GivenByteVirtualAllocatedToContainingWordAndPinnedLowByteCopy_ThenDefinitionSurvives(
      Reg allocated, Reg lowByte) {
    var function = OneBlock(
      Move(0, new MOperand.Immediate(7), MRegSize.Byte),
      Move(1, new MOperand.Register(MReg.Physical_(lowByte, MRegSize.Byte)), MRegSize.Byte));
    var allocation = new Dictionary<int, Reg> { [0] = allocated, [1] = allocated };

    Assert.That(PostRegisterAllocationPeepholes.Run(function, allocation), Is.EqualTo(1));

    var remaining = function.Blocks[0].Instructions.Single();
    Assert.That(remaining.Operands[1], Is.EqualTo(new MOperand.Immediate(7)),
      "the low-byte self-copy may disappear, but the definition feeding it must not");
  }
}
