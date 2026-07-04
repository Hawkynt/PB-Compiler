using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Semantics;

/// <summary>A folded compile-time constant: integral, floating or string.</summary>
public readonly record struct ConstantValue(long? Integer, double? Float, string? Text) {
  public static ConstantValue Of(long value) => new(value, null, null);
  public static ConstantValue Of(double value) => new(null, value, null);
  public static ConstantValue Of(string value) => new(null, null, value);

  public bool IsNumeric => this.Text == null;
  public double AsFloat => this.Integer ?? this.Float ?? 0;
  public long AsInteger => this.Integer ?? (long)Math.Round(this.Float ?? 0, MidpointRounding.ToEven);
}

/// <summary>
/// Evaluates constant expressions at compile time: equate definitions, array
/// bounds, CASE ranges, DATA limits. Equates resolve through the supplied table.
/// </summary>
public sealed class ConstantFolder(IReadOnlyDictionary<string, ConstantValue> equates, IReadOnlyDictionary<string, long>? enumMembers = null, Func<Expression, ConstantValue?>? resolve = null) {

  private static readonly IReadOnlyDictionary<string, ConstantValue> _empty = new Dictionary<string, ConstantValue>();

  public ConstantFolder() : this(_empty) { }

  /// <summary>Folds <paramref name="expression"/>; null when it is not compile-time constant.</summary>
  public ConstantValue? TryFold(Expression expression) {
    switch (expression) {
      case IntegerLiteralExpr i:
        return ConstantValue.Of(i.Value);

      case FloatLiteralExpr f:
        return ConstantValue.Of(f.Value);

      case StringLiteralExpr s:
        return ConstantValue.Of(s.Value);

      case NamedConstantExpr c:
        return equates.TryGetValue(c.Name, out var known) ? known : null;

      case NameExpr n when enumMembers is { } e && e.TryGetValue(n.Name, out var enumValue):
        return ConstantValue.Of(enumValue); // a bare PB 3.6 ENUM member is a compile-time constant

      case UnaryExpr u: {
        if (this.TryFold(u.Operand) is not { } operand)
          return null;
        return u.Op switch {
          UnaryOp.Negate when operand.Integer is { } i => ConstantValue.Of(-i),
          UnaryOp.Negate when operand.Float is { } d => ConstantValue.Of(-d),
          UnaryOp.Not when operand.Integer is { } i => ConstantValue.Of(~i),
          _ => null,
        };
      }

      case BinaryExpr b:
        return this.FoldBinary(b);

      case IfExpr t:
        // PB 3.6 ternary with a compile-time-constant condition folds to the taken
        // branch only (the other branch is never evaluated - short-circuit preserved).
        if (this.TryFold(t.Condition) is not { Integer: { } cond })
          return null;
        return cond != 0 ? this.TryFold(t.WhenTrue) : this.TryFold(t.WhenFalse);

      default:
        // a caller-supplied resolver folds what the surface tree alone cannot
        // (e.g. bind-time-desugared reflection calls recorded in the semantic model)
        return resolve?.Invoke(expression);
    }
  }

  private ConstantValue? FoldBinary(BinaryExpr b) {
    if (this.TryFold(b.Left) is not { } left || this.TryFold(b.Right) is not { } right)
      return null;

    // string folding: concat and comparisons only
    if (left.Text is { } lt && right.Text is { } rt)
      return b.Op switch {
        BinaryOp.Add or BinaryOp.Concat => ConstantValue.Of(lt + rt),
        BinaryOp.Equal => Bool(string.CompareOrdinal(lt, rt) == 0),
        BinaryOp.NotEqual => Bool(string.CompareOrdinal(lt, rt) != 0),
        BinaryOp.Less => Bool(string.CompareOrdinal(lt, rt) < 0),
        BinaryOp.Greater => Bool(string.CompareOrdinal(lt, rt) > 0),
        BinaryOp.LessEqual => Bool(string.CompareOrdinal(lt, rt) <= 0),
        BinaryOp.GreaterEqual => Bool(string.CompareOrdinal(lt, rt) >= 0),
        _ => null,
      };

    if (!left.IsNumeric || !right.IsNumeric)
      return null;

    // integral stays integral except for / and ^
    if (left.Integer is { } li && right.Integer is { } ri)
      switch (b.Op) {
        case BinaryOp.Add: return ConstantValue.Of(li + ri);
        case BinaryOp.Subtract: return ConstantValue.Of(li - ri);
        case BinaryOp.Multiply: return ConstantValue.Of(li * ri);
        case BinaryOp.IntegerDivide: return ri == 0 ? null : ConstantValue.Of(li / ri);
        case BinaryOp.Modulo: return ri == 0 ? null : ConstantValue.Of(li % ri);
        case BinaryOp.And: return ConstantValue.Of(li & ri);
        case BinaryOp.Or: return ConstantValue.Of(li | ri);
        case BinaryOp.Xor: return ConstantValue.Of(li ^ ri);
        // shift-left is width/sign-independent in its low bits (the emitter wraps the
        // result to the bound type). Right shifts and rotates depend on the operand's
        // width and signedness, which this (type-less) folder does not know - they
        // stay runtime so the codegen does them at the correct width.
        case BinaryOp.ShiftLeft when ri is >= 0 and < 64: return ConstantValue.Of(li << (int)ri);
        case BinaryOp.Eqv: return ConstantValue.Of(~(li ^ ri));
        case BinaryOp.Imp: return ConstantValue.Of(~li | ri);
        case BinaryOp.Equal: return Bool(li == ri);
        case BinaryOp.NotEqual: return Bool(li != ri);
        case BinaryOp.Less: return Bool(li < ri);
        case BinaryOp.Greater: return Bool(li > ri);
        case BinaryOp.LessEqual: return Bool(li <= ri);
        case BinaryOp.GreaterEqual: return Bool(li >= ri);
      }

    var l = left.AsFloat;
    var r = right.AsFloat;
    return b.Op switch {
      BinaryOp.Add => ConstantValue.Of(l + r),
      BinaryOp.Subtract => ConstantValue.Of(l - r),
      BinaryOp.Multiply => ConstantValue.Of(l * r),
      BinaryOp.Divide => r == 0 ? null : ConstantValue.Of(l / r),
      BinaryOp.IntegerDivide => r == 0 ? null : ConstantValue.Of((long)l / (long)r),
      BinaryOp.Modulo => r == 0 ? null : ConstantValue.Of((long)l % (long)r),
      BinaryOp.Power => ConstantValue.Of(Math.Pow(l, r)),
      BinaryOp.Equal => Bool(l == r),
      BinaryOp.NotEqual => Bool(l != r),
      BinaryOp.Less => Bool(l < r),
      BinaryOp.Greater => Bool(l > r),
      BinaryOp.LessEqual => Bool(l <= r),
      BinaryOp.GreaterEqual => Bool(l >= r),
      _ => null,
    };
  }

  /// <summary>BASIC truth: TRUE is -1, FALSE is 0.</summary>
  private static ConstantValue Bool(bool value) => ConstantValue.Of(value ? -1L : 0L);
}
