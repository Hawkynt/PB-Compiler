using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Semantics;

/// <summary>
/// Result of binding a compilation unit: every name resolved to a symbol, every
/// expression typed, all declarations collected. Codegen consumes this together
/// with the original AST (side-table design - nodes are keyed by reference).
/// </summary>
public sealed class SemanticModel {

  public required string FileName { get; init; }

  /// <summary>Dialect the unit was bound under; code generation gates quirk emulation on it.</summary>
  public Dialect Dialect { get; set; } = Dialect.Pb35;

  /// <summary>
  /// <c>$COMPAT &lt;dialect&gt;</c> override: the dialect whose numeric PRINT formatting the runtime
  /// should replicate, independent of the compile <see cref="Dialect"/>. Set by the back-emitter so a
  /// transpiled-to-pb35 program still prints floats the way its source dialect did (exponent style,
  /// significant digits, fixed/scientific threshold). Null = format like <see cref="Dialect"/>.
  /// </summary>
  public Dialect? FormatDialect { get; set; }

  /// <summary>Folded named constants (%equates).</summary>
  public Dictionary<string, ConstantValue> Equates { get; } = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>PB 3.6 ENUM members: bare name -> integer value (their own namespace, variable reads win).</summary>
  public Dictionary<string, long> EnumMembers { get; } = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>PB 3.6 ENUM members: bare name -> the enum's underlying integer type.</summary>
  public Dictionary<string, PbType> EnumMemberTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>PB 3.6 ENUM names -> the integer type they alias (so DIM c AS Color works).</summary>
  public Dictionary<string, PbType> EnumTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>Expression nodes the binder resolved to a compile-time integer (an ENUM member reference); codegen emits the literal.</summary>
  public Dictionary<Expression, long> ResolvedConstants { get; } = new(ReferenceEqualityComparer.Instance);

  /// <summary>TYPE/UNION definitions with resolved layout.</summary>
  public Dictionary<string, UdtType> Udts { get; } = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>pb36 nullable types (<c>T?</c>): maps a synthesized nullable UDT's name to its underlying value type. A UDT in this set carries a <c>Value</c> field and a <c>HasValue</c> presence flag.</summary>
  public Dictionary<string, PbType> NullableUnderlying { get; } = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>
  /// First (primary) SUB/FUNCTION of each name - kept for "does a proc named X exist /
  /// is it a function" lookups. With PB 3.6 overloading a name may have several
  /// definitions; <see cref="Overloads"/> holds them all and <see cref="ProcedureList"/>
  /// is the flat emission/analysis order.
  /// </summary>
  public Dictionary<string, ProcedureSymbol> Procedures { get; } = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>Every defined/DECLAREd procedure in source order, including all overloads (the iteration set for binding and codegen).</summary>
  public List<ProcedureSymbol> ProcedureList { get; } = [];

  /// <summary>All overloads of each name (PB 3.6); a non-overloaded name maps to a single-element list.</summary>
  public Dictionary<string, List<ProcedureSymbol>> Overloads { get; } = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>Module-level variables (globals, shared, statics of main).</summary>
  public Dictionary<string, VariableSymbol> ModuleVariables { get; } = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>Module-level executable statements (main program), declarations filtered out.</summary>
  public List<Statement> MainBody { get; } = [];

  /// <summary>Resolved type of every bound expression node.</summary>
  public Dictionary<Expression, PbType> ExpressionTypes { get; } = new(ReferenceEqualityComparer.Instance);

  /// <summary>Variable symbol behind every name/index/member expression that refers to storage.</summary>
  public Dictionary<Expression, VariableSymbol> VariableBindings { get; } = new(ReferenceEqualityComparer.Instance);

  /// <summary>Array symbol behind every REDIM'd variable declaration (the REDIM target has no expression node).</summary>
  public Dictionary<VariableDecl, VariableSymbol> RedimBindings { get; } = new(ReferenceEqualityComparer.Instance);

  /// <summary>
  /// PB 3.6 DIM-initializer lowering: the bound assignment(s) a DIM-with-initializer
  /// produces, in variable order. After binding, a splice pass inserts these right
  /// after their DIM so codegen and every optimizer pass see a real write.
  /// </summary>
  public Dictionary<DimStmt, List<AssignStmt>> DimInitializers { get; } = new(ReferenceEqualityComparer.Instance);

  /// <summary>PB 3.6 named arguments: a call site's arguments reordered to positional order (defaults filled); codegen and IPCP use this when present.</summary>
  public Dictionary<object, IReadOnlyList<Expression>> ReorderedArguments { get; } = new(ReferenceEqualityComparer.Instance);

  /// <summary>PB 3.6 from-end index (<c>arr(^n)</c>): the FromEndExpr node mapped to its bound rewrite <c>UBOUND(arr) - n + 1</c>, which codegen emits in its place.</summary>
  public Dictionary<Expression, Expression> RewrittenIndex { get; } = new(ReferenceEqualityComparer.Instance);

  /// <summary>PB 3.6 interpolated string (<c>$"..."</c>): the InterpolatedStringExpr node mapped to the bound concatenation it desugars to, which codegen emits in its place.</summary>
  public Dictionary<Expression, Expression> Desugared { get; } = new(ReferenceEqualityComparer.Instance);

  /// <summary>PB 3.6 statement-level desugar (member-call statement, property-set assignment): the surface statement mapped to the bound statement codegen emits in its place.</summary>
  public Dictionary<Statement, Statement> DesugaredStatements { get; } = new(ReferenceEqualityComparer.Instance);

  /// <summary>PB 3.6 inline lambdas: each LambdaExpr mapped to the anonymous proc it was lifted to; codegen emits the lambda value as that proc's code pointer.</summary>
  public Dictionary<Expression, ProcedureSymbol> LambdaProcs { get; } = new(ReferenceEqualityComparer.Instance);

  /// <summary>PB 3.6 typed procedure-pointer calls: a call through a FUNCTION/SUB-pointer variable, mapped to its signature (codegen coerces args to it and calls through the pointer).</summary>
  public Dictionary<Expression, ProcPtrType> ProcPtrCalls { get; } = new(ReferenceEqualityComparer.Instance);

  /// <summary>Procedure symbol behind every user call site (CallStmt / CallOrIndexExpr).</summary>
  public Dictionary<object, ProcedureSymbol> CallBindings { get; } = new(ReferenceEqualityComparer.Instance);

  /// <summary>Intrinsic behind every built-in call site.</summary>
  public Dictionary<Expression, IntrinsicInfo> IntrinsicBindings { get; } = new(ReferenceEqualityComparer.Instance);

  /// <summary>Label name behind CODEPTR/CODEPTR32 arguments that reference a label instead of a procedure.</summary>
  public Dictionary<Expression, string> LabelBindings { get; } = new(ReferenceEqualityComparer.Instance);

  /// <summary>Labels defined in module-level code and per procedure (scope key: "" = main).</summary>
  public Dictionary<string, HashSet<string>> Labels { get; } = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>Metastatements seen ($CPU, $STACK, $COMPILE, $LINK, ...) in source order.</summary>
  public List<MetaStmt> MetaStatements { get; } = [];

  public List<Diagnostic> Errors { get; } = [];
  public List<Diagnostic> Warnings { get; } = [];

  public bool Success => this.Errors.Count == 0;

  public PbType TypeOf(Expression e) => this.ExpressionTypes.TryGetValue(e, out var t) ? t : PbType.Integer;
}
