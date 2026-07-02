namespace PowerBasic.Compiler.Syntax.Ast;

/// <summary>Base of all expression nodes.</summary>
public abstract record Expression(SourcePosition Position);

/// <summary>Integer literal, e.g. <c>42</c>, <c>&amp;H4F05</c>; suffix may force LONG.</summary>
public sealed record IntegerLiteralExpr(SourcePosition Position, long Value, TypeSuffix Suffix) : Expression(Position);

/// <summary>Floating-point literal; suffix may force SINGLE/DOUBLE/EXT.</summary>
public sealed record FloatLiteralExpr(SourcePosition Position, double Value, TypeSuffix Suffix) : Expression(Position);

/// <summary>String literal.</summary>
public sealed record StringLiteralExpr(SourcePosition Position, string Value) : Expression(Position);

/// <summary>Named-constant (equate) reference, e.g. <c>%SVGA_MODEX</c>.</summary>
public sealed record NamedConstantExpr(SourcePosition Position, string Name) : Expression(Position);

/// <summary>Bare identifier reference (variable, parameter, or parameterless function - resolved semantically).</summary>
public sealed record NameExpr(SourcePosition Position, string Name, TypeSuffix Suffix) : Expression(Position);

/// <summary>pb36 tuple literal <c>(e1, e2, ...)</c>: an anonymous value aggregate, used to build a tuple value or for parallel/destructuring assignment (a, b = (b, a)).</summary>
public sealed record TupleExpr(SourcePosition Position, IReadOnlyList<Expression> Elements) : Expression(Position);

/// <summary>
/// <c>name(arg, ...)</c> - array element, intrinsic or user FUNCTION call; the
/// distinction is semantic. Also used for pseudo-functions with empty args.
/// </summary>
public sealed record CallOrIndexExpr(SourcePosition Position, string Name, TypeSuffix Suffix, IReadOnlyList<Expression> Arguments, IReadOnlyList<TypeName>? TypeArguments = null) : Expression(Position);

/// <summary>UDT member access, e.g. <c>ctx.CurrentMode</c>; <see cref="Target"/> is a name, index or another member access.</summary>
public sealed record MemberExpr(SourcePosition Position, Expression Target, string Member, TypeSuffix Suffix) : Expression(Position);

/// <summary>
/// Indexing of a non-name target, e.g. the array-field access <c>ctx.NamedTimers(i)</c>
/// (plain <c>name(args)</c> stays <see cref="CallOrIndexExpr"/>).
/// </summary>
public sealed record IndexExpr(SourcePosition Position, Expression Target, IReadOnlyList<Expression> Arguments) : Expression(Position);

/// <summary>
/// Pointer dereference <c>@p</c> (PB 3.2) or indexed <c>@p[i]</c> (PB 3.5,
/// zero-based regardless of OPTION BASE); usable as lvalue and rvalue.
/// </summary>
public sealed record PtrDerefExpr(SourcePosition Position, Expression Pointer, Expression? Index) : Expression(Position);

/// <summary>Argument-position <c>BYVAL</c> override: passes the pointer target / forces by-value.</summary>
public sealed record ByValArgExpr(SourcePosition Position, Expression Value) : Expression(Position);

/// <summary>pb36 <c>NOTHING</c>: the empty value of a nullable type (clears its presence flag on assignment).</summary>
public sealed record NothingExpr(SourcePosition Position) : Expression(Position);

/// <summary>pb36 null-coalescing <c>value ?? fallback</c>: the nullable's value when present, else the fallback.</summary>
public sealed record CoalesceExpr(SourcePosition Position, Expression Value, Expression Fallback) : Expression(Position);

/// <summary>
/// pb36 null-conditional access on a nullable target: <c>target?.Member</c> (<see cref="Member"/> set)
/// or <c>target?[Index]</c> (<see cref="Index"/> set). Reads the member/element of the target's value
/// when it has one, else short-circuits to a fallback (the <c>??</c> default, or zero standalone).
/// </summary>
public sealed record NullConditionalExpr(SourcePosition Position, Expression Target, string? Member, Expression? Index) : Expression(Position);

public enum BinaryOp {
  Add, Subtract, Multiply, Divide, IntegerDivide, Modulo, Power,
  Equal, NotEqual, Less, Greater, LessEqual, GreaterEqual,
  And, Or, Xor, Eqv, Imp,
  /// <summary><c>&amp;</c> string concatenation (PB 3.5).</summary>
  Concat,
  // PB 3.6 shift/rotate operators (left operand's type sets the width)
  ShiftLeft, ShiftRightArith, ShiftRightLogical, RotateLeft, RotateRight,
  // PB 3.6 scaled pointer arithmetic: ptr +* i / ptr -* i scale i by the target size
  PointerAdd, PointerSub,
}

public sealed record BinaryExpr(SourcePosition Position, BinaryOp Op, Expression Left, Expression Right) : Expression(Position);

public enum UnaryOp { Negate, Not }

public sealed record UnaryExpr(SourcePosition Position, UnaryOp Op, Expression Operand) : Expression(Position);

/// <summary>File-number expression, e.g. <c>#1</c> in I/O statements.</summary>
public sealed record FileNumberExpr(SourcePosition Position, Expression Number) : Expression(Position);

/// <summary>
/// Argument-position <c>ANY</c> match-set prefix, e.g. <c>INSTR(s$, ANY "-/")</c> or
/// <c>EXTRACT$(s$, ANY set$)</c>: matches any single character of the set.
/// </summary>
public sealed record AnyMatchExpr(SourcePosition Position, Expression Value) : Expression(Position);

/// <summary>
/// PB 3.6 short-circuit ternary: <c>IF(condition, whenTrue, whenFalse)</c> - evaluates
/// only the selected branch at runtime (VB.NET-style <c>If</c> operator).
/// </summary>
public sealed record IfExpr(SourcePosition Position, Expression Condition, Expression WhenTrue, Expression WhenFalse) : Expression(Position);

/// <summary>
/// PB 3.6 object initializer: <c>NEW type { .field = value, ... }</c>. Valid only as a
/// DIM initializer; the binder lowers it to per-field assignments on the declared
/// variable (unlisted fields keep their zero-initialized value).
/// </summary>
public sealed record NewExpr(SourcePosition Position, string TypeName, IReadOnlyList<(string Field, Expression Value)> Fields) : Expression(Position);

/// <summary>PB 3.6 named call argument: <c>name := value</c>. The binder reorders these to positional order.</summary>
public sealed record NamedArgExpr(SourcePosition Position, string Name, Expression Value) : Expression(Position);

/// <summary>PB 3.6 from-end array index: <c>arr(^n)</c> = the n-th element from the end (^1 = last). Valid only as an array index.</summary>
public sealed record FromEndExpr(SourcePosition Position, Expression Index) : Expression(Position);

/// <summary>
/// PB 3.6 inline lambda: <c>FUNCTION(params) [AS type] =&gt; expr</c>, or the statement-bodied SUB form
/// <c>SUB(params) statement</c> (<see cref="StatementBody"/> set, <see cref="Body"/> unused). Lifted
/// to an anonymous top-level FUNCTION/SUB; the expression's value is its code pointer (callable
/// via <c>CALL DWORD</c> or directly through a delegate-typed variable).
/// </summary>
public sealed record LambdaExpr(SourcePosition Position, IReadOnlyList<Parameter> Parameters, TypeName? ReturnType, Expression Body) : Expression(Position) {
  /// <summary>The single statement a <c>SUB(params) statement</c> lambda executes; null for expression (FUNCTION) lambdas.</summary>
  public Statement? StatementBody { get; init; }
}

/// <summary>One element of a PB 3.6 collection literal: a single value, an inclusive integer range, or a spread of another array.</summary>
public abstract record CollectionElement(SourcePosition Position);
public sealed record ValueElement(SourcePosition Position, Expression Value) : CollectionElement(Position);
public sealed record RangeElement(SourcePosition Position, Expression Lo, Expression Hi) : CollectionElement(Position);
/// <summary>
/// Spread of another array: <c>..arr</c> (all elements) or the slice form <c>..arr(lo TO hi)</c> -
/// either bound may be omitted (defaults to the source's LBOUND/UBOUND) or be a from-end
/// <see cref="FromEndExpr"/> (<c>^1</c> = last), e.g. <c>..b(0 TO 2)</c>, <c>..b(TO ^5)</c>, <c>..c(^7 TO)</c>.
/// </summary>
public sealed record SpreadElement(SourcePosition Position, Expression Source) : CollectionElement(Position) {
  /// <summary>Slice lower bound (null = the source's LBOUND); may be a <see cref="FromEndExpr"/>.</summary>
  public Expression? SliceLo { get; init; }
  /// <summary>Slice upper bound (null = the source's UBOUND); may be a <see cref="FromEndExpr"/>.</summary>
  public Expression? SliceHi { get; init; }
  /// <summary>True when the spread was written with a slice (parens), even if both bounds were omitted.</summary>
  public bool IsSlice { get; init; }
}

/// <summary>
/// PB 3.6 array-initializer literal: <c>{ v1, v2, lo..hi, ..arr }</c>, used as a DIM
/// array initializer. The binder lowers it to per-element assignments.
/// </summary>
public sealed record ArrayLiteralExpr(SourcePosition Position, IReadOnlyList<CollectionElement> Elements) : Expression(Position);

/// <summary>
/// One part of a PB 3.6 interpolated string: a literal text run, or a <c>{expr[:fmt]}</c>
/// hole. Exactly one of <see cref="Literal"/> / <see cref="Hole"/> is set.
/// </summary>
public sealed record InterpolationPart(SourcePosition Position, string? Literal, Expression? Hole, string? Format);

/// <summary>
/// PB 3.6 interpolated string <c>$"text {expr} {expr:fmt}"</c>. The binder desugars it to
/// ordinary string concatenation: literal parts become string literals, a STRING hole
/// stays as-is, a numeric hole becomes <c>STR$(expr)</c>, and a <c>{expr:fmt}</c> hole
/// becomes <c>USING$(fmt, expr)</c> - so it reuses the existing concat / STR$ / PRINT USING
/// runtime with no new codegen.
/// </summary>
public sealed record InterpolatedStringExpr(SourcePosition Position, IReadOnlyList<InterpolationPart> Parts) : Expression(Position);
