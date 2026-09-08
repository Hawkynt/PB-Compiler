namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>A node in the function-local SSA graph for the whole memory state.</summary>
public abstract class IrMemoryAccess {

  protected IrMemoryAccess(IrBasicBlock? block) => this.Block = block;

  /// <summary>The block this access belongs to, or null for live-on-entry.</summary>
  public IrBasicBlock? Block { get; }
}

/// <summary>The initial memory version visible when control enters a function.</summary>
public sealed class IrMemoryLiveOnEntry : IrMemoryAccess {
  internal IrMemoryLiveOnEntry() : base(null) { }
}

/// <summary>A memory access attached to an IR instruction and the memory version reaching it.</summary>
public abstract class IrMemoryInstructionAccess : IrMemoryAccess {

  protected IrMemoryInstructionAccess(IrInstruction instruction, IrMemoryAccess definingAccess)
      : base(instruction.Parent ?? throw new ArgumentException("memory instruction has no parent block", nameof(instruction))) {
    this.Instruction = instruction;
    this.DefiningAccess = definingAccess;
  }

  /// <summary>The IR instruction represented by this memory access.</summary>
  public IrInstruction Instruction { get; }

  /// <summary>The immediately preceding memory definition or phi in memory-SSA order.</summary>
  public IrMemoryAccess DefiningAccess { get; }
}

/// <summary>A read of memory which does not create a new memory version.</summary>
public sealed class IrMemoryUse : IrMemoryInstructionAccess {
  internal IrMemoryUse(IrInstruction instruction, IrMemoryAccess definingAccess)
      : base(instruction, definingAccess) { }
}

/// <summary>A write/barrier which creates a new version of the whole memory state.</summary>
public sealed class IrMemoryDef : IrMemoryInstructionAccess {
  internal IrMemoryDef(IrInstruction instruction, IrMemoryAccess definingAccess)
      : base(instruction, definingAccess) { }
}

/// <summary>One incoming memory version on a CFG edge.</summary>
public readonly record struct IrMemoryIncoming(IrBasicBlock Block, IrMemoryAccess Access);

/// <summary>A merge of memory versions at a control-flow join.</summary>
public sealed class IrMemoryPhi : IrMemoryAccess {

  private readonly List<IrMemoryIncoming> _incoming = [];

  internal IrMemoryPhi(IrBasicBlock block) : base(block) { }

  /// <summary>The incoming memory version for each reachable predecessor.</summary>
  public IReadOnlyList<IrMemoryIncoming> Incoming => this._incoming;

  /// <summary>The memory version arriving from <paramref name="block"/>, or null when no such edge exists.</summary>
  public IrMemoryAccess? IncomingFrom(IrBasicBlock block) {
    foreach (var incoming in this._incoming)
      if (ReferenceEquals(incoming.Block, block))
        return incoming.Access;
    return null;
  }

  internal void AddIncoming(IrBasicBlock block, IrMemoryAccess access) {
    for (var i = 0; i < this._incoming.Count; ++i)
      if (ReferenceEquals(this._incoming[i].Block, block)) {
        this._incoming[i] = new(block, access);
        return;
      }
    this._incoming.Add(new(block, access));
  }
}

/// <summary>
/// Function-local Memory SSA overlay.
///
/// <para>
/// Stores, calls and inline assembly are memory definitions; ordinary loads are memory uses. Memory
/// phis are placed at the iterated dominance frontier of blocks containing definitions, then the
/// graph is renamed down the dominator tree exactly like ordinary SSA. Calls and inline assembly are
/// deliberately opaque barriers. PB-specific precision lives in <see cref="IrAliasAnalysis"/>, not in
/// the graph construction.
/// </para>
///
/// <para>
/// The raw defining edge is intentionally conservative: every definition versions the whole memory
/// state. <see cref="GetClobberingAccess(IrLoad)"/> and <see cref="GetClobberingAccess(IrStore)"/>
/// walk through non-aliasing stores and collapse phis whose incoming paths resolve to the same
/// clobber. That gives clients precise answers without multiplying the graph into per-object memory
/// partitions.
/// </para>
/// </summary>
public sealed class IrMemorySsa {

  private readonly Dictionary<IrInstruction, IrMemoryInstructionAccess> _accesses
    = new(ReferenceEqualityComparer.Instance);
  private readonly Dictionary<IrBasicBlock, IrMemoryPhi> _phis
    = new(ReferenceEqualityComparer.Instance);

  private readonly record struct MemoryLocation(IrValue Pointer, IrType Type);

  private IrMemorySsa(IrFunction function) {
    this.LiveOnEntry = new IrMemoryLiveOnEntry();
    if (function.Entry is null)
      return;

    var dominators = IrDominators.Build(function)!;
    var definitionBlocks = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance);
    foreach (var block in dominators.ReversePostorder)
      if (block.Instructions.Any(IsMemoryDef))
        definitionBlocks.Add(block);

    this.PlacePhis(definitionBlocks, dominators);
    var children = BuildDominatorChildren(dominators);
    this.Rename(function.Entry, this.LiveOnEntry, children);
  }

  /// <summary>The distinguished initial memory state.</summary>
  public IrMemoryLiveOnEntry LiveOnEntry { get; }

  /// <summary>All memory phis in reachable blocks.</summary>
  public IReadOnlyCollection<IrMemoryPhi> Phis => this._phis.Values;

  /// <summary>Builds the Memory SSA overlay for <paramref name="function"/>.</summary>
  public static IrMemorySsa Build(IrFunction function) {
    ArgumentNullException.ThrowIfNull(function);
    return new(function);
  }

  /// <summary>The memory access attached to an instruction, or null for a non-memory/unreachable instruction.</summary>
  public IrMemoryInstructionAccess? AccessFor(IrInstruction instruction) {
    ArgumentNullException.ThrowIfNull(instruction);
    return this._accesses.GetValueOrDefault(instruction);
  }

  /// <summary>The memory phi at <paramref name="block"/>, or null when no merge is required there.</summary>
  public IrMemoryPhi? PhiFor(IrBasicBlock block) {
    ArgumentNullException.ThrowIfNull(block);
    return this._phis.GetValueOrDefault(block);
  }

  /// <summary>
  /// Finds the nearest memory definition which may change the bytes read by <paramref name="load"/>.
  /// Non-aliasing stores are skipped; opaque calls/assembly always stop the walk.
  /// </summary>
  public IrMemoryAccess GetClobberingAccess(IrLoad load) {
    ArgumentNullException.ThrowIfNull(load);
    return this.GetClobberingAccess(load, new(load.Pointer, load.Type));
  }

  /// <summary>
  /// Finds the nearest earlier memory definition which may overlap the bytes written by
  /// <paramref name="store"/>.
  /// </summary>
  public IrMemoryAccess GetClobberingAccess(IrStore store) {
    ArgumentNullException.ThrowIfNull(store);
    return this.GetClobberingAccess(store, new(store.Pointer, store.Value.Type));
  }

  private IrMemoryAccess GetClobberingAccess(IrInstruction instruction, MemoryLocation location) {
    if (!this._accesses.TryGetValue(instruction, out var access))
      throw new ArgumentException("instruction is not a reachable memory access in this Memory SSA graph", nameof(instruction));

    var activePhis = new HashSet<IrMemoryPhi>(ReferenceEqualityComparer.Instance);
    return this.FindClobber(access.DefiningAccess, location, activePhis) ?? access.DefiningAccess;
  }

  private IrMemoryAccess? FindClobber(IrMemoryAccess access, MemoryLocation location,
      HashSet<IrMemoryPhi> activePhis) {
    switch (access) {
      case IrMemoryLiveOnEntry:
        return access;

      case IrMemoryUse use:
        return this.FindClobber(use.DefiningAccess, location, activePhis);

      case IrMemoryDef definition:
        return DefinitionMayClobber(definition, location)
          ? definition
          : this.FindClobber(definition.DefiningAccess, location, activePhis);

      case IrMemoryPhi phi:
        if (!activePhis.Add(phi))
          return null; // loop recurrence with no clobber found on this path yet
        try {
          IrMemoryAccess? common = null;
          foreach (var incoming in phi.Incoming) {
            var clobber = this.FindClobber(incoming.Access, location, activePhis);
            if (clobber is null)
              continue;
            if (common is null)
              common = clobber;
            else if (!ReferenceEquals(common, clobber))
              return phi;
          }
          return common ?? phi;
        } finally {
          activePhis.Remove(phi);
        }

      default:
        return access;
    }
  }

  private static bool DefinitionMayClobber(IrMemoryDef definition, MemoryLocation location)
    => definition.Instruction switch {
      IrStore store => IrAliasAnalysis.MayAlias(store.Pointer, store.Value.Type, location.Pointer, location.Type),
      _ => true, // calls, inline asm and any future opaque memory definition
    };

  private void PlacePhis(HashSet<IrBasicBlock> definitionBlocks, IrDominators dominators) {
    var work = new Queue<IrBasicBlock>(definitionBlocks);
    var queued = new HashSet<IrBasicBlock>(definitionBlocks, ReferenceEqualityComparer.Instance);

    while (work.Count > 0) {
      var block = work.Dequeue();
      foreach (var frontier in dominators.FrontierOf(block)) {
        if (!this._phis.TryAdd(frontier, new IrMemoryPhi(frontier)))
          continue;
        if (queued.Add(frontier))
          work.Enqueue(frontier);
      }
    }
  }

  private void Rename(IrBasicBlock block, IrMemoryAccess incoming,
      Dictionary<IrBasicBlock, List<IrBasicBlock>> children) {
    IrMemoryAccess current = this._phis.TryGetValue(block, out var phi) ? phi : incoming;

    foreach (var instruction in block.Instructions) {
      if (instruction is IrLoad) {
        this._accesses.Add(instruction, new IrMemoryUse(instruction, current));
        continue;
      }
      if (!IsMemoryDef(instruction))
        continue;

      var definition = new IrMemoryDef(instruction, current);
      this._accesses.Add(instruction, definition);
      current = definition;
    }

    foreach (var successor in block.Successors)
      if (this._phis.TryGetValue(successor, out var successorPhi))
        successorPhi.AddIncoming(block, current);

    if (children.TryGetValue(block, out var dominated))
      foreach (var child in dominated)
        this.Rename(child, current, children);
  }

  private static Dictionary<IrBasicBlock, List<IrBasicBlock>> BuildDominatorChildren(IrDominators dominators) {
    var children = new Dictionary<IrBasicBlock, List<IrBasicBlock>>(ReferenceEqualityComparer.Instance);
    foreach (var block in dominators.ReversePostorder) {
      var idom = dominators.ImmediateDominatorOf(block);
      if (idom is null || ReferenceEquals(idom, block))
        continue;
      (children.TryGetValue(idom, out var list) ? list : children[idom] = []).Add(block);
    }
    return children;
  }

  private static bool IsMemoryDef(IrInstruction instruction)
    => instruction is IrStore or IrCall or IrInlineAsm;
}
