using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;

namespace PowerBasic.Compiler.Tests.Backend;

[TestFixture]
public sealed class O0064LeaFusionTests {

  private static readonly MInstrEffect _copyEffect = new([0], [1], false, false, false, false);
  private static readonly MInstrEffect _binaryEffect = new([0], [0, 1], false, true, false, false);
  private static readonly MInstrEffect _compareEffect = new([], [0], false, true, false, false);

  [Test]
  public void Lea386_GivenScaledBaseIndexAndDisp8_WhenAssembled_ThenUsesAddressOverrideAndSib() {
    var assembler = new Assembler();

    assembler.Lea386(Reg.EAX, Mem.AtScaled(Reg.EAX, Reg.ECX, 4, 8));

    Assert.That(assembler.ToArray(), Is.EqualTo(new byte[] { 0x67, 0x66, 0x8D, 0x44, 0x88, 0x08 }));
  }

  [Test]
  public void Lea386_GivenIndexWithoutBase_WhenAssembled_ThenUsesSibNoBaseDisp32Form() {
    var assembler = new Assembler();

    assembler.Lea386(Reg.EAX, Mem.AtScaled(null, Reg.ECX, 8, 0x12345678));

    Assert.That(assembler.ToArray(), Is.EqualTo(new byte[] {
      0x67, 0x66, 0x8D, 0x04, 0xCD, 0x78, 0x56, 0x34, 0x12,
    }));
  }

  [Test]
  public void Lea386_GivenEbpWithoutDisplacement_WhenAssembled_ThenUsesRequiredZeroDisp8() {
    var assembler = new Assembler();

    assembler.Lea386(Reg.EAX, Mem.At(Reg.EBP));

    Assert.That(assembler.ToArray(), Is.EqualTo(new byte[] { 0x67, 0x66, 0x8D, 0x45, 0x00 }));
  }

  [Test]
  public void Mem_GivenEspAsScaledIndex_WhenConstructed_ThenRejectsReservedSibIndex() {
    Assert.Throws<ArgumentException>(() => Mem.AtScaled(Reg.EAX, Reg.ESP, 2));
  }

  [Test]
  public void PostRa_GivenDwordShiftAddWithDeadFlags_WhenRun_ThenFusesXTimesFiveIntoLea() {
    var x = MReg.Virtual(0, MRegSize.Dword);
    var flags = MReg.Virtual(1, MRegSize.Dword);
    var result = MReg.Virtual(2, MRegSize.Dword);
    var function = OneBlock(
      new MInstr(MOpcode.Mov, [new MOperand.Register(result), new MOperand.Register(x)], _copyEffect),
      new MInstr(MOpcode.Shl, [new MOperand.Register(result), new MOperand.Immediate(2)], _binaryEffect),
      new MInstr(MOpcode.Add, [new MOperand.Register(result), new MOperand.Register(x)], _binaryEffect),
      FlagOverwrite(flags));
    MarkOptimized(function);
    IReadOnlyDictionary<int, Reg> allocation = new Dictionary<int, Reg> {
      [0] = Reg.EAX,
      [1] = Reg.EDX,
      [2] = Reg.ECX,
    };

    Assert.That(PostRegisterAllocationPeepholes.Run(function, allocation), Is.EqualTo(1));

    Assert.That(function.Blocks[0].Instructions, Has.Count.EqualTo(2));
    var lea = function.Blocks[0].Instructions[0];
    Assert.Multiple(() => {
      Assert.That(lea.Opcode, Is.EqualTo(MOpcode.Lea));
      Assert.That(lea.Effect.WritesFlags, Is.False);
      Assert.That(lea.Operands[0], Is.EqualTo(new MOperand.Register(result)));
      Assert.That(lea.Operands[1], Is.EqualTo(new MOperand.Memory(x, x, 4, 0, MRegSize.Dword)));
    });
  }

  [Test]
  public void PostRa_GivenDwordRegisterAddWithDeadFlags_WhenRun_ThenFusesBaseIndexLea() {
    var x = MReg.Virtual(0, MRegSize.Dword);
    var y = MReg.Virtual(1, MRegSize.Dword);
    var result = MReg.Virtual(2, MRegSize.Dword);
    var function = OneBlock(
      new MInstr(MOpcode.Mov, [new MOperand.Register(result), new MOperand.Register(x)], _copyEffect),
      new MInstr(MOpcode.Add, [new MOperand.Register(result), new MOperand.Register(y)], _binaryEffect),
      FlagOverwrite(x));
    MarkOptimized(function);
    IReadOnlyDictionary<int, Reg> allocation = new Dictionary<int, Reg> {
      [0] = Reg.EAX,
      [1] = Reg.EBX,
      [2] = Reg.ECX,
    };

    Assert.That(PostRegisterAllocationPeepholes.Run(function, allocation), Is.EqualTo(1));

    Assert.That(function.Blocks[0].Instructions[0].Operands[1],
      Is.EqualTo(new MOperand.Memory(x, y, 1, 0, MRegSize.Dword)));
  }

  [Test]
  public void PostRa_GivenDwordAddImmediateWithDeadFlags_WhenRun_ThenFusesDisplacementLea() {
    var x = MReg.Virtual(0, MRegSize.Dword);
    var result = MReg.Virtual(1, MRegSize.Dword);
    var function = OneBlock(
      new MInstr(MOpcode.Mov, [new MOperand.Register(result), new MOperand.Register(x)], _copyEffect),
      new MInstr(MOpcode.Add, [new MOperand.Register(result), new MOperand.Immediate(127)], _binaryEffect),
      FlagOverwrite(x));
    MarkOptimized(function);
    IReadOnlyDictionary<int, Reg> allocation = new Dictionary<int, Reg> {
      [0] = Reg.EAX,
      [1] = Reg.ECX,
    };

    Assert.That(PostRegisterAllocationPeepholes.Run(function, allocation), Is.EqualTo(1));
    Assert.That(function.Blocks[0].Instructions[0].Operands[1],
      Is.EqualTo(new MOperand.Memory(x, null, 1, 127, MRegSize.Dword)));
  }

  [Test]
  public void PostRa_GivenScaledAddWhoseFlagsAreRead_WhenRun_ThenDoesNotFuse() {
    var x = MReg.Virtual(0, MRegSize.Dword);
    var result = MReg.Virtual(1, MRegSize.Dword);
    var function = OneBlock(
      new MInstr(MOpcode.Mov, [new MOperand.Register(result), new MOperand.Register(x)], _copyEffect),
      new MInstr(MOpcode.Shl, [new MOperand.Register(result), new MOperand.Immediate(2)], _binaryEffect),
      new MInstr(MOpcode.Add, [new MOperand.Register(result), new MOperand.Register(x)], _binaryEffect),
      new MInstr(MOpcode.Adc, [new MOperand.Register(result), new MOperand.Immediate(0)],
        new MInstrEffect([0], [0], true, true, false, false)));
    MarkOptimized(function);
    IReadOnlyDictionary<int, Reg> allocation = new Dictionary<int, Reg> {
      [0] = Reg.EAX,
      [1] = Reg.ECX,
    };

    Assert.That(PostRegisterAllocationPeepholes.Run(function, allocation), Is.Zero);
    Assert.That(function.Blocks[0].Instructions.Select(instruction => instruction.Opcode),
      Is.EqualTo(new[] { MOpcode.Mov, MOpcode.Shl, MOpcode.Add, MOpcode.Adc }));
  }

  [Test]
  public void PostRa_GivenUnoptimizedFunction_WhenRun_ThenLeavesLeaCandidateUntouched() {
    var x = MReg.Virtual(0, MRegSize.Dword);
    var result = MReg.Virtual(1, MRegSize.Dword);
    var function = OneBlock(
      new MInstr(MOpcode.Mov, [new MOperand.Register(result), new MOperand.Register(x)], _copyEffect),
      new MInstr(MOpcode.Add, [new MOperand.Register(result), new MOperand.Immediate(4)], _binaryEffect),
      FlagOverwrite(x));
    IReadOnlyDictionary<int, Reg> allocation = new Dictionary<int, Reg> {
      [0] = Reg.EAX,
      [1] = Reg.ECX,
    };

    Assert.That(PostRegisterAllocationPeepholes.Run(function, allocation), Is.Zero);
    Assert.That(function.Blocks[0].Instructions[0].Opcode, Is.EqualTo(MOpcode.Mov));
  }

  private static MInstr FlagOverwrite(MReg register) => new(MOpcode.Cmp,
    [new MOperand.Register(register), new MOperand.Immediate(7)], _compareEffect);

  private static MFunction OneBlock(params MInstr[] instructions) {
    var function = new MFunction("o0064") { VirtualRegisterCount = 4 };
    var block = new MBlock("entry");
    block.Instructions.AddRange(instructions);
    function.Blocks.Add(block);
    return function;
  }

  private static void MarkOptimized(MFunction function) => Peephole.Run(function);
}
