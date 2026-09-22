using PowerBasic.Compiler.Runtime;

namespace PowerBasic.Compiler.Backend;

/// <summary>Options shared by every target emitter behind the single IR backend.</summary>
public sealed record IrBackendOptions {
  public IrBackendTarget Target { get; init; } = IrBackendTarget.X86_16;
  public bool Optimize { get; init; } = true;
  public bool OptimizeForSpeed { get; init; }
  public bool OptimizeForSize { get; init; }
  public bool EnableFpLookupTables { get; init; }
  public bool RecoverIntegerArithmetic { get; init; }
  public bool PrepareParallelLoops { get; init; }
  public RuntimeTarget RuntimeTarget { get; init; } = RuntimeTarget.Baseline;
}
