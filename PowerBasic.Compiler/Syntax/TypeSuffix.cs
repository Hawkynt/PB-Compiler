namespace PowerBasic.Compiler.Syntax;

/// <summary>PowerBASIC type-declaration suffix attached to an identifier or literal.</summary>
public enum TypeSuffix {
  None,
  /// <summary><c>%</c> — 16-bit signed integer.</summary>
  Integer,
  /// <summary><c>&amp;</c> — 32-bit signed long.</summary>
  Long,
  /// <summary><c>!</c> — 32-bit single-precision float.</summary>
  Single,
  /// <summary><c>#</c> — 64-bit double-precision float.</summary>
  Double,
  /// <summary><c>##</c> — 80-bit extended-precision float.</summary>
  Ext,
  /// <summary><c>$</c> — dynamic string.</summary>
  String,
}
