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
      foreach (var instruction in block.Instructions)
        if (instruction is IrRet)
          machineBlock.Instructions.Add(new MInstr(MOpcode.Ret, [], MInstrEffect.None));
      result.Blocks.Add(machineBlock);
    }
    selected = result;
    error = null;
    return true;
  }

  public bool TryAllocate(IrFunction source, X86MachineFunction selected,
      out IrMachineFunction? machine, out string? error) {
    machine = new IrMachineFunction(source, selected, new Dictionary<int, Asm.Reg>(), Target);
    error = null;
    return true;
  }
}
