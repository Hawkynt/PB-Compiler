namespace PowerBasic.Compiler.Ir;

/// <summary>
/// Changes an IEEE value's sign bit without arithmetic, rounding, or payload conversion.
/// In particular, negating positive zero produces negative zero; positive-zero subtraction does not.
/// </summary>
public sealed class IrFNeg : IrInstruction {

  public IrFNeg(IrValue value) : base((value ?? throw new ArgumentNullException(nameof(value))).Type) {
    if (!value.Type.IsIeeeFloat || value.Type.Bits is not (32 or 64 or 80))
      throw new ArgumentException("Floating negation requires an IEEE 32-, 64-, or 80-bit value.", nameof(value));
    this.AddOperand(value);
  }

  public IrValue Value => this.GetOperand(0);
}
