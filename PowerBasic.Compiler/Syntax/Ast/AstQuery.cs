namespace PowerBasic.Compiler.Syntax.Ast;

/// <summary>
/// AST query helpers shared across the binder and code generator.
/// </summary>
public static class AstQuery {

  /// <summary>
  /// The immediate child expressions of <paramref name="e"/> (empty for leaves).
  /// This is the single registration point that keeps the optimizer's conservative
  /// expression walkers sound: passes that analyze reads / escapes / purity fall
  /// back to this enumerator for any node they do not handle explicitly, so a newly
  /// added expression node's contained variables are still seen (treated as read /
  /// escaping / opaque). EVERY new <see cref="Expression"/> record that nests other
  /// expressions MUST be listed here, otherwise those passes would silently ignore
  /// its children and could miscompile.
  /// </summary>
  public static IReadOnlyList<Expression> Subexpressions(Expression e) => e switch {
    CallOrIndexExpr c => c.Arguments,
    MemberExpr m => [m.Target],
    IndexExpr i => [i.Target, .. i.Arguments],
    PtrDerefExpr p => p.Index == null ? [p.Pointer] : [p.Pointer, p.Index],
    ByValArgExpr v => [v.Value],
    BinaryExpr b => [b.Left, b.Right],
    UnaryExpr u => [u.Operand],
    FileNumberExpr f => [f.Number],
    AnyMatchExpr a => [a.Value],
    IfExpr t => [t.Condition, t.WhenTrue, t.WhenFalse],
    NewExpr n => [.. n.Fields.Select(f => f.Value)],
    NamedArgExpr na => [na.Value],
    // leaves with no child expressions: IntegerLiteralExpr, FloatLiteralExpr,
    // StringLiteralExpr, NamedConstantExpr, NameExpr.
    _ => [],
  };
}
