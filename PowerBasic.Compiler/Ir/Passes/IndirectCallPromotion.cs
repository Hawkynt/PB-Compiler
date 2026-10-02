using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0271 — promotes the hottest profiled target of an indirect call to a guarded direct call.
/// The miss path retains the original indirect call, so profile accuracy affects profitability only,
/// never semantics. Legality stays in this transform; the dynamic-frequency/code-growth decision is
/// delegated to <see cref="IIrCallCostModel"/>. The new direct edge is deliberately exposed before
/// inlining and interprocedural passes run, which is the point of the transform.
/// </summary>
public static class IndirectCallPromotion {

  /// <summary>Promotes eligible indirect calls in <paramref name="module"/>; returns the number changed.</summary>
  public static int Run(IrModule module, IIrCallCostModel? costModel = null) {
    ArgumentNullException.ThrowIfNull(module);
    return Run(module, new IrModuleAnalysisManager(module), costModel).Changes;
  }

  /// <summary>
  /// Analysis-aware promotion. Exact singleton callees belong to O0279 and are excluded before profile
  /// profitability is considered; O0271 only versions calls whose target remains incomplete.
  /// </summary>
  public static IrModulePassResult Run(
      IrModule module,
      IrModuleAnalysisManager analyses,
      IIrCallCostModel? costModel = null) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(analyses);
    if (!ReferenceEquals(module, analyses.Module))
      throw new ArgumentException("Module analysis manager belongs to a different module.", nameof(analyses));
    costModel ??= IrDefaultCallCostModel.Instance;

    var exactTargets = analyses.Get(IrModuleAnalyses.FunctionTargets);

    // Decide the complete sweep before mutating anything. Promotion adds comparisons/direct calls that
    // are themselves function-value users; allowing those new users to perturb later target/profitability
    // decisions would make the result depend on traversal order and would consult stale cached facts.
    var plans = new List<(IrFunction Function, IrCall Call, IrFunction Target)>();
    foreach (var function in module.Functions) {
      if (function.IsDeclaration || function.HasErrorHandler || function.HasInlineAsm)
        continue;
      foreach (var call in function.AllInstructions.OfType<IrCall>()) {
        if (call.Callee is IrFunction || exactTargets.ResolveUnique(call.Callee) is not null)
          continue;
        if (TrySelectTarget(module, call, costModel, out var target))
          plans.Add((function, call, target));
      }
    }

    foreach (var (function, call, target) in plans)
      Promote(call, target, function);

    return plans.Count == 0 ? IrModulePassResult.Unchanged : IrModulePassResult.Changed(plans.Count);
  }

  private static bool TrySelectTarget(
      IrModule module,
      IrCall call,
      IIrCallCostModel costModel,
      out IrFunction target) {
    target = null!;
    if (call.Parent is null || call.Callee is IrFunction || call.GetIndirectTargetProfile() is not { } profile)
      return false;

    var ordered = profile.Targets.OrderByDescending(static item => item.Count).ToArray();
    var hottest = ordered[0];
    if (ordered.Length > 1 && hottest.Count == ordered[1].Count)
      return false; // there is no single dominant target; do not make profile order a semantic choice
    if (!costModel.PreferIndirectCallPromotion(hottest.Count, profile.TotalCount))
      return false;
    if (!module.Functions.Any(candidate => ReferenceEquals(candidate, hottest.Target)))
      return false; // stale profile naming a function from another module
    if (!HasCompatibleSignature(call, hottest.Target))
      return false;

    target = hottest.Target;
    return true;
  }

  private static bool HasCompatibleSignature(IrCall call, IrFunction target) {
    if (!call.Type.SameStorage(target.ReturnType))
      return false;

    var args = call.Args.ToArray();
    if (target.IsVarArgs ? args.Length < target.Parameters.Count : args.Length != target.Parameters.Count)
      return false;
    for (var i = 0; i < target.Parameters.Count; ++i)
      if (!args[i].Type.SameStorage(target.Parameters[i].Type))
        return false;
    return true;
  }

  private static void Promote(IrCall call, IrFunction target, IrFunction function) {
    var host = call.Parent!;
    var callee = call.Callee;
    var args = call.Args.ToArray();
    var users = call.Users.ToArray();

    var direct = function.CreateBlock(UniqueLabel(function, $"{host.Label}.icp.direct"));
    var fallback = function.CreateBlock(UniqueLabel(function, $"{host.Label}.icp.fallback"));
    var continuation = function.CreateBlock(UniqueLabel(function, $"{host.Label}.icp.cont"));

    // Split after the call. The old terminator now leaves from the continuation, so every successor
    // phi must name that block instead of the original host.
    foreach (var instruction in host.Instructions.SkipWhile(item => !ReferenceEquals(item, call)).Skip(1).ToArray()) {
      host.Remove(instruction);
      continuation.Append(instruction);
    }
    foreach (var successor in continuation.Successors)
      foreach (var phi in successor.Phis)
        phi.RenameIncomingBlock(host, continuation);

    // Reuse the original call on the miss path: it preserves the exact callee value, arguments and
    // convention. Clearing its profile makes the pass idempotent when the pipeline runs again.
    host.Remove(call);
    fallback.Append(call);
    call.SetIndirectTargetProfile(null);

    var directCall = direct.Append(new IrCall(call.Type, target, args, call.Convention));
    direct.Append(new IrBr(continuation));
    fallback.Append(new IrBr(continuation));

    // If the indirect call produced a value, both arms feed one continuation phi. Snapshotting users
    // before creating the phi is essential: otherwise ReplaceAllUsesWith would also rewrite the new
    // phi's fallback operand to itself.
    if (!call.Type.IsVoid && users.Length > 0) {
      var result = continuation.AppendPhi(new IrPhi(call.Type));
      result.AddIncoming(directCall, direct);
      result.AddIncoming(call, fallback);
      foreach (var user in users)
        user.ReplaceOperand(call, result);
    }

    var isTarget = host.Append(new IrCmp(IrCmpPred.Eq, callee, target));
    host.Append(new IrCondBr(isTarget, direct, fallback));
  }

  private static string UniqueLabel(IrFunction function, string stem) {
    if (function.Blocks.All(block => block.Label != stem))
      return stem;
    for (var suffix = 2; ; ++suffix) {
      var candidate = $"{stem}.{suffix}";
      if (function.Blocks.All(block => block.Label != candidate))
        return candidate;
    }
  }
}
