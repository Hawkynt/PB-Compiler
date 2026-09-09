namespace PowerBasic.Compiler.Ir;

/// <summary>
/// Dominator tree and dominance frontiers over an <see cref="IrFunction"/>'s CFG,
/// computed with the Cooper-Harvey-Kennedy iterative algorithm and Cytron's frontier
/// walk. Reused by the verifier (operands must dominate their uses) and by mem2reg
/// (phi placement at iterated dominance frontiers).
/// </summary>
public sealed class IrDominators {

  private readonly Dictionary<IrBasicBlock, IrBasicBlock> _idom = new(ReferenceEqualityComparer.Instance);
  private readonly Dictionary<IrBasicBlock, int> _rpoIndex = new(ReferenceEqualityComparer.Instance);
  private readonly Dictionary<IrBasicBlock, HashSet<IrBasicBlock>> _frontier = new(ReferenceEqualityComparer.Instance);

  /// <summary>The reachable blocks in reverse postorder (entry first).</summary>
  public IReadOnlyList<IrBasicBlock> ReversePostorder { get; }

  private IrDominators(IrFunction fn) {
    this.ReversePostorder = ComputeReversePostorder(fn.Entry!);
    for (var i = 0; i < this.ReversePostorder.Count; ++i)
      this._rpoIndex[this.ReversePostorder[i]] = i;
    this.ComputeIdoms();
    this.ComputeFrontiers();
  }

  /// <summary>Builds dominators for a function with a body; returns null for a declaration.</summary>
  public static IrDominators? Build(IrFunction fn) => fn.Entry is null ? null : new IrDominators(fn);

  /// <summary>The immediate dominator of a block (the entry's idom is itself).</summary>
  public IrBasicBlock? ImmediateDominatorOf(IrBasicBlock block) =>
    this._idom.GetValueOrDefault(block);

  /// <summary>The dominance frontier of a block (where its dominance stops).</summary>
  public IReadOnlyCollection<IrBasicBlock> FrontierOf(IrBasicBlock block) =>
    this._frontier.TryGetValue(block, out var f) ? f : [];

  /// <summary>True if <paramref name="a"/> dominates <paramref name="b"/> (every path to b passes through a).</summary>
  public bool Dominates(IrBasicBlock a, IrBasicBlock b) {
    if (!this._rpoIndex.ContainsKey(b))
      return false;                                  // b unreachable: vacuously not dominated for our checks
    for (var cur = b; cur is not null; cur = this.ImmediateDominatorOf(cur)) {
      if (ReferenceEquals(cur, a))
        return true;
      if (ReferenceEquals(cur, this.ImmediateDominatorOf(cur)))
        break;                                       // reached entry (idom == self)
    }
    return false;
  }

  /// <summary>True if the given block is reachable from entry.</summary>
  public bool IsReachable(IrBasicBlock block) => this._rpoIndex.ContainsKey(block);

  private static List<IrBasicBlock> ComputeReversePostorder(IrBasicBlock entry) {
    var postorder = new List<IrBasicBlock>();
    var visited = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance);
    var stack = new Stack<(IrBasicBlock Block, IEnumerator<IrBasicBlock> Succ)>();

    visited.Add(entry);
    stack.Push((entry, entry.Successors.GetEnumerator()));
    while (stack.Count > 0) {
      var (block, succ) = stack.Peek();
      if (succ.MoveNext()) {
        var next = succ.Current;
        if (visited.Add(next))
          stack.Push((next, next.Successors.GetEnumerator()));
      } else {
        postorder.Add(block);
        stack.Pop();
      }
    }

    postorder.Reverse();
    return postorder;
  }

  private void ComputeIdoms() {
    var entry = this.ReversePostorder[0];
    this._idom[entry] = entry;

    bool changed;
    do {
      changed = false;
      for (var i = 1; i < this.ReversePostorder.Count; ++i) {
        var block = this.ReversePostorder[i];
        IrBasicBlock? newIdom = null;
        foreach (var pred in block.Predecessors) {
          if (!this._idom.ContainsKey(pred))
            continue;                                // not yet processed
          newIdom = newIdom is null ? pred : this.Intersect(pred, newIdom);
        }
        if (newIdom is not null && (!this._idom.TryGetValue(block, out var cur) || !ReferenceEquals(cur, newIdom))) {
          this._idom[block] = newIdom;
          changed = true;
        }
      }
    } while (changed);
  }

  private IrBasicBlock Intersect(IrBasicBlock a, IrBasicBlock b) {
    while (!ReferenceEquals(a, b)) {
      while (this._rpoIndex[a] > this._rpoIndex[b])
        a = this._idom[a];
      while (this._rpoIndex[b] > this._rpoIndex[a])
        b = this._idom[b];
    }
    return a;
  }

  private void ComputeFrontiers() {
    foreach (var block in this.ReversePostorder)
      this._frontier[block] = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance);

    foreach (var block in this.ReversePostorder) {
      var preds = block.Predecessors.Where(this.IsReachable).ToList();
      if (preds.Count < 2)
        continue;
      var idom = this._idom[block];
      foreach (var pred in preds) {
        var runner = pred;
        while (!ReferenceEquals(runner, idom)) {
          this._frontier[runner].Add(block);
          runner = this._idom[runner];
        }
      }
    }
  }
}
