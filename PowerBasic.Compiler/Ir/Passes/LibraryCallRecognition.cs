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

    var stores = loop.Region.SelectMany(block => block.Instructions).OfType<IrStore>().ToList();
    if (stores.Count != 1 || stores[0].Type != IrType.Void)
      return false;
    var store = stores[0];
    if (!TryIndexedByte(store.Pointer, loop.Counter, out var targetBase, out var targetGep)
        || DefinedInside(targetBase, loop.Region))
      return false;

    var allowed = new HashSet<IrInstruction>(ReferenceEqualityComparer.Instance) {
      loop.Counter, loop.Test, next, store, targetGep,
    };
    foreach (var block in loop.Region)
      if (block.Terminator is { } terminator)
        allowed.Add(terminator);

    IrFunction callee;
    IrValue[] args;
    if (store.Value is IrLoad load && TryIndexedByte(load.Pointer, loop.Counter, out var sourceBase, out var sourceGep)
        && load.Type.Bits == 8 && load.Users.Count == 1 && ReferenceEquals(load.Users[0], store)
        && !DefinedInside(sourceBase, loop.Region) && ProvenDisjoint(targetBase, sourceBase)) {
      allowed.Add(load);
      allowed.Add(sourceGep);
      callee = MemoryIntrinsic(module, _MEMCPY);
      args = [Start(loop, targetBase, initial), Start(loop, sourceBase, initial),
        new IrConstantInt(IrType.I32, loop.Trips), new IrConstantInt(IrType.I1, 0)];
    } else {
      if (store.Value.Type.Bits != 8 || DefinedInside(store.Value, loop.Region))
        return false;
      callee = MemoryIntrinsic(module, _MEMSET);
      args = [Start(loop, targetBase, initial), store.Value,
        new IrConstantInt(IrType.I32, loop.Trips), new IrConstantInt(IrType.I1, 0)];
    }

    if (loop.Region.SelectMany(block => block.Instructions).Any(instruction => !allowed.Contains(instruction)))
      return false;
    foreach (var instruction in allowed)
      if (!instruction.Type.IsVoid && instruction.Users.Any(user => user.Parent is not null && !loop.Region.Contains(user.Parent)))
        return false;

    var preheader = loop.Preheader;
    preheader.InsertBefore(new IrCall(IrType.Void, callee, args), preBranch);
    preBranch.Target = loop.Exit;
    foreach (var block in loop.Region.ToList())
      function.RemoveBlock(block);
    return true;
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
