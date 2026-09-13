namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>
/// Post-dominator tree and post-dominance frontiers over the reachable CFG.
///
/// <para>
/// The analysis runs ordinary dominance on the reversed control-flow graph with an internal virtual exit.
/// Real terminal blocks are roots of that reversed graph. Reachable blocks that cannot reach any real exit
/// are also attached to the virtual exit, conservatively preventing a terminating path from being mistaken
/// for the unique future of a branch that may instead enter a non-terminating region.
/// </para>
/// </summary>
public sealed class IrPostDominators {

  private const int _VIRTUAL_EXIT = 0;

  private readonly Dictionary<IrBasicBlock, int> _nodeByBlock = new(ReferenceEqualityComparer.Instance);
  private readonly IrBasicBlock?[] _blockByNode;
  private readonly int[] _idom;
  private readonly HashSet<IrBasicBlock> _reachable = new(ReferenceEqualityComparer.Instance);
  private readonly HashSet<IrBasicBlock> _canReachExit = new(ReferenceEqualityComparer.Instance);
  private readonly HashSet<IrBasicBlock> _virtualRoots = new(ReferenceEqualityComparer.Instance);
  private readonly Dictionary<IrBasicBlock, HashSet<IrBasicBlock>> _frontier =
    new(ReferenceEqualityComparer.Instance);

  private IrPostDominators(IrFunction function) {
    this.ComputeReachable(function.Entry!);

    var exits = function.Blocks
      .Where(this._reachable.Contains)
      .Where(block => !block.Successors.Any())
      .ToList();
    this.Exits = exits;

    this.ComputeCanReachExit(exits);
    this._virtualRoots.UnionWith(exits);
    foreach (var block in this._reachable)
      if (!this._canReachExit.Contains(block))
        this._virtualRoots.Add(block);

    this._blockByNode = new IrBasicBlock?[this._reachable.Count + 1];
    var node = 1;
    foreach (var block in function.Blocks)
      if (this._reachable.Contains(block)) {
        this._nodeByBlock[block] = node;
        this._blockByNode[node] = block;
        ++node;
      }

    var reversePostorder = this.ComputeReversePostorder();
    var rpoIndex = Enumerable.Repeat(-1, this._blockByNode.Length).ToArray();
    for (var i = 0; i < reversePostorder.Count; ++i)
      rpoIndex[reversePostorder[i]] = i;

    this._idom = Enumerable.Repeat(-1, this._blockByNode.Length).ToArray();
    this._idom[_VIRTUAL_EXIT] = _VIRTUAL_EXIT;
    this.ComputeImmediatePostDominators(reversePostorder, rpoIndex);
    this.ComputeFrontiers();
  }

  /// <summary>Builds post-dominators for a function with a body; returns null for a declaration.</summary>
  public static IrPostDominators? Build(IrFunction function) {
    ArgumentNullException.ThrowIfNull(function);
    return function.Entry is null ? null : new(function);
  }

  /// <summary>Reachable terminal blocks that end real CFG paths.</summary>
  public IReadOnlyList<IrBasicBlock> Exits { get; }

  /// <summary>Whether a block is reachable from the function entry.</summary>
  public bool IsReachable(IrBasicBlock block) {
    ArgumentNullException.ThrowIfNull(block);
    return this._reachable.Contains(block);
  }

  /// <summary>Whether at least one path from the block reaches a real terminal block.</summary>
  public bool CanReachExit(IrBasicBlock block) {
    ArgumentNullException.ThrowIfNull(block);
    return this._canReachExit.Contains(block);
  }

  /// <summary>
  /// The immediate post-dominator of a block. Returns null when the block is unreachable or its immediate
  /// post-dominator is the internal virtual exit (for example a real exit or a branch with distinct exits).
  /// </summary>
  public IrBasicBlock? ImmediatePostDominatorOf(IrBasicBlock block) {
    ArgumentNullException.ThrowIfNull(block);
    if (!this._nodeByBlock.TryGetValue(block, out var node))
      return null;
    var immediate = this._idom[node];
    return immediate > _VIRTUAL_EXIT ? this._blockByNode[immediate] : null;
  }

  /// <summary>True when every represented future of <paramref name="block"/> passes through <paramref name="candidate"/>.</summary>
  public bool PostDominates(IrBasicBlock candidate, IrBasicBlock block) {
    ArgumentNullException.ThrowIfNull(candidate);
    ArgumentNullException.ThrowIfNull(block);
    if (!this._nodeByBlock.TryGetValue(candidate, out var candidateNode)
        || !this._nodeByBlock.TryGetValue(block, out var node))
      return false;

    while (node != _VIRTUAL_EXIT) {
      if (node == candidateNode)
        return true;
      var next = this._idom[node];
      if (next < 0 || next == node)
        break;
      node = next;
    }
    return false;
  }

  /// <summary>The post-dominance frontier of a block.</summary>
  public IReadOnlyCollection<IrBasicBlock> FrontierOf(IrBasicBlock block) {
    ArgumentNullException.ThrowIfNull(block);
    return this._frontier.TryGetValue(block, out var frontier) ? frontier : [];
  }

  private void ComputeReachable(IrBasicBlock entry) {
    var pending = new Stack<IrBasicBlock>();
    pending.Push(entry);
    while (pending.Count > 0) {
      var block = pending.Pop();
      if (!this._reachable.Add(block))
        continue;
      foreach (var successor in block.Successors)
        pending.Push(successor);
    }
  }

  private void ComputeCanReachExit(IEnumerable<IrBasicBlock> exits) {
    var pending = new Stack<IrBasicBlock>(exits);
    while (pending.Count > 0) {
      var block = pending.Pop();
      if (!this._canReachExit.Add(block))
        continue;
      foreach (var predecessor in block.Predecessors)
        if (this._reachable.Contains(predecessor))
          pending.Push(predecessor);
    }
  }

  private List<int> ComputeReversePostorder() {
    var postorder = new List<int>();
    var visited = new bool[this._blockByNode.Length];
    var stack = new Stack<(int Node, IEnumerator<int> Successors)>();

    visited[_VIRTUAL_EXIT] = true;
    stack.Push((_VIRTUAL_EXIT, this.ReversedSuccessors(_VIRTUAL_EXIT).GetEnumerator()));
    while (stack.Count > 0) {
      var (node, successors) = stack.Peek();
      if (successors.MoveNext()) {
        var next = successors.Current;
        if (visited[next])
          continue;
        visited[next] = true;
        stack.Push((next, this.ReversedSuccessors(next).GetEnumerator()));
      } else {
        successors.Dispose();
        postorder.Add(node);
        stack.Pop();
      }
    }

    postorder.Reverse();
    return postorder;
  }

  private void ComputeImmediatePostDominators(IReadOnlyList<int> reversePostorder, IReadOnlyList<int> rpoIndex) {
    bool changed;
    do {
      changed = false;
      for (var i = 1; i < reversePostorder.Count; ++i) {
        var node = reversePostorder[i];
        var newIdom = -1;
        foreach (var predecessor in this.ReversedPredecessors(node)) {
          if (this._idom[predecessor] < 0)
            continue;
          newIdom = newIdom < 0 ? predecessor : Intersect(predecessor, newIdom);
        }

        if (newIdom >= 0 && this._idom[node] != newIdom) {
          this._idom[node] = newIdom;
          changed = true;
        }
      }
    } while (changed);
    return;

    int Intersect(int left, int right) {
      while (left != right) {
        while (rpoIndex[left] > rpoIndex[right])
          left = this._idom[left];
        while (rpoIndex[right] > rpoIndex[left])
          right = this._idom[right];
      }
      return left;
    }
  }

  private void ComputeFrontiers() {
    foreach (var block in this._reachable)
      this._frontier[block] = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance);

    for (var node = 1; node < this._blockByNode.Length; ++node) {
      var predecessors = this.ReversedPredecessors(node).ToList();
      if (predecessors.Count < 2)
        continue;

      var immediate = this._idom[node];
      foreach (var predecessor in predecessors) {
        var runner = predecessor;
        while (runner != immediate && runner != _VIRTUAL_EXIT) {
          this._frontier[this._blockByNode[runner]!].Add(this._blockByNode[node]!);
          runner = this._idom[runner];
        }
      }
    }
  }

  /// <summary>Successors in the reversed CFG: roots from the virtual exit, otherwise original predecessors.</summary>
  private IEnumerable<int> ReversedSuccessors(int node) {
    if (node == _VIRTUAL_EXIT) {
      foreach (var root in this._virtualRoots)
        yield return this._nodeByBlock[root];
      yield break;
    }

    foreach (var predecessor in this._blockByNode[node]!.Predecessors)
      if (this._nodeByBlock.TryGetValue(predecessor, out var predecessorNode))
        yield return predecessorNode;
  }

  /// <summary>Predecessors in the reversed CFG: original successors, plus the virtual exit for roots.</summary>
  private IEnumerable<int> ReversedPredecessors(int node) {
    var block = this._blockByNode[node]!;
    foreach (var successor in block.Successors)
      if (this._nodeByBlock.TryGetValue(successor, out var successorNode))
        yield return successorNode;
    if (this._virtualRoots.Contains(block))
      yield return _VIRTUAL_EXIT;
  }
}
