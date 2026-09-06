namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>
/// A deliberately small floating domain derived from facts the integer SSA range analysis can prove.
/// It is not a second general-purpose range lattice: it adapts <see cref="IrRangeAnalysis"/> through
/// integer-to-float conversions and simple affine floating expressions so FP optimizations consume the
/// same branch-refined facts as bounds/overflow optimization.
/// </summary>
public sealed class FpDomainAnalysis {

  /// <summary>A closed finite interval plus the IEEE classification facts implied by its provenance.</summary>
  public readonly record struct Domain(double Lo, double Hi, bool NonNaN, bool Finite) {
    public bool IsKnown => this.NonNaN && this.Finite && double.IsFinite(this.Lo) && double.IsFinite(this.Hi) && this.Lo <= this.Hi;
    public bool NonNegative => this.IsKnown && this.Lo >= 0.0;
    public bool Positive => this.IsKnown && this.Lo > 0.0;
    public bool NonZero => this.IsKnown && (this.Lo > 0.0 || this.Hi < 0.0);
    public double MaxAbs => this.IsKnown ? Math.Max(Math.Abs(this.Lo), Math.Abs(this.Hi)) : double.PositiveInfinity;
  }

  /// <summary>
  /// A finite integer-backed FP domain. <see cref="Expression"/> is retained instead of collapsing its
  /// affine arithmetic into one host-double formula: lookup generation must reproduce every declared
  /// binary32/binary64 rounding point, not merely the real-number affine function the tree denotes.
  /// </summary>
  public readonly record struct DiscreteDomain(IrValue Expression, IrValue Source, long Lo, long Hi) {
    public int Count => checked((int)(this.Hi - this.Lo + 1));

    /// <summary>Evaluates one table-domain member with the IR's precision at every operation.</summary>
    public bool TryValueAt(long sourceValue, out double value)
      => TryEvaluate(this.Expression, this.Source, sourceValue, out value);
  }

  private readonly IrRangeAnalysis _integers;

  private FpDomainAnalysis(IrRangeAnalysis integers) => this._integers = integers;

  public static FpDomainAnalysis? Build(IrFunction function)
    => IrRangeAnalysis.Build(function) is { } integers ? new(integers) : null;

  /// <summary>Returns the strongest interval this adapter can prove at a particular use block.</summary>
  public Domain DomainAt(IrValue value, IrBasicBlock block) {
    if (value is IrConstantFloat constant) {
      var finite = double.IsFinite(constant.Value);
      return new(constant.Value, constant.Value, !double.IsNaN(constant.Value), finite);
    }

    // Affineness is used only to prove monotonic dependence on ONE integer source. Evaluate the actual
    // expression at the range endpoints so intermediate SINGLE rounding, overflow and underflow are
    // represented exactly instead of being skipped by `source * scale + offset` algebra.
    if (TryAffine(value, out var affine)) {
      var range = this._integers.RangeAt(affine.Source, block);
      if (!range.IsTop && !range.IsEmpty
          && TryEvaluate(value, affine.Source, range.Lo, out var a)
          && TryEvaluate(value, affine.Source, range.Hi, out var b)
          && double.IsFinite(a) && double.IsFinite(b))
        return new(Math.Min(a, b), Math.Max(a, b), true, true);
    }

    return value switch {
      IrCast { Op: IrCastOp.FPExt } cast => this.DomainAt(cast.Value, block),
      IrCast { Op: IrCastOp.FPTrunc } cast => Truncated(this.DomainAt(cast.Value, block)),
      IrBinary binary when binary.IsFloatOp => this.BinaryDomain(binary, block),
      _ => default,
    };
  }

  /// <summary>
  /// Finds a small exhaustive integer-backed domain. A merely narrow floating interval is not enough:
  /// <c>[0,1]</c> still contains millions of binary32 values, while an integer source <c>[0,255]</c>
  /// contains exactly 256 possibilities and is therefore a real lookup-table domain.
  /// </summary>
  public bool TryDiscreteDomain(IrValue value, IrBasicBlock block, int maxEntries, out DiscreteDomain domain) {
    domain = default;
    if (!TryAffine(value, out var affine))
      return false;
    var range = this._integers.RangeAt(affine.Source, block);
    if (range.IsTop || range.IsEmpty)
      return false;
    long count;
    try { count = checked(range.Hi - range.Lo + 1); }
    catch (OverflowException) { return false; }
    if (count is <= 0 || count > maxEntries
        || !TryEvaluate(value, affine.Source, range.Lo, out _)
        || !TryEvaluate(value, affine.Source, range.Hi, out _))
      return false;
    domain = new(value, affine.Source, range.Lo, range.Hi);
    return true;
  }

  private Domain BinaryDomain(IrBinary binary, IrBasicBlock block) {
    if (binary.Type.Bits is not (32 or 64))
      return default; // no host-independent evaluator for x87 extended precision here

    var left = this.DomainAt(binary.Lhs, block);
    var right = this.DomainAt(binary.Rhs, block);
    if (!left.IsKnown || !right.IsKnown || binary.Op == IrBinaryOp.FDiv && !right.NonZero)
      return default;

    var pairs = binary.Op switch {
      IrBinaryOp.FAdd => new[] { (left.Lo, right.Lo), (left.Hi, right.Hi) },
      IrBinaryOp.FSub => new[] { (left.Lo, right.Hi), (left.Hi, right.Lo) },
      IrBinaryOp.FMul or IrBinaryOp.FDiv => new[] {
        (left.Lo, right.Lo), (left.Lo, right.Hi), (left.Hi, right.Lo), (left.Hi, right.Hi),
      },
      _ => [],
    };
    if (pairs.Length == 0)
      return default;

    var candidates = new double[pairs.Length];
    for (var i = 0; i < pairs.Length; ++i) {
      candidates[i] = EvaluateBinary(binary.Op, binary.Type, pairs[i].Item1, pairs[i].Item2);
      if (!double.IsFinite(candidates[i]))
        return default;
    }
    return new(candidates.Min(), candidates.Max(), true, true);
  }

  private static Domain Truncated(Domain source) {
    if (!source.NonNaN)
      return default;
    if (source.IsKnown && source.Lo >= -float.MaxValue && source.Hi <= float.MaxValue)
      return new((float)source.Lo, (float)source.Hi, true, true);
    return new(source.Lo, source.Hi, true, false);
  }

  private readonly record struct Affine(IrValue Source, double Scale, double Offset);

  /// <summary>
  /// Proves one-dimensional affine dependence. Scale/offset are used for proof (including a negative
  /// slope) only; numerical endpoint/table values are obtained by <see cref="TryEvaluate"/> so the
  /// actual per-node FP precision is preserved.
  /// </summary>
  private static bool TryAffine(IrValue value, out Affine affine) {
    switch (value) {
      case IrCast { Op: IrCastOp.SIToFP or IrCastOp.UIToFP, Value.Type.IsInteger: true } cast:
        affine = new(cast.Value, 1.0, 0.0);
        return true;
      case IrCast { Op: IrCastOp.FPExt or IrCastOp.FPTrunc } cast when TryAffine(cast.Value, out affine):
        return true;
      case IrBinary { Op: IrBinaryOp.FAdd } add when TryAffine(add.Lhs, out var left) && Constant(add.Rhs) is { } rc:
        affine = left with { Offset = left.Offset + rc };
        return Finite(affine);
      case IrBinary { Op: IrBinaryOp.FAdd } add when Constant(add.Lhs) is { } lc && TryAffine(add.Rhs, out var right):
        affine = right with { Offset = right.Offset + lc };
        return Finite(affine);
      case IrBinary { Op: IrBinaryOp.FSub } sub when TryAffine(sub.Lhs, out var minuend) && Constant(sub.Rhs) is { } sc:
        affine = minuend with { Offset = minuend.Offset - sc };
        return Finite(affine);
      case IrBinary { Op: IrBinaryOp.FMul } mul when TryAffine(mul.Lhs, out var multiplicand) && Constant(mul.Rhs) is { } mc:
        affine = new(multiplicand.Source, multiplicand.Scale * mc, multiplicand.Offset * mc);
        return Finite(affine);
      case IrBinary { Op: IrBinaryOp.FMul } mul when Constant(mul.Lhs) is { } ml && TryAffine(mul.Rhs, out var multiplier):
        affine = new(multiplier.Source, multiplier.Scale * ml, multiplier.Offset * ml);
        return Finite(affine);
      case IrBinary { Op: IrBinaryOp.FDiv } div when TryAffine(div.Lhs, out var dividend)
                                                       && Constant(div.Rhs) is { } divisor and not 0.0:
        affine = new(dividend.Source, dividend.Scale / divisor, dividend.Offset / divisor);
        return Finite(affine);
      default:
        affine = default;
        return false;
    }
  }

  /// <summary>
  /// Evaluates the supported affine IR recursively with IEEE precision at each node. Extended-precision
  /// nodes are refused: approximating x87 80-bit intermediates with host double would defeat the point
  /// of retaining the expression tree here.
  /// </summary>
  private static bool TryEvaluate(IrValue value, IrValue source, long sourceValue, out double result) {
    switch (value) {
      case IrConstantFloat constant when constant.Type.Bits is 32 or 64:
        result = constant.Type.Bits == 32 ? (float)constant.Value : constant.Value;
        return true;

      case IrCast { Op: IrCastOp.SIToFP or IrCastOp.UIToFP, Type.Bits: 32 or 64 } cast:
        long integer;
        if (ReferenceEquals(cast.Value, source))
          integer = sourceValue;
        else if (cast.Value is IrConstantInt constant)
          integer = constant.Value;
        else {
          result = default;
          return false;
        }
        result = cast.Type.Bits == 32 ? (float)integer : integer;
        return double.IsFinite(result);

      case IrCast { Op: IrCastOp.FPExt, Type.Bits: 64 } cast
          when cast.Value.Type.Bits == 32 && TryEvaluate(cast.Value, source, sourceValue, out result):
        return true;

      case IrCast { Op: IrCastOp.FPTrunc, Type.Bits: 32 } cast
          when cast.Value.Type.Bits == 64 && TryEvaluate(cast.Value, source, sourceValue, out var widened):
        result = (float)widened;
        return double.IsFinite(result);

      case IrBinary { Type.Bits: 32 or 64 } binary
          when binary.Op is IrBinaryOp.FAdd or IrBinaryOp.FSub or IrBinaryOp.FMul or IrBinaryOp.FDiv
               && TryEvaluate(binary.Lhs, source, sourceValue, out var left)
               && TryEvaluate(binary.Rhs, source, sourceValue, out var right):
        if (binary.Op == IrBinaryOp.FDiv && right == 0.0) {
          result = default;
          return false;
        }
        result = EvaluateBinary(binary.Op, binary.Type, left, right);
        return double.IsFinite(result);

      default:
        result = default;
        return false;
    }
  }

  private static double EvaluateBinary(IrBinaryOp op, IrType type, double left, double right) {
    if (type.Bits == 32) {
      var l = (float)left;
      var r = (float)right;
      return op switch {
        IrBinaryOp.FAdd => l + r,
        IrBinaryOp.FSub => l - r,
        IrBinaryOp.FMul => l * r,
        IrBinaryOp.FDiv => l / r,
        _ => double.NaN,
      };
    }
    return op switch {
      IrBinaryOp.FAdd => left + right,
      IrBinaryOp.FSub => left - right,
      IrBinaryOp.FMul => left * right,
      IrBinaryOp.FDiv => left / right,
      _ => double.NaN,
    };
  }

  private static double? Constant(IrValue value)
    => value is IrConstantFloat { Type.Bits: 32 or 64, Value: var number } && double.IsFinite(number) ? number : null;

  private static bool Finite(Affine value) => double.IsFinite(value.Scale) && double.IsFinite(value.Offset);
}
