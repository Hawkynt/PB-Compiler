using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Backend.Targets;

/// <summary>
/// Target-owned lowering of one Low IR function. Selection and allocation are separate because
/// production routing may need to select a call-graph candidate before pruning stranded callers.
/// </summary>
public interface IMachineFunctionLowerer {
  MachineTargetDescription Target { get; }

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

/// <summary>x86 machine scheduling owned by the x86 target.</summary>
public sealed class X86MachineScheduler(SelectionTarget target) : IMachineScheduler<MFunction> {
  public void Schedule(MFunction function) => MachineScheduler.Schedule(function, target);
}

/// <summary>x86 rewrites that require the final physical-register assignment.</summary>
public sealed class X86MachinePostAllocation
    : IMachinePostAllocation<MFunction, IReadOnlyDictionary<int, Reg>> {
  public void Run(MFunction function, IReadOnlyDictionary<int, Reg> allocation) {
    PostRegisterAllocationPeepholes.Run(function, allocation);
    LateLoadStoreOptimization.Run(function, allocation);
  }
}

/// <summary>Current x86 lowering implementation shared by the 16-, 32- and 64-bit x86 contracts.</summary>
public sealed class X86MachineLowering : IMachineFunctionLowerer {
  private readonly X86MachineSelector _selector;
  private readonly X86MachineAllocator _allocator;
  private readonly X86MachineScheduler _scheduler;
  private readonly X86MachinePostAllocation _postAllocation = new();

  public X86MachineLowering(SelectionTarget target) {
    this.Target = target.TargetFamily switch {
      MachineTargetFamily.X86_64 => new("x86-64", 64, 64),
      MachineTargetFamily.X86_32 => new("x86-32", 32, 32),
      _ => new("x86-16", 16, 16),
    };
    this._selector = new(target);
    this._allocator = new(target);
    this._scheduler = new(target);
  }

  public MachineTargetDescription Target { get; }

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
    machine.TargetFamily = this.Target.Name switch {
      "x86-64" => MachineTargetFamily.X86_64,
      "x86-32" => MachineTargetFamily.X86_32,
      _ => MachineTargetFamily.X86_16,
    };
    selected = machine;
    error = null;
    return true;
  }

  public bool TryAllocate(IrFunction source, MFunction selected,
      out IrMachineFunction? machine, out string? error) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(selected);
    machine = null;
    if (!X86MachineTargetValidation.TryValidate(selected, this.Target, out var targetError)) {
      error = "target: " + targetError;
      return false;
    }
    this._scheduler.Schedule(selected);
    if (this._allocator.TryAllocate(selected, out var allocationReason) is not { } allocation) {
      error = "allocation: " + (allocationReason ?? "register allocation failed");
      return false;
    }
    this._postAllocation.Run(selected, allocation);
    machine = new IrMachineFunction(source, selected, allocation, this.Target);
    error = null;
    return true;
  }
}
