using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

/// <summary>
/// O16 interval lattice: a signed-integer value range [Lo, Hi]. <see cref="Top"/> is the full
/// 64-bit range (value unknown). Every operation OVER-approximates the true result set, so a
/// consumer that acts only when the whole interval qualifies (e.g. "fits int16", "excludes
/// zero") stays sound. All arithmetic returns <see cref="Top"/> on 64-bit overflow or when an
/// operand is unknown, so the lattice never reports a tighter range than the truth.
/// </summary>
public readonly record struct Interval(long Lo, long Hi) {
  public static readonly Interval Top = new(long.MinValue, long.MaxValue);
  public bool IsTop => this.Lo == long.MinValue && this.Hi == long.MaxValue;
  public bool IsEmpty => this.Lo > this.Hi;
  public bool Contains(long v) => v >= this.Lo && v <= this.Hi;
  public static Interval Of(long c) => new(c, c);

  /// <summary>The convex hull (union over-approximation) - the lattice join.</summary>
  public Interval Join(Interval o) => new(Math.Min(this.Lo, o.Lo), Math.Max(this.Hi, o.Hi));

  private static Interval Hull(params long[] xs) => new(xs.Min(), xs.Max());

  public Interval Add(Interval o) {
    if (this.IsTop || o.IsTop) return Top;
    try { return new(checked(this.Lo + o.Lo), checked(this.Hi + o.Hi)); }
    catch (OverflowException) { return Top; }
  }

  public Interval Subtract(Interval o) {
    if (this.IsTop || o.IsTop) return Top;
    try { return new(checked(this.Lo - o.Hi), checked(this.Hi - o.Lo)); }
    catch (OverflowException) { return Top; }
  }

  public Interval Negate() {
    if (this.IsTop) return Top;
    try { return new(checked(-this.Hi), checked(-this.Lo)); }
    catch (OverflowException) { return Top; }
  }

  public Interval Multiply(Interval o) {
    if (this.IsTop || o.IsTop) return Top;
    try {
      return Hull(checked(this.Lo * o.Lo), checked(this.Lo * o.Hi),
                  checked(this.Hi * o.Lo), checked(this.Hi * o.Hi));
    } catch (OverflowException) { return Top; }
  }

  /// <summary>Truncated integer divide (PB's <c>\</c>), monotonic in the dividend for a fixed
  /// non-zero divisor. Top when the divisor interval includes zero.</summary>
  public Interval Divide(Interval o) {
    if (this.IsTop || o.IsTop || o.Contains(0)) return Top;
    try {
      return Hull(checked(this.Lo / o.Lo), checked(this.Lo / o.Hi),
                  checked(this.Hi / o.Lo), checked(this.Hi / o.Hi));
    } catch (OverflowException) { return Top; }
  }

  /// <summary>PB's truncated MOD: |result| &lt; |k| and the result takes the dividend's sign, so
  /// it lies in [-(|k|-1), |k|-1], tightened to [0, |k|-1] when the dividend is provably &gt;= 0.
  /// Only a constant divisor is modelled.</summary>
  public Interval Modulo(Interval o) {
    if (o.Lo != o.Hi || o.Lo == 0) return Top;       // only a constant non-zero divisor
    var bound = Math.Abs(o.Lo) - 1;
    return this.Lo >= 0 ? new(0, bound) : new(-bound, bound);
  }

  /// <summary>Bitwise AND with a non-negative constant mask keeps only the mask's bits, so the
  /// result is in [0, m] for any left operand.</summary>
  public Interval And(Interval o) {
    if (o.Lo == o.Hi && o.Lo >= 0) return new(0, o.Lo);
    if (this.Lo == this.Hi && this.Lo >= 0) return new(0, this.Lo);
    return Top;
  }
}

/// <summary>
/// O16 forward interval propagation over a bound statement list. Computes a per-variable
/// <see cref="Interval"/> environment, the prerequisite for type narrowing (a LONG that provably
/// fits a narrower type) and for dropping checks the FOR-counter lattice cannot reach. This first
/// increment models straight-line scalar-integer assignment / INCR and IF-join; any other
/// statement (or a call-bearing value) is a conservative barrier that sets all tracked variables
/// to <see cref="Interval.Top"/>, so the result is always a sound over-approximation. Absence
/// from the environment means Top (unknown).
/// </summary>
public static class IntervalRangeAnalysis {
  /// <summary>The per-variable interval environment after executing <paramref name="body"/>.</summary>
  public static IReadOnlyDictionary<VariableSymbol, Interval> Analyze(IReadOnlyList<Statement> body, SemanticModel model) {
    var env = new Dictionary<VariableSymbol, Interval>(ReferenceEqualityComparer.Instance);
    Run(body, env, model, null);
    return env;
  }

  /// <summary>
  /// The interval environment at the ENTRY of each statement (keyed by statement reference,
  /// recursively into IF arms) - so a consumer can read a variable's proven range at a specific
  /// use site (e.g. to narrow a LONG operation or drop a check). A statement absent from the map
  /// was unreachable to the analysis; a variable absent from a statement's environment is Top.
  /// </summary>
  public static IReadOnlyDictionary<Statement, IReadOnlyDictionary<VariableSymbol, Interval>>
      AnalyzeProgramPoints(IReadOnlyList<Statement> body, SemanticModel model) {
    var points = new Dictionary<Statement, IReadOnlyDictionary<VariableSymbol, Interval>>(ReferenceEqualityComparer.Instance);
    var env = new Dictionary<VariableSymbol, Interval>(ReferenceEqualityComparer.Instance);
    Run(body, env, model, points);
    return points;
  }

  private static void Run(IReadOnlyList<Statement> body, Dictionary<VariableSymbol, Interval> env, SemanticModel model,
      Dictionary<Statement, IReadOnlyDictionary<VariableSymbol, Interval>>? points) {
    foreach (var s in body) {
      points?.TryAdd(s, Clone(env));                  // snapshot the entry environment
      Transfer(s, env, model, points);
    }
  }

  private static void Transfer(Statement s, Dictionary<VariableSymbol, Interval> env, SemanticModel model,
      Dictionary<Statement, IReadOnlyDictionary<VariableSymbol, Interval>>? points) {
    switch (s) {
      case AssignStmt { Target: NameExpr t, Value: { } v }
          when IntVar(t, model) is { } sym && CallFree(v, model):
        Set(env, sym, Eval(v, env, model));
        return;
      case IncrDecrStmt { Target: NameExpr t } id
          when IntVar(t, model) is { } sym && (id.Amount == null || CallFree(id.Amount, model)): {
        var cur = env.TryGetValue(sym, out var iv) ? iv : Interval.Top;
        var amount = id.Amount == null ? Interval.Of(1) : Eval(id.Amount, env, model);
        Set(env, sym, id.Increment ? cur.Add(amount) : cur.Subtract(amount));
        return;
      }
      case IfStmt iff when CallFree(iff.Condition, model) && iff.ElseIfs.All(e => CallFree(e.Condition, model)): {
        var results = new List<Dictionary<VariableSymbol, Interval>>();
        var thenEnv = Clone(env);
        Run(iff.Then, thenEnv, model, points);
        results.Add(thenEnv);
        foreach (var (_, b) in iff.ElseIfs) {
          var e = Clone(env);
          Run(b, e, model, points);
          results.Add(e);
        }
        var elseEnv = Clone(env);
        if (iff.Else != null)
          Run(iff.Else, elseEnv, model, points);
        results.Add(elseEnv);                          // Else, or (no Else) the not-taken fallthrough
        Replace(env, JoinAll(results));
        return;
      }
      // a call-free PRINT writes no scalar variable - keep the environment intact
      case PrintStmt p when (p.FileNumber == null || CallFree(p.FileNumber, model))
          && (p.UsingFormat == null || CallFree(p.UsingFormat, model))
          && p.Items.All(i => i.Value == null || CallFree(i.Value, model)):
        return;
      // statements that write no scalar variable - keep the environment intact
      case MetaStmt or EquateStmt or DefTypeStmt or DataStmt or EndStmt or LabelStmt:
        return;
      default:
        // an unmodelled statement may write tracked variables (a call by-ref, INPUT, a loop, ...)
        // - drop to the sound conservative fixpoint: everything unknown
        env.Clear();
        return;
    }
  }

  private static Interval Eval(Expression e, IReadOnlyDictionary<VariableSymbol, Interval> env, SemanticModel model) {
    switch (e) {
      case IntegerLiteralExpr lit:
        return Interval.Of(lit.Value);
      case NameExpr n when IntVar(n, model) is { } sym:
        return env.TryGetValue(sym, out var iv) ? iv : Interval.Top;
      case UnaryExpr { Op: UnaryOp.Negate, Operand: { } operand }:
        return Eval(operand, env, model).Negate();
      case BinaryExpr b:
        var l = Eval(b.Left, env, model);
        var r = Eval(b.Right, env, model);
        return b.Op switch {
          BinaryOp.Add => l.Add(r),
          BinaryOp.Subtract => l.Subtract(r),
          BinaryOp.Multiply => l.Multiply(r),
          BinaryOp.IntegerDivide => l.Divide(r),
          BinaryOp.Modulo => l.Modulo(r),
          BinaryOp.And => l.And(r),
          _ => Interval.Top,
        };
      default:
        return Interval.Top;
    }
  }

  /// <summary>The bound symbol when <paramref name="e"/> is a scalar non-float integer variable.</summary>
  private static VariableSymbol? IntVar(Expression e, SemanticModel model)
    => e is NameExpr && model.VariableBindings.TryGetValue(e, out var s)
       && s.Type is ScalarType { IsFloat: false }
       ? s : null;

  /// <summary>True when no user-procedure call appears in the tree (a call could write a tracked
  /// variable by-ref).</summary>
  private static bool CallFree(Expression e, SemanticModel model) => e switch {
    _ when model.CallBindings.ContainsKey(e) || model.ProcPtrCalls.ContainsKey(e) => false,
    UnaryExpr u => CallFree(u.Operand, model),
    BinaryExpr b => CallFree(b.Left, model) && CallFree(b.Right, model),
    CallOrIndexExpr c => c.Arguments.All(a => CallFree(a, model)),
    MemberExpr m => CallFree(m.Target, model),
    ByValArgExpr v => CallFree(v.Value, model),
    _ => true,
  };

  /// <summary>Store an interval, or drop the variable when the interval is Top (absence = Top).</summary>
  private static void Set(Dictionary<VariableSymbol, Interval> env, VariableSymbol sym, Interval iv) {
    if (iv.IsTop)
      env.Remove(sym);
    else
      env[sym] = iv;
  }

  private static Dictionary<VariableSymbol, Interval> Clone(Dictionary<VariableSymbol, Interval> env)
    => new(env, ReferenceEqualityComparer.Instance);

  private static void Replace(Dictionary<VariableSymbol, Interval> env, Dictionary<VariableSymbol, Interval> with) {
    env.Clear();
    foreach (var kv in with)
      env[kv.Key] = kv.Value;
  }

  /// <summary>Join environments: a variable known in every branch joins to the hull; a variable
  /// missing (Top) in any branch is unknown after the merge, so it is dropped.</summary>
  private static Dictionary<VariableSymbol, Interval> JoinAll(List<Dictionary<VariableSymbol, Interval>> envs) {
    var result = new Dictionary<VariableSymbol, Interval>(ReferenceEqualityComparer.Instance);
    if (envs.Count == 0)
      return result;
    foreach (var kv in envs[0]) {
      var joined = kv.Value;
      var inAll = true;
      for (var i = 1; i < envs.Count; ++i) {
        if (!envs[i].TryGetValue(kv.Key, out var other)) { inAll = false; break; }
        joined = joined.Join(other);
      }
      if (inAll && !joined.IsTop)
        result[kv.Key] = joined;
    }
    return result;
  }
}
