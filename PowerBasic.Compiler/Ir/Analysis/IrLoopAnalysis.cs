namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>
/// Natural-loop forest for a function. Loops are discovered from reachable backedges whose targets dominate their
/// sources, then nested by block-set containment. Irreducible cycles without a dominating header are deliberately
/// not reported as loops.
/// </summary>
public sealed class IrLoopAnalysis {

  /// <summary>One natural loop and its structural CFG properties.</summary>
  public sealed class Loop {

    private readonly HashSet<IrBasicBlock> _blocks;
    private readonly List<Loop> _subLoops = [];
    private readonly List<IrBasicBlock> _latches;
    private readonly List<IrBasicBlock> _enteringBlocks;
    private readonly List<IrBasicBlock> _exitingBlocks;
    private readonly List<IrBasicBlock> _exitBlocks;

    internal Loop(IrBasicBlock header, HashSet<IrBasicBlock> blocks) {
      this.Header = header;
      this._blocks = blocks;
      this._latches = blocks
        .Where(block => block.Successors.Any(successor => ReferenceEquals(successor, header)))
        .ToList();
      this._enteringBlocks = header.Predecessors
        .Where(predecessor => !blocks.Contains(predecessor))
        .ToList();
      this._exitingBlocks = blocks
        .Where(block => block.Successors.Any(successor => !blocks.Contains(successor)))
        .ToList();

      var exits = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance);
      foreach (var block in blocks)
        foreach (var successor in block.Successors)
          if (!blocks.Contains(successor))
            exits.Add(successor);
      this._exitBlocks = exits.ToList();
    }

    /// <summary>The unique entry block of the loop.</summary>
    public IrBasicBlock Header { get; }

    /// <summary>All blocks belonging to the loop, including nested loops.</summary>
    public IReadOnlySet<IrBasicBlock> Blocks => this._blocks;

    /// <summary>The directly containing loop, or null for a top-level loop.</summary>
    public Loop? Parent { get; internal set; }

    /// <summary>Loops directly nested inside this loop.</summary>
    public IReadOnlyList<Loop> SubLoops => this._subLoops;

    /// <summary>One-based nesting depth.</summary>
    public int Depth => this.Parent is null ? 1 : this.Parent.Depth + 1;

    /// <summary>Blocks inside the loop with an edge back to the header.</summary>
    public IReadOnlyList<IrBasicBlock> Latches => this._latches;

    /// <summary>Blocks outside the loop with an edge to the header.</summary>
    public IReadOnlyList<IrBasicBlock> EnteringBlocks => this._enteringBlocks;

    /// <summary>The sole outside predecessor of the header, whether or not it is a canonical preheader.</summary>
    public IrBasicBlock? UniqueEnteringBlock
      => this._enteringBlocks.Count == 1 ? this._enteringBlocks[0] : null;

    /// <summary>
    /// The canonical preheader, requiring the unique entering block to branch unconditionally to the header.
    /// This is intentionally stricter than <see cref="UniqueEnteringBlock"/>.
    /// </summary>
    public IrBasicBlock? Preheader
      => this.UniqueEnteringBlock is { Terminator: IrBr branch } block && ReferenceEquals(branch.Target, this.Header)
        ? block
        : null;

    /// <summary>Blocks inside the loop with at least one successor outside it.</summary>
    public IReadOnlyList<IrBasicBlock> ExitingBlocks => this._exitingBlocks;

    /// <summary>Blocks outside the loop reached by exiting edges.</summary>
    public IReadOnlyList<IrBasicBlock> ExitBlocks => this._exitBlocks;

    /// <summary>Whether the loop has a preheader, one latch, and dedicated exits.</summary>
    public bool IsLoopSimplifyForm
      => this.Preheader is not null
         && this._latches.Count == 1
         && this._exitBlocks.All(exit => exit.Predecessors.All(this._blocks.Contains));

    /// <summary>Whether the block belongs to this loop, including any nested loop.</summary>
    public bool Contains(IrBasicBlock block) {
      ArgumentNullException.ThrowIfNull(block);
      return this._blocks.Contains(block);
    }

    internal int BlockCount => this._blocks.Count;

    internal void AddSubLoop(Loop loop) => this._subLoops.Add(loop);

    internal void SortSubLoops(IReadOnlyDictionary<IrBasicBlock, int> order)
      => this._subLoops.Sort((left, right) => order[left.Header].CompareTo(order[right.Header]));
  }

  private readonly Dictionary<IrBasicBlock, Loop> _innermostLoop =
    new(ReferenceEqualityComparer.Instance);

  private IrLoopAnalysis(IrFunction function, IrDominators? dominators) {
    ArgumentNullException.ThrowIfNull(function);
    if (function.Entry is null || dominators is null) {
      this.Loops = [];
      this.TopLevelLoops = [];
      return;
    }

    var byHeader = new Dictionary<IrBasicBlock, HashSet<IrBasicBlock>>(ReferenceEqualityComparer.Instance);
    foreach (var block in dominators.ReversePostorder)
      foreach (var successor in block.Successors) {
        if (!dominators.IsReachable(successor) || !dominators.Dominates(successor, block))
          continue;

        if (!byHeader.TryGetValue(successor, out var body))
          byHeader[successor] = body = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance) { successor };

        var work = new Stack<IrBasicBlock>();
        work.Push(block);
        while (work.Count > 0) {
          var current = work.Pop();
          if (!body.Add(current))
            continue;
          foreach (var predecessor in current.Predecessors)
            if (dominators.IsReachable(predecessor))
              work.Push(predecessor);
        }
      }

    var order = dominators.ReversePostorder
      .Select((block, index) => (block, index))
      .ToDictionary(pair => pair.block, pair => pair.index, ReferenceEqualityComparer.Instance);
    var loops = byHeader
      .Select(pair => new Loop(pair.Key, pair.Value))
      .OrderBy(loop => order[loop.Header])
      .ToList();

    foreach (var loop in loops) {
      Loop? parent = null;
      foreach (var candidate in loops) {
        if (ReferenceEquals(candidate, loop)
            || candidate.BlockCount <= loop.BlockCount
            || !candidate.Blocks.IsSupersetOf(loop.Blocks))
          continue;
        if (parent is null || candidate.BlockCount < parent.BlockCount)
          parent = candidate;
      }
      loop.Parent = parent;
      parent?.AddSubLoop(loop);
    }

    foreach (var loop in loops)
      loop.SortSubLoops(order);

    foreach (var loop in loops.OrderByDescending(loop => loop.BlockCount))
      foreach (var block in loop.Blocks)
        this._innermostLoop[block] = loop;

    this.Loops = loops;
    this.TopLevelLoops = loops.Where(loop => loop.Parent is null).ToList();
  }

  /// <summary>All natural loops in deterministic header order.</summary>
  public IReadOnlyList<Loop> Loops { get; }

  /// <summary>Roots of the loop nesting forest.</summary>
  public IReadOnlyList<Loop> TopLevelLoops { get; }

  /// <summary>The innermost natural loop containing the block, or null when the block is outside every loop.</summary>
  public Loop? LoopFor(IrBasicBlock block) {
    ArgumentNullException.ThrowIfNull(block);
    return this._innermostLoop.GetValueOrDefault(block);
  }

  /// <summary>Builds the natural-loop forest from an already computed dominator tree.</summary>
  internal static IrLoopAnalysis Build(IrFunction function, IrDominators? dominators)
    => new(function, dominators);
}
