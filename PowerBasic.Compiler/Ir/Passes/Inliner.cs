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

  private const int MaxCalleeInstructions = 64;     // keep code growth bounded

  /// <summary>Inlines eligible direct calls across the module; returns how many were inlined.</summary>
  public static int Run(IrModule module) {
    var inlined = 0;
    foreach (var fn in module.Functions) {
      // A function with an armed error handler is not duplicable, in either direction. Its blocks are
      // the target of a jump the CFG does not show, and IrBlockAddress is a CONSTANT - IrCloner maps
      // values, so a cloned handler address still points at the original function's block, which the
      // emitter then cannot find. Inlining into such a caller is no better: the handler's saved frame
      // describes a frame whose contents just changed underneath it.
      if (fn.IsDeclaration || fn.HasErrorHandler)
        continue;
      foreach (var call in fn.AllInstructions.OfType<IrCall>().ToList())
        if (call.Parent is not null && call.Callee is IrFunction callee
            && !callee.HasErrorHandler && IsInlinable(callee, fn)) {
          InlineCall(call, callee, fn, inlined);
          ++inlined;
        }
    }
    return inlined;
  }

  private static bool IsInlinable(IrFunction callee, IrFunction caller) =>
    !callee.IsDeclaration
    && !callee.NoInline                               // the source pinned it as a real call
    && !ReferenceEquals(callee, caller)               // no direct recursion
    && callee.AllInstructions.Count() <= MaxCalleeInstructions;

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
