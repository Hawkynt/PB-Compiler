namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>
/// Conservative whole-program function reachability. A complete result is produced only when the module owns
/// procedure ABI/call-graph semantics, has a program entry, has no external function uses and every reachable
/// call is direct. Otherwise every function is reported reachable and <see cref="IsComplete"/> is false.
/// </summary>
public sealed class IrWholeProgramReachability {
  private readonly HashSet<IrFunction> _reachable;

  private IrWholeProgramReachability(IEnumerable<IrFunction> reachable, bool isComplete) {
    this._reachable = new(reachable, ReferenceEqualityComparer.Instance);
    this.IsComplete = isComplete;
  }

  public bool IsComplete { get; }

  public IReadOnlySet<IrFunction> ReachableFunctions => this._reachable;

  public bool IsReachable(IrFunction function) => this._reachable.Contains(function);

  internal static IrWholeProgramReachability Build(IrModule module, IrCallGraph callGraph) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(callGraph);

    var all = module.Functions.ToArray();
    if (!module.OwnsProcedureAbi)
      return new(all, isComplete: false);

    var roots = all
      .Where(function => function.Name.Equals("main", StringComparison.OrdinalIgnoreCase))
      .ToArray();
    if (roots.Length == 0 || all.Any(callGraph.HasExternalUse))
      return new(all, isComplete: false);

    var moduleFunctions = new HashSet<IrFunction>(all, ReferenceEqualityComparer.Instance);
    var reachable = new HashSet<IrFunction>(roots, ReferenceEqualityComparer.Instance);
    var work = new Queue<IrFunction>(roots);

    while (work.Count > 0) {
      var function = work.Dequeue();
      if (function.IsDeclaration)
        continue;

      if (callGraph.HasIndirectCallsFrom(function))
        return new(all, isComplete: false);

      foreach (var callee in callGraph.DirectCalleesOf(function))
        if (moduleFunctions.Contains(callee) && reachable.Add(callee))
          work.Enqueue(callee);

      // Function values may be passed/stored without being direct callees. Keeping every function value
      // referenced by reachable code is conservative even before a later points-to analysis resolves it.
      foreach (var referenced in function.AllInstructions
                 .SelectMany(instruction => instruction.Operands)
                 .OfType<IrFunction>())
        if (moduleFunctions.Contains(referenced) && reachable.Add(referenced))
          work.Enqueue(referenced);
    }

    return new(reachable, isComplete: true);
  }
}
