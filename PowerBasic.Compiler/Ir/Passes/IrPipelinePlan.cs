namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>Named semantic/optimization regions in the target-independent middle-end plan.</summary>
public enum IrMiddleEndPhase {
  /// <summary>Compatibility registrations outside the production phase plan.</summary>
  Unspecified,
  Legalization,
  Canonicalization,
  SsaPreparation,
  DataLayout,
  LoopPreparation,
  ScalarSimplification,
  MemoryAndObjects,
  ArithmeticSimplification,
  MemoryOptimization,
  LoopOptimization,
  LateScalarCleanup,
  Interprocedural,
}

/// <summary>The IR unit a registered middle-end transform operates on.</summary>
public enum IrPassScope {
  EarlyModule,
  Function,
  Module,
}

/// <summary>One registered transform in the inspectable middle-end execution plan.</summary>
public readonly record struct IrPassDescriptor(
  IrMiddleEndPhase Phase,
  IrPassScope Scope,
  string Name);

/// <summary>
/// Diagnostic emitted when a bounded function fixed point exhausts its iteration budget while transforms
/// are still changing the function. Compilation remains conservative: the optimizer stops at the budget,
/// but callers can see exactly which transforms prevented convergence on the last sweep.
/// </summary>
public sealed record IrFixpointDiagnostic(
  string FunctionName,
  int IterationBudget,
  int CompletedIterations,
  IReadOnlyList<IrPassDescriptor> ChangedPasses);
