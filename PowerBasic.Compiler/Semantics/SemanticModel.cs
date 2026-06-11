using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Semantics;

/// <summary>
/// Result of binding a compilation unit: every name resolved to a symbol, every
/// expression typed, all declarations collected. Codegen consumes this together
/// with the original AST (side-table design - nodes are keyed by reference).
/// </summary>
public sealed class SemanticModel {

  public required string FileName { get; init; }

  /// <summary>Folded named constants (%equates).</summary>
  public Dictionary<string, ConstantValue> Equates { get; } = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>TYPE/UNION definitions with resolved layout.</summary>
  public Dictionary<string, UdtType> Udts { get; } = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>All SUBs/FUNCTIONs: defined, DECLAREd-external, and DEF FNs (named with their FN prefix).</summary>
  public Dictionary<string, ProcedureSymbol> Procedures { get; } = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>Module-level variables (globals, shared, statics of main).</summary>
  public Dictionary<string, VariableSymbol> ModuleVariables { get; } = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>Module-level executable statements (main program), declarations filtered out.</summary>
  public List<Statement> MainBody { get; } = [];

  /// <summary>Resolved type of every bound expression node.</summary>
  public Dictionary<Expression, PbType> ExpressionTypes { get; } = new(ReferenceEqualityComparer.Instance);

  /// <summary>Variable symbol behind every name/index/member expression that refers to storage.</summary>
  public Dictionary<Expression, VariableSymbol> VariableBindings { get; } = new(ReferenceEqualityComparer.Instance);

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
