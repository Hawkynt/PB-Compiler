namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Function inlining for the safe, common case: a direct call to a non-recursive,
/// defined function whose body is a single straight-line block ending in a return.
/// Such a callee has no internal control flow, so inlining is a clean splice - its
/// instructions are cloned before the call site with parameters remapped to the call
/// arguments, and the call is replaced by the (remapped) returned value. Restricting
/// to single-block callees avoids block cloning, phi fix-ups and recursion blow-up,
/// while still folding away the abundant small expression-bodied helpers. Run it
/// between per-function optimization rounds so the exposed body is then optimized in
/// the caller's context.
/// </summary>
public static class Inliner {

  /// <summary>Inlines eligible single-block calls across the module; returns how many were inlined.</summary>
  public static int Run(IrModule module) {
    var inlined = 0;
    foreach (var fn in module.Functions) {
      if (fn.IsDeclaration)
        continue;
      foreach (var call in fn.AllInstructions.OfType<IrCall>().ToList())
        if (call.Parent is not null && call.Callee is IrFunction callee && !ReferenceEquals(callee, fn) && IsInlinable(callee)) {
          InlineSingleBlock(call, callee);
          ++inlined;
        }
    }
    return inlined;
  }

  private static bool IsInlinable(IrFunction callee) {
    if (callee.IsDeclaration || callee.Blocks.Count != 1)
      return false;
    var block = callee.Entry!;
    if (block.Terminator is not IrRet)
      return false;
    // every non-terminator instruction must be one we can clone
    foreach (var inst in block.Instructions)
      if (inst is not (IrBinary or IrCmp or IrCast or IrAlloca or IrLoad or IrStore or IrGep or IrCall or IrRet))
        return false;
    return true;
  }

  private static void InlineSingleBlock(IrCall call, IrFunction callee) {
    var map = new Dictionary<IrValue, IrValue>(ReferenceEqualityComparer.Instance);
    for (var i = 0; i < callee.Parameters.Count; ++i)
      map[callee.Parameters[i]] = call.GetOperand(1 + i);          // operand 0 is the callee

    IrValue Remap(IrValue v) => map.GetValueOrDefault(v, v);

    IrValue? result = null;
    var host = call.Parent!;
    foreach (var inst in callee.Entry!.Instructions) {
      if (inst is IrRet ret) {
        result = ret.HasValue ? Remap(ret.Value!) : null;
        break;
      }
      var clone = CloneInstruction(inst, Remap);
      host.InsertBefore(clone, call);
      map[inst] = clone;
    }

    if (!call.Type.IsVoid)
      call.ReplaceAllUsesWith(result ?? new IrUndef(call.Type));
    call.EraseFromParent();
  }

  private static IrInstruction CloneInstruction(IrInstruction inst, Func<IrValue, IrValue> map) => inst switch {
    IrBinary b => new IrBinary(b.Op, map(b.Lhs), map(b.Rhs)),
    IrCmp c => new IrCmp(c.Pred, map(c.Lhs), map(c.Rhs)),
    IrCast x => new IrCast(x.Op, map(x.Value), x.Type),
    IrAlloca a => new IrAlloca(a.Allocated) { Count = a.Count },
    IrLoad l => new IrLoad(l.Type, map(l.Pointer)),
    IrStore s => new IrStore(map(s.Value), map(s.Pointer)),
    IrGep g => new IrGep(map(g.BasePtr), map(g.ByteOffset)),
    IrCall c => new IrCall(c.Type, map(c.Callee), c.Args.Select(map).ToList()),
    _ => throw new InvalidOperationException($"cannot clone {inst.GetType().Name} for inlining"),
  };
}
