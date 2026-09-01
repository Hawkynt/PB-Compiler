namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Bounded equality saturation for pure integer expression trees. Instead of committing to the first
/// matching rewrite, the pass explores the whole local equivalence class (up to a hard budget), then
/// extracts the expression with the fewest IR operations. Shared subexpressions are leaves, so the cost
/// model never assumes an instruction can disappear when another user still needs it.
/// </summary>
public static class EqualitySaturation {

  private const int _candidateBudget = 256;
  private const int _roundBudget = 8;

  /// <summary>Saturates eligible integer roots and replaces only roots with a strictly cheaper equivalent.</summary>
  public static int Run(IrFunction function) {
    ArgumentNullException.ThrowIfNull(function);
    var changed = 0;
    foreach (var root in function.AllInstructions.OfType<IrBinary>().ToArray()) {
      if (root.Parent is null || !Eligible(root))
        continue;
      var context = new Context(root);
      var initial = context.Import(root, isRoot: true);
      var best = Saturate(initial, context);
      if (Cost(best) >= Cost(initial) || best.Equals(initial))
        continue;

      var replacement = context.Materialize(best, root);
      root.ReplaceAllUsesWith(replacement);
      if (root.HasNoUsers)
        root.EraseFromParent();
      ++changed;
    }
    return changed;
  }

  private static Expr Saturate(Expr initial, Context context) {
    var seen = new HashSet<Expr> { initial };
    var frontier = new List<Expr> { initial };
    var best = initial;

    for (var round = 0; round < _roundBudget && frontier.Count > 0 && seen.Count < _candidateBudget; ++round) {
      var next = new List<Expr>();
      foreach (var expression in frontier) {
        foreach (var candidate in RewriteEverywhere(expression, context)) {
          if (seen.Count >= _candidateBudget)
            break;
          var normalized = Normalize(candidate, context);
          if (!seen.Add(normalized))
            continue;
          next.Add(normalized);
          if (Better(normalized, best, context))
            best = normalized;
        }
        if (seen.Count >= _candidateBudget)
          break;
      }
      frontier = next;
    }
    return best;
  }

  private static IEnumerable<Expr> RewriteEverywhere(Expr expression, Context context) {
    foreach (var rewritten in RewriteRoot(expression, context))
      yield return rewritten;

    if (expression is not Binary binary)
      yield break;
    foreach (var left in RewriteEverywhere(binary.Left, context))
      yield return binary with { Left = left };
    foreach (var right in RewriteEverywhere(binary.Right, context))
      yield return binary with { Right = right };
  }

  private static IEnumerable<Expr> RewriteRoot(Expr expression, Context context) {
    if (expression is not Binary b)
      yield break;

    var type = b.Type;
    if (b.Left is Constant lc && b.Right is Constant rc && TryEvaluate(b.Op, type, lc.Value, rc.Value, out var folded))
      yield return new Constant(type, folded);

    switch (b.Op) {
      case IrBinaryOp.Add:
        if (IsZero(b.Right)) yield return b.Left;
        if (IsZero(b.Left)) yield return b.Right;
        if (b.Left.Equals(b.Right)) yield return new Binary(IrBinaryOp.Shl, type, b.Left, new Constant(type, 1));
        if (b.Left is Binary { Op: IrBinaryOp.Sub } subLeft && subLeft.Right.Equals(b.Right)) yield return subLeft.Left;
        if (b.Right is Binary { Op: IrBinaryOp.Sub } subRight && subRight.Right.Equals(b.Left)) yield return subRight.Left;
        break;
      case IrBinaryOp.Sub:
        if (IsZero(b.Right)) yield return b.Left;
        if (b.Left.Equals(b.Right)) yield return new Constant(type, 0);
        if (b.Left is Binary { Op: IrBinaryOp.Add } addLeft) {
          if (addLeft.Left.Equals(b.Right)) yield return addLeft.Right;
          if (addLeft.Right.Equals(b.Right)) yield return addLeft.Left;
        }
        break;
      case IrBinaryOp.Mul:
        if (IsOne(b.Right)) yield return b.Left;
        if (IsOne(b.Left)) yield return b.Right;
        if (IsZero(b.Left) || IsZero(b.Right)) yield return new Constant(type, 0);
        break;
      case IrBinaryOp.And:
        if (IsZero(b.Left) || IsZero(b.Right)) yield return new Constant(type, 0);
        if (IsAllOnes(b.Right)) yield return b.Left;
        if (IsAllOnes(b.Left)) yield return b.Right;
        if (b.Left.Equals(b.Right)) yield return b.Left;
        if (Absorbs(b.Left, b.Right, IrBinaryOp.Or)) yield return b.Left;
        if (Absorbs(b.Right, b.Left, IrBinaryOp.Or)) yield return b.Right;
        // (a | b) & (a | c) -> a | (b & c)
        if (TryFactor(b, IrBinaryOp.Or, context) is { } factoredAnd) yield return factoredAnd;
        break;
      case IrBinaryOp.Or:
        if (IsZero(b.Right)) yield return b.Left;
        if (IsZero(b.Left)) yield return b.Right;
        if (IsAllOnes(b.Left) || IsAllOnes(b.Right)) yield return new Constant(type, IrConstFold.Wrap(-1, type));
        if (b.Left.Equals(b.Right)) yield return b.Left;
        if (Absorbs(b.Left, b.Right, IrBinaryOp.And)) yield return b.Left;
        if (Absorbs(b.Right, b.Left, IrBinaryOp.And)) yield return b.Right;
        // (a & b) | (a & c) -> a & (b | c)
        if (TryFactor(b, IrBinaryOp.And, context) is { } factoredOr) yield return factoredOr;
        break;
      case IrBinaryOp.Xor:
        if (IsZero(b.Right)) yield return b.Left;
        if (IsZero(b.Left)) yield return b.Right;
        if (b.Left.Equals(b.Right)) yield return new Constant(type, 0);
        if (b.Left is Binary { Op: IrBinaryOp.Xor } xorLeft) {
          if (xorLeft.Left.Equals(b.Right)) yield return xorLeft.Right;
          if (xorLeft.Right.Equals(b.Right)) yield return xorLeft.Left;
        }
        if (b.Right is Binary { Op: IrBinaryOp.Xor } xorRight) {
          if (xorRight.Left.Equals(b.Left)) yield return xorRight.Right;
          if (xorRight.Right.Equals(b.Left)) yield return xorRight.Left;
        }
        break;
      case IrBinaryOp.Shl or IrBinaryOp.LShr or IrBinaryOp.AShr:
        if (IsZero(b.Right)) yield return b.Left;
        break;
    }

    if (IsAssociativeCommutative(b.Op)) {
      // Both associativity directions are admitted. The bounded equality class keeps this from turning
      // a long chain into an unbounded rewrite loop, while extraction chooses the cheapest endpoint.
      if (b.Left is Binary left && left.Op == b.Op)
        yield return new Binary(b.Op, type, left.Left, new Binary(b.Op, type, left.Right, b.Right));
      if (b.Right is Binary right && right.Op == b.Op)
        yield return new Binary(b.Op, type, new Binary(b.Op, type, b.Left, right.Left), right.Right);
    }
  }

  private static Expr? TryFactor(Binary outer, IrBinaryOp innerOp, Context context) {
    if (outer.Left is not Binary left || outer.Right is not Binary right || left.Op != innerOp || right.Op != innerOp)
      return null;
    foreach (var (common, lRest) in CommutativePairs(left))
      foreach (var (otherCommon, rRest) in CommutativePairs(right))
        if (common.Equals(otherCommon))
          return Normalize(new Binary(innerOp, outer.Type, common,
            new Binary(outer.Op, outer.Type, lRest, rRest)), context);
    return null;
  }

  private static IEnumerable<(Expr First, Expr Second)> CommutativePairs(Binary expression) {
    yield return (expression.Left, expression.Right);
    if (!expression.Left.Equals(expression.Right))
      yield return (expression.Right, expression.Left);
  }

  private static bool Absorbs(Expr value, Expr other, IrBinaryOp nested)
    => other is Binary binary && binary.Op == nested && (binary.Left.Equals(value) || binary.Right.Equals(value));

  private static Expr Normalize(Expr expression, Context context) {
    if (expression is not Binary binary)
      return expression;
    var left = Normalize(binary.Left, context);
    var right = Normalize(binary.Right, context);
    if (IsCommutative(binary.Op) && StringComparer.Ordinal.Compare(context.Key(left), context.Key(right)) > 0)
      (left, right) = (right, left);
    return binary with { Left = left, Right = right };
  }

  private static bool Better(Expr candidate, Expr current, Context context) {
    var candidateCost = Cost(candidate);
    var currentCost = Cost(current);
    return candidateCost < currentCost
      || candidateCost == currentCost && StringComparer.Ordinal.Compare(context.Key(candidate), context.Key(current)) < 0;
  }

  private static int Cost(Expr expression) => expression switch {
    Binary binary => 1 + Cost(binary.Left) + Cost(binary.Right),
    _ => 0,
  };

  private static bool Eligible(IrBinary binary)
    => binary.Type.IsInteger && binary.Op is IrBinaryOp.Add or IrBinaryOp.Sub or IrBinaryOp.Mul
      or IrBinaryOp.And or IrBinaryOp.Or or IrBinaryOp.Xor
      or IrBinaryOp.Shl or IrBinaryOp.LShr or IrBinaryOp.AShr;

  private static bool IsAssociativeCommutative(IrBinaryOp op)
    => op is IrBinaryOp.Add or IrBinaryOp.Mul or IrBinaryOp.And or IrBinaryOp.Or or IrBinaryOp.Xor;

  private static bool IsCommutative(IrBinaryOp op) => IsAssociativeCommutative(op);
  private static bool IsZero(Expr expression) => expression is Constant { Value: 0 };
  private static bool IsOne(Expr expression) => expression is Constant { Value: 1 };
  private static bool IsAllOnes(Expr expression) => expression is Constant c && c.Value == IrConstFold.Wrap(-1, c.Type);

  private static bool TryEvaluate(IrBinaryOp op, IrType type, long left, long right, out long result) {
    result = 0;
    switch (op) {
      case IrBinaryOp.Add: result = unchecked(left + right); break;
      case IrBinaryOp.Sub: result = unchecked(left - right); break;
      case IrBinaryOp.Mul: result = unchecked(left * right); break;
      case IrBinaryOp.And: result = left & right; break;
      case IrBinaryOp.Or: result = left | right; break;
      case IrBinaryOp.Xor: result = left ^ right; break;
      case IrBinaryOp.Shl when right >= 0 && right < type.Bits: result = left << (int)right; break;
      case IrBinaryOp.LShr when right >= 0 && right < type.Bits:
        result = (long)(ZeroExtended(left, type.Bits) >> (int)right);
        break;
      case IrBinaryOp.AShr when right >= 0 && right < type.Bits:
        result = IrConstFold.Wrap(left, type) >> (int)right;
        break;
      default:
        return false;
    }
    result = IrConstFold.Wrap(result, type);
    return true;
  }

  private static ulong ZeroExtended(long value, int bits)
    => bits >= 64 ? unchecked((ulong)value) : unchecked((ulong)value) & ((1UL << bits) - 1);

  private abstract record Expr(IrType Type);
  private sealed record Leaf(IrType Type, int Id, IrValue Value) : Expr(Type);
  private sealed record Constant(IrType Type, long Value) : Expr(Type);
  private sealed record Binary(IrBinaryOp Op, IrType Type, Expr Left, Expr Right) : Expr(Type);

  private sealed class Context(IrBinary root) {
    private readonly Dictionary<IrValue, Leaf> _leaves = new(ReferenceEqualityComparer.Instance);
    private int _nextLeaf;

    public Expr Import(IrValue value, bool isRoot = false) {
      if (value is IrConstantInt constant)
        return new Constant(constant.Type, IrConstFold.Wrap(constant.Value, constant.Type));
      if (value is IrBinary binary && Eligible(binary) && (isRoot || binary.Users.Count == 1))
        return Normalize(new Binary(binary.Op, binary.Type, this.Import(binary.Lhs), this.Import(binary.Rhs)), this);
      if (!this._leaves.TryGetValue(value, out var leaf))
        this._leaves[value] = leaf = new Leaf(value.Type, this._nextLeaf++, value);
      return leaf;
    }

    public string Key(Expr expression) => expression switch {
      Leaf leaf => $"L{leaf.Id:D4}",
      Constant constant => $"C{constant.Type}:{constant.Value}",
      Binary binary => $"B{(int)binary.Op:D2}({this.Key(binary.Left)},{this.Key(binary.Right)})",
      _ => string.Empty,
    };

    public IrValue Materialize(Expr expression, IrInstruction before) => expression switch {
      Leaf leaf => leaf.Value,
      Constant constant => new IrConstantInt(constant.Type, constant.Value),
      Binary binary => this.Insert(binary, before),
      _ => throw new InvalidOperationException("unknown equality-saturation expression"),
    };

    private IrValue Insert(Binary binary, IrInstruction before) {
      var left = this.Materialize(binary.Left, before);
      var right = this.Materialize(binary.Right, before);
      return before.Parent!.InsertBefore(new IrBinary(binary.Op, left, right), before);
    }
  }
}
