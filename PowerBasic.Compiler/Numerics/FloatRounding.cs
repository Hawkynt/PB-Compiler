namespace PowerBasic.Compiler.Numerics;

/// <summary>
/// The four rounding directions, numbered as the x87 control word's RC field numbers them, so a
/// control word can be turned into one of these by masking and shifting rather than by a table.
/// </summary>
public enum FloatRounding {
  /// <summary>Round to nearest, ties to even - the x87 default (control word <c>0x037F</c>).</summary>
  ToNearestEven = 0,

  /// <summary>Round toward negative infinity.</summary>
  Down = 1,

  /// <summary>Round toward positive infinity.</summary>
  Up = 2,

  /// <summary>Round toward zero.</summary>
  Truncate = 3,
}
