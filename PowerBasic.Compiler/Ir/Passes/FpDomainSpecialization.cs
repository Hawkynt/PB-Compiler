using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0343/O0332 composition — SPEED-only transcendental specialization driven by the same SSA ranges
/// used for bounds/overflow proofs. A genuinely finite integer-backed domain becomes a direct table
/// load when the selected backend can materialize typed constant data; a continuous but narrow proven
/// interval becomes a small Horner polynomial; everything else stays a general math call and is merely
/// marked <c>afn</c> by <see cref="FpFastMath"/>.
///
/// <para>
/// A narrow floating interval is deliberately NOT treated as a finite domain. Binary32 values in
/// <c>[0,1]</c> are still numerous. Table generation requires <see cref="FpDomainAnalysis.DiscreteDomain"/>,
/// which traces the argument to one integer SSA value and obtains an exhaustive range at the call site.
/// </para>
/// <para>
/// Tables use <see cref="Math"/> only as a compile-time SPEED oracle. Strict optimization never runs
/// this pass, so no claim is made that the host libm is bit-identical to PowerBASIC/x87. Polynomial
/// coefficients below are the independently derived Taylor series evaluated over deliberately small
/// kernels; no external implementation code or implementation-specific table is reproduced.
/// </para>
/// </summary>
public static class FpDomainSpecialization {

  private const int _MAX_TABLE_ENTRIES = 256;

  /// <summary>
  /// Specializes transcendental calls. Polynomial kernels are target-neutral; lookup tables are only
  /// generated when <paramref name="allowLookupTables"/> says the selected backend can materialize the
  /// typed constant array. Keeping the capability explicit prevents native x86 routing from gaining an
  /// IR global its current data-cell resolver cannot address.
  /// </summary>
  public static int Run(IrModule module, bool allowLookupTables = false) {
    ArgumentNullException.ThrowIfNull(module);
    var changes = 0;
    foreach (var function in module.Functions.Where(function => !function.IsDeclaration).ToList()) {
      if (function.HasErrorHandler || function.HasInlineAsm || FpDomainAnalysis.Build(function) is not { } domains)
        continue;
      foreach (var call in function.AllInstructions.OfType<IrCall>().ToList()) {
        if (call.Parent is null || call.Type.Bits is not (32 or 64) || call.Args.Count() != 1
            || !IrFpMath.TryGet(call, out var kind))
          continue;
        if ((allowLookupTables && TryLookup(module, call, kind, domains)) || TryPolynomial(call, kind, domains))
          ++changes;
      }
    }
    return changes;
  }

  private static bool TryLookup(IrModule module, IrCall call, IrFpMathFunction kind, FpDomainAnalysis domains) {
    var block = call.Parent!;
    var argument = call.Args.First();
    if (argument.Type.Bits is not (32 or 64)
        || !domains.TryDiscreteDomain(argument, block, _MAX_TABLE_ENTRIES, out var domain)
        || domain.Source.Type is not { IsInteger: true, Bits: <= 16 })
      return false;

    var values = new double[domain.Count];
    for (var index = 0; index < values.Length; ++index) {
      var source = domain.Lo + index;
      if (!domain.TryValueAt(source, out var input)
          || !IrFpMath.TryEvaluate(kind, input, out var result) || !double.IsFinite(result))
        return false;
      values[index] = call.Type.Bits == 32 ? (float)result : result;
    }

    var table = module.AddGlobal(new IrGlobalVariable(UniqueTableName(module, kind), call.Type) {
      FloatingValues = values,
      Count = values.Length,
      IsZeroInitialized = false,
    });

    // Compute a dense 0-based index in the source's modular integer type. For a signed domain with a
    // negative lower endpoint the subtraction may wrap at the source width, but the proven span is at
    // most 256 values, so the resulting bit pattern is exactly 0..N-1. Widen only after that step.
    IrValue indexValue = domain.Source;
    if (domain.Lo != 0)
      indexValue = block.InsertBefore(new IrBinary(IrBinaryOp.Sub, indexValue,
        new IrConstantInt(indexValue.Type, domain.Lo)), call);
    if (indexValue.Type.Bits < 16)
      indexValue = block.InsertBefore(new IrCast(IrCastOp.ZExt, indexValue, IrType.U16), call);

    var address = block.InsertBefore(new IrGep(table, indexValue, call.Type), call);
    var replacement = block.InsertBefore(new IrLoad(call.Type, address), call);
    call.ReplaceAllUsesWith(replacement);
    call.EraseFromParent();
    return true;
  }

  private static string UniqueTableName(IrModule module, IrFpMathFunction kind) {
    var ordinal = 0;
    while (true) {
      var name = $".fplut.{kind.ToString().ToLowerInvariant()}.{ordinal++}";
      if (module.FindGlobal(name) is null)
        return name;
    }
  }

  private static bool TryPolynomial(IrCall call, IrFpMathFunction kind, FpDomainAnalysis domains) {
    var block = call.Parent!;
    var argument = call.Args.First();
    var domain = domains.DomainAt(argument, block);
    if (!domain.IsKnown)
      return false;

    IrValue? replacement = kind switch {
      IrFpMathFunction.Sin when domain.MaxAbs <= 0.25
        => OddKernel(block, call, argument, [1.0, -1.0 / 6.0, 1.0 / 120.0, -1.0 / 5040.0, 1.0 / 362880.0]),
      IrFpMathFunction.Cos when domain.MaxAbs <= 0.25
        => EvenKernel(block, call, argument, [1.0, -1.0 / 2.0, 1.0 / 24.0, -1.0 / 720.0, 1.0 / 40320.0]),
      IrFpMathFunction.Atan when domain.MaxAbs <= 0.25
        => OddKernel(block, call, argument,
          [1.0, -1.0 / 3.0, 1.0 / 5.0, -1.0 / 7.0, 1.0 / 9.0,
           -1.0 / 11.0, 1.0 / 13.0, -1.0 / 15.0, 1.0 / 17.0, -1.0 / 19.0]),
      IrFpMathFunction.Exp when domain.MaxAbs <= 0.125
        => Horner(block, call, argument,
          [1.0, 1.0, 1.0 / 2.0, 1.0 / 6.0, 1.0 / 24.0, 1.0 / 120.0,
           1.0 / 720.0, 1.0 / 5040.0, 1.0 / 40320.0]),
      IrFpMathFunction.Log when domain.Lo >= 0.875 && domain.Hi <= 1.125
        => Log1pKernel(block, call, argument),
      _ => null,
    };
    if (replacement is null)
      return false;

    call.ReplaceAllUsesWith(replacement);
    call.EraseFromParent();
    return true;
  }

  private static IrValue OddKernel(IrBasicBlock block, IrInstruction anchor, IrValue x,
      IReadOnlyList<double> coefficients) {
    var xx = Binary(block, anchor, IrBinaryOp.FMul, x, x);
    var p = Horner(block, anchor, xx, coefficients);
    return Binary(block, anchor, IrBinaryOp.FMul, x, p);
  }

  private static IrValue EvenKernel(IrBasicBlock block, IrInstruction anchor, IrValue x,
      IReadOnlyList<double> coefficients) {
    var xx = Binary(block, anchor, IrBinaryOp.FMul, x, x);
    return Horner(block, anchor, xx, coefficients);
  }

  private static IrValue Log1pKernel(IrBasicBlock block, IrInstruction anchor, IrValue x) {
    var y = Binary(block, anchor, IrBinaryOp.FSub, x, Constant(x.Type, 1.0));
    var coefficients = new double[12];
    for (var i = 0; i < coefficients.Length; ++i)
      coefficients[i] = (i & 1) == 0 ? 1.0 / (i + 1) : -1.0 / (i + 1);
    return Binary(block, anchor, IrBinaryOp.FMul, y, Horner(block, anchor, y, coefficients));
  }

  private static IrValue Horner(IrBasicBlock block, IrInstruction anchor, IrValue x,
      IReadOnlyList<double> coefficients) {
    IrValue value = Constant(x.Type, coefficients[^1]);
    for (var i = coefficients.Count - 2; i >= 0; --i) {
      value = Binary(block, anchor, IrBinaryOp.FMul, value, x);
      value = Binary(block, anchor, IrBinaryOp.FAdd, value, Constant(x.Type, coefficients[i]));
    }
    return value;
  }

  private static IrBinary Binary(IrBasicBlock block, IrInstruction anchor, IrBinaryOp op, IrValue left, IrValue right)
    => block.InsertBefore(new IrBinary(op, left, right) {
      FastMathFlags = FpFastMath.ArithmeticFlags(IrFastMathFlags.Fast),
    }, anchor);

  private static IrConstantFloat Constant(IrType type, double value)
    => new(type, type.Bits == 32 ? (float)value : value);
}
