namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Target profitability queries for call-site transforms whose legality is target-independent but whose
/// code-growth/runtime tradeoff belongs to the machine/objective policy.
///
/// <para>
/// The interface receives dynamic profile counts rather than a precomputed percentage so implementations
/// can keep comparisons exact and choose a target-specific break-even without the transform knowing it.
/// </para>
/// </summary>
public interface IIrCallCostModel {

  /// <summary>
  /// True when promoting one indirect-call target behind a guard is profitable for this target/objective.
  /// Legality (profile validity, target membership and ABI compatibility) remains the transform's job.
  /// </summary>
  bool PreferIndirectCallPromotion(ulong targetCount, ulong totalCount);
}

/// <summary>
/// Historical targetless middle-end policy. This preserves O0271's established 30% first-target threshold
/// for standalone/hosted callers that do not supply machine profitability information.
/// </summary>
public sealed class IrDefaultCallCostModel : IIrCallCostModel {

  public static IrDefaultCallCostModel Instance { get; } = new();

  private IrDefaultCallCostModel() { }

  public bool PreferIndirectCallPromotion(ulong targetCount, ulong totalCount)
    => totalCount != 0 && (UInt128)targetCount * 100 >= (UInt128)totalCount * 30;
}
