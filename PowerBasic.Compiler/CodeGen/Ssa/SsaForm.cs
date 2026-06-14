using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen.Ssa;

/// <summary>How an <see cref="SsaValue"/> obtains its value.</summary>
public enum SsaDefKind {
  /// <summary>The variable's reaching value at procedure entry: PB zero-initializes locals/globals.</summary>
  EntryZero,
  /// <summary>An <c>x = expr</c> assignment; <see cref="SsaValue.DefExpr"/> is the right-hand side.</summary>
  Assign,
  /// <summary>An <c>INCR</c>/<c>DECR</c>; new value is the prior version +/- the amount.</summary>
  IncrDecr,
  /// <summary>A phi at a control-flow merge; <see cref="SsaValue.PhiInputs"/> are the per-predecessor versions.</summary>
  Phi,
}

/// <summary>A single static-single-assignment version of a tracked scalar variable.</summary>
public sealed class SsaValue {
  public SsaValue(int id, VariableSymbol variable, SsaDefKind kind, BasicBlock block) {
    this.Id = id;
    this.Variable = variable;
    this.Kind = kind;
    this.Block = block;
  }

  public int Id { get; }
  public VariableSymbol Variable { get; }
  public SsaDefKind Kind { get; }
  public BasicBlock Block { get; }

  /// <summary><see cref="SsaDefKind.Assign"/>: the RHS expression.</summary>
  public Expression? DefExpr { get; set; }

  /// <summary><see cref="SsaDefKind.IncrDecr"/>: the prior version, the (optional) amount, and the direction.</summary>
  public SsaValue? IncrBase { get; set; }
  public Expression? IncrAmount { get; set; }
  public bool IncrUp { get; set; }

  /// <summary><see cref="SsaDefKind.Phi"/>: one (predecessor, incoming version) per CFG predecessor.</summary>
  public List<(BasicBlock Pred, SsaValue Value)> PhiInputs { get; } = [];

  public override string ToString() => $"{this.Variable.Name}#{this.Id}({this.Kind})";
}

/// <summary>
/// Static single assignment form over an acyclic structured region
/// (<see cref="ControlFlowGraph"/>). Only non-escaping integral scalar
/// locals/globals are versioned; every read of such a variable is mapped to the
/// version reaching it (<see cref="UseVersions"/>). This is the IR the SCCP pass
/// (and later GVN/range passes) run on - the docs/PB36.md mid-end foundation.
/// Construction is conservative: anything that could alias or escape a tracked
/// variable removes it from tracking, so the SSA is always sound.
/// </summary>
public sealed class SsaForm {
  private SsaForm(ControlFlowGraph cfg, DominatorTree dom, IReadOnlyList<SsaValue> values, IReadOnlyDictionary<NameExpr, SsaValue> useVersions) {
    this.Cfg = cfg;
    this.Dominators = dom;
    this.Values = values;
    this.UseVersions = useVersions;
  }

  public ControlFlowGraph Cfg { get; }
  public DominatorTree Dominators { get; }
  public IReadOnlyList<SsaValue> Values { get; }

  /// <summary>For every tracked-variable read NameExpr, the SSA version reaching it.</summary>
  public IReadOnlyDictionary<NameExpr, SsaValue> UseVersions { get; }

  /// <summary>
  /// Builds SSA for <paramref name="cfg"/>. Returns null when no scalar is
  /// trackable (nothing to analyze). Never produces unsound versions: a variable
  /// is tracked only when every reference is a plain read or whole assignment of
  /// a non-escaping, non-shared integral scalar.
  /// </summary>
  public static SsaForm? TryBuild(SemanticModel model, ControlFlowGraph cfg) {
    var tracked = FindTrackable(model, cfg);
    if (tracked.Count == 0)
      return null;

    var dom = DominatorTree.Build(cfg);
    var reachable = new HashSet<BasicBlock>(dom.ReversePostorder, ReferenceEqualityComparer.Instance);

    // ---- phi placement: iterated dominance frontier of each variable's defs ----
    var phiBlocks = new Dictionary<VariableSymbol, HashSet<BasicBlock>>(ReferenceEqualityComparer.Instance);
    foreach (var v in tracked)
      phiBlocks[v] = new(ReferenceEqualityComparer.Instance);
    foreach (var v in tracked) {
      var work = new Queue<BasicBlock>();
      var placed = phiBlocks[v];
      var seenDef = new HashSet<BasicBlock>(ReferenceEqualityComparer.Instance);
      foreach (var block in dom.ReversePostorder)
        if (DefinesIn(block, v, model)) {
          seenDef.Add(block);
          work.Enqueue(block);
        }
      while (work.Count > 0) {
        var b = work.Dequeue();
        foreach (var df in dom.FrontierOf(b)) {
          if (!reachable.Contains(df) || !placed.Add(df))
            continue;
          if (seenDef.Add(df))
            work.Enqueue(df);
        }
      }
    }

    // ---- renaming over the dominator tree --------------------------------------
    var builder = new Renamer(model, cfg, dom, tracked, phiBlocks, reachable);
    builder.Run();
    return new(cfg, dom, builder.Values, builder.UseVersions);
  }

  /// <summary>True when <paramref name="block"/> contains a whole-variable assignment/INCR of <paramref name="v"/>.</summary>
  private static bool DefinesIn(BasicBlock block, VariableSymbol v, SemanticModel model) {
    foreach (var s in block.Statements)
      if (DefTarget(s, model) is { } def && ReferenceEquals(def, v))
        return true;
    return false;
  }

  /// <summary>The variable a statement assigns as a whole (NameExpr target), else null.</summary>
  internal static VariableSymbol? DefTarget(Statement s, SemanticModel model) => s switch {
    AssignStmt { Target: NameExpr t } when model.VariableBindings.TryGetValue(t, out var sym) => sym,
    IncrDecrStmt { Target: NameExpr t } when model.VariableBindings.TryGetValue(t, out var sym) => sym,
    _ => null,
  };

  #region trackability / escape analysis

  /// <summary>
  /// Scalar non-float Local/Global non-shared variables every one of whose
  /// references is a plain read or whole assignment - never a call/index/member/
  /// pointer argument (BYREF or address escape) and never in an opaque statement.
  /// </summary>
  private static HashSet<VariableSymbol> FindTrackable(SemanticModel model, ControlFlowGraph cfg) {
    var candidates = new HashSet<VariableSymbol>(ReferenceEqualityComparer.Instance);
    var escaped = new HashSet<VariableSymbol>(ReferenceEqualityComparer.Instance);

    void Read(Expression? e, bool dangerous) {
      switch (e) {
        case null:
          return;
        case NameExpr name:
          if (model.VariableBindings.TryGetValue(name, out var sym)) {
            Consider(sym, candidates, escaped);
            if (dangerous)
              escaped.Add(sym);
          }
          break;
        case UnaryExpr u:
          Read(u.Operand, dangerous);
          break;
        case BinaryExpr b:
          Read(b.Left, dangerous);
          Read(b.Right, dangerous);
          break;
        case CallOrIndexExpr call:
          foreach (var a in call.Arguments)
            Read(a, true); // user-call BYREF / address intrinsic / array index - all escape a scalar
          break;
        case IndexExpr index:
          Read(index.Target, true);
          foreach (var a in index.Arguments)
            Read(a, true);
          break;
        case MemberExpr m:
          Read(m.Target, true);
          break;
        case PtrDerefExpr p:
          Read(p.Pointer, true);
          Read(p.Index, true);
          break;
        case ByValArgExpr v:
          Read(v.Value, true);
          break;
        case AnyMatchExpr am:
          Read(am.Value, true);
          break;
        case FileNumberExpr f:
          Read(f.Number, true);
          break;
        // literals / named constants: no variable
      }
    }

    foreach (var block in cfg.Blocks) {
      foreach (var s in block.Statements)
        switch (s) {
          case AssignStmt a:
            if (a.Target is NameExpr) // whole-variable def: target is not a read
              Consider(model, a.Target, candidates, escaped);
            else
              Read(a.Target, true); // arr(i)= / member= : the target sub-exprs are uses
            Read(a.Value, false);
            break;
          case IncrDecrStmt id:
            if (id.Target is NameExpr)
              Consider(model, id.Target, candidates, escaped);
            else
              Read(id.Target, true);
            Read(id.Amount, false);
            break;
          case PrintStmt p:
            Read(p.FileNumber, false);
            Read(p.UsingFormat, false);
            foreach (var item in p.Items)
              Read(item.Value, false);
            break;
          default:
            // any other statement is opaque to scalar tracking: escape every
            // candidate it mentions (walk its expressions as dangerous)
            foreach (var e in StatementExpressions(s))
              Read(e, true);
            break;
        }
      Read(block.Condition, false);
    }

    candidates.ExceptWith(escaped);
    return candidates;
  }

  private static void Consider(SemanticModel model, Expression nameExpr, HashSet<VariableSymbol> candidates, HashSet<VariableSymbol> escaped) {
    if (model.VariableBindings.TryGetValue(nameExpr, out var sym))
      Consider(sym, candidates, escaped);
  }

  private static void Consider(VariableSymbol sym, HashSet<VariableSymbol> candidates, HashSet<VariableSymbol> escaped) {
    if (IsTrackableShape(sym))
      candidates.Add(sym);
    else
      escaped.Add(sym);
  }

  private static bool IsTrackableShape(VariableSymbol sym)
    => sym.Type is ScalarType { IsFloat: false, ByteSize: <= 4 }
      && sym.Storage is VariableStorage.Local or VariableStorage.Global
      && !sym.IsShared;

  /// <summary>All top-level expressions of an opaque statement (for escape scanning); conservative supersets are fine.</summary>
  private static IEnumerable<Expression> StatementExpressions(Statement s) {
    switch (s) {
      case CallStmt c:
        foreach (var a in c.Arguments)
          yield return a;
        break;
      case CommandStmt cmd:
        foreach (var a in cmd.Arguments)
          if (a != null)
            yield return a;
        break;
      case MidAssignStmt m:
        yield return m.Target; yield return m.Start;
        if (m.Length != null) yield return m.Length;
        yield return m.Value;
        break;
      case LsetRsetStmt l:
        yield return l.Target; yield return l.Value;
        break;
      case AscAssignStmt a:
        yield return a.Target;
        if (a.Index != null) yield return a.Index;
        yield return a.Value;
        break;
      case BitStmt b:
        yield return b.Target; yield return b.Bit;
        break;
      case StdOutStmt o when o.Value != null:
        yield return o.Value;
        break;
      case WriteStmt w:
        if (w.FileNumber != null) yield return w.FileNumber;
        foreach (var i in w.Items) yield return i;
        break;
      case OpenStmt op:
        yield return op.FileName; yield return op.FileNumber;
        if (op.RecordLength != null) yield return op.RecordLength;
        break;
      case CloseStmt cl:
        foreach (var f in cl.FileNumbers) yield return f;
        break;
      case SeekStmt sk:
        yield return sk.FileNumber; yield return sk.Target;
        break;
      case DefSegStmt ds when ds.Segment != null:
        yield return ds.Segment;
        break;
      case DimStmt dim:
        foreach (var v in dim.Variables)
          foreach (var (lo, hi) in v.ArrayBounds ?? []) {
            if (lo != null) yield return lo;
            yield return hi;
          }
        break;
    }
  }

  #endregion

  /// <summary>Standard dominator-tree SSA renaming with per-variable version stacks.</summary>
  private sealed class Renamer {
    private readonly SemanticModel _model;
    private readonly ControlFlowGraph _cfg;
    private readonly DominatorTree _dom;
    private readonly HashSet<VariableSymbol> _tracked;
    private readonly Dictionary<VariableSymbol, HashSet<BasicBlock>> _phiBlocks;
    private readonly HashSet<BasicBlock> _reachable;
    private readonly Dictionary<VariableSymbol, Stack<SsaValue>> _stacks = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<BasicBlock, List<SsaValue>> _blockPhis = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<BasicBlock, List<BasicBlock>> _domChildren = new(ReferenceEqualityComparer.Instance);
    private int _nextId;

    public List<SsaValue> Values { get; } = [];
    public Dictionary<NameExpr, SsaValue> UseVersions { get; } = new(ReferenceEqualityComparer.Instance);

    public Renamer(SemanticModel model, ControlFlowGraph cfg, DominatorTree dom, HashSet<VariableSymbol> tracked,
        Dictionary<VariableSymbol, HashSet<BasicBlock>> phiBlocks, HashSet<BasicBlock> reachable) {
      this._model = model;
      this._cfg = cfg;
      this._dom = dom;
      this._tracked = tracked;
      this._phiBlocks = phiBlocks;
      this._reachable = reachable;
    }

    public void Run() {
      foreach (var v in this._tracked)
        this._stacks[v] = new();

      // dominator-tree children
      foreach (var block in this._dom.ReversePostorder)
        this._domChildren[block] = [];
      foreach (var block in this._dom.ReversePostorder) {
        var idom = this._dom.ImmediateDominatorOf(block);
        if (!ReferenceEquals(idom, block))
          this._domChildren[idom].Add(block);
      }

      // materialize phi values per block
      foreach (var block in this._dom.ReversePostorder)
        this._blockPhis[block] = [];
      foreach (var (v, blocks) in this._phiBlocks)
        foreach (var b in blocks)
          this._blockPhis[b].Add(this.NewValue(v, SsaDefKind.Phi, b));

      // seed entry with the zero-initialized version of every tracked variable
      foreach (var v in this._tracked) {
        var zero = this.NewValue(v, SsaDefKind.EntryZero, this._cfg.Entry);
        this._stacks[v].Push(zero);
      }

      this.Rename(this._cfg.Entry);
    }

    private SsaValue NewValue(VariableSymbol v, SsaDefKind kind, BasicBlock block) {
      var value = new SsaValue(this._nextId++, v, kind, block);
      this.Values.Add(value);
      return value;
    }

    private SsaValue Top(VariableSymbol v) => this._stacks[v].Peek();

    private void Rename(BasicBlock block) {
      var pushed = new List<VariableSymbol>();

      foreach (var phi in this._blockPhis[block]) {
        this._stacks[phi.Variable].Push(phi);
        pushed.Add(phi.Variable);
      }

      foreach (var stmt in block.Statements)
        this.RenameStatement(stmt, pushed);

      this.RecordUses(block.Condition);

      // fill phi operands of CFG successors
      foreach (var succ in block.Successors)
        foreach (var phi in this._blockPhis[succ])
          phi.PhiInputs.Add((block, this.Top(phi.Variable)));

      foreach (var child in this._domChildren[block])
        this.Rename(child);

      for (var i = pushed.Count - 1; i >= 0; --i)
        this._stacks[pushed[i]].Pop();
    }

    private void RenameStatement(Statement stmt, List<VariableSymbol> pushed) {
      switch (stmt) {
        case AssignStmt { Target: NameExpr target } a when this.IsTracked(target, out var sym):
          this.RecordUses(a.Value);
          var assigned = this.NewValue(sym, SsaDefKind.Assign, this._cfg.Entry);
          assigned.DefExpr = a.Value;
          this._stacks[sym].Push(assigned);
          pushed.Add(sym);
          break;

        case IncrDecrStmt { Target: NameExpr target } id when this.IsTracked(target, out var sym):
          var prior = this.Top(sym);          // INCR reads the old version first
          this.UseVersions[target] = prior;
          this.RecordUses(id.Amount);
          var next = this.NewValue(sym, SsaDefKind.IncrDecr, this._cfg.Entry);
          next.IncrBase = prior;
          next.IncrAmount = id.Amount;
          next.IncrUp = id.Increment;
          this._stacks[sym].Push(next);
          pushed.Add(sym);
          break;

        case AssignStmt a:
          this.RecordUses(a.Target); // arr(i)= : indices are uses
          this.RecordUses(a.Value);
          break;

        case IncrDecrStmt id:
          this.RecordUses(id.Target);
          this.RecordUses(id.Amount);
          break;

        case PrintStmt p:
          this.RecordUses(p.FileNumber);
          this.RecordUses(p.UsingFormat);
          foreach (var item in p.Items)
            this.RecordUses(item.Value);
          break;

        default:
          foreach (var e in StatementExpressions(stmt))
            this.RecordUses(e);
          break;
      }
    }

    private bool IsTracked(NameExpr name, out VariableSymbol sym) {
      if (this._model.VariableBindings.TryGetValue(name, out sym!) && this._tracked.Contains(sym))
        return true;
      sym = null!;
      return false;
    }

    /// <summary>Records the reaching version for every tracked-variable read in an expression.</summary>
    private void RecordUses(Expression? e) {
      switch (e) {
        case null:
          return;
        case NameExpr name when this.IsTracked(name, out var sym):
          this.UseVersions[name] = this.Top(sym);
          break;
        case UnaryExpr u:
          this.RecordUses(u.Operand);
          break;
        case BinaryExpr b:
          this.RecordUses(b.Left);
          this.RecordUses(b.Right);
          break;
        case CallOrIndexExpr call:
          foreach (var a in call.Arguments)
            this.RecordUses(a);
          break;
        case IndexExpr index:
          this.RecordUses(index.Target);
          foreach (var a in index.Arguments)
            this.RecordUses(a);
          break;
        case MemberExpr m:
          this.RecordUses(m.Target);
          break;
        case PtrDerefExpr p:
          this.RecordUses(p.Pointer);
          this.RecordUses(p.Index);
          break;
        case ByValArgExpr v:
          this.RecordUses(v.Value);
          break;
        case AnyMatchExpr am:
          this.RecordUses(am.Value);
          break;
        case FileNumberExpr f:
          this.RecordUses(f.Number);
          break;
      }
    }
  }
}
