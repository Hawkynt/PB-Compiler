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
    IReadOnlyDictionary<int, Reg>? allocation;
    try {
      this._scheduler.Schedule(selected);
    } catch (NotSupportedException exception) {
      error = $"scheduling: unsupported instruction: {exception.Message}";
      return false;
    } catch (InvalidOperationException exception) {
      error = $"scheduling: invalid machine function: {exception.Message}";
      return false;
    } catch (ArgumentException exception) {
      error = $"scheduling: invalid operand: {exception.Message}";
      return false;
    }

    try {
      allocation = this._allocator.TryAllocate(selected, out var allocationReason);
      if (allocation is null) {
        error = "allocation: " + (allocationReason ?? "register allocation failed");
        return false;
      }
    } catch (NotSupportedException exception) {
      error = $"allocation: unsupported machine function: {exception.Message}";
      return false;
    } catch (InvalidOperationException exception) {
      error = $"allocation: invalid machine function: {exception.Message}";
      return false;
    } catch (ArgumentException exception) {
      error = $"allocation: invalid operand: {exception.Message}";
      return false;
    }

    try {
      this._postAllocation.Run(selected, allocation);
    } catch (NotSupportedException exception) {
      error = $"post-allocation: unsupported instruction: {exception.Message}";
      return false;
    } catch (InvalidOperationException exception) {
      error = $"post-allocation: invalid machine function: {exception.Message}";
      return false;
    } catch (ArgumentException exception) {
      error = $"post-allocation: invalid operand: {exception.Message}";
      return false;
    }
    var provisional = new IrMachineFunction(source, selected, allocation, this.Target);
    var hostedFailure = TryBuildHosted(provisional, out var hosted);
    // The hosted function is how x86-32 and x86-64 are EMITTED, so there it is the product and a
    // failure to build or encode it is a failure to compile. x86-16 is not emitted from it:
    // X86ProductionEmitter sends DOS through MachineEmitter on the allocated MFunction, which owns
    // the stack ABI, far operands, verbatim inline assembly and 386 operand-size prefixes. Making the
    // hosted build a GATE for x86-16 therefore refused programs the emitter that actually produces
    // their bytes handles - every `! DEC` without a semantic model, every $CPU 80386 function using
    // EAX in real mode - and what was lost was routing, not correctness. On x86-16 the hosted build
    // is best-effort: its reason is kept for TryEmitHostedX86, which already reports a missing one.
    if (hostedFailure is not null && !IsDosTarget(this.Target.Name)) {
      error = hostedFailure;
      return false;
    }
    machine = new IrMachineFunction(source, selected, allocation, this.Target,
      hostedFailure is null ? hosted : null, hostedFailure);
    error = null;
    return true;
  }

  private static bool IsDosTarget(string targetName)
    => targetName.Equals("x86-16", StringComparison.OrdinalIgnoreCase);

  /// <summary>Builds and encode-checks the hosted function, answering why not, or null on success.</summary>
  private static string? TryBuildHosted(IrMachineFunction provisional, out X86TargetMachineFunction? hosted) {
    if (!X86HostedMachineBuilder.TryBuild(provisional, out hosted, out var hostedError))
      return "hosted machine lowering: " + (hostedError ?? "unsupported target instruction");
    if (hosted is null)
      return "hosted machine lowering: succeeded without producing a target function";
    if (!TryValidateEncoding(hosted, provisional.Target.Name, out var encodingError))
      return "x86 encoding: " + encodingError;
    return null;
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
    } catch (OverflowException exception) {
      error = exception.Message;
      return false;
    }
  }
}
