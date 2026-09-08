namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Target profitability queries consumed by arithmetic middle-end transforms whose legality is
/// target-neutral but whose value depends on the machine that will execute the result.
/// </summary>
public interface IIrArithmeticCostModel {

  /// <summary>
  /// True when replacing <paramref name="divisionCount"/> repeated floating divisions by one reciprocal
  /// division plus the same number of multiplies is profitable for <paramref name="type"/>.
  /// </summary>
  bool PreferReciprocalReuse(IrType type, int divisionCount);
}
