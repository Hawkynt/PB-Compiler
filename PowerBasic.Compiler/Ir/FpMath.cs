namespace PowerBasic.Compiler.Ir;

/// <summary>Canonical math operations shared by fast-math annotation and range specialization.</summary>
internal enum IrFpMathFunction {
  Sqrt,
  Sin,
  Cos,
  Tan,
  Atan,
  Exp,
  Log,
  Pow,
  Fma,
}

internal static class IrFpMath {

  public static bool TryGet(IrCall call, out IrFpMathFunction kind) {
    kind = default;
    if (call.Callee is not IrFunction function)
      return false;

    var name = function.Name.AsSpan();
    if (name.StartsWith("llvm.", StringComparison.Ordinal)) {
      name = name[5..];
      var dot = name.IndexOf('.');
      if (dot >= 0)
        name = name[..dot];
    } else if (name.StartsWith("rt_", StringComparison.OrdinalIgnoreCase))
      name = name[3..];
    else
      return false;

    kind = name switch {
      "sqrt" or "sqr" => IrFpMathFunction.Sqrt,
      "sin" => IrFpMathFunction.Sin,
      "cos" => IrFpMathFunction.Cos,
      "tan" => IrFpMathFunction.Tan,
      "atan" or "atn" => IrFpMathFunction.Atan,
      "exp" => IrFpMathFunction.Exp,
      "log" => IrFpMathFunction.Log,
      "pow" => IrFpMathFunction.Pow,
      "fma" => IrFpMathFunction.Fma,
      _ => (IrFpMathFunction)(-1),
    };
    return (int)kind >= 0;
  }

  /// <summary>
  /// Compile-time oracle used only by the SPEED approximation path. The BCL result is intentionally
  /// not advertised as bit-identical to PowerBASIC/x87; strict mode never calls this evaluator.
  /// </summary>
  public static bool TryEvaluate(IrFpMathFunction kind, double argument, out double result) {
    result = kind switch {
      IrFpMathFunction.Sqrt => Math.Sqrt(argument),
      IrFpMathFunction.Sin => Math.Sin(argument),
      IrFpMathFunction.Cos => Math.Cos(argument),
      IrFpMathFunction.Tan => Math.Tan(argument),
      IrFpMathFunction.Atan => Math.Atan(argument),
      IrFpMathFunction.Exp => Math.Exp(argument),
      IrFpMathFunction.Log => Math.Log(argument),
      _ => double.NaN,
    };
    return kind is IrFpMathFunction.Sqrt or IrFpMathFunction.Sin or IrFpMathFunction.Cos
      or IrFpMathFunction.Tan or IrFpMathFunction.Atan or IrFpMathFunction.Exp or IrFpMathFunction.Log;
  }
}
