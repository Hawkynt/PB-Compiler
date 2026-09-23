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

/// <summary>x86 instruction selection owned by the x86 target, not by the generic machine pipeline.</summary>
public sealed class X86MachineSelector(SelectionTarget target) : IMachineSelector<MFunction> {
  public MFunction? TrySelect(IrFunction function, out string? declineReason)
    => InstructionSelector.TrySelect(function, out declineReason, target);
}

/// <summary>x86 register allocation owned by the x86 target.</summary>
public sealed class X86MachineAllocator(SelectionTarget target)
    : IMachineAllocator<MFunction, IReadOnlyDictionary<int, Reg>> {
  public IReadOnlyDictionary<int, Reg>? TryAllocate(MFunction function, out string? declineReason)
    => LinearScanAllocator.Allocate(function, target, out declineReason);
}

/// <summary>Current x86 lowering implementation shared by the 16-, 32- and 64-bit x86 contracts.</summary>
public sealed class X86MachineLowering : IMachineFunctionLowerer {
  private readonly SelectionTarget _target;
  private readonly X86MachineSelector _selector;
  private readonly X86MachineAllocator _allocator;

  public X86MachineLowering(SelectionTarget target) {
    this._target = target;
    this._selector = new(target);
    this._allocator = new(target);
  }

  public bool TrySelect(IrFunction function, out MFunction? selected, out string? error) {
    ArgumentNullException.ThrowIfNull(function);
    selected = null;
    if (function.IsDeclaration || function.Entry is null) {
      error = "selection: declaration";
      return false;
    }
    if (this._selector.TrySelect(function, out var declineReason) is not { } machine) {
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
    MachineScheduler.Schedule(selected, this._target);
    if (this._allocator.TryAllocate(selected, out var allocationReason) is not { } allocation) {
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
