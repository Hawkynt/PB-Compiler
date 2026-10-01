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

  /// <summary>
  /// When set, the portable runtime (<c>Runtime/Portable</c>) is defined into the module with a string
  /// heap of this many bytes: before the middle end, so the optimizer works on the runtime together
  /// with the program, and again after it, for the runtime calls the optimizer itself introduced.
  /// </summary>
  public int? PortableRuntimeHeapBytes { get; init; }

  /// <summary>The portable runtime's index width: 16 for a target whose pointers are 16 bits, else 32.</summary>
  public int PortableRuntimeIndexBits { get; init; } = 32;

  /// <summary>
  /// Units the program <c>$LINK</c>s, as IR (<see cref="Ir.IrUnitFile"/>): joined to it right after
  /// lowering, so the middle end optimizes program and units as one module.
  /// </summary>
  public IReadOnlyList<Ir.IrModule> LinkedModules { get; init; } = [];

  /// <summary>Whether the portable runtime also supplies the math intrinsics, for a target with no floating-point hardware.</summary>
  public bool PortableRuntimeSoftMath { get; init; }
}
