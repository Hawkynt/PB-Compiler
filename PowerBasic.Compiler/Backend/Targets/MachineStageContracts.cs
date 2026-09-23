using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Backend.Targets;

/// <summary>
/// The target-neutral machine pipeline split. Selection, allocation and emission are separate
/// contracts so a target cannot smuggle register allocation or object-file policy into selection.
/// The concrete machine-function and allocation types remain target-owned.
/// </summary>
public interface IMachineSelector<out TMachineFunction> {
  TMachineFunction? TrySelect(IrFunction function, out string? declineReason);
}

public interface IMachineAllocator<in TMachineFunction, out TAllocation> {
  TAllocation? TryAllocate(TMachineFunction function, out string? declineReason);
}

/// <summary>Target-owned ordering of selected machine instructions before allocation.</summary>
public interface IMachineScheduler<in TMachineFunction> {
  void Schedule(TMachineFunction function);
}

public interface IMachineCodeEmitter<in TMachineFunction, in TAllocation> {
  MachineCode Emit(TMachineFunction function, TAllocation allocation);
}

/// <summary>Emission stage for backends whose object/image sink is supplied by the host.</summary>
public interface IMachineEmitterStage<in TMachineFunction, in TAllocation, in TContext> {
  void Emit(TMachineFunction function, TAllocation allocation, TContext context);
}

/// <summary>Explicit ownership of the three target-specific stages after optimized IR.</summary>
public readonly record struct MachineStageSet<TMachineFunction, TAllocation>(
    IMachineSelector<TMachineFunction> Selector,
    IMachineAllocator<TMachineFunction, TAllocation> Allocator,
    IMachineCodeEmitter<TMachineFunction, TAllocation> Emitter);
