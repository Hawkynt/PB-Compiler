namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0279 — whole-program devirtualization for indirect calls whose complete target set contains one
/// procedure.
///
/// <para>
/// This is deliberately a proof pass, not a prediction pass. It follows only value relations the IR
/// makes exact: a function value, pointer-preserving bitcasts, phi/select merges, closed local pointer
/// cells, and formal parameters of functions whose every caller is visible in this module. Anything
/// else is incomplete and stays indirect. In particular, a null-capable value or a set containing two
/// functions is not rewritten; guarded promotion belongs to O0271/O0307.
/// </para>
/// <para>
/// Parameters are where the whole-program part matters. If every visible caller passes the same
/// procedure for a callback parameter, a call through that parameter has one complete target even
/// though no local function pass can prove it. The visibility predicate is shared with
/// <see cref="IpConstantProp"/> because both optimizations require exactly the same promise: there is
/// no caller outside the module and the function's address has not escaped.
/// </para>
/// </summary>
public static class WholeProgramDevirtualization {

  /// <summary>Replaces provably singleton indirect callees with their direct <see cref="IrFunction"/>.</summary>
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);

    var resolver = new TargetResolver(module);
    var changed = 0;
    foreach (var function in module.Functions) {
      if (function.IsDeclaration || function.HasErrorHandler || function.HasInlineAsm)
        continue;

      foreach (var call in function.AllInstructions.OfType<IrCall>().ToList()) {
        if (call.Callee is IrFunction)
          continue;
        var target = resolver.ResolveUnique(call.Callee);
        if (target is null || !SignatureMatches(call, target))
          continue;

        call.SetOperand(0, target);
        ++changed;
      }
    }
    return changed;
  }

  /// <summary>
  /// Opaque pointers do not carry a signature, so target provenance alone is insufficient. The direct
  /// callee must agree with the call shape the indirect site already had; otherwise replacing the
  /// operand would turn an invalid/incompatible function-pointer cast into a different ABI call.
  /// </summary>
  private static bool SignatureMatches(IrCall call, IrFunction target) {
    if (!call.Type.SameStorage(target.ReturnType))
      return false;

    var arguments = call.Args.ToList();
    if (target.IsVarArgs ? arguments.Count < target.Parameters.Count : arguments.Count != target.Parameters.Count)
      return false;

    for (var i = 0; i < target.Parameters.Count; ++i)
      if (!arguments[i].Type.SameStorage(target.Parameters[i].Type))
        return false;
    return true;
  }

  private sealed class TargetResolver(IrModule module) {

    private readonly IrModule _module = module;
    private readonly HashSet<IrFunction> _functions = new(module.Functions, ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrValue, TargetSet> _cache = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<IrValue> _active = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrFunction, IrDominators> _dominators = new(ReferenceEqualityComparer.Instance);

    public IrFunction? ResolveUnique(IrValue value) {
      var targets = this.Resolve(value);
      return targets is { IsComplete: true, HasNull: false } && targets.Functions.Count == 1
        ? targets.Functions.Single()
        : null;
    }

    private TargetSet Resolve(IrValue value) {
      if (this._cache.TryGetValue(value, out var cached))
        return cached;
      if (!this._active.Add(value))
        return TargetSet.Unknown;                    // cyclic value flow needs a real fixpoint, not a guess

      var result = this.ResolveCore(value);
      this._active.Remove(value);
      this._cache[value] = result;
      return result;
    }

    private TargetSet ResolveCore(IrValue value) => value switch {
      IrFunction function when this._functions.Contains(function) => TargetSet.For(function),
      IrNullPtr => TargetSet.Null,
      IrCast { Op: IrCastOp.BitCast } cast => this.Resolve(cast.Value),
      IrSelect select => TargetSet.Union([this.Resolve(select.IfTrue), this.Resolve(select.IfFalse)]),
      IrPhi phi => TargetSet.Union(phi.Operands.Select(this.Resolve)),
      IrArgument argument => this.ResolveArgument(argument),
      IrLoad load => this.ResolveClosedLocalLoad(load),
      _ => TargetSet.Unknown,
    };

    private TargetSet ResolveArgument(IrArgument argument) {
      var owner = argument.Parent;
      if (owner is null || !IpConstantProp.IsFullyVisible(this._module, owner))
        return TargetSet.Unknown;

      var calls = owner.Users.OfType<IrCall>()
        .Where(call => ReferenceEquals(call.Callee, owner))
        .ToList();
      if (calls.Count == 0)
        return TargetSet.Unknown;

      var actuals = new List<TargetSet>(calls.Count);
      foreach (var call in calls) {
        if (argument.Index >= call.ArgCount)
          return TargetSet.Unknown;
        actuals.Add(this.Resolve(call.Operands[argument.Index + 1]));
      }
      return TargetSet.Union(actuals);
    }

    /// <summary>
    /// A local pointer cell is exact when its address never escapes and every access is a compatible
    /// direct load/store. At least one store must dominate the load; otherwise the cell's implicit
    /// zero initialization is another possible target. All stores are joined, not merely the reaching
    /// one, because this is a whole-cell points-to fact and a later store can affect a later execution
    /// of the same call in a loop.
    /// </summary>
    private TargetSet ResolveClosedLocalLoad(IrLoad load) {
      if (load.Pointer is not IrAlloca { Count: 1 } storage || !storage.Allocated.IsPointer
          || !load.Type.SameStorage(storage.Allocated))
        return TargetSet.Unknown;
      if (load.Parent?.Parent is not { } owner || owner.HasErrorHandler || owner.HasInlineAsm)
        return TargetSet.Unknown;

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
            return TargetSet.Unknown;                // GEP, call argument, stored address, or cross-function use
        }

      if (stores.Count == 0 || !stores.Any(store => this.Dominates(store, load, owner)))
        return TargetSet.Unknown;
      return TargetSet.Union(stores.Select(store => this.Resolve(store.Value)));
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

  private sealed class TargetSet(bool isComplete, bool hasNull, HashSet<IrFunction> functions) {

    public static TargetSet Unknown { get; } = new(false, false, new(ReferenceEqualityComparer.Instance));
    public static TargetSet Null { get; } = new(true, true, new(ReferenceEqualityComparer.Instance));

    public bool IsComplete { get; } = isComplete;
    public bool HasNull { get; } = hasNull;
    public HashSet<IrFunction> Functions { get; } = functions;

    public static TargetSet For(IrFunction function)
      => new(true, false, new HashSet<IrFunction>([function], ReferenceEqualityComparer.Instance));

    public static TargetSet Union(IEnumerable<TargetSet> sets) {
      var functions = new HashSet<IrFunction>(ReferenceEqualityComparer.Instance);
      var hasNull = false;
      var any = false;
      foreach (var set in sets) {
        any = true;
        if (!set.IsComplete)
          return Unknown;
        hasNull |= set.HasNull;
        functions.UnionWith(set.Functions);
      }
      return any ? new TargetSet(true, hasNull, functions) : Unknown;
    }
  }
}
