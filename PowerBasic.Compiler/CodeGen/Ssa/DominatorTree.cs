namespace PowerBasic.Compiler.CodeGen.Ssa;

/// <summary>
/// Immediate dominators and dominance frontiers over a <see cref="ControlFlowGraph"/>,
/// computed with the Cooper-Harvey-Kennedy iterative algorithm and the
/// Cytron et al. frontier walk. Only blocks reachable from the entry are
/// considered (the structured builder can leave unreachable tails). This is the
/// scaffolding SSA phi placement needs (docs/PB36.md mid-end).
/// </summary>
public sealed class DominatorTree {
  private readonly Dictionary<BasicBlock, BasicBlock> _idom = new(ReferenceEqualityComparer.Instance);
  private readonly Dictionary<BasicBlock, HashSet<BasicBlock>> _frontier = new(ReferenceEqualityComparer.Instance);

  /// <summary>Blocks reachable from the entry, in reverse postorder (entry first).</summary>
  public IReadOnlyList<BasicBlock> ReversePostorder { get; }

  private DominatorTree(IReadOnlyList<BasicBlock> reversePostorder) => this.ReversePostorder = reversePostorder;

  /// <summary>The immediate dominator of <paramref name="block"/> (the entry dominates itself).</summary>
  public BasicBlock ImmediateDominatorOf(BasicBlock block) => this._idom[block];

  /// <summary>The dominance frontier of <paramref name="block"/> (may be empty).</summary>
  public IReadOnlyCollection<BasicBlock> FrontierOf(BasicBlock block)
    => this._frontier.TryGetValue(block, out var set) ? set : [];

  /// <summary>True when <paramref name="a"/> dominates <paramref name="b"/> (reflexive).</summary>
  public bool Dominates(BasicBlock a, BasicBlock b) {
    for (var runner = b; ; runner = this._idom[runner]) {
      if (ReferenceEquals(runner, a))
        return true;
      if (ReferenceEquals(runner, this._idom[runner])) // reached the entry's self-loop
        return ReferenceEquals(a, runner);
    }
  }

  public static DominatorTree Build(ControlFlowGraph cfg) {
    // reverse postorder from the entry (only reachable blocks get a number)
    var postorder = new List<BasicBlock>();
    var visited = new HashSet<BasicBlock>(ReferenceEqualityComparer.Instance);
    var stack = new Stack<(BasicBlock Block, IEnumerator<BasicBlock> Succ)>();
    visited.Add(cfg.Entry);
    stack.Push((cfg.Entry, cfg.Entry.Successors.GetEnumerator()));
    while (stack.Count > 0) {
      var (block, succ) = stack.Peek();
      if (succ.MoveNext()) {
        if (visited.Add(succ.Current))
          stack.Push((succ.Current, succ.Current.Successors.GetEnumerator()));
      } else {
        postorder.Add(block);
        stack.Pop();
      }
    }
    postorder.Reverse();
    var rpo = postorder; // entry first

    var order = new Dictionary<BasicBlock, int>(ReferenceEqualityComparer.Instance);
    for (var i = 0; i < rpo.Count; ++i)
      order[rpo[i]] = i;

    var tree = new DominatorTree(rpo);
    var idom = tree._idom;
    idom[cfg.Entry] = cfg.Entry; // the entry dominates itself

    var changed = true;
    while (changed) {
      changed = false;
      foreach (var block in rpo) {
        if (ReferenceEquals(block, cfg.Entry))
          continue;
        BasicBlock? newIdom = null;
        foreach (var pred in block.Predecessors) {
          if (!order.ContainsKey(pred) || !idom.ContainsKey(pred))
            continue; // unreachable or not yet processed predecessor
          newIdom = newIdom == null ? pred : Intersect(pred, newIdom, idom, order);
        }
        if (newIdom != null && (!idom.TryGetValue(block, out var current) || !ReferenceEquals(current, newIdom))) {
          idom[block] = newIdom;
          changed = true;
        }
      }
    }

    // Cytron dominance frontiers: at every join, walk each predecessor up to
    // (but not including) the join's immediate dominator
    foreach (var block in rpo)
      tree._frontier[block] = new(ReferenceEqualityComparer.Instance);
    foreach (var block in rpo) {
      if (block.Predecessors.Count < 2)
        continue;
      var bIdom = idom[block];
      foreach (var pred in block.Predecessors) {
        if (!idom.ContainsKey(pred))
          continue;
        for (var runner = pred; !ReferenceEquals(runner, bIdom); runner = idom[runner]) {
          tree._frontier[runner].Add(block);
          if (ReferenceEquals(runner, idom[runner])) // entry self-loop guard
            break;
        }
      }
    }

    return tree;
  }

  /// <summary>Walks the two fingers up the dominator chain to their common ancestor (CHK intersect).</summary>
  private static BasicBlock Intersect(BasicBlock a, BasicBlock b, Dictionary<BasicBlock, BasicBlock> idom, Dictionary<BasicBlock, int> order) {
    while (!ReferenceEquals(a, b)) {
      while (order[a] > order[b])
        a = idom[a];
      while (order[b] > order[a])
        b = idom[b];
    }
    return a;
  }
}
