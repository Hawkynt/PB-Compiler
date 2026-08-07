namespace PowerBasic.Compiler.Syntax;

/// <summary>
/// Compiler dialect selected with <c>--dialect</c> (default <see cref="Pb35"/>).
/// Two ordered families share the value space: the Borland lineage (Turbo
/// Basic, PB's direct ancestor by the same author, sits below PB 2.0 so every
/// PB feature gate automatically excludes it) occupies values &lt; 100; the
/// Microsoft lineage (QuickBASIC and its BASIC PDS successor) starts at 100.
/// Ordinal comparisons are only meaningful within one family - cross-family
/// checks go through <see cref="DialectFacts"/>.
/// </summary>
public enum Dialect {
  Tb10 = 10,
  Tb11 = 11,
  Pb20 = 20,
  Pb21 = 21,
  Pb30 = 30,
  Pb31 = 31,
  Pb32 = 32,
  Pb35 = 35,
  /// <summary>
  /// The envisioned optimizing successor (docs/PB36.md). Today it is a strict
  /// superset of <see cref="Pb35"/> with byte-identical observable behavior that
  /// only switches the (now dialect-agnostic) optimizer on by default - i.e. a
  /// preset for "pb35 front-end + Optimize". It is kept as its own dialect on
  /// purpose: it is the planned home for genuinely new syntax and a richer
  /// runtime that WOULD make it a real language - e.g. <c>VAR</c>/<c>AUTO</c>
  /// type inference with fused declare+initialize, lambdas and expression-bodied
  /// members (VB.NET-style), EMS/XMS-backed heaps beyond 1 MB, a deterministic
  /// reference-counting GC, and additional data types. Those are future work;
  /// for now pb36 == optimized pb35.
  /// </summary>
  Pb36 = 36,
  /// <summary>BASICA - IBM's Advanced BASIC interpreter (Microsoft lineage, pre-QuickBASIC). Line-numbered, Microsoft Binary Format (MBF) floats. Verified against the genuine interpreter by output diff (not byte-identical EXE).</summary>
  Basica = 100,
  /// <summary>GW-BASIC - Microsoft's MS-DOS BASIC interpreter, language-identical to <see cref="Basica"/> (MBF floats, line numbers).</summary>
  Gw = 101,
  Qb10 = 110,
  Qb20 = 120,
  Qb30 = 130,
  Qb40 = 140,
  Qb45 = 145,
  /// <summary>QBasic - the interpreter shipped with MS-DOS 5.0+ (the QuickBASIC 4.5 environment minus the compiler/linker). Same language surface as <see cref="Qb45"/>, IEEE floats; cannot produce an EXE itself, so it is verified by interpreter output diff.</summary>
  Qbasic = 146,
  Pds70 = 170,
  Pds71 = 171,
}

/// <summary>BASIC product family; feature gating and runtime quirks route on it.</summary>
public enum DialectFamily {
  /// <summary>Turbo Basic and PowerBASIC (Bob Zale's lineage).</summary>
  Borland,
  /// <summary>QuickBASIC and BASIC PDS.</summary>
  Microsoft,
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
  // PB 3.6 (new syntax, see docs/PB36.md)
  ExpressionBodiedProc,
  CompoundAssignment,
  DimInitializer,
  TernaryIf,
  ObjectInitializer,
  ShortCircuitOps,
  SubFunctionOverloading,
  ShiftRotateOps,
  PointerArithmetic,
  EnumType,
  WithBlock,
  DefaultParameters,
  NamedArguments,
  XmsEmsArrays,
  FromEndIndex,
  ArrayInitializer,
  NestedProcedures,
  Lambdas,
  ProcPointers,
  NamedDelegates,
  CollectionLiteral,
  ForEach,
  StringInterpolation,
  TryCatch,
  Coroutines,
  TypeMethods,
  Generics,
  Defer,
  OperatorOverloading,
  Tuples,
  BitFields,
  TypeLayout,
  TypeAlias,
  StaticAssert,
  InOperator,
  StackArrays,
  ArraySlices,
  PartialApplication,
  DiscriminatedUnions,
  ResourceEmbed,
  Contracts,
  CompileTimeReflection,
  NullableTypes,
  WideIntegers,
  TypeAliases,
  SegmentedPeekPoke,
  ChainedComparison,
  NullConditional,
  Events,
  UsingStatement,
  NoInline,

  // Statements the Borland line has always had and the Microsoft line never did. They are gated at
  // the OLDEST Borland dialect, so within that family they are simply always available; what the gate
  // buys is the other half - a feature absent from the Microsoft table is unavailable in every
  // Microsoft dialect, which is exactly the truth about these.
  IncrDecrStatements,
  DelayStatement,
  SharedTypeClause,
  ExtendedNumericTypes,
  PublicStorage,
  // The same shape, settled against the genuine compilers rather than the history books: PBC 3.0
  // and 3.5 compile all four cleanly, and BC 1.00 and 4.50 reject every one of them (each a Severe
  // Error against a control program that compiles with none). They were accepted in every dialect
  // here, BASICA included, until that was checked.
  EquateStatement,
  IterateStatement,
  ArraySortScan,
  BitStatements,

  /// <summary>
  /// The PB 3.0 machine-level wave: REG, and the SHIFT / ROTATE STATEMENTS. Not to be confused with
  /// <see cref="ShiftRotateOps"/>, which is the PB 3.6 operator spelling of the same idea
  /// (<c>x &lt;&lt; 1</c>) - the statement came fifteen years earlier.
  /// </summary>
  RegStatement,
  ShiftRotateStatements,

  /// <summary>
  /// <c>UNION</c> specifically. QuickBASIC 4.0 brought TYPE to the Microsoft line but never the
  /// overlapping variant, so the two share <see cref="TypeUnion"/> for the TYPE half and part company
  /// here - which is why the gate exists at all rather than TYPE and UNION being one feature.
  /// </summary>
  UnionType,

  /// <summary>
  /// The 32-bit LONG. This one is genuinely in both lineages but not from the beginning of either:
  /// Turbo Basic brought it to Bob Zale's line and carried it into PowerBASIC, and QuickBASIC brought
  /// it to Microsoft's - but BASICA and GW-BASIC have only INTEGER, SINGLE, DOUBLE and STRING, so
  /// DEFLNG is meaningless there.
  /// </summary>
  LongType,
  /// <summary>Named statement labels; BASICA/GW-BASIC accept numeric line labels only.</summary>
  NamedLabels,
  /// <summary>Multi-line IF / ELSEIF / END IF; BASICA/GW-BASIC have only single-line IF.</summary>
  BlockIf,
  /// <summary>DO / LOOP; BASICA/GW-BASIC use WHILE / WEND instead.</summary>
  DoLoop,
  /// <summary>SELECT CASE blocks, absent from BASICA/GW-BASIC.</summary>
  SelectCase,
  /// <summary>SUB/FUNCTION/DECLARE procedures, absent from BASICA/GW-BASIC.</summary>
  Procedures,
  /// <summary>Structured EXIT statements, absent from BASICA/GW-BASIC.</summary>
  ExitStatement,
  /// <summary>PowerBASIC/Turbo Basic's EXIT FAR extension, absent from the Microsoft line.</summary>
  ExitFar,
  /// <summary>PowerBASIC/Turbo Basic compiler metastatements such as $CPU.</summary>
  MetaStatements,
  /// <summary>QuickBASIC/PDS REM/apostrophe comment metacommands such as REM $INCLUDE.</summary>
  MicrosoftCommentMetaStatements,
  /// <summary>PB-Compiler's PB 3.6-only cross-dialect runtime directive.</summary>
  CompatMeta,
  /// <summary>The $DIM ALL / $DIM ARRAY declaration-discipline metastatement (PB 3.0; docs/DIALECTS.md).</summary>
  DimMetaStatement,
  /// <summary>PB 3.6 optimizer modes, 80486+ tiers, and CPU feature flags.</summary>
  ExtendedMetaArguments,
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
    [LanguageFeature.ExpressionBodiedProc] = (Dialect.Pb36, "expression-bodied FUNCTION ('= expression' single-expression body)"),
    [LanguageFeature.CompoundAssignment] = (Dialect.Pb36, "compound assignment operators (+=, -=, *=, /=, \\=, ^=, &=)"),
    [LanguageFeature.DimInitializer] = (Dialect.Pb36, "DIM with initializer ('DIM x = value' / 'DIM x AS type = value')"),
    [LanguageFeature.TernaryIf] = (Dialect.Pb36, "ternary IF() operator ('IF(condition, trueValue, falseValue)')"),
    [LanguageFeature.ObjectInitializer] = (Dialect.Pb36, "object initializer ('NEW type { .field = value }')"),
    [LanguageFeature.ShortCircuitOps] = (Dialect.Pb36, "short-circuit operators (ANDALSO / ORELSE)"),
    [LanguageFeature.SubFunctionOverloading] = (Dialect.Pb36, "SUB/FUNCTION overloading (same name, different signature)"),
    [LanguageFeature.ShiftRotateOps] = (Dialect.Pb36, "shift/rotate/bitwise operators (<<, >>, <<<, >>>, <<>, <>>, |)"),
    [LanguageFeature.PointerArithmetic] = (Dialect.Pb36, "scaled pointer arithmetic (ptr +* index / ptr -* index)"),
    [LanguageFeature.EnumType] = (Dialect.Pb36, "ENUM declarations"),
    [LanguageFeature.WithBlock] = (Dialect.Pb36, "WITH ... END WITH blocks"),
    [LanguageFeature.DefaultParameters] = (Dialect.Pb36, "default parameter values ('param AS type = value')"),
    [LanguageFeature.NamedArguments] = (Dialect.Pb36, "named arguments ('name := value')"),
    [LanguageFeature.XmsEmsArrays] = (Dialect.Pb36, "XMS/EMS arrays ('DIM XMS/EMS a(...)')"),
    [LanguageFeature.FromEndIndex] = (Dialect.Pb36, "from-end array index ('arr(^n)')"),
    [LanguageFeature.ArrayInitializer] = (Dialect.Pb36, "array initializer literal ('= { v1, v2, lo TO hi, ..arr }')"),
    [LanguageFeature.NestedProcedures] = (Dialect.Pb36, "nested local SUB/FUNCTION (with stack capture of outer locals)"),
    [LanguageFeature.Lambdas] = (Dialect.Pb36, "inline lambdas ('FUNCTION(params) => expr')"),
    [LanguageFeature.ProcPointers] = (Dialect.Pb36, "typed procedure pointers ('DIM f AS FUNCTION(types) AS type')"),
    [LanguageFeature.NamedDelegates] = (Dialect.Pb36, "named delegate types (a DECLAREd SUB/FUNCTION name reused as a procedure-pointer type)"),
    [LanguageFeature.CollectionLiteral] = (Dialect.Pb36, "bracketed collection/range literal ('[v1, v2, lo TO hi, ..arr]')"),
    [LanguageFeature.ForEach] = (Dialect.Pb36, "FOR EACH ... IN (array or '[lo..hi]' range)"),
    [LanguageFeature.StringInterpolation] = (Dialect.Pb36, "interpolated string ('$\"text {expr} {expr:fmt}\"')"),
    [LanguageFeature.TryCatch] = (Dialect.Pb36, "structured exception handling (TRY / CATCH / FINALLY / END TRY)"),
    [LanguageFeature.Coroutines] = (Dialect.Pb36, "coroutines (YIELD statement)"),
    [LanguageFeature.TypeMethods] = (Dialect.Pb36, "TYPE methods/properties (SUB / FUNCTION / PROPERTY members)"),
    [LanguageFeature.Generics] = (Dialect.Pb36, "compile-time generics ('TYPE Name OF T' / 'AS Name OF type')"),
    [LanguageFeature.Defer] = (Dialect.Pb36, "DEFER statement (runs on block exit)"),
    [LanguageFeature.OperatorOverloading] = (Dialect.Pb36, "OPERATOR overloading inside a TYPE"),
    [LanguageFeature.Tuples] = (Dialect.Pb36, "tuples / multiple return values ('AS (T1, T2)', 'a, b = f()')"),
    [LanguageFeature.BitFields] = (Dialect.Pb36, "bit-field TYPE members ('field AS BIT * n')"),
    [LanguageFeature.TypeLayout] = (Dialect.Pb36, "TYPE layout control ('TYPE Name PACKED | ALIGN n | SIZE n', 'field AS T AT offset')"),
    [LanguageFeature.TypeAlias] = (Dialect.Pb36, "type alias ('TYPE Name AS type')"),
    [LanguageFeature.StaticAssert] = (Dialect.Pb36, "compile-time assertion ('$ASSERT cond [, \"message\"]')"),
    [LanguageFeature.InOperator] = (Dialect.Pb36, "membership test ('x IN lo TO hi', 'x IN {a, b, lo TO hi}')"),
    [LanguageFeature.StackArrays] = (Dialect.Pb36, "stack arrays ('DIM STACK a(1 TO n) AS T' - frame-resident, reentrant)"),
    [LanguageFeature.ArraySlices] = (Dialect.Pb36, "array slices ('b() = a(lo TO hi)', 'FOR EACH v IN a(lo TO hi)' - bounds omissible or from-end ^n)"),
    [LanguageFeature.PartialApplication] = (Dialect.Pb36, "partial application / composition ('BIND(f, consts...)', 'COMPOSE(f, g)' - synthesized delegate thunks)"),
    [LanguageFeature.DiscriminatedUnions] = (Dialect.Pb36, "discriminated unions ('UNION Name / CASE Tag / fields... / END UNION' with IS pattern tests)"),
    [LanguageFeature.ResourceEmbed] = (Dialect.Pb36, "embedded binary resource ('$RESOURCE name, \"file\"')"),
    [LanguageFeature.Contracts] = (Dialect.Pb36, "contracts ('REQUIRE cond [, \"msg\"]' / 'ENSURE ...' - checked at run time, compiled out under $OPTIMIZE SPEED)"),
    [LanguageFeature.CompileTimeReflection] = (Dialect.Pb36, "compile-time reflection (TYPEOF$/FIELDCOUNT/FIELDNAME$/FIELDOFFSET/FIELDSIZE, SIZEOF of a type name)"),
    [LanguageFeature.NullableTypes] = (Dialect.Pb36, "nullable types ('AS T?', NOTHING, the '??' null-coalescing operator)"),
    [LanguageFeature.WideIntegers] = (Dialect.Pb36, "wide integer types (INT128/256/512, UINT128/256/512 - emulated multi-word)"),
    [LanguageFeature.TypeAliases] = (Dialect.Pb36, "natural type-name aliases (INT8/SBYTE, INT16/SHORT, INT32, INT64, UINT8/UINT16/UINT32, DQUAD/QQUAD, DQWORD/QQWORD - alternative spellings of the existing types)"),
    [LanguageFeature.SegmentedPeekPoke] = (Dialect.Pb36, "segmented PEEK/POKE ('POKE seg:offset, val' / 'PEEK(seg:offset)')"),
    [LanguageFeature.ChainedComparison] = (Dialect.Pb36, "chained comparison ('lo <= x < hi')"),
    [LanguageFeature.NullConditional] = (Dialect.Pb36, "null-conditional access ('obj?.field', 'arr?[i]')"),
    [LanguageFeature.Events] = (Dialect.Pb36, "events ('EVENT name AS delegate', 'name += handler', raised by invoking 'name(args)')"),
    [LanguageFeature.UsingStatement] = (Dialect.Pb36, "USING statement ('USING v AS Type' - Dispose runs on scope exit)"),
    [LanguageFeature.NoInline] = (Dialect.Pb36, "NOINLINE procedure modifier ('SUB Name(...) NOINLINE' - the optimizer keeps a real call)"),
    [LanguageFeature.IncrDecrStatements] = (Dialect.Tb10, "INCR / DECR"),
    [LanguageFeature.DelayStatement] = (Dialect.Tb10, "DELAY"),
    [LanguageFeature.SharedTypeClause] = (Dialect.Tb10, "SHARED / STATIC inside a DIM type clause ('DIM x AS SHARED type')"),
    [LanguageFeature.ExtendedNumericTypes] = (Dialect.Tb10, "the extended numeric types (EXT / FIX / BCD / FLEX)"),
    [LanguageFeature.PublicStorage] = (Dialect.Tb10, "PUBLIC / EXT storage declarations"),
    [LanguageFeature.EquateStatement] = (Dialect.Tb10, "the equate statement ('%name = value')"),
    [LanguageFeature.IterateStatement] = (Dialect.Tb10, "ITERATE"),
    [LanguageFeature.ArraySortScan] = (Dialect.Tb10, "the ARRAY SORT / ARRAY SCAN statements"),
    [LanguageFeature.BitStatements] = (Dialect.Tb10, "the BIT SET / BIT RESET / BIT TOGGLE statements"),
    [LanguageFeature.RegStatement] = (Dialect.Pb30, "the REG statement"),
    [LanguageFeature.ShiftRotateStatements] = (Dialect.Pb30, "the SHIFT / ROTATE statements"),
    [LanguageFeature.UnionType] = (Dialect.Pb30, "UNION (the overlapping variant of TYPE)"),
    [LanguageFeature.LongType] = (Dialect.Tb10, "LONG (32-bit) type"),
    [LanguageFeature.NamedLabels] = (Dialect.Tb10, "named labels"),
    [LanguageFeature.BlockIf] = (Dialect.Tb10, "multi-line IF blocks"),
    [LanguageFeature.DoLoop] = (Dialect.Tb10, "DO / LOOP"),
    [LanguageFeature.SelectCase] = (Dialect.Tb10, "SELECT CASE"),
    [LanguageFeature.Procedures] = (Dialect.Tb10, "SUB/FUNCTION procedures"),
    [LanguageFeature.ExitStatement] = (Dialect.Tb10, "EXIT statements"),
    [LanguageFeature.ExitFar] = (Dialect.Tb10, "EXIT FAR"),
    [LanguageFeature.MetaStatements] = (Dialect.Tb10, "compiler metastatements"),
    // The Borland minimum is unused: the lexer only recognizes this syntax in the Microsoft family.
    [LanguageFeature.MicrosoftCommentMetaStatements] = (Dialect.Pb36, "REM/apostrophe comment metacommands"),
    // pb35, not pb36: $COMPAT is what the DECOMPILER emits so that a cross-family program recompiles
    // under --dialect pb35 with its own runtime semantics - "un-parses back to compile-clean PB 3.5"
    // is the contract, and roundtrip-check.sh recompiles at pb35 exactly. Gating it at pb36 made
    // BasicWriter's own output uncompilable by the gate that checks it (28 of the corpus failed).
    [LanguageFeature.CompatMeta] = (Dialect.Pb35, "$COMPAT metastatement"),
    // PB 3.0 per docs/DIALECTS.md, which lists "$DIM ALL/ARRAY" among that release's additions. The
    // argument was already validated; nothing checked the dialect, so Turbo Basic took it too.
    [LanguageFeature.DimMetaStatement] = (Dialect.Pb30, "the $DIM ALL / $DIM ARRAY metastatement"),
    [LanguageFeature.ExtendedMetaArguments] = (Dialect.Pb36, "extended metastatement arguments and 80486+ CPU tiers"),
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

  /// <summary>
  /// Features the Microsoft lineage provides, with the QB/PDS version that
  /// introduced them. A feature absent here is Borland-only and unavailable in
  /// every Microsoft dialect (and vice versa for the PB gate table).
  /// </summary>
  private static readonly Dictionary<LanguageFeature, Dialect> _microsoftGates = new() {
    [LanguageFeature.TypeUnion] = Dialect.Qb40,        // TYPE...END TYPE (QB has no UNION; the binder rejects UNION separately)
    [LanguageFeature.RedimPreserve] = Dialect.Pds70,   // REDIM with far strings; QB REDIM never preserves
    [LanguageFeature.LongType] = Dialect.Qb10,        // QuickBASIC has LONG; BASICA and GW-BASIC do not
    [LanguageFeature.NamedLabels] = Dialect.Qb10,
    [LanguageFeature.BlockIf] = Dialect.Qb10,
    [LanguageFeature.DoLoop] = Dialect.Qb10,
    [LanguageFeature.SelectCase] = Dialect.Qb10,
    [LanguageFeature.Procedures] = Dialect.Qb10,
    [LanguageFeature.ExitStatement] = Dialect.Qb10,
    [LanguageFeature.MicrosoftCommentMetaStatements] = Dialect.Qb10,
  };

  /// <summary>Human-readable dialect name, e.g. "PB 3.5", "TB 1.1", "QB 4.5", "PDS 7.1", "GW-BASIC".</summary>
  public static string DisplayName(this Dialect dialect) {
    switch (dialect) {
      case Dialect.Basica: return "BASICA";
      case Dialect.Gw: return "GW-BASIC";
      case Dialect.Qbasic: return "QBasic";
    }
    var v = (int)dialect % 100;
    var prefix = dialect.Family() == DialectFamily.Microsoft
      ? dialect >= Dialect.Pds70 ? "PDS" : "QB"
      : dialect.IsTurboBasic() ? "TB" : "PB";
    return $"{prefix} {v / 10}.{v % 10}";
  }

  /// <summary>The canonical lowercase token for a dialect (the <c>--dialect</c> / <c>$COMPAT</c> spelling); the inverse of <see cref="TryParse"/>.</summary>
  public static string CanonicalName(this Dialect dialect) => dialect switch {
    Dialect.Basica => "basica", Dialect.Gw => "gw", Dialect.Qbasic => "qbasic",
    _ => (dialect.Family() == DialectFamily.Microsoft
        ? dialect >= Dialect.Pds70 ? "pds" : "qb"
        : dialect.IsTurboBasic() ? "tb" : "pb") + ((int)dialect % 100),
  };

  /// <summary>Parses a dialect token (<c>tb11</c>, <c>qb45</c>, <c>pds70</c>, <c>pb35</c>, ...); the inverse of <see cref="CanonicalName"/>.</summary>
  public static bool TryParse(string name, out Dialect dialect) {
    switch (name.Trim().ToLowerInvariant()) {
      case "basica": dialect = Dialect.Basica; return true;
      case "gw": case "gwbasic": dialect = Dialect.Gw; return true;
      case "qbasic": dialect = Dialect.Qbasic; return true;
      case "qb10": dialect = Dialect.Qb10; return true;
      case "qb20": dialect = Dialect.Qb20; return true;
      case "qb30": dialect = Dialect.Qb30; return true;
      case "qb40": dialect = Dialect.Qb40; return true;
      case "qb45": dialect = Dialect.Qb45; return true;
      case "pds70": dialect = Dialect.Pds70; return true;
      case "pds71": dialect = Dialect.Pds71; return true;
      case "tb10": dialect = Dialect.Tb10; return true;
      case "tb11": dialect = Dialect.Tb11; return true;
      case "pb20": dialect = Dialect.Pb20; return true;
      case "pb21": dialect = Dialect.Pb21; return true;
      case "pb30": dialect = Dialect.Pb30; return true;
      case "pb31": dialect = Dialect.Pb31; return true;
      case "pb32": dialect = Dialect.Pb32; return true;
      case "pb35": dialect = Dialect.Pb35; return true;
      case "pb36": dialect = Dialect.Pb36; return true;
      default: dialect = Dialect.Pb35; return false;
    }
  }

  /// <summary>The classic Microsoft BASIC interpreters (BASICA / GW-BASIC / QBasic): they ship no compiler, so they are oracle-verified by output diff rather than a byte-identical EXE.</summary>
  public static bool IsInterpreter(this Dialect dialect)
    => dialect is Dialect.Basica or Dialect.Gw or Dialect.Qbasic;

  /// <summary>BASICA / GW-BASIC: the line-numbered MBF-float interpreters (language-identical; pre-QuickBASIC).</summary>
  public static bool IsGwBasica(this Dialect dialect)
    => dialect is Dialect.Basica or Dialect.Gw;

  /// <summary>Turbo Basic 1.x - Borland's PB predecessor (16-digit double-everything runtime).</summary>
  public static bool IsTurboBasic(this Dialect dialect) => dialect <= Dialect.Tb11;

  /// <summary>
  /// QB 1.0-3.0 share the BASCOM runtime heritage: half-away-from-zero
  /// float-to-integer rounding and a CP/M-style ^Z terminating sequential
  /// output (oracle-verified identical across all three).
  /// </summary>
  public static bool IsBascomRuntime(this Dialect dialect)
    => dialect.Family() == DialectFamily.Microsoft && dialect < Dialect.Qb40;

  /// <summary>Product family - ordinal dialect comparisons are only valid within one family. The Microsoft lineage occupies values &gt;= 100 (the interpreters BASICA/GW-BASIC sit below QB 1.0).</summary>
  public static DialectFamily Family(this Dialect dialect)
    => (int)dialect >= 100 ? DialectFamily.Microsoft : DialectFamily.Borland;

  /// <summary>True for a Borland-lineage dialect of at least <paramref name="min"/> (false for every Microsoft dialect).</summary>
  public static bool IsPbAtLeast(this Dialect dialect, Dialect min)
    => dialect.Family() == DialectFamily.Borland && dialect >= min;

  /// <summary>Lowest dialect providing <paramref name="feature"/>.</summary>
  public static Dialect MinimumDialect(LanguageFeature feature) => _gates[feature].Min;

  public static bool IsAvailable(LanguageFeature feature, Dialect dialect)
    => dialect.Family() == DialectFamily.Microsoft
      ? _microsoftGates.TryGetValue(feature, out var min) && dialect >= min
      : dialect >= _gates[feature].Min;

  /// <summary>Diagnostic text: "X requires PowerBASIC 3.2 (current dialect: PB 3.0)".</summary>
  public static string RequirementMessage(LanguageFeature feature, Dialect dialect) {
    var (min, what) = _gates[feature];
    if (dialect.Family() == DialectFamily.Microsoft)
      return _microsoftGates.TryGetValue(feature, out var msMin)
        ? $"{what} requires {msMin.DisplayName()} (current dialect: {dialect.DisplayName()})"
        : $"{what} is not available in the Microsoft BASIC family (current dialect: {dialect.DisplayName()})";
    return $"{what} requires PowerBASIC {(int)min / 10}.{(int)min % 10} (current dialect: {dialect.DisplayName()})";
  }

  /// <summary>Gate of an intrinsic function name; null when the intrinsic is available everywhere.</summary>
  public static LanguageFeature? IntrinsicGate(string name) => _intrinsicGates.TryGetValue(name, out var feature) ? feature : null;
}
