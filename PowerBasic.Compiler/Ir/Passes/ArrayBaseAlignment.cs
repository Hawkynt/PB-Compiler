using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0325 — aligns the base pointer of private scalar arrays to the target vector width.
/// </summary>
/// <remarks>
/// The IR deliberately has no target-specific alignment metadata on <see cref="IrAlloca"/>. Instead,
/// this pass over-allocates by one vector and rounds an interior byte pointer forward. That makes the
/// alignment guarantee explicit in ordinary IR, so the x86, LLVM and C backends all preserve it.
/// </remarks>
public static class ArrayBaseAlignment {

  public static int Run(IrFunction fn, int vectorBytes, int pointerBits) {
    if (fn.Entry is null || !IsPowerOfTwo(vectorBytes) || PointerInteger(pointerBits) is not { } pointerType)
      return 0;

    // A real-mode segment base is paragraph-aligned, so offset alignment implies linear-address
    // alignment only through 16 bytes. Wider vectors need a target fact the current layout contract
    // does not carry, and declining is safer than silently promising an alignment we cannot prove.
    if (pointerBits == 16 && vectorBytes > 16)
      return 0;

    var changed = 0;
    foreach (var root in fn.Entry.Instructions.OfType<IrAlloca>().ToList()) {
      if (root.Name?.EndsWith(".aligned.storage", StringComparison.Ordinal) == true)
        continue;
      if (IrAliasAnalysis.StorageBytes(root.Allocated) is not { } elementBytes
          || root.Count < 16
          || vectorBytes <= elementBytes
          || vectorBytes % elementBytes != 0
          || !PrivatePointerTree(root))
        continue;

      var laneCount = vectorBytes / elementBytes;
      int backingCount;
      try {
        // One complete extra vector is sufficient for every possible forward adjustment and keeps
        // the backing count vector-sized, so the tail-padding half of O0325 is stable at fixpoint.
        backingCount = checked(root.Count + laneCount);
      } catch (OverflowException) {
        continue;
      }

      var baseName = root.Name is { } name && name.EndsWith(".padded", StringComparison.Ordinal)
        ? name[..^".padded".Length]
        : root.Name ?? "array";
      var backing = InsertAllocaAfter(root, backingCount, baseName + ".aligned.storage");
      backing.IsSourceVariable = root.IsSourceVariable;

      // Build the aligned view immediately after its backing object. This preserves SSA dominance even
      // for hand-built/test IR whose allocas are not a contiguous entry prefix.
      var insert = fn.Entry.Instructions.ToList().IndexOf(backing) + 1;
      var address = fn.Entry.InsertAt(insert++, new IrCast(IrCastOp.PtrToInt, backing, pointerType));
      var negated = fn.Entry.InsertAt(insert++, new IrBinary(
        IrBinaryOp.Sub,
        new IrConstantInt(pointerType, 0),
        address));
      var adjustment = fn.Entry.InsertAt(insert++, new IrBinary(
        IrBinaryOp.And,
        negated,
        new IrConstantInt(pointerType, vectorBytes - 1)));
      var aligned = fn.Entry.InsertAt(insert, new IrGep(backing, adjustment));

      root.ReplaceAllUsesWith(aligned);
      root.EraseFromParent();
      ++changed;
    }
    return changed;
  }

  private static IrAlloca InsertAllocaAfter(IrAlloca anchor, int count, string name) {
    var block = anchor.Parent ?? throw new InvalidOperationException("alloca has no parent");
    var at = block.Instructions.ToList().IndexOf(anchor);
    return block.InsertAt(at + 1, new IrAlloca(anchor.Allocated) { Count = count, Name = name });
  }

  private static bool PrivatePointerTree(IrValue root) {
    var seen = new HashSet<IrValue>(ReferenceEqualityComparer.Instance) { root };
    var queue = new Queue<IrValue>([root]);
    while (queue.Count > 0) {
      var pointer = queue.Dequeue();
      foreach (var user in pointer.Users)
        switch (user) {
          case IrGep gep when ReferenceEquals(gep.BasePtr, pointer):
            if (seen.Add(gep))
              queue.Enqueue(gep);
            break;
          case IrLoad load when ReferenceEquals(load.Pointer, pointer):
            break;
          case IrStore store when ReferenceEquals(store.Pointer, pointer) && !ReferenceEquals(store.Value, pointer):
            break;
          default:
            return false;
        }
    }
    return true;
  }

  private static IrType? PointerInteger(int pointerBits) => pointerBits switch {
    16 => IrType.U16,
    32 => IrType.U32,
    64 => IrType.U64,
    _ => null,
  };

  private static bool IsPowerOfTwo(int value) => value > 1 && (value & (value - 1)) == 0;
}
