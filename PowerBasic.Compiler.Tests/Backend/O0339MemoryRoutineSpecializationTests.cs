using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>O0339 target hand-off: widest-first IR accesses become legal native machine moves per CPU.</summary>
[TestFixture]
public sealed class O0339MemoryRoutineSpecializationTests {

  [Test]
  public void Schedule_GivenFourByteMemcpyOn386_ThenUsesOneNativeDwordTransfer() {
    var cost = new TargetCost(CpuTier.I80386, CostObjective.Balanced);
    var target = new SelectionTarget(CpuLevel: 386, Optimize: true);
    var fn = MemcpyFunction(4);
    Assert.That(MemoryRoutineSpecialization.Run(fn, cost), Is.EqualTo(1));

    var machine = SelectAndSchedule(fn, target);

    var dwordLoads = machine.AllInstructions.Where(instruction => instruction.Opcode == MOpcode.Mov
      && instruction.Operands is [MOperand.Register { Reg.Size: MRegSize.Dword }, var source]
      && OperandSize(source) == MRegSize.Dword).ToList();
    var dwordStores = machine.AllInstructions.Where(instruction => instruction.Opcode == MOpcode.Mov
      && instruction.Operands is [var destination, MOperand.Register { Reg.Size: MRegSize.Dword }]
      && OperandSize(destination) == MRegSize.Dword).ToList();
    Assert.Multiple(() => {
      Assert.That(dwordLoads, Has.Count.EqualTo(1));
      Assert.That(dwordStores, Has.Count.EqualTo(1));
      Assert.That(machine.AllInstructions.SelectMany(instruction => instruction.Operands)
        .OfType<MOperand.Register>().Count(operand => operand.Reg.IsVirtual && operand.Reg.Size == MRegSize.Dword),
        Is.EqualTo(2), "the same dword virtual appears once at its load and once at its store");
    });

    var allocation = LinearScanAllocator.Allocate(machine, target, out var reason);
    Assert.That(allocation, Is.Not.Null, $"allocation declined: {reason}");
  }

  [Test]
  public void Schedule_GivenEightByteMemcpyOn286_ThenContainsNoDwordOperand() {
    var cost = new TargetCost(CpuTier.I80286, CostObjective.Speed);
    var target = new SelectionTarget(CpuLevel: 286, Optimize: true, OptimizeSpeed: true, Cost: cost);
    var fn = MemcpyFunction(8);
    Assert.That(MemoryRoutineSpecialization.Run(fn, cost), Is.EqualTo(1));

    var machine = SelectAndSchedule(fn, target);

    Assert.That(machine.AllInstructions.SelectMany(instruction => instruction.Operands).Any(IsDwordOperand), Is.False,
      "a target-aware combine must not smuggle a 386 operand-size prefix into a 286 image");
    var allocation = LinearScanAllocator.Allocate(machine, target, out var reason);
    Assert.That(allocation, Is.Not.Null, $"allocation declined: {reason}");
  }

  [Test]
  public void Schedule_GivenFourByteConstantMemsetOn386Balanced_ThenUsesOneDwordImmediateStore() {
    var cost = new TargetCost(CpuTier.I80386, CostObjective.Balanced);
    var target = new SelectionTarget(CpuLevel: 386, Optimize: true);
    var fn = MemsetFunction(4, 0x5a);
    Assert.That(MemoryRoutineSpecialization.Run(fn, cost), Is.EqualTo(1));

    var machine = SelectAndSchedule(fn, target);

    var store = machine.AllInstructions.SingleOrDefault(instruction => instruction.Opcode == MOpcode.Mov
      && instruction.Operands is [var destination, MOperand.Immediate { Value: 0x5a5a5a5a }]
      && OperandSize(destination) == MRegSize.Dword);
    Assert.That(store, Is.Not.Null,
      "DWORD legality belongs to the 386 target, not to the selector's separate SPEED residency policy");
  }

  [Test]
  public void MachineCombiner_GivenAWordPairWithAnotherUse_ThenDoesNotWidenTheCopy() {
    var low = MReg.Virtual(0);
    var high = MReg.Virtual(1);
    var extra = MReg.Virtual(2);
    var function = new MFunction("shared") { VirtualRegisterCount = 3 };
    var block = new MBlock("entry");
    var sourceLow = new MOperand.DataCell("source", 0, MRegSize.Word);
    var sourceHigh = new MOperand.DataCell("source", 2, MRegSize.Word);
    var targetLow = new MOperand.DataCell("target", 0, MRegSize.Word);
    var targetHigh = new MOperand.DataCell("target", 2, MRegSize.Word);
    block.Instructions.Add(Move(new MOperand.Register(low), sourceLow, write: true, read: false));
    block.Instructions.Add(Move(new MOperand.Register(high), sourceHigh, write: true, read: false));
    block.Instructions.Add(Move(targetLow, new MOperand.Register(low), write: false, read: true));
    block.Instructions.Add(Move(targetHigh, new MOperand.Register(high), write: false, read: true));
    block.Instructions.Add(Move(new MOperand.Register(extra), new MOperand.Register(low), write: true, read: true));
    function.Blocks.Add(block);

    Assert.That(MachineCombiner.Run(function, new SelectionTarget(CpuLevel: 386, Optimize: true)), Is.Zero);
    Assert.That(function.AllInstructions.SelectMany(instruction => instruction.Operands).Any(IsDwordOperand), Is.False,
      "the widening is only valid for the private load-to-store transfer value");
  }

  private static MFunction SelectAndSchedule(IrFunction fn, SelectionTarget target) {
    var machine = InstructionSelector.TrySelect(fn, out var reason, target);
    Assert.That(machine, Is.Not.Null, $"selection declined: {reason}");
    MachineScheduler.Schedule(machine!, target);
    return machine!;
  }

  private static IrFunction MemcpyFunction(int size) {
    var memcpy = new IrFunction("llvm.memcpy.p0.p0.i32", IrType.Void, [
      new IrArgument(IrType.Ptr, 0), new IrArgument(IrType.Ptr, 1),
      new IrArgument(IrType.I32, 2), new IrArgument(IrType.I1, 3),
    ]);
    var fn = new IrFunction("copy", IrType.Void);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var source = entry.Append(new IrAlloca(IrType.I8) { Count = size });
    var target = entry.Append(new IrAlloca(IrType.I8) { Count = size });
    entry.Append(new IrCall(IrType.Void, memcpy, [target, source,
      new IrConstantInt(IrType.I32, size), new IrConstantInt(IrType.I1, 0)]));
    entry.Append(new IrRet());
    return fn;
  }

  private static IrFunction MemsetFunction(int size, byte fill) {
    var memset = new IrFunction("llvm.memset.p0.i32", IrType.Void, [
      new IrArgument(IrType.Ptr, 0), new IrArgument(IrType.I8, 1),
      new IrArgument(IrType.I32, 2), new IrArgument(IrType.I1, 3),
    ]);
    var fn = new IrFunction("fill", IrType.Void);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var target = entry.Append(new IrAlloca(IrType.I8) { Count = size });
    entry.Append(new IrCall(IrType.Void, memset, [target, new IrConstantInt(IrType.I8, fill),
      new IrConstantInt(IrType.I32, size), new IrConstantInt(IrType.I1, 0)]));
    entry.Append(new IrRet());
    return fn;
  }

  private static MInstr Move(MOperand destination, MOperand source, bool write, bool read)
    => new(MOpcode.Mov, [destination, source], new MInstrEffect(
      WrittenRegs: write && destination is MOperand.Register ? [0] : [],
      ReadRegs: read && source is MOperand.Register ? [1] : [],
      ReadsFlags: false, WritesFlags: false,
      ReadsMemory: source.IsMemoryAccess(), WritesMemory: destination.IsMemoryAccess()));

  private static bool IsDwordOperand(MOperand operand) => operand switch {
    MOperand.Register { Reg.Size: MRegSize.Dword } => true,
    _ => OperandSize(operand) == MRegSize.Dword,
  };

  private static MRegSize? OperandSize(MOperand operand) => operand switch {
    MOperand.Memory memory => memory.Size,
    MOperand.StackSlot stack => stack.Size,
    MOperand.DataCell data => data.Size,
    MOperand.ParamCell parameter => parameter.Size,
    _ => null,
  };
}
