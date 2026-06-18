using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen.Ssa;

/// <summary>
/// Backward live-variable analysis over a <see cref="ControlFlowGraph"/> for the
/// trackable scalar variables (the same non-escaping scalar-integer locals/globals
/// the SSA pass considers: <see cref="SsaForm.FindTrackable"/>). For each such
/// variable it computes the per-block live-in / live-out sets, the interference
/// graph (which pairs are ever simultaneously live), and whether the live range
/// crosses a CALL/command statement (which clobbers every GP register in our ABI).
///
/// This is the data-flow prerequisite for a register allocator that keeps a hot
/// scalar in SI/DI across an arbitrary region (docs/PB36.md O5, "generalize beyond
/// the FOR-loop shape"): two variables may share a register only if they do not
/// interfere, and a variable may stay resident only if its range crosses no call.
///
/// Because the trackable set excludes every variable that appears in an opaque
/// statement (a call/index/member/pointer argument escapes it - see FindTrackable),
/// every read of a tracked variable occurs in an AssignStmt value, an IncrDecrStmt,
/// a PrintStmt item, an IF/loop condition or a FOR bound - all handled here - so the
/// use/def sets are exact and the analysis is sound. ANALYSIS ONLY: nothing here
/// changes code generation; it is consumed by a later allocator increment.
/// </summary>
public sealed class ScalarLiveness {
  private readonly Dictionary<BasicBlock, HashSet<VariableSymbol>> _liveIn;
  private readonly Dictionary<BasicBlock, HashSet<VariableSymbol>> _liveOut;
  private readonly IReadOnlyDictionary<VariableSymbol, int> _id;
  private readonly HashSet<(int, int)> _interferes;
  private readonly HashSet<VariableSymbol> _crossesCall;

  private ScalarLiveness(
      IReadOnlyCollection<VariableSymbol> variables,
      IReadOnlyDictionary<VariableSymbol, int> id,
      Dictionary<BasicBlock, HashSet<VariableSymbol>> liveIn,
      Dictionary<BasicBlock, HashSet<VariableSymbol>> liveOut,
      HashSet<(int, int)> interferes,
      HashSet<VariableSymbol> crossesCall) {
    this.Variables = variables;
    this._id = id;
    this._liveIn = liveIn;
    this._liveOut = liveOut;
    this._interferes = interferes;
    this._crossesCall = crossesCall;
  }

  /// <summary>The trackable scalar variables the analysis covers.</summary>
  public IReadOnlyCollection<VariableSymbol> Variables { get; }

  /// <summary>Variables live on entry to <paramref name="block"/>.</summary>
  public IReadOnlySet<VariableSymbol> LiveIn(BasicBlock block) =>
    this._liveIn.TryGetValue(block, out var set) ? set : EmptySet;

  /// <summary>Variables live on exit from <paramref name="block"/> (the union of successors' live-in).</summary>
  public IReadOnlySet<VariableSymbol> LiveOut(BasicBlock block) =>
    this._liveOut.TryGetValue(block, out var set) ? set : EmptySet;

  /// <summary>True when <paramref name="a"/> and <paramref name="b"/> are ever simultaneously live, so they cannot share a register.</summary>
  public bool Interferes(VariableSymbol a, VariableSymbol b) =>
    !ReferenceEquals(a, b)
    && this._id.TryGetValue(a, out var ia) && this._id.TryGetValue(b, out var ib)
    && this._interferes.Contains(ia < ib ? (ia, ib) : (ib, ia));

  /// <summary>True when <paramref name="v"/> is live across a CALL/command statement (which clobbers every GP register), so it cannot stay register-resident over its whole range.</summary>
  public bool CrossesCall(VariableSymbol v) => this._crossesCall.Contains(v);

  private static readonly HashSet<VariableSymbol> EmptySet = new(ReferenceEqualityComparer.Instance);

  /// <summary>Computes liveness, interference and call-crossing over the trackable scalars of <paramref name="cfg"/>.</summary>
  public static ScalarLiveness Compute(ControlFlowGraph cfg, SemanticModel model) {
    var tracked = SsaForm.FindTrackable(model, cfg, null);
    var id = new Dictionary<VariableSymbol, int>(ReferenceEqualityComparer.Instance);
    foreach (var v in tracked)
      id[v] = id.Count;

    // per-block gen (used before any def) / kill (defined) sets over tracked vars
    var gen = new Dictionary<BasicBlock, HashSet<VariableSymbol>>();
    var kill = new Dictionary<BasicBlock, HashSet<VariableSymbol>>();
    foreach (var block in cfg.Blocks) {
      var (g, k) = BlockGenKill(block, tracked, model);
      gen[block] = g;
      kill[block] = k;
    }

    var liveIn = new Dictionary<BasicBlock, HashSet<VariableSymbol>>();
    var liveOut = new Dictionary<BasicBlock, HashSet<VariableSymbol>>();
    foreach (var block in cfg.Blocks) {
      liveIn[block] = new(ReferenceEqualityComparer.Instance);
      liveOut[block] = new(ReferenceEqualityComparer.Instance);
    }

    // backward fixpoint: liveOut(b) = U liveIn(succ); liveIn(b) = gen(b) U (liveOut(b) - kill(b))
    bool changed;
    do {
      changed = false;
      for (var i = cfg.Blocks.Count - 1; i >= 0; --i) {
        var block = cfg.Blocks[i];
        var outSet = liveOut[block];
        foreach (var succ in block.Successors)
          foreach (var v in liveIn[succ])
            changed |= outSet.Add(v);

        var inSet = liveIn[block];
        foreach (var v in outSet)
          if (!kill[block].Contains(v))
            changed |= inSet.Add(v);
        foreach (var v in gen[block])
          changed |= inSet.Add(v);
      }
    } while (changed);

    // a second pass refines interference and call-crossing at every program point
    var interferes = new HashSet<(int, int)>();
    var crossesCall = new HashSet<VariableSymbol>(ReferenceEqualityComparer.Instance);
    foreach (var block in cfg.Blocks)
      RefinePerPoint(block, tracked, model, id, liveOut[block], interferes, crossesCall);

    return new(tracked, id, liveIn, liveOut, interferes, crossesCall);
  }

  /// <summary>
  /// gen = variables read before being defined in the block (incl. the trailing
  /// condition / FOR-bound reads, which execute after the statements); kill =
  /// variables wholly assigned in the block.
  /// </summary>
  private static (HashSet<VariableSymbol> Gen, HashSet<VariableSymbol> Kill) BlockGenKill(
      BasicBlock block, HashSet<VariableSymbol> tracked, SemanticModel model) {
    var gen = new HashSet<VariableSymbol>(ReferenceEqualityComparer.Instance);
    var kill = new HashSet<VariableSymbol>(ReferenceEqualityComparer.Instance);

    // process forward so a use after a def in the same block is not counted in gen
    foreach (var s in block.Statements) {
      foreach (var u in Uses(s, model))
        if (tracked.Contains(u) && !kill.Contains(u))
          gen.Add(u);
      if (SsaForm.DefTarget(s, model) is { } def && tracked.Contains(def))
        kill.Add(def);
    }
    // the branch condition and FOR bounds are evaluated last; their reads are uses
    // unless a prior statement in the block already defined the variable
    foreach (var e in TrailingReads(block))
      foreach (var u in VarsOf(e, model))
        if (tracked.Contains(u) && !kill.Contains(u))
          gen.Add(u);

    return (gen, kill);
  }

  /// <summary>
  /// Walks the block backward from <paramref name="liveAtEnd"/>, recording for every
  /// pair simultaneously live that they interfere, and for every variable live across
  /// a CALL/command statement that it crosses a call.
  /// </summary>
  private static void RefinePerPoint(
      BasicBlock block, HashSet<VariableSymbol> tracked, SemanticModel model,
      IReadOnlyDictionary<VariableSymbol, int> id,
      HashSet<VariableSymbol> liveAtEnd,
      HashSet<(int, int)> interferes, HashSet<VariableSymbol> crossesCall) {
    var live = new HashSet<VariableSymbol>(liveAtEnd, ReferenceEqualityComparer.Instance);

    // the trailing condition / FOR-bound reads are live just before the branch
    foreach (var e in TrailingReads(block))
      foreach (var u in VarsOf(e, model))
        if (tracked.Contains(u))
          live.Add(u);
    MarkInterference(live, id, interferes);

    for (var i = block.Statements.Count - 1; i >= 0; --i) {
      var s = block.Statements[i];
      // `live` here is the set live AFTER statement s
      if (IsCallLike(s))
        foreach (var v in live)
          crossesCall.Add(v);

      if (SsaForm.DefTarget(s, model) is { } def && tracked.Contains(def))
        live.Remove(def);
      foreach (var u in Uses(s, model))
        if (tracked.Contains(u))
          live.Add(u);
      MarkInterference(live, id, interferes);
    }
  }

  private static void MarkInterference(HashSet<VariableSymbol> live, IReadOnlyDictionary<VariableSymbol, int> id, HashSet<(int, int)> interferes) {
    if (live.Count < 2)
      return;
    var ids = live.Select(v => id[v]).ToArray();
    for (var i = 0; i < ids.Length; ++i)
      for (var j = i + 1; j < ids.Length; ++j)
        interferes.Add(ids[i] < ids[j] ? (ids[i], ids[j]) : (ids[j], ids[i]));
  }

  /// <summary>A CALL/command statement clobbers every GP register in our internal ABI.</summary>
  private static bool IsCallLike(Statement s) => s is CallStmt or CommandStmt;

  /// <summary>The variables a statement reads (excluding a whole-variable assignment target, which is a def not a use; an INCR target is both).</summary>
  private static IEnumerable<VariableSymbol> Uses(Statement s, SemanticModel model) {
    switch (s) {
      case AssignStmt { Target: NameExpr } a:
        return VarsOf(a.Value, model);
      case AssignStmt a: // arr(i)= / member= : the target sub-expressions are reads too
        return VarsOf(a.Target, model).Concat(VarsOf(a.Value, model));
      case IncrDecrStmt { Target: NameExpr } id: // INCR reads and writes the target
        return VarsOf(id.Target, model).Concat(VarsOf(id.Amount, model));
      case IncrDecrStmt id:
        return VarsOf(id.Target, model).Concat(VarsOf(id.Amount, model));
      case PrintStmt p:
        return VarsOf(p.FileNumber, model)
          .Concat(VarsOf(p.UsingFormat, model))
          .Concat(p.Items.SelectMany(item => VarsOf(item.Value, model)));
      default:
        // any other statement kind admitted into a CFG block (OPEN/CLOSE/WRITE/...)
        // contains no tracked variable - they all escape it - so it has no tracked uses
        return [];
    }
  }

  /// <summary>The reads evaluated at the block's exit branch: an IF/loop condition and FOR/SELECT bound expressions.</summary>
  private static IEnumerable<Expression> TrailingReads(BasicBlock block) {
    if (block.Condition != null)
      yield return block.Condition;
    foreach (var e in block.ExtraReads)
      yield return e;
  }

  /// <summary>Every tracked-shaped variable referenced in an expression tree (recursively).</summary>
  private static IEnumerable<VariableSymbol> VarsOf(Expression? e, SemanticModel model) {
    if (e == null)
      yield break;
    if (e is NameExpr name && model.VariableBindings.TryGetValue(name, out var sym))
      yield return sym;
    foreach (var child in AstQuery.Subexpressions(e))
      foreach (var v in VarsOf(child, model))
        yield return v;
  }
}
