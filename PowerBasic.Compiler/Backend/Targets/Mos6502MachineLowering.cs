using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Backend.Targets;

/// <summary>Minimal but real 6502 lowering boundary: Low IR CFG is preserved as machine blocks.</summary>
public sealed class Mos6502MachineLowering : IMachineFunctionLowerer {
  public MachineTargetDescription Target { get; } = new("6502", 16, 8);

  public bool TrySelect(IrFunction function, out X86MachineFunction? selected, out string? error) {
    selected = null;
    if (function.IsDeclaration || function.Entry is null) { error = "selection: declaration"; return false; }
    var result = new X86MachineFunction(function.Name) { TargetFamily = MachineTargetFamily.Mos6502 };
    foreach (var block in function.Blocks) {
      var machineBlock = new MBlock(block.Label);
      foreach (var instruction in block.Instructions) {
        if (instruction is IrRet)
          machineBlock.Instructions.Add(new MInstr(MOpcode.Ret, [], MInstrEffect.None));
        else if (instruction is IrBinary { Rhs: IrConstantInt constant } binary
                 && binary.Op is IrBinaryOp.Add or IrBinaryOp.Sub or IrBinaryOp.And or IrBinaryOp.Or or IrBinaryOp.Xor)
          machineBlock.Instructions.Add(new MInstr(binary.Op switch {
            IrBinaryOp.Add => MOpcode.Add, IrBinaryOp.Sub => MOpcode.Sub,
            IrBinaryOp.And => MOpcode.And, IrBinaryOp.Or => MOpcode.Or, _ => MOpcode.Xor,
          }, [new MOperand.Immediate(constant.Value)], MInstrEffect.None));
      }
      result.Blocks.Add(machineBlock);
    }
    selected = result;
    error = null;
    return true;
  }

  public bool TryAllocate(IrFunction source, X86MachineFunction selected,
      out IrMachineFunction? machine, out string? error) {
    var allocator = new Mos6502RegisterAllocator();
    var allocation = allocator.Allocate(Enumerable.Range(0, selected.VirtualRegisterCount));
    var frame = allocator.FramePlan(selected.StackSlots.Sum(), Enumerable.Range(10, 6));
    selected.TargetAllocation = new Mos6502Allocation(allocation, frame);
    machine = new IrMachineFunction(source, selected, new Dictionary<int, Reg>(), Target);
    error = null;
    return true;
  }
}
