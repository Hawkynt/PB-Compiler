namespace PowerBasic.Compiler.Syntax;

/// <summary>PowerBASIC/DOS compiler version selected with <c>--dialect</c> (default <see cref="Pb35"/>).</summary>
public enum Dialect {
  Pb20 = 20,
  Pb21 = 21,
  Pb30 = 30,
  Pb31 = 31,
  Pb32 = 32,
  Pb35 = 35,
  /// <summary>
  /// The envisioned optimizing successor (docs/PB36.md): a strict language
  /// superset of <see cref="Pb35"/> with byte-identical observable behavior -
  /// it only enables optimizations (runtime trimming, constant folding,
  /// strength reduction, zero idioms, ...).
  /// </summary>
  Pb36 = 36,
}

/// <summary>Version-gated language features (see docs/DIALECTS.md for the researched matrix).</summary>
public enum LanguageFeature {
  // PB 3.0
  InlineAsm,
  UnsignedTypes,
  QuadType,
  TypeUnion,
  HugeArrays,
  // PB 3.1
  TypedRadixLiterals,
  AliasClause,
  AnyParameter,
  UdtComparison,
  // PB 3.2
  Pointers,
  CodePointers,
  Ptr32Functions,
  IdentifierUnderscores,
  // PB 3.5
  AsciizType,
  ConcatOperator,
  ElseIfMeta,
  TrimFunction,
  RedimPreserve,
  IndexedPointers,
  StdInOut,
  SizeofFunction,
  ErrClear,
  RndRange,
  CvStartOffset,
  VirtualArrays,
  AscStatement,
  SetEof,
  ConsInOut,
  StringPtrInType,
}

/// <summary>
/// The single data-driven gating table: which dialect introduced which feature,
/// and the diagnostic text used when source needs a newer dialect.
/// </summary>
public static class DialectFacts {

  private static readonly Dictionary<LanguageFeature, (Dialect Min, string What)> _gates = new() {
    [LanguageFeature.InlineAsm] = (Dialect.Pb30, "inline assembler ('!' statements)"),
    [LanguageFeature.UnsignedTypes] = (Dialect.Pb30, "unsigned types (BYTE/WORD/DWORD, '?'/'??'/'???' suffixes)"),
    [LanguageFeature.QuadType] = (Dialect.Pb30, "QUAD (64-bit) type ('&&' suffix)"),
    [LanguageFeature.TypeUnion] = (Dialect.Pb30, "TYPE/UNION user-defined types"),
    [LanguageFeature.HugeArrays] = (Dialect.Pb30, "HUGE arrays"),
    [LanguageFeature.TypedRadixLiterals] = (Dialect.Pb31, "type suffix on a radix literal"),
    [LanguageFeature.AliasClause] = (Dialect.Pb31, "ALIAS clause"),
    [LanguageFeature.AnyParameter] = (Dialect.Pb31, "ANY parameter type"),
    [LanguageFeature.UdtComparison] = (Dialect.Pb31, "whole-value TYPE/UNION comparison"),
    [LanguageFeature.Pointers] = (Dialect.Pb32, "data pointers (PTR types, '@' dereference)"),
    [LanguageFeature.CodePointers] = (Dialect.Pb32, "code pointers (CALL/GOTO/GOSUB DWORD)"),
    [LanguageFeature.Ptr32Functions] = (Dialect.Pb32, "VARPTR32/STRPTR32/CODEPTR32"),
    [LanguageFeature.IdentifierUnderscores] = (Dialect.Pb32, "underscores in identifiers and labels"),
    [LanguageFeature.AsciizType] = (Dialect.Pb35, "ASCIIZ strings"),
    [LanguageFeature.ConcatOperator] = (Dialect.Pb35, "'&' string concatenation operator"),
    [LanguageFeature.ElseIfMeta] = (Dialect.Pb35, "$ELSEIF metastatement"),
    [LanguageFeature.TrimFunction] = (Dialect.Pb35, "TRIM$"),
    [LanguageFeature.RedimPreserve] = (Dialect.Pb35, "REDIM PRESERVE"),
    [LanguageFeature.IndexedPointers] = (Dialect.Pb35, "indexed pointer dereference '@p[i]'"),
    [LanguageFeature.StdInOut] = (Dialect.Pb35, "STDIN/STDOUT"),
    [LanguageFeature.SizeofFunction] = (Dialect.Pb35, "SIZEOF"),
    [LanguageFeature.ErrClear] = (Dialect.Pb35, "ERRCLEAR"),
    [LanguageFeature.RndRange] = (Dialect.Pb35, "RND(a, z) range form"),
    [LanguageFeature.CvStartOffset] = (Dialect.Pb35, "CVx start offset"),
    [LanguageFeature.VirtualArrays] = (Dialect.Pb35, "VIRTUAL arrays"),
    [LanguageFeature.AscStatement] = (Dialect.Pb35, "ASC statement"),
    [LanguageFeature.SetEof] = (Dialect.Pb35, "SETEOF"),
    [LanguageFeature.ConsInOut] = (Dialect.Pb35, "CONSIN/CONSOUT"),
    [LanguageFeature.StringPtrInType] = (Dialect.Pb35, "STRING PTR fields inside TYPE/UNION"),
  };

  /// <summary>Version-gated intrinsic functions (checked by the binder at call sites).</summary>
  private static readonly Dictionary<string, LanguageFeature> _intrinsicGates = new(StringComparer.OrdinalIgnoreCase) {
    ["TRIM$"] = LanguageFeature.TrimFunction,
    ["SIZEOF"] = LanguageFeature.SizeofFunction,
    ["ERRCLEAR"] = LanguageFeature.ErrClear,
    ["CONSIN"] = LanguageFeature.ConsInOut,
    ["CONSOUT"] = LanguageFeature.ConsInOut,
    ["VARPTR32"] = LanguageFeature.Ptr32Functions,
    ["STRPTR32"] = LanguageFeature.Ptr32Functions,
    ["CODEPTR32"] = LanguageFeature.Ptr32Functions,
  };

  /// <summary>Human-readable dialect name, e.g. "PB 3.5".</summary>
  public static string DisplayName(this Dialect dialect) => $"PB {(int)dialect / 10}.{(int)dialect % 10}";

  /// <summary>Lowest dialect providing <paramref name="feature"/>.</summary>
  public static Dialect MinimumDialect(LanguageFeature feature) => _gates[feature].Min;

  public static bool IsAvailable(LanguageFeature feature, Dialect dialect) => dialect >= _gates[feature].Min;

  /// <summary>Diagnostic text: "X requires PowerBASIC 3.2 (current dialect: PB 3.0)".</summary>
  public static string RequirementMessage(LanguageFeature feature, Dialect dialect) {
    var (min, what) = _gates[feature];
    return $"{what} requires PowerBASIC {(int)min / 10}.{(int)min % 10} (current dialect: {dialect.DisplayName()})";
  }

  /// <summary>Gate of an intrinsic function name; null when the intrinsic is available everywhere.</summary>
  public static LanguageFeature? IntrinsicGate(string name) => _intrinsicGates.TryGetValue(name, out var feature) ? feature : null;
}
