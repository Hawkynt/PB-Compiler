namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>
/// Complete target-set fact for a procedure-valued IR value.
///
/// <para>
/// <see cref="IsComplete"/> means the set contains every target represented by the current closed-module
/// proof. <see cref="HasNull"/> records the null alternative separately because a singleton procedure
/// plus null is not devirtualizable without a guard. Incomplete sets must never be used as negative
/// information.
/// </para>
/// </summary>
public sealed class IrFunctionTargetSet {

  private readonly HashSet<IrFunction> _functions;

  internal IrFunctionTargetSet(bool isComplete, bool hasNull, IEnumerable<IrFunction>? functions = null) {
    this.IsComplete = isComplete;
    this.HasNull = hasNull;
    this._functions = functions is null
      ? new HashSet<IrFunction>(ReferenceEqualityComparer.Instance)
      : new HashSet<IrFunction>(functions, ReferenceEqualityComparer.Instance);
  }

  public bool IsComplete { get; }

  public bool HasNull { get; }

  public IReadOnlySet<IrFunction> Functions => this._functions;

  public IrFunction? UniqueNonNullTarget
    => this.IsComplete && !this.HasNull && this._functions.Count == 1
      ? this._functions.Single()
      : null;

  internal static IrFunctionTargetSet Unknown { get; } = new(false, false);

  internal static IrFunctionTargetSet Null { get; } = new(true, true);

  internal static IrFunctionTargetSet For(IrFunction function)
    => new(true, false, [function]);

  internal static IrFunctionTargetSet Union(IEnumerable<IrFunctionTargetSet> sets) {
    var functions = new HashSet<IrFunction>(ReferenceEqualityComparer.Instance);
    var hasNull = false;
    var any = false;

    foreach (var set in sets) {
      any = true;
      if (!set.IsComplete)
        return Unknown;
      hasNull |= set.HasNull;
      functions.UnionWith(set._functions);
    }

    return any ? new IrFunctionTargetSet(true, hasNull, functions) : Unknown;
  }
}

/// <summary>
/// Closed-module function-value target analysis.
///
/// <para>
/// This is a proof domain, not a prediction domain. It follows only exact IR relations: procedure
/// constants, null, pointer-preserving bitcasts, phi/select joins, visible formal arguments, and
/// closed local pointer cells. Unknown values, escaping cells, invisible callers and cyclic value
/// flow remain incomplete. Profile-driven candidate choice belongs to speculative transforms and does
/// not enter this analysis.
/// </para>
/// </summary>
public sealed class IrFunctionTargetAnalysis {

  private readonly HashSet<IrFunction> _functions;
  private readonly IrCallGraph _callGraph;
  private readonly Dictionary<IrValue, IrFunctionTargetSet> _cache
    = new(ReferenceEqualityComparer.Instance);
  private readonly HashSet<IrValue> _active
    = new(ReferenceEqualityComparer.Instance);
  private readonly Dictionary<IrFunction, IrDominators> _dominators
    = new(ReferenceEqualityComparer.Instance);

  internal IrFunctionTargetAnalysis(IrModule module, IrCallGraph callGraph) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(callGraph);
    this._functions = new(module.Functions, ReferenceEqualityComparer.Instance);
    this._callGraph = callGraph;
  }

  /// <summary>Returns the complete/incomplete function target set for <paramref name="value"/>.</summary>
  public IrFunctionTargetSet Resolve(IrValue value) {
    ArgumentNullException.ThrowIfNull(value);

    if (this._cache.TryGetValue(value, out var cached))
      return cached;
    if (!this._active.Add(value))
      return IrFunctionTargetSet.Unknown;

    try {
      var result = this.ResolveCore(value);
      this._cache[value] = result;
      return result;
    } finally {
      this._active.Remove(value);
    }
  }

  /// <summary>Returns one proven non-null target, or null when the complete set is not exactly one.</summary>
  public IrFunction? ResolveUnique(IrValue value) => this.Resolve(value).UniqueNonNullTarget;

  private IrFunctionTargetSet ResolveCore(IrValue value) => value switch {
    IrFunction function when this._functions.Contains(function) => IrFunctionTargetSet.For(function),
    IrNullPtr => IrFunctionTargetSet.Null,
    IrCast { Op: IrCastOp.BitCast } cast when cast.Value.Type.IsPointer && cast.Type.IsPointer
      => this.Resolve(cast.Value),
    IrSelect select when select.Type.IsPointer
      => IrFunctionTargetSet.Union([this.Resolve(select.IfTrue), this.Resolve(select.IfFalse)]),
    IrPhi phi when phi.Type.IsPointer
      => IrFunctionTargetSet.Union(phi.Operands.Select(this.Resolve)),
    IrArgument argument => this.ResolveArgument(argument),
    IrLoad load => this.ResolveClosedLocalLoad(load),
    _ => IrFunctionTargetSet.Unknown,
  };

  private IrFunctionTargetSet ResolveArgument(IrArgument argument) {
    var owner = argument.Parent;
    if (owner is null || !this._callGraph.IsFullyVisible(owner))
      return IrFunctionTargetSet.Unknown;

    var calls = this._callGraph.DirectCallsTo(owner);
    if (calls.Count == 0)
      return IrFunctionTargetSet.Unknown;

    var actuals = new List<IrFunctionTargetSet>(calls.Count);
    foreach (var call in calls) {
      if (argument.Index >= call.ArgCount)
        return IrFunctionTargetSet.Unknown;
      actuals.Add(this.Resolve(call.GetOperand(argument.Index + 1)));
    }

    return IrFunctionTargetSet.Union(actuals);
  }

  /// <summary>
  /// A local pointer cell is exact when its address never escapes and every access is a compatible
  /// direct load/store. At least one store must dominate this load; otherwise the implicit zero value
  /// is another possible target. All stores are joined because a later store can feed a later
  /// execution of the same load in a loop.
  /// </summary>
  private IrFunctionTargetSet ResolveClosedLocalLoad(IrLoad load) {
    if (load.Pointer is not IrAlloca { Count: 1 } storage || !storage.Allocated.IsPointer
        || !load.Type.SameStorage(storage.Allocated))
      return IrFunctionTargetSet.Unknown;
    if (load.Parent?.Parent is not { } owner || owner.HasErrorHandler || owner.HasInlineAsm)
      return IrFunctionTargetSet.Unknown;

    var stores = new List<IrStore>();
    foreach (var user in storage.Users)
      switch (user) {
        case IrLoad read when ReferenceEquals(read.Pointer, storage)
                              && read.Type.SameStorage(storage.Allocated)
                              && ReferenceEquals(read.Parent?.Parent, owner):
          break;
        case IrStore store when ReferenceEquals(store.Pointer, storage)
                                && !ReferenceEquals(store.Value, storage)
                                && store.Value.Type.SameStorage(storage.Allocated)
                                && ReferenceEquals(store.Parent?.Parent, owner):
          stores.Add(store);
          break;
        default:
          return IrFunctionTargetSet.Unknown;
      }

    if (stores.Count == 0 || !stores.Any(store => this.Dominates(store, load, owner)))
      return IrFunctionTargetSet.Unknown;

    return IrFunctionTargetSet.Union(stores.Select(store => this.Resolve(store.Value)));
  }

  private bool Dominates(IrInstruction definition, IrInstruction use, IrFunction owner) {
    if (definition.Parent is not { } definitionBlock || use.Parent is not { } useBlock)
      return false;

    if (ReferenceEquals(definitionBlock, useBlock)) {
      foreach (var instruction in definitionBlock.Instructions) {
        if (ReferenceEquals(instruction, definition))
          return true;
        if (ReferenceEquals(instruction, use))
          return false;
      }
      return false;
    }

    if (!this._dominators.TryGetValue(owner, out var dominators)) {
      dominators = IrDominators.Build(owner)!;
      this._dominators[owner] = dominators;
    }
    return dominators.Dominates(definitionBlock, useBlock);
  }
}
