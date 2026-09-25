using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0018 / O0159 — interprocedural constant propagation across the call graph, in both directions.
///
/// <para>
/// <b>Arguments in.</b> When every call to a function passes the same constant for a parameter, that
/// parameter is that constant inside the body. Replacing it lets SCCP fold the arithmetic built from
/// it, and lets whole branches that test it disappear — which is the point: a SUB called once with a
/// literal flag is really two SUBs, and this is what tells the rest of the pipeline so.
/// </para>
/// <para>
/// <b>Results out.</b> When every <c>ret</c> in a function returns the same constant, the call result
/// is that constant at every call site, and the callers fold in turn. A FUNCTION whose body is a
/// lookup that always lands on the same answer costs a call and a frame for a number the compiler
/// already knows.
/// </para>
/// <para>
/// Both directions rest on the same precondition, which is the whole reason this pass is careful:
/// the module must see <b>every</b> call. A function whose address is taken, or which is exported for
/// another unit to call, can be entered with arguments this module never wrote down, so "every call
/// site passes 1" is a statement about the calls that happen to be visible and not about the program.
/// <see cref="IsFullyVisible"/> is where that is decided, and it errs toward declining.
/// </para>
/// <para>
/// It runs to a fixpoint, because propagating a return value into a caller can make one of THAT
/// caller's arguments constant, which can make its return constant, and so on up the graph. Once the
/// constant fixpoint is reached, O0069 consumes any parameters that became dead and can specialize a
/// bounded dominant literal call shape while those whole-program ownership facts are still available.
/// </para>
/// </summary>
public static class IpConstantProp {

  /// <summary>Propagates as far as it can through <paramref name="module"/>; returns how many values it replaced.</summary>
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    return RunCore(module, IrCallGraph.Build(module));
  }

  /// <summary>Analysis-aware module-pass entry using the shared cached call graph.</summary>
  public static IrModulePassResult Run(IrModule module, IrModuleAnalysisManager analyses) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(analyses);
    if (!ReferenceEquals(module, analyses.Module))
      throw new ArgumentException("Module analysis manager belongs to a different module.", nameof(analyses));

    var changes = RunCore(module, analyses.Get(IrModuleAnalyses.CallGraph));
    return changes == 0 ? IrModulePassResult.Unchanged : IrModulePassResult.Changed(changes);
  }

  private static int RunCore(IrModule module, IrCallGraph callGraph) {
    var replaced = 0;
    for (var changed = true; changed;) {
      changed = false;
      foreach (var function in module.Functions.ToList()) {
        if (function.IsDeclaration || function.HasErrorHandler || !callGraph.IsFullyVisible(function))
          continue;
        var took = PropagateArguments(function, callGraph) + PropagateResult(function, callGraph);
        if (took > 0) {
          replaced += took;
          changed = true;
        }
      }
    }
    return replaced + DeadParameterElimination.Run(module);
  }

  /// <summary>
  /// Whether this module contains every call that can reach <paramref name="function"/>. It must not
  /// be the entry point (the runtime calls that one), and its every use must be the CALLEE operand of
  /// a call — a use in any other position is the address escaping into a variable, a table or an
  /// argument, after which the call sites are no longer enumerable.
  /// </summary>
  internal static bool IsFullyVisible(IrModule module, IrFunction function) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(function);
    return IrCallGraph.Build(module).IsFullyVisible(function);
  }

  /// <summary>Two constants agree only when the same declared type carries the same bits.</summary>
  private static bool Same(IrValue a, IrValue b) => (a, b) switch {
    (IrConstantInt x, IrConstantInt y) => x.Value == y.Value && Equals(x.Type, y.Type),
    (IrConstantFloat x, IrConstantFloat y) => x.SameBits(y),
    _ => ReferenceEquals(a, b),
  };

  private static bool IsConstant(IrValue value) => value is IrConstantInt or IrConstantFloat;

  private static int PropagateArguments(IrFunction function, IrCallGraph callGraph) {
    var calls = callGraph.DirectCallsTo(function).ToList();
    if (calls.Count == 0)
      return 0;

    var replaced = 0;
    for (var i = 0; i < function.Parameters.Count; ++i) {
      var parameter = function.Parameters[i];
      if (parameter.HasNoUsers)
        continue;

      IrValue? agreed = null;
      foreach (var call in calls) {
        if (i >= call.ArgCount) {
          agreed = null;                           // a call with a different shape: say nothing
          break;
        }
        var argument = call.Operands[i + 1];
        if (!IsConstant(argument) || (agreed is not null && !Same(agreed, argument))) {
          agreed = null;
          break;
        }
        agreed ??= argument;
      }

      if (agreed is null || !Equals(agreed.Type, parameter.Type))
        continue;                                  // a widened or narrowed argument is not this value
      parameter.ReplaceAllUsesWith(agreed);
      ++replaced;
    }
    return replaced;
  }

  private static int PropagateResult(IrFunction function, IrCallGraph callGraph) {
    if (function.ReturnType.Kind == IrTypeKind.Void)
      return 0;

    IrValue? agreed = null;
    var returns = 0;
    foreach (var block in function.Blocks)
      if (block.Terminator is IrRet { HasValue: true } ret) {
        ++returns;
        var value = ret.Value!;
        if (!IsConstant(value) || (agreed is not null && !Same(agreed, value)))
          return 0;
        agreed ??= value;
      }
    if (agreed is null || returns == 0 || !Equals(agreed.Type, function.ReturnType))
      return 0;

    var replaced = 0;
    foreach (var call in callGraph.DirectCallsTo(function).ToList()) {
      if (call.HasNoUsers)
        continue;
      call.ReplaceAllUsesWith(agreed);              // the call itself stays: the body may still print
      ++replaced;
    }
    return replaced;
  }
}
