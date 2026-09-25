using PowerBasic.Compiler.Ir.Analysis;

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

  /// <summary>Compatibility entry point for direct pass tests.</summary>
  public static int Run(IrFunction fn) => RunCore(fn, fitsNarrow: null);

  /// <summary>Runs integer recovery inside the shared analysis-aware function pipeline.</summary>
  public static IrPassResult Run(IrFunction fn, IrAnalysisManager analyses) {
    ArgumentNullException.ThrowIfNull(fn);
    ArgumentNullException.ThrowIfNull(analyses);
    if (!ReferenceEquals(fn, analyses.Function))
      throw new ArgumentException("Analysis manager belongs to a different function.", nameof(analyses));

    // a WIDER leaf is recovered only where the range analysis proves it fits the target - asked lazily,
    // because most functions have no such leaf and the analysis is not free
    Analysis.IrRangeAnalysis? ranges = null;
    var recovered = RunCore(fn, (value, block, type) => FitsSigned(
      (ranges ??= analyses.Get(Analysis.IrAnalyses.Ranges)).RangeAt(value, block), type));
    return recovered == 0
      ? IrPassResult.Unchanged
      : IrPassResult.ChangedPreservingSets(recovered, IrAnalysisSets.Cfg);
  }

  private static int RunCore(IrFunction fn, Func<IrValue, IrBasicBlock, IrType, bool>? fitsNarrow) {
    ArgumentNullException.ThrowIfNull(fn);
    // Direct compatibility callers still need the hidden-control-flow guard. The production function
    // pipeline also refuses error-handler/inline-asm bodies before dispatching any pass.
    if (fn.HasErrorHandler)
      return 0;
    var recovered = 0;
    foreach (var block in fn.Blocks)
      foreach (var instr in block.Instructions.ToList())   // snapshot - we insert while iterating
        // both spellings close a float-shaped integer tree: the rounding one because that is what an
        // assignment to an integer variable emits, the truncating one because FIX/INT do. Whether the
        // conversion rounds or truncates cannot matter here - the recovered tree is integer-valued
        if (instr is IrCast { Op: IrCastOp.FPToSI or IrCastOp.FPToSIRound } cast && cast.Type.IsInteger
            && TryRecover(cast.Value, cast.Type, block, cast, fitsNarrow) is { } intValue) {
          cast.ReplaceAllUsesWith(intValue);
          ++recovered;
        }
    return recovered;
  }

  /// <summary>An integer (type <paramref name="intType"/>) equivalent of the float expression, inserting any new integer ops before <paramref name="at"/>, or null if it is not a recoverable integer tree.</summary>
  private static IrValue? TryRecover(IrValue value, IrType intType, IrBasicBlock block, IrInstruction at,
      Func<IrValue, IrBasicBlock, IrType, bool>? fitsNarrow) {
    switch (value) {
      case IrCast { Op: IrCastOp.SIToFP } widen when widen.Value.Type.Equals(intType):
        return widen.Value;                                // sitofp(x : iN) -> x

      // a NARROWER leaf widens into the target instead of blocking recovery. L& = A2% * B2% is the
      // case that matters: the leaves are INTEGERs and the target a LONG, and 32767 * 32767 is
      // 1073676289 - which an f32's 24-bit mantissa cannot hold, so leaving the product on the float
      // path answered 1073676288. Widening first and multiplying in 32 bits is exact, and it is what
      // the direct emitter effectively does by keeping the product in an x87 temporary with 64 bits
      // of mantissa. A 16x16 product cannot overflow 32 bits, so nothing is lost the other way.
      case IrCast { Op: IrCastOp.SIToFP } narrow
          when narrow.Value.Type.IsInteger && narrow.Value.Type.Bits < intType.Bits:
        return block.InsertBefore(
          new IrCast(narrow.Value.Type.Signed ? IrCastOp.SExt : IrCastOp.ZExt, narrow.Value, intType), at);

      // a WIDER leaf the ranges prove fits the target is that same small integer: n% + LEN(s$), whose
      // LEN is a LONG in [0, 32767]. It truncates exactly, and a float sum of values that small is
      // exact too, so the modular argument above holds for it as for a leaf of the target's width.
      case IrCast { Op: IrCastOp.SIToFP } wide
          when wide.Value.Type.IsInteger && wide.Value.Type.Bits > intType.Bits
            && fitsNarrow?.Invoke(wide.Value, block, intType) == true:
        return block.InsertBefore(new IrCast(IrCastOp.Trunc, wide.Value, intType), at);

      // a float-precision cast is transparent to the integer value underneath it: PB widens a
      // SINGLE subtree to DOUBLE before combining it with a wider operand (`a%*a% + b%` computes
      // the product in SINGLE, extends to DOUBLE for the add). Recurse straight through - the
      // leaf-width check still forces every leaf to the target integer type, so this never crosses
      // into a mixed-width tree the direct back end would (correctly) leave on the FPU.
      case IrCast { Op: IrCastOp.FPExt or IrCastOp.FPTrunc } precision:
        return TryRecover(precision.Value, intType, block, at, fitsNarrow);

      case IrConstantFloat c when c.TryGetDoubleExact(out var constant) && IsExactInteger(constant, intType):
        return new IrConstantInt(intType, (long)constant);  // a float constant that is an exact integer

      case IrBinary b when MapOp(b.Op) is { } op: {
        if (TryRecover(b.Lhs, intType, block, at, fitsNarrow) is not { } lhs
            || TryRecover(b.Rhs, intType, block, at, fitsNarrow) is not { } rhs)
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

  private static bool FitsSigned(Analysis.ValueRange range, IrType type)
    => !range.IsEmpty && type.Bits < 64
       && range.Lo >= -(1L << (type.Bits - 1)) && range.Hi <= (1L << (type.Bits - 1)) - 1;

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
