namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Function inlining for direct calls to non-recursive defined callees within a size
/// budget. The callee's blocks are cloned into the caller (IrCloner), parameters are
/// mapped to the call arguments, the call site's block is split so the code after the
/// call becomes a continuation, each cloned <c>ret</c> branches to that continuation,
/// and the call's result is the single returned value (or a phi over the returns).
/// Eliminates call overhead and exposes the callee body to the caller's optimizer.
/// </summary>
public static class Inliner {

  private const int DefaultMaxCalleeInstructions = 64;
  private const int SpeedMaxCalleeInstructions = 256;
  private const int ProfileMaxCalleeInstructions = 512;
  private const int SpeedProfileMaxCalleeInstructions = 1024;
  private const ulong DefaultProfileGrowthPenalty = 8;
  private const ulong SpeedProfileGrowthPenalty = 4;

  /// <summary>
  /// Inlines eligible direct calls across the module; returns how many were inlined. SPEED accepts a
  /// larger body because eliminating call/argument/return overhead and exposing the body to the rest
  /// of the SSA pipeline matters more than code growth. <c>NOINLINE</c> remains an absolute contract.
  /// </summary>
  /// <param name="module">The module whose direct calls may be substituted.</param>
  /// <param name="optimizeForSpeed">Whether code growth is traded more aggressively for runtime work.</param>
  /// <param name="callEdgeCount">
  /// Optional profile lookup for a call edge. A returned count is the observed execution count for
  /// that exact call site; <see langword="null"/> means no profile is available for the site and keeps
  /// the ordinary static budget. An explicit zero means the profiled edge was never taken and therefore
  /// earns no inline budget. O0268 can supply this lookup once profile loading/stable identities exist;
  /// keeping the lookup abstract here avoids coupling the inliner to that representation.
  /// </param>
  public static int Run(IrModule module, bool optimizeForSpeed = false, Func<IrCall, ulong?>? callEdgeCount = null) {
    var inlined = 0;
    foreach (var fn in module.Functions) {
      // A function with an armed error handler is not duplicable, in either direction. Its blocks are
      // the target of a jump the CFG does not show, and IrBlockAddress is a CONSTANT - IrCloner maps
      // values, so a cloned handler address still points at the original function's block, which the
      // emitter then cannot find. Inlining into such a caller is no better: the handler's saved frame
      // describes a frame whose contents just changed underneath it.
      //
      // Inline assembly is the same kind of wall. It may name the caller's frame slots and BASIC
      // labels, so changing that frame by cloning another body into it is not a transformation the IR
      // can prove safe. The ordinary function pipeline already skips such bodies; the module inliner
      // must honour the same boundary rather than sneaking around it.
      if (fn.IsDeclaration || fn.HasErrorHandler || fn.HasInlineAsm)
        continue;
      foreach (var call in fn.AllInstructions.OfType<IrCall>().ToList())
        if (call.Parent is not null && call.Callee is IrFunction callee
            && !callee.HasErrorHandler
            && IsInlinable(callee, fn, InlineBudgetFor(call, optimizeForSpeed, callEdgeCount?.Invoke(call)))) {
          InlineCall(call, callee, fn, inlined);
          ++inlined;
        }
    }
    return inlined;
  }

  /// <summary>
  /// Converts one edge count into the maximum body growth that edge has earned. The static inliner is
  /// deliberately binary: a callee is either under 64/256 instructions or it is not. A profile lets
  /// the budget follow payoff instead. Each execution is credited with two target-neutral units for
  /// eliminating CALL/RET plus one for every argument transfer; the accumulated credit is divided by
  /// a code-growth penalty and capped so even pathological profiles cannot inline an arbitrarily large
  /// body. SPEED halves the penalty and raises the cap, matching its existing willingness to spend size.
  /// </summary>
  private static int InlineBudgetFor(IrCall call, bool optimizeForSpeed, ulong? edgeCount) {
    if (edgeCount is null)
      return optimizeForSpeed ? SpeedMaxCalleeInstructions : DefaultMaxCalleeInstructions;

    var payoffPerExecution = 2UL + (ulong)call.ArgCount;
    var count = edgeCount.Value;
    var weightedPayoff = count > ulong.MaxValue / payoffPerExecution
      ? ulong.MaxValue
      : count * payoffPerExecution;
    var growthPenalty = optimizeForSpeed ? SpeedProfileGrowthPenalty : DefaultProfileGrowthPenalty;
    var earnedBudget = weightedPayoff / growthPenalty;
    var hardCap = optimizeForSpeed ? SpeedProfileMaxCalleeInstructions : ProfileMaxCalleeInstructions;
    return (int)Math.Min((ulong)hardCap, earnedBudget);
  }

  private static bool IsInlinable(IrFunction callee, IrFunction caller, int maxCalleeInstructions) =>
    !callee.IsDeclaration
    && !callee.NoInline                               // the source pinned it as a real call
    // ...and neither is a body holding inline assembly, for the same reason an error handler is not:
    // IrCloner has no case for IrInlineAsm and throws. The block is deliberately OPAQUE - it carries
    // its text and the operands the lowering bound, and nothing maps that through a clone - so the
    // attempt aborted the whole compile with "cannot clone IrInlineAsm" on any `!` block inside a SUB
    // small enough to inline, valid assembly included. Declining to inline it is the same trade
    // HasErrorHandler already makes, and it costs nothing: the block is an optimization barrier
    // wherever it sits.
    && !callee.HasInlineAsm
    && !ReferenceEquals(callee, caller)               // no direct recursion
    && callee.AllInstructions.Count() <= maxCalleeInstructions;

  private static void InlineCall(IrCall call, IrFunction callee, IrFunction caller, int id) {
    var host = call.Parent!;
    var prefix = $"inl{id}.";

    // 1. split the host block at the call: everything after it becomes the continuation
    var cont = caller.CreateBlock(prefix + "cont");
    var after = host.Instructions.SkipWhile(i => !ReferenceEquals(i, call)).Skip(1).ToList();
    foreach (var inst in after) {
      host.Remove(inst);
      cont.Append(inst);
    }
    // the moved terminator now leaves from cont, so successor phis must name cont, not host
    foreach (var succ in cont.Successors)
      foreach (var phi in succ.Phis)
        phi.RenameIncomingBlock(host, cont);

    // 2. map parameters to the call arguments and clone the callee body in
    var seed = new Dictionary<IrValue, IrValue>(ReferenceEqualityComparer.Instance);
    for (var i = 0; i < callee.Parameters.Count; ++i)
      seed[callee.Parameters[i]] = call.GetOperand(1 + i);
    var blocks = IrCloner.Clone(caller, callee.Blocks, seed, prefix);

    // 3. turn each cloned return into a branch to the continuation, collecting its value
    var returns = new List<(IrValue Value, IrBasicBlock From)>();
    foreach (var cloned in blocks.Values)
      if (cloned.Terminator is IrRet ret) {
        if (ret.HasValue)
          returns.Add((ret.Value!, cloned));
        ret.EraseFromParent();
        cloned.Append(new IrBr(cont));
      }

    // 4. wire the call's result and the entry edge
    if (!call.Type.IsVoid && returns.Count > 0) {
      if (returns.Count == 1) {
        call.ReplaceAllUsesWith(returns[0].Value);
      } else {
        var phi = new IrPhi(call.Type);
        cont.AppendPhi(phi);
        foreach (var (value, from) in returns)
          phi.AddIncoming(value, from);
        call.ReplaceAllUsesWith(phi);
      }
    }

    call.EraseFromParent();
    host.Append(new IrBr(blocks[callee.Entry!]));
  }
}
