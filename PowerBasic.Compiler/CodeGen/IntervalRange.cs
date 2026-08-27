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
/// What the analysis knows about one value: its range AND its bits. The two domains answer
/// different questions and neither subsumes the other - an interval proves <c>x MOD 2</c> is never
/// 2, while only the bits prove <c>(x \ 2) * 2</c> is never 1 (it spans nearly the whole type, but
/// its low bit is always 0). Consumers ask whichever one their question needs.
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
/// O16 forward interval propagation over a bound statement list: the range tag every tracked
/// scalar carries at every program point. It is the prerequisite for type narrowing (a LONG that
/// provably fits a narrower type) and for dropping the checks the FOR-counter lattice cannot
/// reach. Modelled: scalar-integer assignment / INCR, IF and SELECT CASE arms (each refined by
/// what its own test proves) with their joins, and FOR/DO loops by a fixpoint with widening.
///
/// A statement that is not modelled invalidates only what it can actually write: a call reaches
/// module-level data, this frame's parameters and whatever the statement names, but not the
/// procedure's private locals - unless the body takes an address, stores through a pointer,
/// POKEs, runs inline assembly or captures the frame in a lambda / nested procedure, in which
/// case everything is dropped. Anything else unmodelled, and every label in a body that contains
/// a jump, resets the whole environment. Every rule is an over-approximation, so a consumer that
/// acts only when the whole interval qualifies stays sound; absence from the environment means
/// <see cref="Interval.Top"/> (unknown).
/// </summary>
public static class IntervalRangeAnalysis {
  /// <summary>The per-variable interval environment after executing <paramref name="body"/>.</summary>
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
        // a lambda or a nested SUB/FUNCTION captures this frame's locals BYREF, so calling it
        // writes them from the outside - the same hazard as a taken address
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
  /// The interval environment at the ENTRY of each statement (keyed by statement reference,
  /// recursively into IF arms) - so a consumer can read a variable's proven range at a specific
  /// use site (e.g. to narrow a LONG operation or drop a check). A statement absent from the map
  /// was unreachable to the analysis; a variable absent from a statement's environment is Top.
  /// </summary>
  public static IReadOnlyDictionary<Statement, IReadOnlyDictionary<VariableSymbol, ValueFacts>>
      AnalyzeProgramPoints(IReadOnlyList<Statement> body, SemanticModel model) {
    var points = new Dictionary<Statement, IReadOnlyDictionary<VariableSymbol, ValueFacts>>(ReferenceEqualityComparer.Instance);
    var env = new Dictionary<VariableSymbol, ValueFacts>(ReferenceEqualityComparer.Instance);
    Run(body, env, ScopeOf(body, model), points);
    return points;
  }

  private static void Run(IReadOnlyList<Statement> body, Dictionary<VariableSymbol, ValueFacts> env, Scope scope,
      Dictionary<Statement, IReadOnlyDictionary<VariableSymbol, ValueFacts>>? points) {
    foreach (var s in body) {
      if (points != null && !IsLoop(s))
        points.TryAdd(s, Clone(env));                 // snapshot the entry environment
      Transfer(s, env, scope, points);
    }
  }

  /// <summary>
  /// A statement whose recorded program point is NOT its entry environment, because the emitter reads
  /// that point while emitting something the loop re-executes - the pre/post test, which runs again on
  /// every back edge with loop-carried values.
  ///
  /// <para>
  /// <see cref="TransferLoop"/> is the sole author of a loop's point and writes the widened invariant
  /// there for exactly this reason. It only runs when the loop is analyzable, though, and the entry
  /// snapshot taken in front of it survived when it did not - so a loop whose body the analysis
  /// refuses (a procedure call, <c>INPUT</c>, <c>EXIT SUB</c>, <c>GOTO</c>, ...) left its test reading
  /// the values that held BEFORE the loop. <c>i = 0 : WHILE i &lt; 3 : i = i + 1 : Note i : WEND</c>
  /// folded <c>i &lt; 3</c> to a constant TRUE (O16 <c>TryEmitRangeComparison</c>, <c>MOV AX,-1</c>) and
  /// the program never terminated. No entry at all is the honest answer: absence means Top.
  /// </para>
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
        var amount = id.Amount == null ? ValueFacts.Of(1, 64) : Eval(id.Amount, env, model);
        var stepped = new ValueFacts(
          id.Increment ? cur.Range.Add(amount.Range) : cur.Range.Subtract(amount.Range),
          cur.Bits.AddSub(amount.Bits, subtract: !id.Increment),
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
        results.Add(elseEnv);                          // Else, or (no Else) the not-taken fallthrough
        Replace(env, JoinAll(results));
        return;
      }
      // a FOR loop: the counter is bounded by [From,To]; the body's loop-carried effect is found
      // by a fixpoint with widening (accumulators widen to Top; values recomputed from the counter
      // stay bounded). Only fires when the body is itself call-free.
      case ForStmt f when IntVar(f.Variable, model) is { } ctr
          && CallFree(f.From, model) && CallFree(f.To, model) && BodyCallFree(f.Body, model): {
        var range = Eval(f.From, env, model).Range.Join(Eval(f.To, env, model).Range);
        TransferLoop(f, f.Body, ctr, range, env, scope, points);
        return;
      }
      // a DO/WHILE loop: no counter, so just the fixpoint-with-widening over a call-free body
      case DoLoopStmt d when (d.PreCondition == null || CallFree(d.PreCondition, model))
          && (d.PostCondition == null || CallFree(d.PostCondition, model)) && BodyCallFree(d.Body, model):
        TransferLoop(d, d.Body, null, Interval.Top, env, scope, points);
        return;
      // SELECT CASE: each arm is entered only when the subject matches one of its selectors, so
      // the arm body sees the subject narrowed to the hull of those selectors (exactly the IF-arm
      // refinement, generalized to a selector list). Arms are independent; their exits join, plus
      // the no-match fall-through when there is no CASE ELSE.
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
          results.Add(Clone(env));                     // no CASE ELSE: nothing may match
        Replace(env, JoinAll(results));
        return;
      }

      // a call-free PRINT writes no scalar variable - keep the environment intact
      case PrintStmt p when (p.FileNumber == null || CallFree(p.FileNumber, model))
          && (p.UsingFormat == null || CallFree(p.UsingFormat, model))
          && p.Items.All(i => i.Value == null || CallFree(i.Value, model)):
        return;
      // a label is a join point for every jump that targets it, so what was true on the way here
      // says nothing about the state a GOTO/RESUME arrives with
      case LabelStmt:
        if (scope.Jumps)
          env.Clear();
        return;

      // statements that write no scalar variable - keep the environment intact
      case MetaStmt or EquateStmt or DefTypeStmt or DataStmt or EndStmt:
        return;
      // A procedure call is not a wall. It can write module-level data, anything reached through
      // its own arguments and any parameter cell of this frame (a BYREF one aliases the caller's
      // variable) - but it cannot see this scope's other locals, so those keep their ranges across
      // it. That only holds while no address escaped anywhere in the body; when one did, the call
      // could write anything and the environment is cleared as before.
      case CallStmt or AssignStmt or IncrDecrStmt or PrintStmt when !scope.Escapes:
        KillReachableByCall(s, env, model);
        return;

      default:
        // an unmodelled statement may write tracked variables (INPUT, a jump, inline asm, ...)
        // - drop to the sound conservative fixpoint: everything unknown
        env.Clear();
        return;
    }
  }

  /// <summary>
  /// Invalidates exactly what a call inside <paramref name="s"/> can reach: every variable that is
  /// not a private local of this frame, plus every variable the statement names (an argument may
  /// be passed BYREF, and the statement's own assignment target is named too). Only called when no
  /// address escaped in this body - see <see cref="Scope.Escapes"/>.
  /// </summary>
  private static void KillReachableByCall(Statement s, Dictionary<VariableSymbol, ValueFacts> env, SemanticModel model) {
    foreach (var v in env.Keys.ToList())
      if (v.Storage != VariableStorage.Local || v.IsShared)
        env.Remove(v);
    foreach (var node in OptReachability.DescendantNodes(s))
      if (node is Expression e && model.VariableBindings.TryGetValue(e, out var named))
        env.Remove(named);
  }

  /// <summary>True when every expression of a CASE selector is call-free (so matching it cannot itself change the state).</summary>
  private static bool SelectorCallFree(CaseSelector selector, SemanticModel model)
    => (selector.Value == null || CallFree(selector.Value, model))
       && (selector.RangeUpper == null || CallFree(selector.RangeUpper, model));

  /// <summary>
  /// Narrows <paramref name="subject"/> to the hull of the ranges its selectors admit - the arm
  /// runs when ANY selector matches, so the union (over-approximated by the hull) is what the body
  /// can see. A selector whose bound is not evaluable yields no refinement at all (Top), which is
  /// the sound answer for "this arm might be entered with anything".
  /// </summary>
  private static void RefineForSelectors(Dictionary<VariableSymbol, ValueFacts> env, VariableSymbol subject,
      IReadOnlyList<CaseSelector> selectors, SemanticModel model) {
    Interval? admitted = null;
    foreach (var selector in selectors) {
      var one = SelectorRange(selector, env, model);
      if (one is not { } iv)
        return;                                        // an unbounded selector - keep what we knew
      admitted = admitted is { } sofar ? sofar.Join(iv) : iv;
    }
    if (admitted is not { } range)
      return;
    var current = env.TryGetValue(subject, out var known) ? known.Range : TypeRange(subject.Type);
    var refined = new Interval(Math.Max(current.Lo, range.Lo), Math.Min(current.Hi, range.Hi));
    if (!refined.IsEmpty)
      SetRange(env, subject, refined);
  }

  /// <summary>The values one CASE selector admits: <c>CASE v</c>, <c>CASE lo TO hi</c> or <c>CASE IS &lt;op&gt; v</c>.</summary>
  private static Interval? SelectorRange(CaseSelector selector, IReadOnlyDictionary<VariableSymbol, ValueFacts> env, SemanticModel model) {
    if (selector.Value is not { } value)
      return null;
    var low = Eval(value, env, model).Range;
    if (low.IsTop)
      return null;
    if (selector.RangeUpper is { } upper) {            // CASE lo TO hi
      var high = Eval(upper, env, model).Range;
      return high.IsTop ? null : new Interval(low.Lo, high.Hi);
    }
    return selector.IsComparison switch {              // CASE IS <relation> v
      null or CaseComparison.Equal => low,
      CaseComparison.Less => new Interval(long.MinValue, low.Hi - 1),
      CaseComparison.LessEqual => new Interval(long.MinValue, low.Hi),
      CaseComparison.Greater => new Interval(low.Lo + 1, long.MaxValue),
      CaseComparison.GreaterEqual => new Interval(low.Lo, long.MaxValue),
      _ => null,                                       // NotEqual is a hole, not an interval
    };
  }

  /// <summary>A modulus that divides some 2^n, so wrapping cannot move a value off its residue.</summary>
  private static bool IsPowerOfTwo(long m) => m > 0 && (m & (m - 1)) == 0;

  /// <summary>The width in bits of an expression's own type; 0 when it is not a sized integer.</summary>
  private static int WidthOf(Expression e, SemanticModel model) =>
    model.TypeOf(e) is ScalarType { IsFloat: false, ByteSize: var n } ? n * 8 : 0;

  private static ValueFacts Eval(Expression e, IReadOnlyDictionary<VariableSymbol, ValueFacts> env, SemanticModel model) {
    switch (e) {
      case IntegerLiteralExpr lit:
        return ValueFacts.Of(lit.Value, WidthOf(lit, model));
      case NameExpr n when IntVar(n, model) is { } sym:
        return env.TryGetValue(sym, out var iv) ? iv : ValueFacts.Unknown;
      case UnaryExpr { Op: UnaryOp.Negate, Operand: { } operand }: {
        var inner = Eval(operand, env, model);
        return new(inner.Range.Negate(), KnownBits.Unknown, inner.Mod.Negate());
      }
      case UnaryExpr { Op: UnaryOp.Not, Operand: { } notOperand }:
        // NOT is exact on bits, and PB's NOT is the bitwise complement (-x-1) on the range
        return new(Interval.Top, Eval(notOperand, env, model).Bits.Not().Narrow(WidthOf(e, model)), Congruence.Unknown);
      case BinaryExpr b: {
        var l = Eval(b.Left, env, model);
        var r = Eval(b.Right, env, model);
        var width = WidthOf(b, model);
        var range = b.Op switch {
          BinaryOp.Add => l.Range.Add(r.Range),
          BinaryOp.Subtract => l.Range.Subtract(r.Range),
          BinaryOp.Multiply => l.Range.Multiply(r.Range),
          BinaryOp.IntegerDivide => l.Range.Divide(r.Range),
          BinaryOp.Modulo => l.Range.Modulo(r.Range),
          BinaryOp.And => l.Range.And(r.Range),
          _ => Interval.Top,
        };
        // Bits survive what ranges do not: two's-complement wrapping is arithmetic modulo 2^n, so
        // the low bits of a wrapped result are still exactly the low bits of the true one. Only
        // the range has to be discarded when it leaves the type.
        var bits = b.Op switch {
          BinaryOp.And => l.Bits.And(r.Bits),
          BinaryOp.Or => l.Bits.Or(r.Bits),
          BinaryOp.Xor => l.Bits.Xor(r.Bits),
          BinaryOp.Add => l.Bits.AddSub(r.Bits, subtract: false),
          BinaryOp.Subtract => l.Bits.AddSub(r.Bits, subtract: true),
          BinaryOp.Multiply => l.Bits.Multiply(r.Bits, width),
          BinaryOp.ShiftLeft when r.Range is { Lo: var sl, Hi: var sh } && sl == sh => l.Bits.ShiftLeft((int)sl),
          BinaryOp.ShiftRightArith when r.Range is { Lo: var al, Hi: var ah } && al == ah => l.Bits.ShiftRight((int)al, width, arithmetic: true),
          BinaryOp.ShiftRightLogical when r.Range is { Lo: var ll, Hi: var lh } && ll == lh => l.Bits.ShiftRight((int)ll, width, arithmetic: false),
          _ => KnownBits.Unknown,
        };
        // Congruences, like bits, survive wrapping only when the modulus divides 2^width - which
        // is exactly what the power-of-two check below asks. Otherwise a wrapped value can land on
        // any residue, so the fact is dropped with the range.
        var mod = b.Op switch {
          BinaryOp.Add => l.Mod.Add(r.Mod),
          BinaryOp.Subtract => l.Mod.Subtract(r.Mod),
          BinaryOp.Multiply => l.Mod.Multiply(r.Mod),
          _ => Congruence.Unknown,
        };
        var fitted = FitOrTop(range, model.TypeOf(b));
        if (fitted.IsTop && !IsPowerOfTwo(mod.Modulus))
          mod = Congruence.Unknown;                    // the value may have wrapped off its residue
        return new(fitted, bits.Narrow(width), mod);
      }
      default:
        return ValueFacts.Unknown;
    }
  }

  /// <summary>
  /// Narrow a variable's interval by a comparison condition known to hold (<paramref
  /// name="whenTrue"/>) or not hold inside an IF arm - "x OP const" or "const OP x" with x a
  /// tracked integer variable. Sound: the branch guarantees the (possibly negated) comparison, so
  /// x lies in the intersection of its incoming range and the comparison's implied range.
  /// </summary>
  private static void RefineForCondition(Dictionary<VariableSymbol, ValueFacts> env, Expression cond,
      bool whenTrue, SemanticModel model) {
    // A composite condition refines through the side that is decided by it: "A AND B" being TRUE
    // means both held, "A OR B" being FALSE means neither did, and NOT flips the question. That
    // needs each operand to be a truth value (-1/0), since PB's AND/OR are bitwise - "3 AND 5" is
    // 1 without either operand being a condition at all.
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
    // a variable's value always lies inside its type, so that is the range a refinement
    // starts from - it turns a one-sided test (x% <= 5) into a usable two-sided interval
    var cur = env.TryGetValue(v, out var iv) ? iv.Range : TypeRange(v.Type);
    Interval? refined = op switch {
      BinaryOp.Less => new Interval(cur.Lo, Math.Min(cur.Hi, c - 1)),
      BinaryOp.LessEqual => new Interval(cur.Lo, Math.Min(cur.Hi, c)),
      BinaryOp.Greater => new Interval(Math.Max(cur.Lo, c + 1), cur.Hi),
      BinaryOp.GreaterEqual => new Interval(Math.Max(cur.Lo, c), cur.Hi),
      BinaryOp.Equal => new Interval(Math.Max(cur.Lo, c), Math.Min(cur.Hi, c)),
      _ => null,                                       // NotEqual is a hole, not an interval; a
                                                       // bitwise op is not a condition at all
    };
    if (refined is { IsEmpty: false } narrowed)
      SetRange(env, v, narrowed);
  }

  /// <summary>
  /// True when <paramref name="e"/> can only be one of PB's truth values (-1 / 0): a comparison,
  /// or a bitwise combination of such. Only then does "the whole thing is true/false" say anything
  /// about the operands - for arbitrary integers AND/OR/NOT are just bit twiddling.
  /// </summary>
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
    _ => op,                                           // Equal / NotEqual are symmetric
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

  /// <summary>The representable range of an integer type; Top for floats, QUAD, or anything else
  /// (so it never clamps a value we cannot bound). Integer arithmetic wraps within this range.</summary>
  private static Interval TypeRange(PbType type) => type switch {
    ScalarType { IsFloat: false, ByteSize: 1, Signed: true } => new(-128, 127),
    ScalarType { IsFloat: false, ByteSize: 1, Signed: false } => new(0, 255),
    ScalarType { IsFloat: false, ByteSize: 2, Signed: true } => new(-32768, 32767),
    ScalarType { IsFloat: false, ByteSize: 2, Signed: false } => new(0, 65535),
    ScalarType { IsFloat: false, ByteSize: 4, Signed: true } => new(-2147483648, 2147483647),
    ScalarType { IsFloat: false, ByteSize: 4, Signed: false } => new(0, 4294967295),
    _ => Interval.Top,
  };

  /// <summary>
  /// What a variable of <paramref name="type"/> holds after storing <paramref name="facts"/>: the
  /// range only survives when it fits (a store that wraps makes it a fiction), while the bits
  /// simply narrow to the type's width - wrapping cannot disturb them.
  /// </summary>
  private static ValueFacts StoreInto(ValueFacts facts, PbType type) {
    var fitted = FitOrTop(facts.Range, type);
    var mod = fitted.IsTop && !IsPowerOfTwo(facts.Mod.Modulus) ? Congruence.Unknown : facts.Mod;
    return new(fitted, facts.Bits.Narrow(type is ScalarType { IsFloat: false, ByteSize: var n } ? n * 8 : 0), mod);
  }

  /// <summary>The interval unchanged when it fits the type's representable range; otherwise Top -
  /// a value that overflows the type wraps to something the lattice cannot predict.</summary>
  private static Interval FitOrTop(Interval iv, PbType type) {
    if (iv.IsTop)
      return Interval.Top;
    var t = TypeRange(type);
    return iv.Lo >= t.Lo && iv.Hi <= t.Hi ? iv : Interval.Top;
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

  /// <summary>
  /// Analyze a loop body: compute the body-entry invariant by a fixpoint with widening (so it
  /// terminates), record per-program-point environments inside the body using that invariant, and
  /// leave <paramref name="env"/> as the post-loop state (0 iterations joined with the body's
  /// effect). A FOR counter is pinned to its <paramref name="counterRange"/> at body entry and
  /// removed (Top) after the loop; loop-carried accumulators widen to Top, values recomputed each
  /// iteration from the counter stay bounded.
  /// </summary>
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
        SetRange(exit, counter, counterRange);       // the counter is back in range at re-entry
      var widened = WidenEnv(inv, JoinAll([entry, exit]));
      if (EnvEquals(widened, inv))
        break;
      inv = widened;
    }

    if (points != null) {
      // the loop's OWN condition is re-evaluated every iteration with loop-carried values, so it
      // must see the invariant (widened), NOT the pre-loop entry env that Run recorded.
      points[self] = Clone(inv);
      var bodyEnv = Clone(inv);
      Run(body, bodyEnv, scope, points);             // per-point envs inside the body
    }

    var afterExit = Clone(inv);
    Run(body, afterExit, scope, null);
    var after = JoinAll([entry, afterExit]);          // 0 iterations, or the body's exit
    if (counter != null)
      after.Remove(counter);                          // post-loop counter value is not tracked
    Replace(env, after);
  }

  /// <summary>Interval widening: an endpoint that grew jumps to +/-infinity, so the fixpoint
  /// terminates after a bounded number of rounds.</summary>
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
      // the range widens (so the fixpoint terminates); the bits only ever merge, which is already
      // monotone - a loop-carried value keeps "always even" even as its range gives up
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
          return false;                               // a call / unmodelled statement -> not analyzable
      }
    return true;
  }

  /// <summary>Store what is known, or drop the variable when nothing is (absence = unknown).</summary>
  private static void Set(Dictionary<VariableSymbol, ValueFacts> env, VariableSymbol sym, ValueFacts facts) {
    if (facts.IsUnknown)
      env.Remove(sym);
    else
      env[sym] = facts;
  }

  /// <summary>Stores a range while keeping whatever was known about the bits (a refinement narrows one domain).</summary>
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

  /// <summary>Join environments: a variable known in every branch joins to the hull; a variable
  /// missing (Top) in any branch is unknown after the merge, so it is dropped.</summary>
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
      if (inAll && !joined.IsUnknown)
        result[kv.Key] = joined;
    }
    return result;
  }
}
