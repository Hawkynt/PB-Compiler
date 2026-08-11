namespace PowerBasic.Compiler.Ir;

/// <summary>
/// The two questions a back end asks an <see cref="IrSwitch"/> before it selects one, kept out of
/// <see cref="IrInstructions"/> as extension methods: they are readings of the case list rather than
/// state, and <see cref="Passes.Sccp"/> and <see cref="Passes.SimplifyCfg"/> already spell the second
/// one inline. Should the instruction ever grow them as members, the members win and this file simply
/// stops being reached.
/// </summary>
public static class IrSwitchQueries {

  /// <summary>
  /// Whether a case value can be told apart from every other value the condition can hold. The 8086
  /// compare chain narrows each case to the condition's WIDTH, so a value outside that width would be
  /// compared as its truncation and match a value the source never named - which is why a case that
  /// does not fit has to decline rather than be silently wrapped.
  /// </summary>
  public static bool IsCaseValueRepresentable(this IrSwitch sw, long value) {
    var type = sw.Condition.Type;
    if (!type.IsInteger)
      return false;
    if (type.Bits >= 64)
      return true;
    return type.Signed
      ? value >= -(1L << (type.Bits - 1)) && value < 1L << (type.Bits - 1)
      : value >= 0 && value < 1L << type.Bits;
  }

  /// <summary>The block a known selector value reaches: its own case, or the default when it names none.</summary>
  public static IrBasicBlock TargetFor(this IrSwitch sw, long value)
    => sw.Cases.FirstOrDefault(c => c.Value == value).Target ?? sw.DefaultTarget;
}
