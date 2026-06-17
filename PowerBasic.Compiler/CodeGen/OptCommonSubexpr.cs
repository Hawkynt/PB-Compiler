using System.Text;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

/// <summary>
/// pb36 O3 - block-local common subexpression elimination (docs/PB36.md). A
/// pure integer subexpression tree (the SVGA corpus's <c>y*320+x</c> address
/// arithmetic, recomputed across statements) is evaluated once into a frame
/// slot and reloaded at every further occurrence within the same straight-line
/// run.
///
/// Soundness rests on one invariant: a USE only ever reloads a slot that a
/// DEFINE populated from <em>identical inputs</em> with no intervening write to
/// those inputs and no barrier. The DEFINE emits the subtree verbatim, so any
/// trap it would raise ($ERROR NUMERIC overflow on a <c>*</c>) fires exactly
/// where the un-CSE'd first occurrence would have - and a USE that reloads the
/// same value is what recomputing would have produced. Hence byte-identical.
///
/// The run model is deliberately block-local: every control-flow or
/// side-effecting statement (calls, labels, branches, loops, POKE/DEF SEG,
/// inline asm, pointer/member stores) ends the run and clears the cache; only
/// straight-line assignments / prints over pure scalar-integer arithmetic
/// keep it alive, invalidating the slots that read a written scalar.
/// </summary>
public static class OptCommonSubexpr {

  public readonly record struct CseMark(int Slot, bool IsDefine);

  /// <summary>Analysis result: which AST nodes to define/reload, and how many 4-byte slots the frame must reserve.</summary>
  public sealed class Result {
    public Dictionary<Expression, CseMark> Marks { get; } = new(ReferenceEqualityComparer.Instance);
    public int SlotCount { get; set; }
  }

  /// <summary>
  /// pb36 LICM analysis result: a hoistable loop-invariant subexpression, the
  /// first AST node that computes it (emitted once in the preheader as a DEFINE),
  /// and the subsequent body occurrences (marked as reloads). The slot index is
  /// allocated relative to the <c>firstSlot</c> offset supplied to
  /// <see cref="AnalyzeLicm"/>; the caller merges it into its CSE mark dictionary
  /// and bumps <c>_cseBytes</c> accordingly.
  /// </summary>
  public sealed class LicmResult {
    /// <summary>New mark entries to merge into the frame-wide CSE mark dict.</summary>
    public Dictionary<Expression, CseMark> Marks { get; } = new(ReferenceEqualityComparer.Instance);
    /// <summary>The DEFINE node for each invariant (in discovery order): caller emits these in the preheader.</summary>
    public List<Expression> Preheader { get; } = [];
    /// <summary>
    /// Subset of <see cref="Preheader"/> whose nodes are modular-int16 trees (typed
    /// <c>Single</c> by the binder but computed on the 16-bit ALU). The caller must
    /// emit these via <c>EmitModularInt16</c> rather than <c>EmitExpression</c>.
    /// </summary>
    public HashSet<Expression> ModularPreheader { get; } = new(ReferenceEqualityComparer.Instance);
    /// <summary>Number of new 4-byte frame slots needed (one per unique invariant key).</summary>
    public int SlotCount { get; set; }
  }

  /// <summary>
  /// pb36 LICM: identifies pure integer subexpressions in the loop body whose
  /// operands are all loop-invariant (not written anywhere in the body, not the
  /// loop counter) and that cannot trap when computed unconditionally.
  ///
  /// Safety contract:
  /// <list type="bullet">
  ///   <item>Only fires when <c>checkedArithmetic</c> is false (no $ERROR
  ///   NUMERIC/OVERFLOW/ALL) — overflow traps would fire even in a zero-trip
  ///   loop if we hoisted them.</item>
  ///   <item><c>\</c> and <c>MOD</c> are excluded unless their right operand is a
  ///   compile-time non-zero constant — a zero divisor could trap in the body but
  ///   not in a zero-trip preheader, changing behavior.</item>
  ///   <item>The body must be a flat straight-line sequence (no control flow
  ///   nesting): any nested block makes the written-set conservative but the
  ///   structure check is the belt-and-suspenders gate ensuring no conditional
  ///   write is missed.</item>
  /// </list>
  ///
  /// Slot indices start at <paramref name="firstSlot"/> so the returned marks
  /// slot-interleave cleanly with the existing block-local CSE slots.
  /// </summary>
  public static LicmResult AnalyzeLicm(
      IReadOnlyList<Statement> body,
      VariableSymbol? counter,
      int firstSlot,
      bool checkedArithmetic,
      SemanticModel model) {
    var result = new LicmResult();
    if (checkedArithmetic)
      return result; // overflow/numeric traps must fire in-body, never in preheader

    // the body must be a flat straight-line sequence with no nested control flow -
    // any branching makes conditional writes invisible in the write-set scan
    if (!IsBodyFlatStraightLine(body))
      return result;

    // collect every variable written anywhere in the body (conservative union)
    var written = new HashSet<VariableSymbol>(ReferenceEqualityComparer.Instance);
    if (counter != null)
      written.Add(counter); // the FOR counter is always written by the increment; a DO loop has none
    CollectWrites(body, written, model);

    // track which invariant keys we have already seen (key -> slot), and the
    // set of variable ids for stable key generation (shared, no id collisions)
    var slotOfKey = new Dictionary<string, int>(StringComparer.Ordinal);
    var varId = new Dictionary<VariableSymbol, int>(ReferenceEqualityComparer.Instance);

    // walk each statement's expressions to discover cacheable invariants
    foreach (var stmt in body)
      WalkStmtForLicm(stmt, written, firstSlot, slotOfKey, varId, result, model);

    result.SlotCount = slotOfKey.Count;
    return result;
  }

  /// <summary>
  /// True when every statement in <paramref name="body"/> is a flat, straight-line
  /// kind with no nested control-flow blocks and no opaque writes. Accepted:
  /// AssignStmt, IncrDecrStmt, PrintStmt (no side effects on tracked scalars, or
  /// side effects we can fully account for), plus inert metadata statements
  /// (MetaStmt, EquateStmt, DefTypeStmt, DataStmt - never executed).
  /// Rejected: CallStmt (BYREF args can write anything), InputStmt (writes targets),
  /// ReadStmt (writes targets), SwapStmt (writes two variables), CommandStmt (POKE,
  /// DEF SEG, SOUND, etc. - runtime side effects, may alias memory), and any
  /// control-flow statement (IF, FOR, DO, SELECT, GOTO, GOSUB, labels, ...).
  /// </summary>
  private static bool IsBodyFlatStraightLine(IReadOnlyList<Statement> body) {
    foreach (var s in body)
      if (s is not (AssignStmt or IncrDecrStmt or PrintStmt
          or MetaStmt or EquateStmt or DefTypeStmt or DataStmt))
        return false;
    return true;
  }

  /// <summary>Collects every variable symbol written anywhere in <paramref name="body"/> (conservatively traverses nested blocks).</summary>
  private static void CollectWrites(IReadOnlyList<Statement> body, HashSet<VariableSymbol> written, SemanticModel model) {
    foreach (var stmt in body) {
      switch (stmt) {
        case AssignStmt a:
          if (ScalarSymbolOfStatic(a.Target, model) is { } sym)
            written.Add(sym);
          // an array-element write touches a cached array read (redundant-load
          // elimination), so record the array symbol too - a value reading it must
          // not be retained across a merge whose branch wrote the array
          else if (a.Target is CallOrIndexExpr && model.VariableBindings.TryGetValue(a.Target, out var arr)
              && arr.Type is ArrayType)
            written.Add(arr);
          break;
        case IncrDecrStmt id:
          if (ScalarSymbolOfStatic(id.Target, model) is { } isym)
            written.Add(isym);
          else if (id.Target is CallOrIndexExpr && model.VariableBindings.TryGetValue(id.Target, out var iarr)
              && iarr.Type is ArrayType)
            written.Add(iarr);
          break;
        // PrintStmt, MetaStmt, EquateStmt, DefTypeStmt, DataStmt: no tracked writes
        default:
          break;
      }
    }
  }

  private static VariableSymbol? ScalarSymbolOfStatic(Expression e, SemanticModel model)
    => e is NameExpr && model.VariableBindings.TryGetValue(e, out var s)
       && s.Type is ScalarType && s.Storage is not VariableStorage.Static
       ? s : null;

  /// <summary>A scalar-integer name or integer literal/constant - a leaf index with no nested cacheable subtree.</summary>
  private static bool IsSimpleIndex(Expression e, SemanticModel model)
    => e is IntegerLiteralExpr
       || (e is NamedConstantExpr c && !(model.Equates.TryGetValue(c.Name, out var v) && v.Text != null))
       || (e is NameExpr && !model.IntrinsicBindings.ContainsKey(e)
           && model.VariableBindings.TryGetValue(e, out var s)
           && s.Type is ScalarType { IsFloat: false });

  /// <summary>
  /// pb36 redundant-load elimination: the array variable whose element <paramref name="e"/>
  /// reads, when that read is safe to cache as a common subexpression - a CallOrIndexExpr
  /// bound to a plain (non HUGE/VIRTUAL/ABSOLUTE), static, 2-byte non-float-element array,
  /// indexed only by simple integer names/literals (so the index has no nested cacheable
  /// subtree to interact with). null for function calls, intrinsics, strings, dynamic or
  /// special arrays, or composite indices. The cached value is invalidated by any write to
  /// the array or to an index name, and by any barrier (call / pointer write / REDIM).
  /// </summary>
  private static VariableSymbol? CacheableArrayReadSymbol(Expression e, SemanticModel model) {
    if (e is not CallOrIndexExpr c || model.IntrinsicBindings.ContainsKey(e))
      return null;
    if (!model.VariableBindings.TryGetValue(e, out var sym))
      return null;
    if (sym.Type is not ArrayType { Element: ScalarType { IsFloat: false, ByteSize: 2 }, IsDynamic: false })
      return null;
    if (sym.ArrayClass is ArrayClass.Huge or ArrayClass.Virtual or ArrayClass.Absolute)
      return null;
    if (c.Arguments.Count == 0)
      return null;
    foreach (var arg in c.Arguments)
      if (!IsSimpleIndex(arg, model))
        return null;
    return sym;
  }

  /// <summary>
  /// Walks a single statement's expressions for LICM candidates. For each
  /// cacheable pure subexpression whose inputs are all loop-invariant (not in
  /// <paramref name="written"/> and not the counter), records the first occurrence
  /// as a DEFINE and all subsequent occurrences as reloads.
  /// </summary>
  private static void WalkStmtForLicm(
      Statement stmt,
      HashSet<VariableSymbol> written,
      int firstSlot,
      Dictionary<string, int> slotOfKey,
      Dictionary<VariableSymbol, int> varId,
      LicmResult result,
      SemanticModel model) {
    // we only scan statements whose expressions are barrier-free (no calls,
    // no intrinsic reads, no pointer deref) - same criterion as the block-local CSE
    switch (stmt) {
      case AssignStmt a when IsBarrierFree(a.Value, model):
        FindLicmIn(a.Value, written, firstSlot, slotOfKey, varId, result, model);
        // array index expressions on the target are also emitted and can be hoisted
        if (a.Target is CallOrIndexExpr { Arguments: { } args })
          foreach (var arg in args)
            if (IsBarrierFree(arg, model))
              FindLicmIn(arg, written, firstSlot, slotOfKey, varId, result, model);
        break;
      case IncrDecrStmt id when id.Amount != null && IsBarrierFree(id.Amount, model):
        FindLicmIn(id.Amount, written, firstSlot, slotOfKey, varId, result, model);
        break;
      case PrintStmt p when !p.IsLPrint && p.UsingFormat == null:
        if (p.FileNumber is { } fn && IsBarrierFree(fn, model))
          FindLicmIn(fn, written, firstSlot, slotOfKey, varId, result, model);
        foreach (var item in p.Items)
          if (item.Value is { } v && IsBarrierFree(v, model))
            FindLicmIn(v, written, firstSlot, slotOfKey, varId, result, model);
        break;
    }
    // MetaStmt, EquateStmt, DefTypeStmt, DataStmt: inert, no expressions to scan
  }

  /// <summary>
  /// Recursively finds cacheable pure subexpressions of <paramref name="e"/> that
  /// are loop-invariant (no input in <paramref name="written"/>). The first
  /// occurrence of each unique key becomes a DEFINE (preheader computation);
  /// subsequent occurrences become reloads.
  /// </summary>
  private static void FindLicmIn(
      Expression e,
      HashSet<VariableSymbol> written,
      int firstSlot,
      Dictionary<string, int> slotOfKey,
      Dictionary<VariableSymbol, int> varId,
      LicmResult result,
      SemanticModel model) {
    if (!IsLicmCacheable(e, model))
      goto recurse;

    // check: all inputs are loop-invariant (not written, not the counter —
    // counter is already in `written`) and the expression itself cannot trap
    var inputs = Inputs(e, model);
    if (inputs.Any(sym => written.Contains(sym)))
      goto recurse; // a variant input: not hoistable; recurse into children

    // trap check: exclude \ and MOD unless divisor is a constant non-zero literal
    if (!IsHoistableSafely(e, model))
      goto recurse;

    // hoistable invariant: assign a slot if new, record define/use marks
    var isModular = model.TypeOf(e) is ScalarType { IsFloat: true };
    var key = (isModular ? "LM" : "L") + BuildKey(e, varId, model);
    if (!slotOfKey.TryGetValue(key, out var slot)) {
      slot = firstSlot + slotOfKey.Count;
      slotOfKey[key] = slot;
      // first occurrence: the DEFINE - preheader emits this, body reloads it
      result.Preheader.Add(e);
      result.Marks[e] = new CseMark(slot, IsDefine: true);
      if (isModular)
        result.ModularPreheader.Add(e);
    } else if (!result.Marks.ContainsKey(e)) {
      // subsequent occurrence (by reference identity): a reload
      result.Marks[e] = new CseMark(slot, IsDefine: false);
    }
    return; // do NOT recurse into children of a hoisted node (the define covers them)

    recurse:
    // not hoistable at this level: recurse into children to find inner invariants
    foreach (var child in Children(e))
      FindLicmIn(child, written, firstSlot, slotOfKey, varId, result, model);
  }

  /// <summary>
  /// True for a composite worth a LICM slot: a pure integer-typed BinaryExpr or
  /// UnaryExpr (ByteSize &lt;= 4, not float), OR a pure modular-int16 tree (typed
  /// Single/Double by the binder but computed on the 16-bit ALU). Mirrors
  /// <see cref="State.IsCacheable"/> for both Integer and Modular modes.
  /// </summary>
  private static bool IsLicmCacheable(Expression e, SemanticModel model)
    => e is (BinaryExpr or UnaryExpr)
       && IsPure(e, model)
       && (model.TypeOf(e) is ScalarType { IsFloat: false, ByteSize: <= 4 }
           || (model.TypeOf(e) is ScalarType { IsFloat: true } && IsModularInt16Tree(e, model, 0)));

  /// <summary>
  /// True when <paramref name="e"/> is a modular-int16 tree: a tree of +,-,* (and
  /// unary negate) nodes over 16-bit-or-narrower integer leaves. These are typed as
  /// <c>Single</c> or <c>Double</c> by the binder (PB 2.0+ arithmetic widening) but
  /// are actually computed on the 16-bit ALU by <c>EmitModularInt16</c>, producing
  /// the modular 16-bit result. Mirrors <see cref="State.IsModularInt16Tree"/>.
  /// </summary>
  private static bool IsModularInt16Tree(Expression e, SemanticModel model, int depth) {
    if (depth > 16) return false;
    if (model.TypeOf(e) is ScalarType { IsFloat: false, ByteSize: <= 2 }) return true;
    return e switch {
      UnaryExpr { Op: UnaryOp.Negate } u => IsModularInt16Tree(u.Operand, model, depth + 1),
      BinaryExpr { Op: BinaryOp.Add or BinaryOp.Subtract or BinaryOp.Multiply } b =>
        IsModularInt16Tree(b.Left, model, depth + 1) && IsModularInt16Tree(b.Right, model, depth + 1),
      _ => false,
    };
  }

  /// <summary>
  /// True when <paramref name="e"/> (already known to be pure) cannot trap when
  /// evaluated in a loop preheader (which runs even when the loop body does not).
  /// <c>\</c> and <c>MOD</c> can raise divide-by-zero (error 11) or quotient
  /// overflow — they are only safe when the right operand is a compile-time
  /// non-zero integer literal or equate. All other operators (+,-,*,AND,OR,XOR,
  /// shifts, NEG) are safe: modular wrap-around cannot trap when checked
  /// arithmetic is off.
  /// </summary>
  private static bool IsHoistableSafely(Expression e, SemanticModel model) {
    switch (e) {
      case BinaryExpr { Op: BinaryOp.IntegerDivide or BinaryOp.Modulo } b: {
        // safe only when divisor is a statically-known non-zero constant
        var divisor = FoldToInteger(b.Right, model);
        if (divisor is null or 0)
          return false;
        return IsHoistableSafely(b.Left, model);
      }
      case BinaryExpr b:
        return IsHoistableSafely(b.Left, model) && IsHoistableSafely(b.Right, model);
      case UnaryExpr u:
        return IsHoistableSafely(u.Operand, model);
      default:
        return true; // leaves (NameExpr, literal): trivially safe
    }
  }

  /// <summary>Folds a pure-integer expression to its compile-time value, or null if not a constant.</summary>
  private static long? FoldToInteger(Expression e, SemanticModel model) => e switch {
    IntegerLiteralExpr i => i.Value,
    NamedConstantExpr c when model.Equates.TryGetValue(c.Name, out var v) && v.Text == null => v.AsInteger,
    _ => null,
  };

  private static string BuildKey(Expression e, Dictionary<VariableSymbol, int> varId, SemanticModel model) {
    var sb = new StringBuilder();
    AppendLicmKey(sb, e, varId, model);
    return sb.ToString();
  }

  private static void AppendLicmKey(StringBuilder sb, Expression e, Dictionary<VariableSymbol, int> varId, SemanticModel model) {
    switch (e) {
      case IntegerLiteralExpr i:
        sb.Append('#').Append(i.Value).Append(';');
        break;
      case NamedConstantExpr c:
        sb.Append('#').Append(model.Equates.TryGetValue(c.Name, out var v) ? v.AsInteger : 0).Append(';');
        break;
      case NameExpr when model.VariableBindings.TryGetValue(e, out var sym):
        if (!varId.TryGetValue(sym, out var id)) {
          id = varId.Count;
          varId[sym] = id;
        }
        sb.Append('v').Append(id).Append(';');
        break;
      case UnaryExpr u:
        sb.Append('u').Append((int)u.Op).Append('(');
        AppendLicmKey(sb, u.Operand, varId, model);
        sb.Append(')');
        break;
      case BinaryExpr b:
        sb.Append('b').Append((int)b.Op).Append('(');
        AppendLicmKey(sb, b.Left, varId, model);
        AppendLicmKey(sb, b.Right, varId, model);
        sb.Append(')');
        break;
      default:
        sb.Append('?').Append(e.GetHashCode()).Append(';');
        break;
    }
  }

  public static Result Analyze(IReadOnlyList<Statement> body, SemanticModel model) {
    // the modular-int16 emission path (a% = y%*320+x% computed on the 16-bit
    // ALU) is disabled whenever $ERROR NUMERIC/OVERFLOW/ALL is active, so its
    // CSE is too - keeping the analysis context in lock-step with EmitAssign
    var checkedArithmetic = model.MetaStatements.Any(m =>
      m.Command.Equals("ERROR", StringComparison.OrdinalIgnoreCase)
      && m.Arguments.Count >= 2
      && m.Arguments[0].Text.ToUpperInvariant() is "NUMERIC" or "OVERFLOW" or "ALL"
      && m.Arguments[^1].Text.Equals("ON", StringComparison.OrdinalIgnoreCase));
    var state = new State(model, allowModular: !checkedArithmetic);
    state.Run(body);
    return state.Out;
  }

  private enum Mode { Integer, Modular }

  private sealed class State(SemanticModel model, bool allowModular) {
    public readonly Result Out = new();

    // key -> slot index (assigned the first time a key is reused; shared across runs)
    private readonly Dictionary<string, int> _slotOfKey = new(StringComparer.Ordinal);
    // key -> the inputs it reads, for write-invalidation
    private readonly Dictionary<string, HashSet<VariableSymbol>> _inputsOfKey = new(StringComparer.Ordinal);
    // stable small ids for variable symbols, used in the structural key
    private readonly Dictionary<VariableSymbol, int> _varId = new(ReferenceEqualityComparer.Instance);

    // live within the current straight-line run: key -> the node currently holding the value
    private Dictionary<string, Expression> _live = new(StringComparer.Ordinal);

    public void Run(IReadOnlyList<Statement> statements) => this.RunInheriting(statements, new(StringComparer.Ordinal));

    /// <summary>Runs a block starting from an inherited live cache (the dominating code's still-valid values).</summary>
    private void RunInheriting(IReadOnlyList<Statement> statements, Dictionary<string, Expression> inherited) {
      var savedLive = this._live;
      this._live = inherited;
      foreach (var statement in statements)
        this.Walk(statement);
      this._live = savedLive;
    }

    private void Walk(Statement statement) {
      switch (statement) {
        case AssignStmt a when this.IsStraightLineSafe(a):
          // a modular-int16 assignment computes its RHS on the 16-bit ALU
          // (EmitModularInt16); CSE there caches the modular value
          this.Register(a.Value, this.IsModularAssign(a) ? Mode.Modular : Mode.Integer);
          // index expressions on an array target are emitted via the normal path
          if (a.Target is CallOrIndexExpr { Arguments: { } targetArgs })
            foreach (var arg in targetArgs)
              this.Register(arg, Mode.Integer);
          this.InvalidateAfterWrite(a.Target);
          return;

        case PrintStmt p when this.IsStraightLinePrint(p):
          if (p.FileNumber is { } fn)
            this.Register(fn, Mode.Integer);
          foreach (var item in p.Items)
            if (item.Value is { } value)
              this.Register(value, Mode.Integer);
          return;

        case IncrDecrStmt id when id.Target is NameExpr && this.ScalarSymbolOf(id.Target) is { } target
            && (id.Amount == null || IsPure(id.Amount, model)):
          if (id.Amount is { } amount)
            this.Register(amount, Mode.Integer);
          this.Invalidate(target);
          return;

        case IfStmt iff when IsBarrierFree(iff.Condition, model) && iff.ElseIfs.All(e => IsBarrierFree(e.Condition, model)):
          // cross-block CSE: the code before the IF dominates every branch, so a value
          // computed before the IF is still available at each branch's start. The
          // (side-effect-free) conditions write nothing, so the inherited cache stays
          // valid; each branch invalidates a key incrementally when it writes that key's
          // inputs. Branch computations do not survive the merge, so the cache is cleared
          // afterwards. Branches emit through the mark-aware EmitExpression, so a reload
          // pairs with the unconditional pre-IF DEFINE.
          this.RunInheriting(iff.Then, new(this._live, StringComparer.Ordinal));
          foreach (var (_, elseIfBody) in iff.ElseIfs)
            this.RunInheriting(elseIfBody, new(this._live, StringComparer.Ordinal));
          if (iff.Else != null)
            this.RunInheriting(iff.Else, new(this._live, StringComparer.Ordinal));
          var ifBranches = new List<IReadOnlyList<Statement>> { iff.Then };
          foreach (var (_, elseIfBody) in iff.ElseIfs)
            ifBranches.Add(elseIfBody);
          if (iff.Else != null)
            ifBranches.Add(iff.Else);
          this.RetainPastMerge(ifBranches);
          return;

        case SelectStmt sel when IsBarrierFree(sel.Subject, model) && sel.Arms.All(SelectorsBarrierFree):
          // a SELECT join behaves like an IF merge: the subject is evaluated once and
          // dominates every arm; the (barrier-free) subject and CASE selectors write
          // nothing, so the inherited cache is valid in each arm and a value that no arm
          // overwrites flows past the merge - including the implicit "no arm matched" path
          foreach (var arm in sel.Arms)
            this.RunInheriting(arm.Body, new(this._live, StringComparer.Ordinal));
          this.RetainPastMerge(sel.Arms.Select(a => a.Body).ToList());
          return;

        default:
          // a barrier ends the run; sub-blocks are their own runs
          this._live.Clear();
          foreach (var block in ChildBlocks(statement))
            this.Run(block);
          this._live.Clear();
          return;
      }
    }

    /// <summary>Registers every cacheable subtree of <paramref name="e"/> bottom-up, marking define/use pairs.</summary>
    private void Register(Expression e, Mode mode) {
      if (!this.IsCacheable(e, mode)) {
        foreach (var child in Children(e))
          this.Register(child, mode);
        return;
      }

      var key = (mode == Mode.Modular ? "M" : "I") + this.Key(e);
      if (this._live.TryGetValue(key, out var definer)) {
        // reuse: the earlier node becomes the DEFINE, this one a USE (and we
        // do not descend - a USE never emits its children)
        var slot = this.SlotFor(key);
        this.Out.Marks[definer] = new(slot, IsDefine: true);
        this.Out.Marks[e] = new(slot, IsDefine: false);
        return;
      }

      // first occurrence: register children first (nested CSE), then go live
      foreach (var child in Children(e))
        this.Register(child, mode);
      this._live[key] = e;
      this._inputsOfKey[key] = Inputs(e, model);
    }

    /// <summary>A composite worth a slot: an integer-typed pure tree, or (modular mode) a float-typed +,-,* tree over 16-bit integral leaves.</summary>
    private bool IsCacheable(Expression e, Mode mode) {
      // redundant-load elimination: a repeated array-element read is a cacheable leaf
      // (integer mode only - modular trees never have an array read as a leaf)
      if (mode == Mode.Integer && CacheableArrayReadSymbol(e, model) != null)
        return true;
      if (e is not (BinaryExpr or UnaryExpr))
        return false;
      if (mode == Mode.Modular)
        return model.TypeOf(e) is ScalarType { IsFloat: true }
          && this.IsModularInt16Tree(e);
      return model.TypeOf(e) is ScalarType { IsFloat: false, ByteSize: <= 4 } && IsPure(e, model);
    }

    /// <summary>Replicates CodeGenerator.IsModularInt16Tree: a +,-,* (and unary negate) tree over 16-bit-or-narrower integral leaves.</summary>
    private bool IsModularInt16Tree(Expression e, int depth = 0) {
      if (depth > 16)
        return false;
      if (model.TypeOf(e) is ScalarType { IsFloat: false, ByteSize: <= 2 })
        return true;
      return e switch {
        UnaryExpr { Op: UnaryOp.Negate } u => this.IsModularInt16Tree(u.Operand, depth + 1),
        BinaryExpr { Op: BinaryOp.Add or BinaryOp.Subtract or BinaryOp.Multiply } b =>
          this.IsModularInt16Tree(b.Left, depth + 1) && this.IsModularInt16Tree(b.Right, depth + 1),
        _ => false,
      };
    }

    /// <summary>Exactly the EmitAssign condition that routes a store through EmitModularInt16.</summary>
    private bool IsModularAssign(AssignStmt a)
      => allowModular
        && model.TypeOf(a.Target) is ScalarType { IsFloat: false, ByteSize: <= 2 }
        && model.TypeOf(a.Value) is ScalarType { IsFloat: true }
        && this.IsModularInt16Tree(a.Value);

    private int SlotFor(string key) {
      if (this._slotOfKey.TryGetValue(key, out var slot))
        return slot;
      slot = this.Out.SlotCount++;
      this._slotOfKey[key] = slot;
      return slot;
    }

    private void InvalidateAfterWrite(Expression target) {
      // scalar target -> invalidate slots reading it; an array-element write -> invalidate
      // every cached read of that array (we can't prove the index differs); member/ptr
      // already routed through the barrier path by IsStraightLineSafe
      if (this.ScalarSymbolOf(target) is { } symbol)
        this.Invalidate(symbol);
      else if (target is CallOrIndexExpr && model.VariableBindings.TryGetValue(target, out var arr)
          && arr.Type is ArrayType)
        this.Invalidate(arr);
    }

    private void Invalidate(VariableSymbol symbol) {
      var stale = this._live
        .Where(kv => this._inputsOfKey.TryGetValue(kv.Key, out var ins) && ins.Contains(symbol))
        .Select(kv => kv.Key)
        .ToList();
      foreach (var key in stale)
        this._live.Remove(key);
    }

    /// <summary>
    /// Broader GVN: flow the inherited cache PAST the IF merge. A value computed
    /// (and DEFINEd) before the IF stays live afterwards as long as no branch can
    /// have overwritten its inputs. This is only sound when every branch is a flat,
    /// call-free straight line — then <see cref="CollectWrites"/> captures the exact
    /// set of scalars any branch may write; entries reading none of them survive.
    /// Otherwise (nested control, calls, anything that could write unseen) we clear.
    /// </summary>
    private void RetainPastMerge(List<IReadOnlyList<Statement>> branches) {
      foreach (var branch in branches)
        if (!this.IsRetainableBranch(branch)) {
          this._live.Clear();
          return;
        }

      // the merge falls through when no branch (or the implicit empty else) runs,
      // so a retained value must be untouched on EVERY path; take the union of writes
      var written = new HashSet<VariableSymbol>(ReferenceEqualityComparer.Instance);
      foreach (var branch in branches)
        CollectWrites(branch, written, model);
      foreach (var symbol in written)
        this.Invalidate(symbol);
    }

    /// <summary>
    /// A branch whose writes are fully captured by <see cref="CollectWrites"/>: only
    /// flat assignments / incr-decr / prints / metadata, every operand call-free, no
    /// nested control flow. A call or nested block could write inputs we never see.
    /// </summary>
    private bool IsRetainableBranch(IReadOnlyList<Statement> body) {
      foreach (var s in body)
        switch (s) {
          case AssignStmt a when IsBarrierFree(a.Value, model)
              && (a.Target is NameExpr || IsBarrierFree(a.Target, model)):
            break;
          case IncrDecrStmt id
              when (id.Target is NameExpr || (id.Target is CallOrIndexExpr && IsBarrierFree(id.Target, model)))
              && (id.Amount == null || IsPure(id.Amount, model)):
            break;
          case PrintStmt p
              when (p.FileNumber == null || IsBarrierFree(p.FileNumber, model))
              && p.Items.All(i => i.Value == null || IsBarrierFree(i.Value, model)):
            break;
          case MetaStmt or EquateStmt or DefTypeStmt or DataStmt:
            break;
          default:
            return false;
        }
      return true;
    }

    /// <summary>
    /// A CASE arm whose selector expressions are all call-free, so evaluating them (the
    /// comparisons the SELECT performs) writes nothing and can't invalidate the cache.
    /// </summary>
    private bool SelectorsBarrierFree(CaseArm arm) {
      foreach (var sel in arm.Selectors) {
        if (sel.Value != null && !IsBarrierFree(sel.Value, model))
          return false;
        if (sel.RangeUpper != null && !IsBarrierFree(sel.RangeUpper, model))
          return false;
      }
      return true;
    }

    private bool IsStraightLineSafe(AssignStmt a) {
      // RHS must be barrier-free; the target is either a pure scalar (we
      // invalidate it) or an array element with barrier-free indices
      if (!IsBarrierFree(a.Value, model))
        return false;
      switch (a.Target) {
        case NameExpr:
          return this.ScalarSymbolOf(a.Target) != null
            && model.TypeOf(a.Target) is ScalarType { IsFloat: false };
        case CallOrIndexExpr { Arguments: { } args } call
            when model.VariableBindings.ContainsKey(call):
          return args.All(arg => IsBarrierFree(arg, model));
        default:
          return false; // member/ptr stores may alias - barrier
      }
    }

    private bool IsStraightLinePrint(PrintStmt p)
      => !p.IsLPrint && p.UsingFormat == null
        && (p.FileNumber == null || IsBarrierFree(p.FileNumber, model))
        && p.Items.All(item => item.Value == null || IsBarrierFree(item.Value, model));

    private VariableSymbol? ScalarSymbolOf(Expression e)
      => e is NameExpr && model.VariableBindings.TryGetValue(e, out var s)
        && s.Type is ScalarType && s.Storage is not VariableStorage.Static
        ? s : null;

    private string Key(Expression e) {
      var sb = new StringBuilder();
      this.AppendKey(sb, e);
      return sb.ToString();
    }

    private void AppendKey(StringBuilder sb, Expression e) {
      switch (e) {
        case IntegerLiteralExpr i:
          sb.Append('#').Append(i.Value).Append(';');
          break;
        case NamedConstantExpr c:
          sb.Append('#').Append(model.Equates.TryGetValue(c.Name, out var v) ? v.AsInteger : 0).Append(';');
          break;
        case NameExpr when model.VariableBindings.TryGetValue(e, out var sym):
          sb.Append('v').Append(this.IdOf(sym)).Append(';');
          break;
        case UnaryExpr u:
          sb.Append('u').Append((int)u.Op).Append('(');
          this.AppendKey(sb, u.Operand);
          sb.Append(')');
          break;
        case BinaryExpr b:
          sb.Append('b').Append((int)b.Op).Append('(');
          this.AppendKey(sb, b.Left);
          this.AppendKey(sb, b.Right);
          sb.Append(')');
          break;
        case CallOrIndexExpr when CacheableArrayReadSymbol(e, model) is { } arr:
          sb.Append('a').Append(this.IdOf(arr)).Append('[');
          foreach (var arg in ((CallOrIndexExpr)e).Arguments)
            this.AppendKey(sb, arg);
          sb.Append(']');
          break;
        default:
          sb.Append('?').Append(e.GetHashCode()).Append(';');
          break;
      }
    }

    private int IdOf(VariableSymbol symbol) {
      if (this._varId.TryGetValue(symbol, out var id))
        return id;
      id = this._varId.Count;
      this._varId[symbol] = id;
      return id;
    }
  }

  // ---- pure static predicates shared with the (identical) emission walk ----

  /// <summary>True for an expression built only from scalar integer reads, literals, equates and integer operators.</summary>
  private static bool IsPure(Expression e, SemanticModel model) => e switch {
    IntegerLiteralExpr => true,
    NamedConstantExpr c => !(model.Equates.TryGetValue(c.Name, out var v) && v.Text != null),
    NameExpr n => !model.IntrinsicBindings.ContainsKey(n)
      && model.VariableBindings.TryGetValue(n, out var s)
      && s.Type is ScalarType { IsFloat: false },
    UnaryExpr { Op: UnaryOp.Negate or UnaryOp.Not } u => IsPure(u.Operand, model),
    BinaryExpr b => b.Op is BinaryOp.Add or BinaryOp.Subtract or BinaryOp.Multiply
        or BinaryOp.IntegerDivide or BinaryOp.Modulo
        or BinaryOp.And or BinaryOp.Or or BinaryOp.Xor or BinaryOp.Eqv or BinaryOp.Imp
        or BinaryOp.ShiftLeft or BinaryOp.ShiftRightArith or BinaryOp.ShiftRightLogical
        or BinaryOp.RotateLeft or BinaryOp.RotateRight
      && IsPure(b.Left, model) && IsPure(b.Right, model),
    _ => false,
  };

  /// <summary>An expression that calls nothing, dereferences nothing and reads no volatile intrinsic.</summary>
  private static bool IsBarrierFree(Expression e, SemanticModel model) {
    switch (e) {
      case IntegerLiteralExpr or FloatLiteralExpr or StringLiteralExpr or NamedConstantExpr:
        return true;
      case NameExpr n:
        return !model.IntrinsicBindings.ContainsKey(n);
      case UnaryExpr u:
        return IsBarrierFree(u.Operand, model);
      case BinaryExpr b:
        return IsBarrierFree(b.Left, model) && IsBarrierFree(b.Right, model);
      case ByValArgExpr bv:
        return IsBarrierFree(bv.Value, model);
      case FileNumberExpr f:
        return IsBarrierFree(f.Number, model);
      case CallOrIndexExpr call when model.VariableBindings.ContainsKey(call):
        // a variable array read - barrier-free iff its indices are
        return call.Arguments.All(a => IsBarrierFree(a, model));
      default:
        return false; // function/intrinsic calls, pointer deref, member access
    }
  }

  private static HashSet<VariableSymbol> Inputs(Expression e, SemanticModel model) {
    var set = new HashSet<VariableSymbol>(ReferenceEqualityComparer.Instance);
    Collect(e);
    return set;

    void Collect(Expression node) {
      if (node is NameExpr && model.VariableBindings.TryGetValue(node, out var sym))
        set.Add(sym);
      // an array-element read also depends on the array itself, so any write to the
      // array (any element) invalidates the cached value
      if (CacheableArrayReadSymbol(node, model) is { } arr)
        set.Add(arr);
      foreach (var child in Children(node))
        Collect(child);
    }
  }

  private static IEnumerable<Expression> Children(Expression e) => e switch {
    UnaryExpr u => [u.Operand],
    BinaryExpr b => [b.Left, b.Right],
    CallOrIndexExpr c => c.Arguments,
    ByValArgExpr v => [v.Value],
    FileNumberExpr f => [f.Number],
    _ => [],
  };

  private static IEnumerable<IReadOnlyList<Statement>> ChildBlocks(Statement s) {
    switch (s) {
      case IfStmt i:
        yield return i.Then;
        foreach (var (_, body) in i.ElseIfs)
          yield return body;
        if (i.Else != null)
          yield return i.Else;
        break;
      case SelectStmt sel:
        foreach (var arm in sel.Arms)
          yield return arm.Body;
        break;
      case ForStmt f:
        yield return f.Body;
        break;
      case DoLoopStmt d:
        yield return d.Body;
        break;
    }
  }
}
