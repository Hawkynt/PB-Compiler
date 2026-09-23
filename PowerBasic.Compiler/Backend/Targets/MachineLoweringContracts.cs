using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Backend.Targets;

/// <summary>
/// Target-owned lowering of one Low IR function. Selection and allocation are separate because
/// production routing may need to select a call-graph candidate before pruning stranded callers.
/// </summary>
public interface IMachineFunctionLowerer {
  bool TrySelect(IrFunction function, out MFunction? selected, out string? error);

  bool TryAllocate(IrFunction source, MFunction selected,
      out IrMachineFunction? machine, out string? error);
}

/// <summary>Current x86 lowering implementation shared by the 16-, 32- and 64-bit x86 contracts.</summary>
public sealed class X86MachineLowering(SelectionTarget target) : IMachineFunctionLowerer {
  public bool TrySelect(IrFunction function, out MFunction? selected, out string? error) {
    ArgumentNullException.ThrowIfNull(function);
    selected = null;
    if (function.IsDeclaration || function.Entry is null) {
      error = "selection: declaration";
      return false;
    }
    if (InstructionSelector.TrySelect(function, out var declineReason, target) is not { } machine) {
      error = "selection: " + (declineReason ?? "unknown machine construct");
      return false;
    }
    selected = machine;
    error = null;
    return true;
  }

  public bool TryAllocate(IrFunction source, MFunction selected,
      out IrMachineFunction? machine, out string? error) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(selected);
    machine = null;
    MachineScheduler.Schedule(selected, target);
    if (LinearScanAllocator.Allocate(selected, target, out var allocationReason) is not { } allocation) {
      error = "allocation: " + (allocationReason ?? "register allocation failed");
      return false;
    }
    PostRegisterAllocationPeepholes.Run(selected, allocation);
    LateLoadStoreOptimization.Run(selected, allocation);
    machine = new IrMachineFunction(source, selected, allocation);
    error = null;
    return true;
  }
}
