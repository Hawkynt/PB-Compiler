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

  public bool TryEmitMos6502(out IReadOnlyDictionary<string, MachineCode> emitted,
      out IReadOnlyList<string> errors) {
    emitted = new Dictionary<string, MachineCode>(StringComparer.Ordinal);
    errors = [];
    if (this.Options.Target != IrBackendTarget.Mos6502 || this.Machine is null) {
      errors = ["MOS 6502 emission requires a MOS 6502 machine module"];
      return false;
    }
    var target = IrBackendTargetContract.CreateMachineTarget(IrBackendTarget.Mos6502);
    if (target is not Mos6502MachineTarget mosTarget) {
      errors = ["MOS 6502 machine target is unavailable"];
      return false;
    }
    if (mosTarget.Emitter is not Mos6502MachineEmitter emitter) {
      errors = ["MOS 6502 target has no compatible machine emitter"];
      return false;
    }
    var output = new Dictionary<string, MachineCode>(StringComparer.Ordinal);
    foreach (var function in this.Machine.Functions)
      output[function.Source.Name] = emitter.EmitFunction(function.Function);
    emitted = output;
    return true;
  }

  /// <summary>Completes the explicit Low IR → Machine SSA → Machine IR boundary.</summary>
  public bool TryLowerMachine(out IReadOnlyList<string> errors) {
    var target = IrBackendTargetContract.SelectionTarget(Options);
    if (Options.Target == IrBackendTarget.Mos6502) {
      if (!IrMachinePipeline.TryLower(this.Module, new Mos6502MachineLowering(), target,
            out var mosMachine, out errors))
        return false;
      this.Machine = mosMachine;
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
    if (options.Target is IrBackendTarget.C or IrBackendTarget.Llvm or IrBackendTarget.PowerBasic35)
      IrMiddleEndPipeline.RunHostedModule(module, options.Optimize, options.OptimizeForSpeed,
        options.EnableFpLookupTables, options.RecoverIntegerArithmetic, options.PrepareParallelLoops);
    else if (options.Target is IrBackendTarget.X86_16 or IrBackendTarget.Mos6502)
      IrMiddleEndPipeline.RunNativeModule(module, options.Optimize, options.OptimizeForSpeed,
        options.OptimizeForSize, minimumIntegerStorageBits: 16,
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
