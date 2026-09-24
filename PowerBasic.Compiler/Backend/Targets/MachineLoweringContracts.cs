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

  bool TrySelect(IrFunction function, out X86MachineFunction? selected, out string? error);

  bool TryAllocate(IrFunction source, X86MachineFunction selected,
      out IrMachineFunction? machine, out string? error);
}

/// <summary>x86 instruction selection owned by the x86 target, not by the generic machine pipeline.</summary>
public sealed class X86MachineSelector(SelectionTarget target) : IMachineSelector<X86MachineFunction> {
  public X86MachineFunction? TrySelect(IrFunction function, out string? declineReason)
    => InstructionSelector.TrySelect(function, out declineReason, target);
}

/// <summary>x86 register allocation owned by the x86 target.</summary>
public sealed class X86MachineAllocator(SelectionTarget target)
    : IMachineAllocator<X86MachineFunction, IReadOnlyDictionary<int, Reg>> {
  public IReadOnlyDictionary<int, Reg>? TryAllocate(X86MachineFunction function, out string? declineReason)
    => LinearScanAllocator.Allocate(function, target, out declineReason);
}

/// <summary>x86 machine scheduling owned by the x86 target.</summary>
public sealed class X86MachineScheduler(SelectionTarget target) : IMachineScheduler<X86MachineFunction> {
  public void Schedule(X86MachineFunction function) => MachineScheduler.Schedule(function, target);
}

/// <summary>x86 rewrites that require the final physical-register assignment.</summary>
public sealed class X86MachinePostAllocation
    : IMachinePostAllocation<X86MachineFunction, IReadOnlyDictionary<int, Reg>> {
  public void Run(X86MachineFunction function, IReadOnlyDictionary<int, Reg> allocation) {
    PostRegisterAllocationPeepholes.Run(function, allocation);
    LateLoadStoreOptimization.Run(function, allocation);
  }
}

/// <summary>Current x86 lowering implementation shared by the 16-, 32- and 64-bit x86 contracts.</summary>
public sealed class X86MachineLowering : IMachineFunctionLowerer {
  private readonly MachineTargetFamily _targetFamily;
  private readonly X86MachineSelector _selector;
  private readonly X86MachineAllocator _allocator;
  private readonly X86MachineScheduler _scheduler;
  private readonly X86MachinePostAllocation _postAllocation = new();

  public X86MachineLowering(SelectionTarget target) {
    this.Target = target.TargetFamily switch {
      MachineTargetFamily.X86_16 => new("x86-16", 16, 16),
      MachineTargetFamily.X86_64 => new("x86-64", 64, 64),
      MachineTargetFamily.X86_32 => new("x86-32", 32, 32),
      var unsupported => throw new ArgumentOutOfRangeException(
        nameof(target), target, $"target family '{unsupported}' is not an x86 target"),
    };
    this._targetFamily = target.TargetFamily;
    this._selector = new(target);
    this._allocator = new(target);
    this._scheduler = new(target);
  }

  public MachineTargetDescription Target { get; }

  public bool TrySelect(IrFunction function, out X86MachineFunction? selected, out string? error) {
    ArgumentNullException.ThrowIfNull(function);
    selected = null;
    if (function.IsDeclaration || function.Entry is null) {
      error = "selection: declaration";
      return false;
    }
    var verification = IrVerifier.Verify(function);
    if (verification.Count != 0) {
      error = "selection: input IR failed verification: " + string.Join("; ", verification);
      return false;
    }
    X86MachineFunction? machine;
    string? declineReason;
    try {
      machine = this._selector.TrySelect(function, out declineReason);
    } catch (NotSupportedException exception) {
      error = $"selection: unsupported construct: {exception.Message}";
      return false;
    } catch (InvalidOperationException exception) {
      error = $"selection: invalid construct: {exception.Message}";
      return false;
    } catch (ArgumentException exception) {
      error = $"selection: invalid operand: {exception.Message}";
      return false;
    }
    if (machine is null) {
      error = "selection: " + (declineReason ?? "unknown machine construct");
      return false;
    }
    machine.TargetFamily = this._targetFamily;
    selected = machine;
    error = null;
    return true;
  }

  public bool TryAllocate(IrFunction source, X86MachineFunction selected,
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
    var provisional = new IrMachineFunction(source, selected, allocation, this.Target);
    if (!X86HostedMachineBuilder.TryBuild(provisional, out var hosted, out var hostedError)) {
      error = "hosted machine lowering: " + (hostedError ?? "unsupported target instruction");
      return false;
    }
    if (hosted is null) {
      error = "hosted machine lowering: succeeded without producing a target function";
      return false;
    }
    if (!TryValidateEncoding(hosted, this.Target.Name, out var encodingError)) {
      error = "x86 encoding: " + encodingError;
      return false;
    }
    machine = new IrMachineFunction(source, selected, allocation, this.Target, hosted, hostedError);
    error = null;
    return true;
  }

  private static bool TryValidateEncoding(X86TargetMachineFunction function, string targetName, out string? error) {
    var mode = targetName switch {
      "x86-16" => X86Mode.Bit16,
      "x86-32" => X86Mode.Bit32,
      "x86-64" => X86Mode.Bit64,
      _ => throw new ArgumentOutOfRangeException(nameof(targetName), targetName, "not an x86 target"),
    };
    try {
      _ = new X86TargetMachineEmitter(new X86InstructionEncoder(mode))
        .Emit(function, preserveFramePointer: true, emitReturn: true);
      error = null;
      return true;
    } catch (ArgumentException exception) {
      error = exception.Message;
      return false;
    } catch (InvalidOperationException exception) {
      error = exception.Message;
      return false;
    } catch (NotSupportedException exception) {
      error = exception.Message;
      return false;
    }
  }
}
