namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0307 speculative devirtualization. Versions an indirect call around one statically likely target:
/// a guarded direct-call fast path and the original indirect call as the fallback.
/// </summary>
public static class SpeculativeDevirtualization {

  /// <summary>
  /// Speculatively devirtualizes indirect calls for which the module provides one unambiguous static
  /// candidate. A candidate is signature-compatible and has its address used as data; a unique use in
  /// the caller wins over the module-wide heuristic. The original call remains on the mismatch path.
  /// </summary>
  public static int Run(IrModule module) {
    var sites = module.Functions
      .Where(function => !function.IsDeclaration && !function.HasErrorHandler && !function.HasInlineAsm)
      .SelectMany(function => function.AllInstructions.OfType<IrCall>()
        .Where(call => call.Callee is not IrFunction)
        .Select(call => (Function: function, Call: call)))
      .ToList();

    // Pick all candidates before rewriting anything. The guards/direct calls introduced below are
    // address uses themselves and must not perturb the heuristic for later sites in the same sweep.
    var plans = sites
      .Select(site => (site.Call, Candidate: CandidateFor(module, site.Function, site.Call)))
      .Where(plan => plan.Candidate is not null)
      .Select(plan => (plan.Call, Candidate: plan.Candidate!))
      .Where(plan => !IsGuardedFallback(plan.Call, plan.Candidate))
      .ToList();

    var changed = 0;
    foreach (var (call, candidate) in plans)
      if (VersionCall(call, candidate))
        ++changed;
    return changed;
  }

  private static IrFunction? CandidateFor(IrModule module, IrFunction caller, IrCall call) {
    var compatible = module.Functions
      .Where(candidate => IsCompatible(candidate, call) && IsAddressTaken(candidate))
      .ToList();

    var local = compatible.Where(candidate => IsAddressUsedIn(candidate, caller)).Take(2).ToList();
    if (local.Count == 1)
      return local[0];
    if (local.Count > 1)
      return null;

    return compatible.Take(2).ToList() is [var only] ? only : null;
  }

  private static bool IsCompatible(IrFunction candidate, IrCall call) {
    if (candidate.IsVarArgs || !candidate.ReturnType.Equals(call.Type)
        || candidate.Parameters.Count != call.ArgCount)
      return false;

    using var parameters = candidate.Parameters.Select(parameter => parameter.Type).GetEnumerator();
    using var arguments = call.Args.Select(argument => argument.Type).GetEnumerator();
    while (parameters.MoveNext() && arguments.MoveNext())
      if (!parameters.Current.Equals(arguments.Current))
        return false;
    return true;
  }

  private static bool IsAddressTaken(IrFunction candidate)
    => candidate.Users.Any(user => !IsDirectCallUse(user, candidate));

  private static bool IsAddressUsedIn(IrFunction candidate, IrFunction caller)
    => candidate.Users.Any(user => ReferenceEquals(user.Parent?.Parent, caller)
      && !IsDirectCallUse(user, candidate));

  private static bool IsDirectCallUse(IrInstruction user, IrFunction candidate)
    => user is IrCall call && ReferenceEquals(call.Callee, candidate);

  /// <summary>
  /// A second pipeline run must not version the fallback again. Recognize the exact guard shape this
  /// pass creates rather than adding optimization-only metadata to the otherwise target-neutral call.
  /// </summary>
  private static bool IsGuardedFallback(IrCall call, IrFunction candidate) {
    if (call.Parent is not { } fallback)
      return false;

    foreach (var predecessor in fallback.Predecessors) {
      if (predecessor.Terminator is not IrCondBr branch
          || !ReferenceEquals(branch.IfFalse, fallback)
          || branch.Condition is not IrCmp { Pred: IrCmpPred.Eq } comparison)
        continue;
      if (ReferenceEquals(comparison.Lhs, call.Callee) && ReferenceEquals(comparison.Rhs, candidate)
          || ReferenceEquals(comparison.Rhs, call.Callee) && ReferenceEquals(comparison.Lhs, candidate))
        return true;
    }
    return false;
  }

  private static bool VersionCall(IrCall call, IrFunction candidate) {
    if (call.Parent is not { Parent: { } function } block || block.Terminator is null)
      return false;

    var tail = block.Instructions.SkipWhile(instruction => !ReferenceEquals(instruction, call)).ToList();
    if (tail.Count == 0 || !tail[^1].IsTerminator)
      return false;

    var originalCallee = call.Callee;
    var arguments = call.Args.ToArray();
    var oldSuccessors = block.Successors.Distinct<IrBasicBlock>(ReferenceEqualityComparer.Instance).ToArray();
    var direct = CreateFreshBlock(function, block.Label + ".devirt.direct");
    var fallback = CreateFreshBlock(function, block.Label + ".devirt.fallback");
    var continuation = CreateFreshBlock(function, block.Label + ".devirt.cont");

    foreach (var instruction in tail)
      block.Remove(instruction);
    foreach (var instruction in tail.Skip(1))
      continuation.Append(instruction);
    fallback.Append(call);

    var comparison = block.Append(new IrCmp(IrCmpPred.Eq, originalCallee, candidate));
    block.Append(new IrCondBr(comparison, direct, fallback));

    var directCall = direct.Append(new IrCall(call.Type, candidate, arguments, call.Convention));
    direct.Append(new IrBr(continuation));
    fallback.Append(new IrBr(continuation));

    // The old terminator now lives in continuation, so successor phis must name continuation as their
    // predecessor instead of the block that now ends in the devirtualization guard.
    foreach (var successor in oldSuccessors)
      foreach (var phi in successor.Phis)
        phi.RenameIncomingBlock(block, continuation);

    if (!call.Type.IsVoid) {
      var merged = continuation.AppendPhi(new IrPhi(call.Type));
      call.ReplaceAllUsesWith(merged);
      merged.AddIncoming(directCall, direct);
      merged.AddIncoming(call, fallback);
    }

    return true;
  }

  private static IrBasicBlock CreateFreshBlock(IrFunction function, string stem) {
    var label = stem;
    for (var suffix = 2; function.Blocks.Any(block => block.Label == label); ++suffix)
      label = $"{stem}.{suffix}";
    return function.CreateBlock(label);
  }
}
