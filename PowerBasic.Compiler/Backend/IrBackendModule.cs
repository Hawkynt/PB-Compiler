using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;

namespace PowerBasic.Compiler.Backend;

/// <summary>The single shared compilation product consumed by all target emitters.</summary>
public sealed class IrBackendModule {
  private IrBackendModule(IrModule module, IrBackendOptions options) {
    this.Module = module;
    this.Options = options;
  }

  public IrModule Module { get; }
  public IrBackendOptions Options { get; }

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

    module.RepresentationStage = IrRepresentationStage.OptimizedSsa;

    return new(module, options);
  }
}
