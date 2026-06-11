namespace PowerBasic.Compiler.Syntax;

/// <summary>PowerBASIC type-declaration suffix attached to an identifier or literal.</summary>
public enum TypeSuffix {
  None,
  /// <summary><c>?</c> — 8-bit unsigned byte (PB 3.0+).</summary>
  Byte,
  /// <summary><c>??</c> — 16-bit unsigned word (PB 3.0+).</summary>
  Word,
  /// <summary><c>???</c> — 32-bit unsigned double word (PB 3.0+).</summary>
  Dword,
  /// <summary><c>%</c> — 16-bit signed integer.</summary>
  Integer,
  /// <summary><c>&amp;</c> — 32-bit signed long.</summary>
  Long,
  /// <summary><c>&amp;&amp;</c> — 64-bit signed quad (PB 3.0+).</summary>
  Quad,
  /// <summary><c>!</c> — 32-bit single-precision float.</summary>
  Single,
  /// <summary><c>#</c> — 64-bit double-precision float.</summary>
  Double,
  /// <summary><c>##</c> — 80-bit extended-precision float.</summary>
  Ext,
  /// <summary><c>@</c> — 8-byte BCD fixed-point (FIX).</summary>
  Fix,
  /// <summary><c>@@</c> — 10-byte BCD floating-point (BCD).</summary>
  Bcd,
  /// <summary><c>$</c> — dynamic string.</summary>
  String,
  /// <summary><c>$$</c> — FLEX string.</summary>
  Flex,
}

public static class TypeSuffixExtensions {

  /// <summary>
  /// Canonical key text appended to a variable name to form its symbol-table key
  /// (binder and code generator must agree on this).
  /// </summary>
  public static string KeyText(this TypeSuffix suffix) => suffix switch {
    TypeSuffix.Byte => "?",
    TypeSuffix.Word => "??",
    TypeSuffix.Dword => "???",
    TypeSuffix.Integer => "%",
    TypeSuffix.Long => "&",
    TypeSuffix.Quad => "&&",
    TypeSuffix.Single => "!",
    TypeSuffix.Double => "#",
    TypeSuffix.Ext => "E",
    TypeSuffix.Fix => "@",
    TypeSuffix.Bcd => "@@",
    TypeSuffix.String => "$",
    TypeSuffix.Flex => "$$",
    _ => "",
  };
}
