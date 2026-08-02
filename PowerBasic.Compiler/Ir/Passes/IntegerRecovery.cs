namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Recovers integer arithmetic from the floating-point form the front end emits for PowerBASIC's
/// integral <c>+</c>/<c>-</c>/<c>*</c> (it computes them in floating point for display precision -
/// <c>PRINT A%*B%</c> shows <c>9E+8</c>). A value stored back into an integer
/// (<c>fptosi(float-tree) to iN</c>) where the float tree is built only from <c>sitofp(iN)</c> leaves,
/// integer-valued float constants and <c>fadd</c>/<c>fsub</c>/<c>fmul</c> is rewritten to the integer
/// tree <c>add</c>/<c>sub</c>/<c>mul</c> over the same iN values. This is sound: the result stored is
/// taken mod 2^N either way, and modular arithmetic commutes with the intermediate wrapping
/// (<c>(a*2 + b*3) mod 2^N == ((a*2 mod 2^N) + (b*3 mod 2^N)) mod 2^N</c>) - exactly what the direct
/// codegen already does for these statements. Giving such functions a genuine integer IR lets the
/// in-house x86-16 back end select them (it handles integers, not the FP ops + conversions).
/// </summary>
public static class IntegerRecovery {

  /// <summary>Rewrites integer-typed fptosi(float-tree) chains to integer arithmetic; returns how many were recovered.</summary>
  public static int Run(IrFunction fn) {
    var recovered = 0;
    foreach (var block in fn.Blocks)
      foreach (var instr in block.Instructions.ToList())   // snapshot - we insert while iterating
        if (instr is IrCast { Op: IrCastOp.FPToSI } cast && cast.Type.IsInteger
            && TryRecover(cast.Value, cast.Type, block, cast) is { } intValue) {
          cast.ReplaceAllUsesWith(intValue);
          ++recovered;
        }
    return recovered;
  }

  /// <summary>An integer (type <paramref name="intType"/>) equivalent of the float expression, inserting any new integer ops before <paramref name="at"/>, or null if it is not a recoverable integer tree.</summary>
  private static IrValue? TryRecover(IrValue value, IrType intType, IrBasicBlock block, IrInstruction at) {
    switch (value) {
      case IrCast { Op: IrCastOp.SIToFP } widen when widen.Value.Type.Equals(intType):
        return widen.Value;                                // sitofp(x : iN) -> x

      // a float-precision cast is transparent to the integer value underneath it: PB widens a
      // SINGLE subtree to DOUBLE before combining it with a wider operand (`a%*a% + b%` computes
      // the product in SINGLE, extends to DOUBLE for the add). Recurse straight through - the
      // leaf-width check still forces every leaf to the target integer type, so this never crosses
      // into a mixed-width tree the direct back end would (correctly) leave on the FPU.
      case IrCast { Op: IrCastOp.FPExt or IrCastOp.FPTrunc } precision:
        return TryRecover(precision.Value, intType, block, at);

      case IrConstantFloat c when IsExactInteger(c.Value, intType):
        return new IrConstantInt(intType, (long)c.Value);  // a float constant that is an exact integer

      case IrBinary b when MapOp(b.Op) is { } op: {
        if (TryRecover(b.Lhs, intType, block, at) is not { } lhs || TryRecover(b.Rhs, intType, block, at) is not { } rhs)
          return null;
        return block.InsertBefore(new IrBinary(op, lhs, rhs), at);
      }

      default:
        return null;
    }
  }

  private static IrBinaryOp? MapOp(IrBinaryOp op) => op switch {
    IrBinaryOp.FAdd => IrBinaryOp.Add,
    IrBinaryOp.FSub => IrBinaryOp.Sub,
    IrBinaryOp.FMul => IrBinaryOp.Mul,
    _ => null,
  };

  private static bool IsExactInteger(double v, IrType intType) {
    if (v != System.Math.Truncate(v) || double.IsInfinity(v) || double.IsNaN(v))
      return false;
    // representable in the target signed integer width
    var bits = intType.Bits;
    if (bits >= 64)
      return v is >= -9.2233720368547758E18 and <= 9.2233720368547758E18;
    var max = (double)((1L << (bits - 1)) - 1);
    var min = -(double)(1L << (bits - 1));
    return v >= min && v <= max;
  }
}
