namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0330 — recognizes canonical counted byte fill/copy loops and replaces them with the existing LLVM
/// memory intrinsics. The matcher is deliberately narrower than a general loop-idiom pass: one
/// byte per iteration, unit positive stride, no extra effects, and memcpy only when distinct storage
/// objects prove non-overlap.
/// </summary>
public static class LibraryCallRecognition {

  private const string _MEMCPY = "llvm.memcpy.p0.p0.i32";
  private const string _MEMSET = "llvm.memset.p0.i32";

  /// <summary>Recognizes library idioms in the module; returns the number of loops replaced.</summary>
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    var replaced = 0;
    foreach (var function in module.Functions.Where(function => !function.IsDeclaration).ToList()) {
      if (function.HasErrorHandler || function.HasInlineAsm)
        continue;
      foreach (var header in function.Blocks.ToList()) {
        if (header.Parent is null || CountedLoop.Match(function, header) is not { } loop)
          continue;
        if (TryReplace(module, function, loop))
          ++replaced;
      }
    }
    return replaced;
  }

  private static bool TryReplace(IrModule module, IrFunction function, CountedLoop loop) {
    if (loop.Preheader.Terminator is not IrBr preBranch || !ReferenceEquals(preBranch.Target, loop.Header)
        || loop.Exit.Phis.Any() || loop.Counter.IncomingFrom(loop.Preheader) is not IrConstantInt initial
        || loop.Counter.IncomingFrom(loop.Latch) is not IrBinary { Op: IrBinaryOp.Add } next
        || !ReferenceEquals(next.Lhs, loop.Counter) || next.Rhs is not IrConstantInt { Value: 1 })
      return false;

    if (!HasSingleIterationControlFlow(loop) || IrDominators.Build(function) is not { } dominators)
      return false;

    var stores = loop.Region.SelectMany(block => block.Instructions).OfType<IrStore>().ToList();
    if (stores.Count != 1)
      return false;
    var store = stores[0];
    if (store.Parent is not { } storeBlock || ReferenceEquals(storeBlock, loop.Header)
        || !dominators.Dominates(storeBlock, loop.Latch)
        || !TryIndexedByte(store.Pointer, loop.Counter, out var targetBase, out var targetGep)
        || DefinedInside(targetBase, loop.Region))
      return false;

    var allowed = new HashSet<IrInstruction>(ReferenceEqualityComparer.Instance) {
      loop.Counter, loop.Test, next, store, targetGep,
    };
    foreach (var block in loop.Region)
      if (block.Terminator is { } terminator)
        allowed.Add(terminator);

    var isCopy = false;
    IrValue? sourceBase = null;
    if (store.Value is IrLoad load && load.Parent is { } loadBlock && loop.Region.Contains(loadBlock)
        && !ReferenceEquals(loadBlock, loop.Header) && dominators.Dominates(loadBlock, storeBlock)
        && TryIndexedByte(load.Pointer, loop.Counter, out var copyBase, out var sourceGep)
        && load.Type.IsInteger && load.Type.Bits == 8 && load.Users.Count == 1 && ReferenceEquals(load.Users[0], store)
        && !DefinedInside(copyBase, loop.Region) && ProvenDisjoint(targetBase, copyBase)) {
      isCopy = true;
      sourceBase = copyBase;
      allowed.Add(load);
      allowed.Add(sourceGep);
    } else if (!store.Value.Type.IsInteger || store.Value.Type.Bits != 8 || DefinedInside(store.Value, loop.Region))
      return false;

    if (loop.Region.SelectMany(block => block.Instructions).Any(instruction => !allowed.Contains(instruction)))
      return false;
    foreach (var instruction in allowed)
      if (!instruction.Type.IsVoid && instruction.Users.Any(user => user.Parent is not null && !loop.Region.Contains(user.Parent)))
        return false;

    var callee = MemoryIntrinsic(module, isCopy ? _MEMCPY : _MEMSET);
    var target = Start(loop, targetBase, initial);
    IrValue[] args = isCopy
      ? [target, Start(loop, sourceBase!, initial), new IrConstantInt(IrType.I32, loop.Trips), new IrConstantInt(IrType.I1, 0)]
      : [target, store.Value, new IrConstantInt(IrType.I32, loop.Trips), new IrConstantInt(IrType.I1, 0)];
    loop.Preheader.InsertBefore(new IrCall(IrType.Void, callee, args), preBranch);
    preBranch.Target = loop.Exit;
    foreach (var block in loop.Region.ToList())
      function.RemoveBlock(block);
    return true;
  }

  /// <summary>
  /// Proves that removing the region removes exactly one acyclic body execution per counted iteration.
  /// The header's false edge is the sole exit and the latch-to-header edge is the sole cycle.
  /// </summary>
  private static bool HasSingleIterationControlFlow(CountedLoop loop) {
    var indegree = loop.Region.ToDictionary<IrBasicBlock, IrBasicBlock, int>(
      block => block,
      _ => 0,
      ReferenceEqualityComparer.Instance);

    foreach (var block in loop.Region) {
      if (!ReferenceEquals(block, loop.Header)
          && block.Predecessors.Any(predecessor => !loop.Region.Contains(predecessor)))
        return false;                               // deleting the region would strand a side entrance
      if (block.Terminator is not { } terminator || terminator is IrRet or IrUnreachable)
        return false;                               // EXIT SUB/FUNCTION is observable control flow

      foreach (var successor in terminator.Successors) {
        if (!loop.Region.Contains(successor)) {
          if (!ReferenceEquals(block, loop.Header) || !ReferenceEquals(successor, loop.Exit))
            return false;                           // EXIT LOOP or another side exit
          continue;
        }
        if (ReferenceEquals(block, loop.Latch) && ReferenceEquals(successor, loop.Header))
          continue;                                // the one counted-loop backedge
        ++indegree[successor];
      }
    }

    // After removing the counted backedge, one iteration must be a DAG. Otherwise a store could run
    // more than once before the induction variable advances (an inner/self loop), which no single
    // memcpy/memset call can reproduce.
    var ready = new Queue<IrBasicBlock>(indegree.Where(pair => pair.Value == 0).Select(pair => pair.Key));
    var visited = 0;
    while (ready.Count > 0) {
      var block = ready.Dequeue();
      ++visited;
      foreach (var successor in block.Successors) {
        if (!loop.Region.Contains(successor)
            || ReferenceEquals(block, loop.Latch) && ReferenceEquals(successor, loop.Header))
          continue;
        if (--indegree[successor] == 0)
          ready.Enqueue(successor);
      }
    }
    return visited == loop.Region.Count;
  }

  private static IrValue Start(CountedLoop loop, IrValue basePointer, IrConstantInt initial) {
    if (initial.IsZero)
      return basePointer;
    return loop.Preheader.InsertBefore(new IrGep(basePointer,
      new IrConstantInt(loop.Counter.Type, initial.Value), IrType.I8), loop.Preheader.Terminator!);
  }

  private static bool TryIndexedByte(IrValue pointer, IrPhi counter, out IrValue basePointer, out IrGep gep) {
    if (pointer is IrGep indexed && ReferenceEquals(indexed.ByteOffset, counter)
        && (indexed.ElementType is null || indexed.ElementType.SameStorage(IrType.I8))) {
      basePointer = indexed.BasePtr;
      gep = indexed;
      return true;
    }
    basePointer = null!;
    gep = null!;
    return false;
  }

  private static bool DefinedInside(IrValue value, HashSet<IrBasicBlock> region)
    => value is IrInstruction { Parent: { } parent } && region.Contains(parent);

  private static bool ProvenDisjoint(IrValue left, IrValue right) {
    if (ReferenceEquals(left, right))
      return false;
    return (left, right) switch {
      (IrAlloca, IrAlloca) => true,
      (IrGlobalVariable, IrGlobalVariable) => true,
      (IrAlloca, IrGlobalVariable) or (IrGlobalVariable, IrAlloca) => true,
      _ => false,
    };
  }

  private static IrFunction MemoryIntrinsic(IrModule module, string name) {
    if (module.FindFunction(name) is { } existing)
      return existing;
    return name == _MEMCPY
      ? module.AddFunction(new IrFunction(name, IrType.Void, [
        new IrArgument(IrType.Ptr, 0), new IrArgument(IrType.Ptr, 1),
        new IrArgument(IrType.I32, 2), new IrArgument(IrType.I1, 3),
      ]))
      : module.AddFunction(new IrFunction(name, IrType.Void, [
        new IrArgument(IrType.Ptr, 0), new IrArgument(IrType.I8, 1),
        new IrArgument(IrType.I32, 2), new IrArgument(IrType.I1, 3),
      ]));
  }
}
