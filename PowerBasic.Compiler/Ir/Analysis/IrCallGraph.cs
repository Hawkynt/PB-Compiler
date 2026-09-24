namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>
/// Module-wide direct-call graph and call-site visibility facts. Indirect calls remain explicit unknown
/// edges; consumers that require a closed call graph must check <see cref="HasIndirectCallsFrom"/>.
/// </summary>
public sealed class IrCallGraph {
  private readonly HashSet<IrFunction> _functions;
  private readonly Dictionary<IrFunction, List<IrCall>> _callsFrom
    = new(ReferenceEqualityComparer.Instance);
  private readonly Dictionary<IrFunction, List<IrCall>> _callsTo
    = new(ReferenceEqualityComparer.Instance);
  private readonly HashSet<IrFunction> _indirectCallers
    = new(ReferenceEqualityComparer.Instance);

  private IrCallGraph(IrModule module) {
    this._functions = new(module.Functions, ReferenceEqualityComparer.Instance);

    foreach (var caller in module.Functions) {
      if (caller.IsDeclaration)
        continue;
      foreach (var call in caller.AllInstructions.OfType<IrCall>()) {
        if (call.Callee is not IrFunction callee) {
          this._indirectCallers.Add(caller);
          continue;
        }

        (this._callsFrom.TryGetValue(caller, out var outgoing)
          ? outgoing
          : this._callsFrom[caller] = []).Add(call);
        (this._callsTo.TryGetValue(callee, out var incoming)
          ? incoming
          : this._callsTo[callee] = []).Add(call);
      }
    }
  }

  public static IrCallGraph Build(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    return new(module);
  }

  /// <summary>Direct call sites originating in <paramref name="function"/>.</summary>
  public IReadOnlyList<IrCall> DirectCallsFrom(IrFunction function)
    => this._callsFrom.TryGetValue(function, out var calls) ? calls : [];

  /// <summary>Direct call sites targeting <paramref name="function"/>.</summary>
  public IReadOnlyList<IrCall> DirectCallsTo(IrFunction function)
    => this._callsTo.TryGetValue(function, out var calls) ? calls : [];

  /// <summary>Distinct direct callees referenced by <paramref name="function"/>.</summary>
  public IEnumerable<IrFunction> DirectCalleesOf(IrFunction function)
    => this.DirectCallsFrom(function)
      .Select(call => (IrFunction)call.Callee)
      .Distinct(ReferenceEqualityComparer.Instance);

  /// <summary>True when the function contains at least one call whose callee is not a known function value.</summary>
  public bool HasIndirectCallsFrom(IrFunction function) => this._indirectCallers.Contains(function);

  /// <summary>
  /// True when every possible call site is represented by a direct call in this module. The program entry is
  /// intentionally excluded because the runtime invokes it, and any non-callee use means the function address
  /// escaped into a value whose eventual calls are not enumerable here.
  /// </summary>
  public bool IsFullyVisible(IrFunction function) {
    if (!this._functions.Contains(function)
        || function.Name.Equals("main", StringComparison.OrdinalIgnoreCase))
      return false;

    foreach (var user in function.Users) {
      if (user is not IrCall call || !ReferenceEquals(call.Callee, function))
        return false;
      if (user.Parent?.Parent is not { } owner || !this._functions.Contains(owner))
        return false;
    }
    return true;
  }

  /// <summary>True when a function value has a use owned outside this module or detached from any function.</summary>
  internal bool HasExternalUse(IrFunction function) {
    foreach (var user in function.Users)
      if (user.Parent?.Parent is not { } owner || !this._functions.Contains(owner))
        return true;
    return false;
  }
}
