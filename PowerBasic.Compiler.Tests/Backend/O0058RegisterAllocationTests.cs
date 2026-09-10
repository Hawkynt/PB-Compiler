using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// O0058's 386-only whole-register representation for LONG/DWORD values. The public stack ABI remains
/// two words; only an optimized SPEED selection may keep an incoming 32-bit value whole in MIR and
/// carry that representation through a loop recurrence.
/// </summary>
[TestFixture]
public sealed class O0058RegisterAllocationTests {

  private static readonly SelectionTarget _speed386 = new(CpuLevel: 386, Optimize: true, OptimizeSpeed: true);

  [Test]
  public void Select_GivenLongParameter_When386Speed_ThenLoadsOneDwordVirtualRegister() {
    var fn = IdentityFunction();

    var machine = InstructionSelector.TrySelect(fn, out var reason, _speed386);

    Assert.That(machine, Is.Not.Null, $"selection declined: {reason}");
    var incoming = machine!.ArgumentLoads.Where(load => load.ArgumentIndex == 0).ToList();
    Assert.That(incoming, Has.Count.EqualTo(1), "O0058 keeps the incoming LONG whole under 386 SPEED");
    var virtualId = incoming[0].VirtualId;
    Assert.That(machine.AllInstructions
      .SelectMany(instruction => instruction.Operands)
      .OfType<MOperand.Register>()
      .Any(operand => operand.Reg.IsVirtual && operand.Reg.VirtualId == virtualId
        && operand.Reg.Size == MRegSize.Dword), Is.True,
      "the one prologue load must feed a dword vreg, not the low half of a hidden pair");
  }

  [TestCase(86, true, true, TestName = "Select_Given8086SpeedLongParameter_ThenKeepsWordPair")]
  [TestCase(386, false, true, TestName = "Select_Given386OptimizeOffLongParameter_ThenKeepsWordPair")]
  [TestCase(386, true, false, TestName = "Select_Given386WithoutSpeedLongParameter_ThenKeepsWordPair")]
  public void Select_GivenTargetOutside386Speed_WhenLongParameter_ThenKeepsTwoWordLoads(
      int cpu, bool optimize, bool optimizeSpeed) {
    var fn = IdentityFunction();
    var target = new SelectionTarget(CpuLevel: cpu, Optimize: optimize, OptimizeSpeed: optimizeSpeed);

    var machine = InstructionSelector.TrySelect(fn, out var reason, target);

    Assert.That(machine, Is.Not.Null, $"selection declined: {reason}");
    var incoming = machine!.ArgumentLoads.Where(load => load.ArgumentIndex == 0).ToList();
    Assert.That(incoming, Has.Count.EqualTo(2));
    Assert.That(incoming.Select(load => load.ByteDelta), Is.EquivalentTo(new[] { 0, 2 }));
    Assert.That(machine.AllInstructions
      .SelectMany(instruction => instruction.Operands)
      .OfType<MOperand.Register>()
      .Any(operand => operand.Reg.IsVirtual && incoming.Any(load => load.VirtualId == operand.Reg.VirtualId)
        && operand.Reg.Size == MRegSize.Dword), Is.False);
  }

  [Test]
  public void Select_GivenLongRecurrenceUsingLongParameter_When386Speed_ThenArgumentRemainsNativeLeaf() {
    var step = new IrArgument(IrType.I32, 0);
    var fn = new IrFunction("F", IrType.I32, [step]);
    var entry = fn.CreateBlock("entry");
    var head = fn.CreateBlock("head");
    var body = fn.CreateBlock("body");
    var exit = fn.CreateBlock("exit");

    new IrBuilder(entry).Br(head);
    var loop = new IrBuilder(head);
    var accumulator = loop.Phi(IrType.I32);
    loop.CondBr(loop.Cmp(IrCmpPred.Slt, accumulator, IrBuilder.ConstI32(4)), body, exit);

    var latch = new IrBuilder(body);
    var next = latch.Add(accumulator, step);
    latch.Br(head);
    accumulator.AddIncoming(IrBuilder.ConstI32(0), entry);
    accumulator.AddIncoming(next, body);
    new IrBuilder(exit).Ret(accumulator);

    var machine = InstructionSelector.TrySelect(fn, out var reason, _speed386);

    Assert.That(machine, Is.Not.Null, $"selection declined: {reason}");
    var argument = machine!.ArgumentLoads.Single(load => load.ArgumentIndex == 0);
    var add = machine.AllInstructions.Single(instruction => instruction.Opcode == MOpcode.Add
      && instruction.Operands[0] is MOperand.Register { Reg.Size: MRegSize.Dword });
    Assert.Multiple(() => {
      Assert.That(add.Operands[1] is MOperand.Register { Reg: var register }
        && register.IsVirtual && register.VirtualId == argument.VirtualId && register.Size == MRegSize.Dword,
        Is.True, "the parameter is a native dword leaf of the recurrence rather than forcing ADD/ADC pairs");
      Assert.That(machine.AllInstructions.Any(instruction => instruction.Opcode == MOpcode.Adc), Is.False);
      Assert.That(machine.AllInstructions.Any(instruction => instruction.Operands
        .OfType<MOperand.Register>()
        .Any(operand => operand.Reg.IsVirtual && operand.Reg.Size == MRegSize.Dword)), Is.True);
    });

    MachineScheduler.Schedule(machine);
    var allocation = LinearScanAllocator.Allocate(machine, _speed386, out var allocationReason);
    Assert.That(allocation, Is.Not.Null, $"allocation declined: {allocationReason}");
  }

  [Test]
  public void Select_GivenNativeLongParameterConsumedByPairAbi_When386Speed_ThenBridgesLosslesslyThroughDwordCell() {
    var fn = IdentityFunction();

    var machine = InstructionSelector.TrySelect(fn, out var reason, _speed386);

    Assert.That(machine, Is.Not.Null, $"selection declined: {reason}");
    var incoming = machine!.ArgumentLoads.Single(load => load.ArgumentIndex == 0);
    Assert.That(machine.StackSlots, Does.Contain(4), "the pair-only return ABI needs one four-byte bridge cell");
    Assert.That(machine.AllInstructions.Any(instruction => instruction.Opcode == MOpcode.Mov
      && instruction.Operands[0] is MOperand.StackSlot { Size: MRegSize.Dword }
      && instruction.Operands[1] is MOperand.Register { Reg: var source }
      && source.IsVirtual && source.VirtualId == incoming.VirtualId && source.Size == MRegSize.Dword), Is.True,
      "the whole parameter is stored before its low/high words are consumed by DX:AX return staging");
  }

  private static IrFunction IdentityFunction() {
    var argument = new IrArgument(IrType.I32, 0);
    var fn = new IrFunction("F", IrType.I32, [argument]);
    var entry = fn.CreateBlock("entry");
    new IrBuilder(entry).Ret(argument);
    return fn;
  }
}
