using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Backend.Targets;

/// <summary>
/// Target-owned adapters for the existing x86-16 selector and allocator. They deliberately contain no
/// optimization policy: callers provide the already-resolved selection target, and each stage reports
/// its own decline reason.
/// </summary>
public sealed class X86LegacySelector(SelectionTarget target) : IMachineSelector<MFunction> {
  public MFunction? TrySelect(IrFunction function, out string? declineReason)
    => InstructionSelector.TrySelect(function, out declineReason, target);
}

public sealed class X86LegacyAllocator(SelectionTarget target)
    : IMachineAllocator<MFunction, IReadOnlyDictionary<int, Reg>> {
  public IReadOnlyDictionary<int, Reg>? TryAllocate(MFunction function, out string? declineReason)
    => LinearScanAllocator.Allocate(function, target, out declineReason);
}

/// <summary>Selection and allocation stages for the legacy x86-16 machine representation.</summary>
public readonly record struct X86LegacyMachineStages(
    X86LegacySelector Selector,
    X86LegacyAllocator Allocator);
