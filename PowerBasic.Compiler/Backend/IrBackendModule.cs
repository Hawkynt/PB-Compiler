using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Backend.Targets;

namespace PowerBasic.Compiler.Backend;

/// <summary>The single shared compilation product consumed by all target emitters.</summary>
public sealed class IrBackendModule {
  private IrBackendModule(IrModule module, IrBackendOptions options) {
    this.Module = module;
    this.Options = options;
  }

  public IrModule Module { get; }
  public IrBackendOptions Options { get; }
  public IrMachineModule? Machine { get; private set; }

  /// <summary>Emits the target-owned hosted x86 products produced by machine lowering.</summary>
  public bool TryEmitHostedX86(
      out IReadOnlyDictionary<string, MachineCode> emitted,
      out IReadOnlyList<string> errors) {
    emitted = new Dictionary<string, MachineCode>(StringComparer.Ordinal);
    if (this.Machine is null) {
      errors = ["hosted emission requires machine lowering"];
      return false;
    }
    if (this.Options.Target is not (IrBackendTarget.X86_16 or IrBackendTarget.X86_32 or IrBackendTarget.X86_64)) {
      errors = [$"target '{this.Options.Target}' is not an x86 hosted target"];
      return false;
    }

    var target = IrBackendTargetContract.CreateMachineTarget(this.Options.Target);
    if (target is not X86MachineTarget x86) {
      errors = [$"target '{this.Options.Target}' has no x86 emitter contract"];
      return false;
    }
    var results = new Dictionary<string, MachineCode>(StringComparer.Ordinal);
    foreach (var function in this.Machine.Functions) {
      if (function.HostedFunction is null) {
        errors = [$"function '{function.Source.Name}' is not representable by the hosted x86 machine emitter"];
        return false;
      }
      results.Add(function.Source.Name, x86.HostedEmitter.Emit(function.HostedFunction));
    }
    emitted = results;
    errors = [];
    return true;
  }

  /// <summary>Completes the explicit Low IR → Machine SSA → Machine IR boundary.</summary>
  public bool TryLowerMachine(out IReadOnlyList<string> errors) {
    var target = IrBackendTargetContract.SelectionTarget(Options);
    if (Options.Target == IrBackendTarget.Mos6502) {
      errors = ["target 'Mos6502' has no Low IR instruction selector yet"];
      return false;
    }

    if (Options.Target is IrBackendTarget.X86_32 or IrBackendTarget.X86_64) {
      var targetModel = IrBackendTargetContract.CreateMachineTarget(Options.Target);
      if (targetModel is null) {
        errors = [$"target '{Options.Target}' has no machine target contract"];
        return false;
      }
      if (!IrMachinePipeline.TryLower(this.Module, targetModel.CreateLowerer(target), target,
            out var targetMachine, out errors))
        return false;
      this.Machine = targetMachine;
      return true;
    }

    if (!IrMachinePipeline.TryLower(this.Module, target, out var machine, out errors))
      return false;
    this.Machine = machine;
    return true;
  }

  public static IrBackendModule? TryCompile(
      SemanticModel model,
      IrBackendOptions? options,
      out string? declinedBecause) {
    ArgumentNullException.ThrowIfNull(model);
    options ??= new();
    var module = IrLowering.TryLowerModule(model, out declinedBecause);
    if (module is null)
      return null;

    module.AsciiOnly = model.AsciiOnly;
    if (options.Target is IrBackendTarget.C or IrBackendTarget.PowerBasic35 or IrBackendTarget.X86_64)
      IrMiddleEndPipeline.RunHostedModule(module, options.Optimize, options.OptimizeForSpeed,
        options.EnableFpLookupTables, options.RecoverIntegerArithmetic, options.PrepareParallelLoops);
    else if (options.Target is IrBackendTarget.X86_16 or IrBackendTarget.X86_32)
      IrMiddleEndPipeline.RunNativeModule(module, options.Optimize, options.OptimizeForSpeed,
        options.OptimizeForSize, minimumIntegerStorageBits: options.Target == IrBackendTarget.X86_16 ? 16 : 32,
        recoverIntegerArithmetic: options.RecoverIntegerArithmetic);
    else {
      declinedBecause = $"target '{options.Target}' has no emitter yet";
      return null;
    }

    var errors = IrVerifier.Verify(module);
    if (errors.Count != 0) {
      declinedBecause = "optimized IR failed verification: " + string.Join("; ", errors);
      return null;
    }

    if (!module.TryAdvanceRepresentationStage(IrRepresentationStage.OptimizedSsa, out var stageError)) {
      declinedBecause = stageError;
      return null;
    }
    if (!IrLowIrLegalization.TryLegalize(module, out var legalizationErrors)) {
      declinedBecause = "optimized IR failed Low IR legalization: " + string.Join("; ", legalizationErrors);
      return null;
    }
    if (module.RepresentationStage < IrBackendTargetContract.RequiredInputStage(options.Target))
      throw new InvalidOperationException($"target '{options.Target}' requires {IrBackendTargetContract.RequiredInputStage(options.Target)}");

    return new(module, options);
  }
}
