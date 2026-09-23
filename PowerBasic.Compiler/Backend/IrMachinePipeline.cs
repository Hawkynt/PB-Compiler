using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Backend;

/// <summary>The target-specific machine boundary after target-independent Low IR.</summary>
public static class IrMachinePipeline {
  /// <summary>Selects one Low IR function without scheduling or allocating it.</summary>
  public static bool TrySelectFunction(
      IrFunction function,
      SelectionTarget target,
      out MFunction? selected,
      out string? error) {
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

  /// <summary>Schedules and allocates a selected machine function, then applies late rewrites.</summary>
  public static bool TryAllocateFunction(
      IrFunction source,
      MFunction selected,
      SelectionTarget target,
      out IrMachineFunction? machine,
      out string? error) {
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

  /// <summary>Selects, schedules, allocates, and performs late machine rewrites for one IR function.</summary>
  public static bool TryLowerFunction(
      IrFunction function,
      SelectionTarget target,
      out IrMachineFunction? machine,
      out string? error) {
    ArgumentNullException.ThrowIfNull(function);
    machine = null;
    if (!TrySelectFunction(function, target, out var selected, out error))
      return false;
    return TryAllocateFunction(function, selected!, target, out machine, out error);
  }

  public static bool TryLower(
      IrModule module,
      SelectionTarget target,
      out IrMachineModule? machine,
      out IReadOnlyList<string> errors) {
    ArgumentNullException.ThrowIfNull(module);
    machine = null;
    if (module.RepresentationStage != IrRepresentationStage.LowIr) {
      errors = [$"machine lowering requires LowIr, got {module.RepresentationStage}"];
      return false;
    }

    var verification = IrVerifier.Verify(module);
    if (verification.Count != 0) {
      errors = verification;
      return false;
    }

    var selected = new List<IrMachineFunction>();
    foreach (var function in module.Functions) {
      if (function.IsDeclaration || function.Entry is null)
        continue;
      if (!TryLowerFunction(function, target, out var selectedMachine, out var declineReason)) {
        errors = [$"function '{function.Name}' was not lowered: {declineReason ?? "unknown machine construct"}"];
        return false;
      }
      selected.Add(selectedMachine!);
    }

    if (!module.TryAdvanceRepresentationStage(IrRepresentationStage.MachineSsa, out var stageError)
        || !module.TryAdvanceRepresentationStage(IrRepresentationStage.MachineIr, out stageError)) {
      errors = [stageError ?? "unable to advance through the machine representation boundaries"];
      return false;
    }

    machine = new IrMachineModule(module, target, selected);
    errors = [];
    return true;
  }
}
