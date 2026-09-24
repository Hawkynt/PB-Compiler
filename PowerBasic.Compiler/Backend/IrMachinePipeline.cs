using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Backend.Targets;

namespace PowerBasic.Compiler.Backend;

/// <summary>The target-specific machine boundary after target-independent Low IR.</summary>
public static class IrMachinePipeline {
  /// <summary>Selects one Low IR function without scheduling or allocating it.</summary>
  public static bool TrySelectFunction(
      IrFunction function,
      SelectionTarget target,
      out X86MachineFunction? selected,
      out string? error) {
    return TrySelectFunction(function, new X86MachineLowering(target), out selected, out error);
  }

  public static bool TrySelectFunction(
      IrFunction function,
      IMachineFunctionLowerer lowerer,
      out X86MachineFunction? selected,
      out string? error) {
    ArgumentNullException.ThrowIfNull(function);
    ArgumentNullException.ThrowIfNull(lowerer);
    var succeeded = lowerer.TrySelect(function, out selected, out error);
    if (succeeded && selected is null) {
      error = "selection: lowerer reported success without producing a machine function";
      return false;
    }
    if (!succeeded)
      selected = null;
    return succeeded;
  }

  /// <summary>Schedules and allocates a selected machine function, then applies late rewrites.</summary>
  public static bool TryAllocateFunction(
      IrFunction source,
      X86MachineFunction selected,
      SelectionTarget target,
      out IrMachineFunction? machine,
      out string? error) {
    return TryAllocateFunction(source, selected, new X86MachineLowering(target), out machine, out error);
  }

  public static bool TryAllocateFunction(
      IrFunction source,
      X86MachineFunction selected,
      IMachineFunctionLowerer lowerer,
      out IrMachineFunction? machine,
      out string? error) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(selected);
    ArgumentNullException.ThrowIfNull(lowerer);
    var succeeded = lowerer.TryAllocate(source, selected, out machine, out error);
    if (succeeded && machine is null) {
      error = "allocation: lowerer reported success without producing a machine function";
      return false;
    }
    if (!succeeded)
      machine = null;
    return succeeded;
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
    return TryLower(module, new X86MachineLowering(target), target, out machine, out errors);
  }

  public static bool TryLower(
      IrModule module,
      IMachineFunctionLowerer lowerer,
      SelectionTarget target,
      out IrMachineModule? machine,
      out IReadOnlyList<string> errors) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(lowerer);
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
    var failures = new List<string>();
    foreach (var function in module.Functions) {
      if (function.IsDeclaration || function.Entry is null)
        continue;
      if (!TryLowerFunction(function, lowerer, out var selectedMachine, out var declineReason)) {
        failures.Add($"function '{function.Name}' was not lowered: {declineReason ?? "unknown machine construct"}");
        continue;
      }
      selected.Add(selectedMachine!);
    }
    if (failures.Count != 0) {
      errors = failures;
      return false;
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

  private static bool TryLowerFunction(
      IrFunction function,
      IMachineFunctionLowerer lowerer,
      out IrMachineFunction? machine,
      out string? error) {
    if (!TrySelectFunction(function, lowerer, out var selected, out error)) {
      machine = null;
      return false;
    }
    return TryAllocateFunction(function, selected!, lowerer, out machine, out error);
  }
}
