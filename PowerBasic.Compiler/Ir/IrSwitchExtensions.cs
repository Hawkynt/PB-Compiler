namespace PowerBasic.Compiler.Ir;

/// <summary>
/// The two questions a back end asks of an <see cref="IrSwitch"/> before it can select one: whether a
/// case label is even a value the condition could hold, and - when the condition folded to a constant -
/// which arm the dispatch has already been decided in favour of.
/// </summary>
public static class IrSwitchExtensions {

  /// <summary>
  /// True when the case label fits the condition's type. A label outside it can never compare equal,
  /// so a target that cannot encode the comparison is not thereby wrong to refuse it; a selector
  /// asking this is asking whether the arm is reachable at all.
  /// </summary>
  public static bool IsCaseValueRepresentable(this IrSwitch self, long value) {
    var type = self.Condition.Type;
    if (!type.IsInteger)
      return false;

    var bits = type.Bits;
    if (bits >= 64)
      return true;

    if (type.Signed || bits == 1) {
      var limit = 1L << (bits - 1);
      return value >= -limit && value < limit;
    }

    return value >= 0 && value < 1L << bits;
  }

  /// <summary>
  /// The block a constant condition dispatches to: the first case with that label, or the default.
  /// First rather than only, because nothing forbids a duplicate label and BASIC's own SELECT CASE
  /// takes the earliest arm that matches.
  /// </summary>
  public static IrBasicBlock TargetFor(this IrSwitch self, long value) {
    foreach (var (caseValue, target) in self.Cases)
      if (caseValue == value)
        return target;

    return self.DefaultTarget;
  }
}
