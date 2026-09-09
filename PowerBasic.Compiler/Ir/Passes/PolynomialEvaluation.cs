namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0337 — rewrites integer polynomials in one value into Horner form when doing so removes
/// multiplications. Floating point is deliberately excluded: reassociation changes rounding and the
/// IR has no fast-math contract yet.
/// </summary>
public static class PolynomialEvaluation {

  private const int _MAX_DEGREE = 8;

  /// <summary>Rewrites profitable integer polynomial roots; returns the number rewritten.</summary>
  public static int Run(IrFunction fn) {
    ArgumentNullException.ThrowIfNull(fn);
    if (fn.HasErrorHandler || fn.HasInlineAsm)
      return 0;

    var rewritten = 0;
    foreach (var instruction in fn.AllInstructions.ToList())
      if (instruction is IrBinary root && IsRoot(root) && TryRewrite(root))
        ++rewritten;
    return rewritten;
  }

  private static bool IsRoot(IrBinary root) {
    if (root.Type.Kind != IrTypeKind.Int || root.Op is not (IrBinaryOp.Add or IrBinaryOp.Sub or IrBinaryOp.Mul)
        || root.HasNoUsers)
      return false;
    return !root.Users.Any(user => user is IrBinary { Op: IrBinaryOp.Add or IrBinaryOp.Sub or IrBinaryOp.Mul } parent
                                   && parent.Type.SameStorage(root.Type));
  }

  private static bool TryRewrite(IrBinary root) {
    IrValue? variable = null;
    if (!TryRead(root, root.Type, ref variable, out var coefficients) || variable is null)
      return false;

    var degree = Degree(coefficients, root.Type);
    if (degree < 2 || CountMultiplies(root, new HashSet<IrValue>(ReferenceEqualityComparer.Instance)) <= degree)
      return false;

    var block = root.Parent;
    if (block is null)
      return false;

    IrValue accumulator = new IrConstantInt(root.Type, Wrap(root.Type, coefficients[degree]));
    for (var power = degree - 1; power >= 0; --power) {
      accumulator = block.InsertBefore(new IrBinary(IrBinaryOp.Mul, accumulator, variable), root);
      if (!IsZero(coefficients[power], root.Type))
        accumulator = block.InsertBefore(new IrBinary(IrBinaryOp.Add, accumulator,
          new IrConstantInt(root.Type, Wrap(root.Type, coefficients[power]))), root);
    }

    root.ReplaceAllUsesWith(accumulator);
    root.EraseFromParent();
    return true;
  }

  private static bool TryRead(IrValue value, IrType type, ref IrValue? variable, out long[] coefficients) {
    coefficients = new long[_MAX_DEGREE + 1];
    switch (value) {
      case IrConstantInt constant when constant.Type.SameStorage(type):
        coefficients[0] = Wrap(type, constant.Value);
        return true;
      case IrBinary binary when binary.Type.SameStorage(type)
                                && binary.Op is IrBinaryOp.Add or IrBinaryOp.Sub or IrBinaryOp.Mul:
        if (!TryRead(binary.Lhs, type, ref variable, out var left)
            || !TryRead(binary.Rhs, type, ref variable, out var right))
          return false;
        coefficients = binary.Op switch {
          IrBinaryOp.Add => Add(type, left, right, subtract: false),
          IrBinaryOp.Sub => Add(type, left, right, subtract: true),
          _ => Multiply(type, left, right),
        };
        return coefficients.Length != 0;
      default:
        if (!value.Type.SameStorage(type))
          return false;
        if (variable is null)
          variable = value;
        else if (!ReferenceEquals(variable, value))
          return false;
        coefficients[1] = 1;
        return true;
    }
  }

  private static long[] Add(IrType type, long[] left, long[] right, bool subtract) {
    var result = new long[_MAX_DEGREE + 1];
    for (var i = 0; i < result.Length; ++i)
      result[i] = Wrap(type, subtract ? unchecked(left[i] - right[i]) : unchecked(left[i] + right[i]));
    return result;
  }

  private static long[] Multiply(IrType type, long[] left, long[] right) {
    var result = new long[_MAX_DEGREE + 1];
    for (var i = 0; i < left.Length; ++i) {
      if (IsZero(left[i], type))
        continue;
      for (var j = 0; j < right.Length; ++j) {
        if (IsZero(right[j], type))
          continue;
        if (i + j > _MAX_DEGREE)
          return [];
        result[i + j] = Wrap(type, unchecked(result[i + j] + unchecked(left[i] * right[j])));
      }
    }
    return result;
  }

  private static int Degree(long[] coefficients, IrType type) {
    for (var i = coefficients.Length - 1; i >= 0; --i)
      if (!IsZero(coefficients[i], type))
        return i;
    return 0;
  }

  private static int CountMultiplies(IrValue value, HashSet<IrValue> seen) {
    if (!seen.Add(value) || value is not IrBinary binary)
      return 0;
    return (binary.Op == IrBinaryOp.Mul ? 1 : 0)
           + CountMultiplies(binary.Lhs, seen)
           + CountMultiplies(binary.Rhs, seen);
  }

  private static bool IsZero(long value, IrType type) => Wrap(type, value) == 0;

  private static long Wrap(IrType type, long value) {
    if (type.Bits >= 64)
      return value;
    var mask = (1UL << type.Bits) - 1;
    return unchecked((long)(unchecked((ulong)value) & mask));
  }
}
