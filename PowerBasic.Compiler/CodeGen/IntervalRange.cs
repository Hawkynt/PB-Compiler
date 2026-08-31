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
/// What the analysis knows about one integer value: a range, known one/zero bits, and an affine
/// congruence. No one domain subsumes the others, so <see cref="ValueFactReduction"/> treats them as
/// a reduced product: facts discovered in any domain are fed back into the other two until stable.
/// </summary>
public readonly record struct ValueFacts(Interval Range, KnownBits Bits, Congruence Mod) {
  public static readonly ValueFacts Unknown = new(Interval.Top, KnownBits.Unknown, Congruence.Unknown);
  public bool IsUnknown => this.Range.IsTop && this.Bits.IsUnknown && this.Mod.IsUnknown;
  public static ValueFacts Of(long c, int width) => new(Interval.Of(c), KnownBits.Of(c, width), Congruence.Of(c));

  /// <summary>The lattice join: each domain merges on its own terms.</summary>
  public ValueFacts Join(ValueFacts o) => new(this.Range.Join(o.Range), this.Bits.Join(o.Bits), this.Mod.Join(o.Mod));

  /// <summary>
  /// True when <paramref name="candidate"/> is consistent with everything known. A false answer
  /// proves the value can never be that one, whichever domain saw it first - which is the point
  /// of keeping three: <c>x MOD 2</c> is excluded by the range, <c>(x \ 2) * 2 = 1</c> by the
  /// bits, and <c>x * 10 = 25</c> only by the congruence.
  /// </summary>
  public bool Allows(long candidate, int width) =>
    this.Range.Contains(candidate) && this.Bits.Allows(candidate, width) && this.Mod.Allows(candidate);
}

/// <summary>
/// O16 forward value propagation over a bound statement list. Every tracked integer carries a
/// reduced product of interval, known-bit and congruence facts at each program point. It is the
/// prerequisite for type narrowing, bounds/check elimination, comparison folding and target-specific
/// instruction selection.
///
/// A statement that is not modelled invalidates only what it can actually write: a call reaches
/// module-level data, this frame's parameters and whatever the statement names, but not the
/// procedure's private locals - unless the body takes an address, stores through a pointer,
/// POKEs, runs inline assembly or captures the frame in a lambda / nested procedure, in which
/// case everything is dropped. Anything else unmodelled, and every label in a body that contains
/// a jump, resets the whole environment. Every rule is an over-approximation; absence from the
/// environment remains the conventional representation of completely unknown facts.
/// </summary>
public static class IntervalRangeAnalysis {
  /// <summary>The per-variable value-fact environment after executing <paramref name="body"/>.</summary>
  public static IReadOnlyDictionary<VariableSymbol, ValueFacts> Analyze(IReadOnlyList<Statement> body, SemanticModel model) {
    var env = new Dictionary<VariableSymbol, ValueFacts>(ReferenceEqualityComparer.Instance);
    Run(body, env, ScopeOf(body, model), null);
    return env;
  }

  /// <summary>
  /// The analysis context: the bound model plus whether this body contains anything that can
  /// write memory the analysis cannot name - an address-taking intrinsic (VARPTR/STRPTR/...),
  /// a pointer store, POKE or inline assembly. When it does, a call may reach even a private
  /// local, so calls invalidate everything; when it does not (the overwhelmingly common case),
  /// a call can only touch module-level data, parameters and its own arguments.
  /// </summary>
  private readonly record struct Scope(SemanticModel Model, bool Escapes, bool Jumps);

  /// <summary>Intrinsics that hand out the address of a variable, after which any call may write it.</summary>
  private static readonly HashSet<string> _addressIntrinsics = new(StringComparer.OrdinalIgnoreCase) {
    "VARPTR", "VARPTR32", "VARSEG", "STRPTR", "STRPTR32", "CODEPTR", "CODEPTR32",
  };

  /// <summary>
  /// Scans <paramref name="body"/> once for the two facts the kill-set reasoning needs: whether an
  /// address escapes (an address-taking intrinsic, a pointer store, POKE, inline assembly, or a
  /// lambda / nested procedure capturing this frame) and whether control can jump to a label
  /// (GOTO/GOSUB/ON..GOTO/RESUME/ON ERROR). Uses the reflective node walk, so it is complete by
  /// construction: a newly added AST node cannot silently introduce either hazard.
  /// </summary>
  private static Scope ScopeOf(IReadOnlyList<Statement> body, SemanticModel model) {
    var escapes = false;
    var jumps = false;
    foreach (var node in OptReachability.DescendantNodes(body)) {
      switch (node) {
        case InlineAsmStmt:
        case PtrDerefExpr:
        case CommandStmt { Keyword: "POKE" or "POKE$" or "PEEK" or "PEEK$" }:
        case LambdaExpr or SubDecl or FunctionDecl:
          escapes = true;
          break;
        case CallOrIndexExpr c when _addressIntrinsics.Contains(c.Name):
          escapes = true;
          break;
        case GotoStmt or GosubStmt or OnGotoStmt or OnErrorStmt or ResumeStmt:
          jumps = true;
          break;
      }
      if (escapes && jumps)
        break;
    }
    return new(model, escapes, jumps);
  }

  /// <summary>
  /// The value-fact environment at the ENTRY of each statement (keyed by statement reference,
  /// recursively into IF arms). A statement absent from the map was unreachable to the analysis;
  /// a variable absent from a statement's environment is completely unknown.
  /// </summary>
  public static IReadOnlyDictionary<Statement, IReadOnlyDictionary<VariableSymbol, ValueFacts>>
      AnalyzeProgramPoints(IReadOnlyList<Statement> body, SemanticModel model) {
    var points = new Dictionary<Statement, IReadOnlyDictionary<VariableSymbol, ValueFacts>>(ReferenceEqualityComparer.Instance);
    var env = new Dictionary<VariableSymbol, ValueFacts>(ReferenceEqualityComparer.Instance);
    Run(body, env, ScopeOf(body, model), points);
    return points;
  }

  /// <summary>
  /// Evaluates an arbitrary integer expression in an already-computed program-point environment.
  /// This is the single query the emitter uses when a target instruction can exploit value shape.
  /// </summary>
  public static ValueFacts Evaluate(Expression expression, IReadOnlyDictionary<VariableSymbol, ValueFacts> environment, SemanticModel model)
    => Eval(expression, environment, model);

  private static void Run(IReadOnlyList<Statement> body, Dictionary<VariableSymbol, ValueFacts> env, Scope scope,
      Dictionary<Statement, IReadOnlyDictionary<VariableSymbol, ValueFacts>>? points) {
    foreach (var s in body) {
      if (points != null && !IsLoop(s))
        points.TryAdd(s, Clone(env));
      Transfer(s, env, scope, points);
    }
  }

  /// <summary>
  /// A statement whose recorded program point is NOT its entry environment, because the emitter reads
  /// that point while emitting something the loop re-executes - the pre/post test, which runs again on
  /// every back edge with loop-carried values. <see cref="TransferLoop"/> is the sole author of a
  /// loop's point and writes the widened invariant there. A loop the analysis refuses has no point,
  /// which is the honest Top answer rather than its first-iteration pre-loop state.
  /// </summary>
  private static bool IsLoop(Statement s) => s is ForStmt or DoLoopStmt;

  private static void Transfer(Statement s, Dictionary<VariableSymbol, ValueFacts> env, Scope scope,
      Dictionary<Statement, IReadOnlyDictionary<VariableSymbol, ValueFacts>>? points) {
    var model = scope.Model;
    switch (s) {
      case AssignStmt { Target: NameExpr t, Value: { } v }
          when IntVar(t, model) is { } sym && CallFree(v, model):
        Set(env, sym, StoreInto(Eval(v, env, model), sym.Type));
        return;
      case IncrDecrStmt { Target: NameExpr t } id
          when IntVar(t, model) is { } sym && (id.Amount == null || CallFree(id.Amount, model)): {
        var cur = env.TryGetValue(sym, out var iv) ? iv : ValueFacts.Unknown;
        var amount = id.Amount == null ? ValueFacts.Of(1, WidthOf(sym.Type)) : Eval(id.Amount, env, model);
        var width = WidthOf(sym.Type);
        var stepped = new ValueFacts(
          id.Increment ? cur.Range.Add(amount.Range) : cur.Range.Subtract(amount.Range),
          ValueFactReduction.AddSub(cur.Bits, amount.Bits, width, subtract: !id.Increment),
          id.Increment ? cur.Mod.Add(amount.Mod) : cur.Mod.Subtract(amount.Mod));
        Set(env, sym, StoreInto(stepped, sym.Type));
        return;
      }
      case IfStmt iff when CallFree(iff.Condition, model) && iff.ElseIfs.All(e => CallFree(e.Condition, model)): {
        var results = new List<Dictionary<VariableSymbol, ValueFacts>>();
        var thenEnv = Clone(env);
        RefineForCondition(thenEnv, iff.Condition, whenTrue: true, model);
        Run(iff.Then, thenEnv, scope, points);
        results.Add(thenEnv);
        foreach (var (cond, b) in iff.ElseIfs) {
          var e = Clone(env);
          RefineForCondition(e, cond, whenTrue: true, model);
          Run(b, e, scope, points);
          results.Add(e);
        }
        var elseEnv = Clone(env);
        RefineForCondition(elseEnv, iff.Condition, whenTrue: false, model);
        if (iff.Else != null)
          Run(iff.Else, elseEnv, scope, points);
        results.Add(elseEnv);
        Replace(env, JoinAll(results));
        return;
      }
      case ForStmt f when IntVar(f.Variable, model) is { } ctr
          && CallFree(f.From, model) && CallFree(f.To, model) && BodyCallFree(f.Body, model): {
        var range = Eval(f.From, env, model).Range.Join(Eval(f.To, env, model).Range);
        TransferLoop(f, f.Body, ctr, range, env, scope, points);
        return;
      }
      case DoLoopStmt d when (d.PreCondition == null || CallFree(d.PreCondition, model))
          && (d.PostCondition == null || CallFree(d.PostCondition, model)) && BodyCallFree(d.Body, model):
        TransferLoop(d, d.Body, null, Interval.Top, env, scope, points);
        return;
      case SelectStmt sel when CallFree(sel.Subject, model)
          && sel.Arms.All(a => a.Selectors.All(sl => SelectorCallFree(sl, model))): {
        var subject = IntVar(sel.Subject, model);
        var results = new List<Dictionary<VariableSymbol, ValueFacts>>();
        foreach (var arm in sel.Arms) {
          var armEnv = Clone(env);
          if (subject != null && arm.Selectors.Count > 0)
            RefineForSelectors(armEnv, subject, arm.Selectors, model);
          Run(arm.Body, armEnv, scope, points);
          results.Add(armEnv);
        }
        if (!sel.Arms.Any(a => a.Selectors.Count == 0))
          results.Add(Clone(env));
        Replace(env, JoinAll(results));
        return;
      }
      case PrintStmt p when (p.FileNumber == null || CallFree(p.FileNumber, model))
          && (p.UsingFormat == null || CallFree(p.UsingFormat, model))
          && p.Items.All(i => i.Value == null || CallFree(i.Value, model)):
        return;
      case LabelStmt:
        if (scope.Jumps)
          env.Clear();
        return;
      case MetaStmt or EquateStmt or DefTypeStmt or DataStmt or EndStmt:
        return;
      case CallStmt or AssignStmt or IncrDecrStmt or PrintStmt when !scope.Escapes:
        KillReachableByCall(s, env, model);
        return;
      default:
        env.Clear();
        return;
    }
  }

  private static void KillReachableByCall(Statement s, Dictionary<VariableSymbol, ValueFacts> env, SemanticModel model) {
    foreach (var v in env.Keys.ToList())
      if (v.Storage != VariableStorage.Local || v.IsShared)
        env.Remove(v);
    foreach (var node in OptReachability.DescendantNodes(s))
      if (node is Expression e && model.VariableBindings.TryGetValue(e, out var named))
        env.Remove(named);
  }

  private static bool SelectorCallFree(CaseSelector selector, SemanticModel model)
    => (selector.Value == null || CallFree(selector.Value, model))
       && (selector.RangeUpper == null || CallFree(selector.RangeUpper, model));

  private static void RefineForSelectors(Dictionary<VariableSymbol, ValueFacts> env, VariableSymbol subject,
      IReadOnlyList<CaseSelector> selectors, SemanticModel model) {
    Interval? admitted = null;
    foreach (var selector in selectors) {
      var one = SelectorRange(selector, env, model);
      if (one is not { } iv)
        return;
      admitted = admitted is { } sofar ? sofar.Join(iv) : iv;
    }
    if (admitted is not { } range)
      return;
    var current = env.TryGetValue(subject, out var known) ? known.Range : TypeRange(subject.Type);
    var refined = new Interval(Math.Max(current.Lo, range.Lo), Math.Min(current.Hi, range.Hi));
    if (!refined.IsEmpty)
      SetRange(env, subject, refined);
  }

  private static Interval? SelectorRange(CaseSelector selector, IReadOnlyDictionary<VariableSymbol, ValueFacts> env, SemanticModel model) {
    if (selector.Value is not { } value)
      return null;
    var low = Eval(value, env, model).Range;
    if (low.IsTop)
      return null;
    if (selector.RangeUpper is { } upper) {
      var high = Eval(upper, env, model).Range;
      return high.IsTop ? null : new Interval(low.Lo, high.Hi);
    }
    return selector.IsComparison switch {
      null or CaseComparison.Equal => low,
      CaseComparison.Less => new Interval(long.MinValue, low.Hi - 1),
      CaseComparison.LessEqual => new Interval(long.MinValue, low.Hi),
      CaseComparison.Greater => new Interval(low.Lo + 1, long.MaxValue),
      CaseComparison.GreaterEqual => new Interval(low.Lo, long.MaxValue),
      _ => null,
    };
  }

  private static bool IsPowerOfTwo(long m) => m > 0 && (m & (m - 1)) == 0;

  private static int WidthOf(Expression e, SemanticModel model) => WidthOf(model.TypeOf(e));
  private static int WidthOf(PbType type) => type is ScalarType { IsFloat: false, ByteSize: var n } ? n * 8 : 0;
  private static bool SignedOf(Expression e, SemanticModel model) => model.TypeOf(e) is not ScalarType { IsFloat: false, Signed: false };

  private static ValueFacts Eval(Expression e, IReadOnlyDictionary<VariableSymbol, ValueFacts> env, SemanticModel model) {
    switch (e) {
      case IntegerLiteralExpr lit:
        return ValueFacts.Of(lit.Value, WidthOf(lit, model));
      case NameExpr n when IntVar(n, model) is { } sym:
        return env.TryGetValue(sym, out var iv) ? iv : ValueFacts.Unknown;
      case UnaryExpr { Op: UnaryOp.Negate, Operand: { } operand }: {
        var inner = Eval(operand, env, model);
        var width = WidthOf(e, model);
        return width > 0
          ? ValueFactReduction.Negate(inner, width, SignedOf(e, model))
          : new(inner.Range.Negate(), KnownBits.Unknown, inner.Mod.Negate());
      }
      case UnaryExpr { Op: UnaryOp.Not, Operand: { } notOperand }: {
        var inner = Eval(notOperand, env, model);
        var width = WidthOf(e, model);
        return width > 0
          ? ValueFactReduction.Not(inner, width, SignedOf(e, model))
          : ValueFacts.Unknown;
      }
      case BinaryExpr b: {
        var l = Eval(b.Left, env, model);
        var r = Eval(b.Right, env, model);
        var width = WidthOf(b, model);
        if (width > 0)
          return ValueFactReduction.Binary(b.Op, l, r, width, SignedOf(b, model));

        // PB-lineage + - * may still be float-promoted here. Keep the established mathematical
        // range and residue transfer; there is no integer bit-pattern until a later integral store.
        var range = b.Op switch {
          BinaryOp.Add => l.Range.Add(r.Range),
          BinaryOp.Subtract => l.Range.Subtract(r.Range),
          BinaryOp.Multiply => l.Range.Multiply(r.Range),
          BinaryOp.IntegerDivide => l.Range.Divide(r.Range),
          BinaryOp.Modulo => l.Range.Modulo(r.Range),
          BinaryOp.And => l.Range.And(r.Range),
          _ => Interval.Top,
        };
        var bits = b.Op switch {
          BinaryOp.And => l.Bits.And(r.Bits),
          BinaryOp.Or => l.Bits.Or(r.Bits),
          BinaryOp.Xor => l.Bits.Xor(r.Bits),
          BinaryOp.Add => l.Bits.AddSub(r.Bits, subtract: false),
          BinaryOp.Subtract => l.Bits.AddSub(r.Bits, subtract: true),
          BinaryOp.Multiply => l.Bits.Multiply(r.Bits, width),
          _ => KnownBits.Unknown,
        };
        var mod = b.Op switch {
          BinaryOp.Add => l.Mod.Add(r.Mod),
          BinaryOp.Subtract => l.Mod.Subtract(r.Mod),
          BinaryOp.Multiply => l.Mod.Multiply(r.Mod),
          _ => Congruence.Unknown,
        };
        var fitted = FitOrTop(range, model.TypeOf(b));
        if (fitted.IsTop && !IsPowerOfTwo(mod.Modulus))
          mod = Congruence.Unknown;
        return new(fitted, bits.Narrow(width), mod);
      }
      default:
        return ValueFacts.Unknown;
    }
  }

  private static void RefineForCondition(Dictionary<VariableSymbol, ValueFacts> env, Expression cond,
      bool whenTrue, SemanticModel model) {
    switch (cond) {
      case BinaryExpr { Op: BinaryOp.And } and2 when whenTrue && IsTruthValued(and2.Left) && IsTruthValued(and2.Right):
        RefineForCondition(env, and2.Left, whenTrue: true, model);
        RefineForCondition(env, and2.Right, whenTrue: true, model);
        return;
      case BinaryExpr { Op: BinaryOp.Or } or2 when !whenTrue && IsTruthValued(or2.Left) && IsTruthValued(or2.Right):
        RefineForCondition(env, or2.Left, whenTrue: false, model);
        RefineForCondition(env, or2.Right, whenTrue: false, model);
        return;
      case UnaryExpr { Op: UnaryOp.Not, Operand: { } negated } when IsTruthValued(negated):
        RefineForCondition(env, negated, !whenTrue, model);
        return;
    }

    if (cond is not BinaryExpr b)
      return;
    VariableSymbol? v;
    long c;
    BinaryOp op;
    if (IntVar(b.Left, model) is { } vl && b.Right is IntegerLiteralExpr rl) { v = vl; c = rl.Value; op = b.Op; }
    else if (IntVar(b.Right, model) is { } vr && b.Left is IntegerLiteralExpr ll) { v = vr; c = ll.Value; op = SwapCompare(b.Op); }
    else return;
    if (!whenTrue)
      op = NegateCompare(op);
    var cur = env.TryGetValue(v, out var iv) ? iv.Range : TypeRange(v.Type);
    Interval? refined = op switch {
      BinaryOp.Less => new Interval(cur.Lo, Math.Min(cur.Hi, c - 1)),
      BinaryOp.LessEqual => new Interval(cur.Lo, Math.Min(cur.Hi, c)),
      BinaryOp.Greater => new Interval(Math.Max(cur.Lo, c + 1), cur.Hi),
      BinaryOp.GreaterEqual => new Interval(Math.Max(cur.Lo, c), cur.Hi),
      BinaryOp.Equal => new Interval(Math.Max(cur.Lo, c), Math.Min(cur.Hi, c)),
      _ => null,
    };
    if (refined is { IsEmpty: false } narrowed)
      SetRange(env, v, narrowed);
  }

  private static bool IsTruthValued(Expression e) => e switch {
    BinaryExpr { Op: BinaryOp.Equal or BinaryOp.NotEqual or BinaryOp.Less or BinaryOp.Greater
      or BinaryOp.LessEqual or BinaryOp.GreaterEqual } => true,
    BinaryExpr { Op: BinaryOp.And or BinaryOp.Or or BinaryOp.Xor } b => IsTruthValued(b.Left) && IsTruthValued(b.Right),
    UnaryExpr { Op: UnaryOp.Not } u => IsTruthValued(u.Operand),
    _ => false,
  };

  private static BinaryOp SwapCompare(BinaryOp op) => op switch {
    BinaryOp.Less => BinaryOp.Greater,
    BinaryOp.Greater => BinaryOp.Less,
    BinaryOp.LessEqual => BinaryOp.GreaterEqual,
    BinaryOp.GreaterEqual => BinaryOp.LessEqual,
    _ => op,
  };

  private static BinaryOp NegateCompare(BinaryOp op) => op switch {
    BinaryOp.Less => BinaryOp.GreaterEqual,
    BinaryOp.LessEqual => BinaryOp.Greater,
    BinaryOp.Greater => BinaryOp.LessEqual,
    BinaryOp.GreaterEqual => BinaryOp.Less,
    BinaryOp.Equal => BinaryOp.NotEqual,
    BinaryOp.NotEqual => BinaryOp.Equal,
    _ => op,
  };

  private static Interval TypeRange(PbType type) => type switch {
    ScalarType { IsFloat: false, ByteSize: 1, Signed: true } => new(-128, 127),
    ScalarType { IsFloat: false, ByteSize: 1, Signed: false } => new(0, 255),
    ScalarType { IsFloat: false, ByteSize: 2, Signed: true } => new(-32768, 32767),
    ScalarType { IsFloat: false, ByteSize: 2, Signed: false } => new(0, 65535),
    ScalarType { IsFloat: false, ByteSize: 4, Signed: true } => new(-2147483648, 2147483647),
    ScalarType { IsFloat: false, ByteSize: 4, Signed: false } => new(0, 4294967295),
    _ => Interval.Top,
  };

  private static ValueFacts StoreInto(ValueFacts facts, PbType type) {
    var fitted = FitOrTop(facts.Range, type);
    var width = WidthOf(type);
    var mod = fitted.IsTop && !IsPowerOfTwo(facts.Mod.Modulus) ? Congruence.Unknown : facts.Mod;
    var stored = new ValueFacts(fitted, facts.Bits.Narrow(width), mod);
    return type is ScalarType { IsFloat: false, Signed: var signed } && width > 0
      ? ValueFactReduction.Reduce(stored, width, signed)
      : stored;
  }

  private static Interval FitOrTop(Interval iv, PbType type) {
    if (iv.IsTop)
      return Interval.Top;
    var t = TypeRange(type);
    return iv.Lo >= t.Lo && iv.Hi <= t.Hi ? iv : Interval.Top;
  }

  private static VariableSymbol? IntVar(Expression e, SemanticModel model)
    => e is NameExpr && model.VariableBindings.TryGetValue(e, out var s)
       && s.Type is ScalarType { IsFloat: false }
       ? s : null;

  private static bool CallFree(Expression e, SemanticModel model) => e switch {
    _ when model.CallBindings.ContainsKey(e) || model.ProcPtrCalls.ContainsKey(e) => false,
    UnaryExpr u => CallFree(u.Operand, model),
    BinaryExpr b => CallFree(b.Left, model) && CallFree(b.Right, model),
    CallOrIndexExpr c => c.Arguments.All(a => CallFree(a, model)),
    MemberExpr m => CallFree(m.Target, model),
    ByValArgExpr v => CallFree(v.Value, model),
    _ => true,
  };

  private static void TransferLoop(Statement self, IReadOnlyList<Statement> body, VariableSymbol? counter,
      Interval counterRange, Dictionary<VariableSymbol, ValueFacts> env, Scope scope,
      Dictionary<Statement, IReadOnlyDictionary<VariableSymbol, ValueFacts>>? points) {
    var entry = Clone(env);
    if (counter != null)
      SetRange(entry, counter, counterRange);

    var inv = Clone(entry);
    for (var iter = 0; iter < 16; ++iter) {
      var exit = Clone(inv);
      Run(body, exit, scope, null);
      if (counter != null)
        SetRange(exit, counter, counterRange);
      var widened = WidenEnv(inv, JoinAll([entry, exit]));
      if (EnvEquals(widened, inv))
        break;
      inv = widened;
    }

    if (points != null) {
      points[self] = Clone(inv);
      var bodyEnv = Clone(inv);
      Run(body, bodyEnv, scope, points);
    }

    var afterExit = Clone(inv);
    Run(body, afterExit, scope, null);
    var after = JoinAll([entry, afterExit]);
    if (counter != null)
      after.Remove(counter);
    Replace(env, after);
  }

  private static Interval Widen(Interval old, Interval candidate) {
    if (old.IsTop)
      return Interval.Top;
    var lo = candidate.Lo < old.Lo ? long.MinValue : old.Lo;
    var hi = candidate.Hi > old.Hi ? long.MaxValue : old.Hi;
    return new(lo, hi);
  }

  private static Dictionary<VariableSymbol, ValueFacts> WidenEnv(Dictionary<VariableSymbol, ValueFacts> old,
      Dictionary<VariableSymbol, ValueFacts> candidate) {
    var result = new Dictionary<VariableSymbol, ValueFacts>(ReferenceEqualityComparer.Instance);
    foreach (var kv in candidate) {
      var range = old.TryGetValue(kv.Key, out var o) ? Widen(o.Range, kv.Value.Range) : kv.Value.Range;
      var bits = old.TryGetValue(kv.Key, out var ob) ? ob.Bits.Join(kv.Value.Bits) : kv.Value.Bits;
      var mod = old.TryGetValue(kv.Key, out var om) ? om.Mod.Join(kv.Value.Mod) : kv.Value.Mod;
      var widened = new ValueFacts(range, bits, mod);
      if (!widened.IsUnknown)
        result[kv.Key] = widened;
    }
    return result;
  }

  private static bool EnvEquals(Dictionary<VariableSymbol, ValueFacts> a, Dictionary<VariableSymbol, ValueFacts> b) {
    if (a.Count != b.Count)
      return false;
    foreach (var kv in a)
      if (!b.TryGetValue(kv.Key, out var o) || !o.Equals(kv.Value))
        return false;
    return true;
  }

  private static bool BodyCallFree(IReadOnlyList<Statement> body, SemanticModel model) {
    foreach (var s in body)
      switch (s) {
        case AssignStmt a when CallFree(a.Value, model) && (a.Target is NameExpr || CallFree(a.Target, model)):
          break;
        case IncrDecrStmt id when id.Amount == null || CallFree(id.Amount, model):
          break;
        case IfStmt iff when CallFree(iff.Condition, model)
            && iff.ElseIfs.All(e => CallFree(e.Condition, model))
            && BodyCallFree(iff.Then, model) && iff.ElseIfs.All(e => BodyCallFree(e.Body, model))
            && (iff.Else == null || BodyCallFree(iff.Else, model)):
          break;
        case ForStmt f when CallFree(f.From, model) && CallFree(f.To, model) && BodyCallFree(f.Body, model):
          break;
        case PrintStmt p when (p.FileNumber == null || CallFree(p.FileNumber, model))
            && p.Items.All(i => i.Value == null || CallFree(i.Value, model)):
          break;
        case MetaStmt or EquateStmt or DefTypeStmt or DataStmt or EndStmt or LabelStmt:
          break;
        default:
          return false;
      }
    return true;
  }

  private static void Set(Dictionary<VariableSymbol, ValueFacts> env, VariableSymbol sym, ValueFacts facts) {
    var width = WidthOf(sym.Type);
    if (width > 0 && sym.Type is ScalarType { Signed: var signed })
      facts = ValueFactReduction.Reduce(facts, width, signed);
    if (facts.IsUnknown)
      env.Remove(sym);
    else
      env[sym] = facts;
  }

  private static void SetRange(Dictionary<VariableSymbol, ValueFacts> env, VariableSymbol sym, Interval range) {
    var known = env.TryGetValue(sym, out var k) ? k : ValueFacts.Unknown;
    Set(env, sym, new(range, known.Bits, known.Mod));
  }

  private static Dictionary<VariableSymbol, ValueFacts> Clone(Dictionary<VariableSymbol, ValueFacts> env)
    => new(env, ReferenceEqualityComparer.Instance);

  private static void Replace(Dictionary<VariableSymbol, ValueFacts> env, Dictionary<VariableSymbol, ValueFacts> with) {
    env.Clear();
    foreach (var kv in with)
      env[kv.Key] = kv.Value;
  }

  private static Dictionary<VariableSymbol, ValueFacts> JoinAll(List<Dictionary<VariableSymbol, ValueFacts>> envs) {
    var result = new Dictionary<VariableSymbol, ValueFacts>(ReferenceEqualityComparer.Instance);
    if (envs.Count == 0)
      return result;
    foreach (var kv in envs[0]) {
      var joined = kv.Value;
      var inAll = true;
      for (var i = 1; i < envs.Count; ++i) {
        if (!envs[i].TryGetValue(kv.Key, out var other)) { inAll = false; break; }
        joined = joined.Join(other);
      }
      if (inAll && !joined.IsUnknown) {
        var width = WidthOf(kv.Key.Type);
        if (width > 0 && kv.Key.Type is ScalarType { Signed: var signed })
          joined = ValueFactReduction.Reduce(joined, width, signed);
        if (!joined.IsUnknown)
          result[kv.Key] = joined;
      }
    }
    return result;
  }
}
