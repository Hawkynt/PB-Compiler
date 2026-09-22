using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Backend;

/// <summary>The target-specific machine boundary after target-independent Low IR.</summary>
public static class IrMachinePipeline {
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

    var selected = new List<(IrFunction Source, MFunction Function)>();
    foreach (var function in module.Functions) {
      if (function.IsDeclaration || function.Entry is null)
        continue;
      if (InstructionSelector.TrySelect(function, out var declineReason, target) is not { } selectedFunction) {
        errors = [$"function '{function.Name}' was not selected: {declineReason ?? "unknown machine construct"}"];
        return false;
      }
      MachineScheduler.Schedule(selectedFunction, target);
      selected.Add((function, selectedFunction));
    }

    var allocated = new List<IrMachineFunction>(selected.Count);
    foreach (var (source, function) in selected) {
      if (LinearScanAllocator.Allocate(function, target, out var declineReason) is not { } allocation) {
        errors = [$"function '{source.Name}' was not allocated: {declineReason ?? "register allocation failed"}"];
        return false;
      }
      PostRegisterAllocationPeepholes.Run(function, allocation);
      LateLoadStoreOptimization.Run(function, allocation);
      allocated.Add(new(source, function, allocation));
    }

    if (!module.TryAdvanceRepresentationStage(IrRepresentationStage.MachineSsa, out var stageError)
        || !module.TryAdvanceRepresentationStage(IrRepresentationStage.MachineIr, out stageError)) {
      errors = [stageError ?? "unable to advance through the machine representation boundaries"];
      return false;
    }

    machine = new IrMachineModule(module, target, allocated);
    errors = [];
    return true;
  }
}
