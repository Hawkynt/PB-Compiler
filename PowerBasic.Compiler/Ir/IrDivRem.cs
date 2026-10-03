namespace PowerBasic.Compiler.Ir;

/// <summary>
/// The quotient and remainder of one division (O0079). A divide instruction yields both - x86's
/// <c>DIV</c>/<c>IDIV</c> in <c>AX</c> and <c>DX</c>, the 6502 runtime's routine in two cells - so a back
/// end that meets <c>n \ d</c> asks here whether <c>n MOD d</c> follows in the same block, and if so
/// stores both from the one divide. The operands are SSA values, so "the same operands" is identity,
/// and nothing between the two can change them; a raised error checks the divisor before the first.
/// </summary>
public static class IrDivRem {

  /// <summary>
  /// The later instruction in <paramref name="division"/>'s block that divides the same operands for
  /// the other half of the answer, or null.
  /// </summary>
  public static IrBinary? PartnerOf(IrBinary division) {
    var partnerOp = division.Op switch {
      IrBinaryOp.SDiv => IrBinaryOp.SRem,
      IrBinaryOp.SRem => IrBinaryOp.SDiv,
      IrBinaryOp.UDiv => IrBinaryOp.URem,
      IrBinaryOp.URem => IrBinaryOp.UDiv,
      _ => (IrBinaryOp?)null,
    };
    if (partnerOp is null || division.Parent is not { } block)
      return null;
    var instructions = block.Instructions;
    for (var i = instructions.Count - 1; i >= 0 && !ReferenceEquals(instructions[i], division); --i)
      if (instructions[i] is IrBinary candidate && candidate.Op == partnerOp
          && ReferenceEquals(candidate.Lhs, division.Lhs) && ReferenceEquals(candidate.Rhs, division.Rhs)
          && candidate.Type.Equals(division.Type))
        return candidate;
    return null;
  }
}
