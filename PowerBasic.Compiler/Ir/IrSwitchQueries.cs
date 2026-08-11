namespace PowerBasic.Compiler.Ir;

/// <summary>
/// The two questions a back end asks an <see cref="IrSwitch"/> that are properties of the
/// instruction rather than of any target: does a case value fit the width the condition is
/// compared at, and - when the condition is a constant - which arm does control actually take.
/// </summary>
/// <remarks>
/// These are extension methods rather than members so that a selector may ask them without the
/// instruction growing target-shaped API. Should <see cref="IrSwitch"/> ever gain methods of the
/// same names, those win by the ordinary C# rule and this file becomes dead weight rather than a
/// conflict.
/// </remarks>
public static class IrSwitchQueries {

  /// <summary>
  /// True when <paramref name="value"/> survives being truncated to the condition's storage width
  /// and read back. A switch decides by EQUALITY, which is a question about bits, so a value is
  /// representable when the low <c>Bits</c> of it - read either as signed or as unsigned - are the
  /// value itself. Anything else would compare a truncated immediate against a full-width condition
  /// and match a case the source never named.
  /// </summary>
  public static bool IsCaseValueRepresentable(this IrSwitch sw, long value) {
    var bits = sw.Condition.Type.Bits;
    if (bits is <= 0 or >= 64)
      return true;

    var mask = (1L << bits) - 1;
    var zeroExtended = value & mask;
    var sign = 1L << (bits - 1);
    var signExtended = (zeroExtended ^ sign) - sign;
    return zeroExtended == value || signExtended == value;
  }

  /// <summary>
  /// The block a condition of <paramref name="value"/> reaches: the first case naming it, or the
  /// default arm when none does. Cases are matched in source order, which is the order
  /// <see cref="IrSwitch.AddCase"/> recorded them, so a duplicated value behaves as the compare
  /// chain would.
  /// </summary>
  public static IrBasicBlock TargetFor(this IrSwitch sw, long value) {
    foreach (var (caseValue, target) in sw.Cases)
      if (caseValue == value)
        return target;

    return sw.DefaultTarget;
  }
}
