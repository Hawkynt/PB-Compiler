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

/// <summary>
/// <c>name(arg, ...)</c> - array element, intrinsic or user FUNCTION call; the
/// distinction is semantic. Also used for pseudo-functions with empty args.
/// </summary>
public sealed record CallOrIndexExpr(SourcePosition Position, string Name, TypeSuffix Suffix, IReadOnlyList<Expression> Arguments) : Expression(Position);

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
