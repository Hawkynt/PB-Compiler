namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0290 — reuses a non-escaping fixed-size array-heap temporary across counted-loop iterations.
///
/// <para>
/// The runtime array allocator returns zero-filled storage. Keeping one block live across iterations is
/// therefore legal only when every read in an iteration is preceded by writes covering the bytes it
/// reads; otherwise the second iteration could observe bytes left by the first instead of the fresh
/// zeroes a new allocation supplied. This pass proves that property directly over the buffer's typed
/// load/store uses.
/// </para>
///
/// <para>
/// This first slice is deliberately strict: one canonical counted body block, one
/// <c>rt_arr_alloc</c>/<c>rt_arr_free</c> pair, a constant byte size, no other calls, and no pointer
/// escape. Those restrictions do two jobs. They make the allocation execute on every iteration, and
/// they keep the array block topmost in PB's bump allocator, so hoisting the allocation and sinking
/// the free changes neither the address-space lifetime nor the allocator's rollback behaviour.
/// </para>
/// </summary>
public static class LoopTemporaryReuse {

  private const string _ALLOC = "rt_arr_alloc";
  private const string _FREE = "rt_arr_free";

  /// <summary>Reuses qualifying loop temporaries; returns the number of allocation/free pairs moved.</summary>
  public static int Run(IrFunction function) {
    ArgumentNullException.ThrowIfNull(function);
    if (function.IsDeclaration || function.HasErrorHandler || function.HasInlineAsm || function.Entry is null)
      return 0;

    var changed = 0;
    foreach (var header in function.Blocks.ToList()) {
      if (header.Parent is null || CountedLoop.Match(function, header) is not { Trips: > 1 } loop)
        continue;
      if (TryReuse(loop))
        ++changed;
    }
    return changed;
  }

  private static bool TryReuse(CountedLoop loop) {
    if (loop.Region.Count != 2 || loop.Preheader.Terminator is not IrBr preBranch
        || !ReferenceEquals(preBranch.Target, loop.Header)
        || loop.Header.Terminator is not IrCondBr branch || !ReferenceEquals(branch.IfFalse, loop.Exit))
      return false;

    var body = loop.Region.Single(block => !ReferenceEquals(block, loop.Header));
    if (!ReferenceEquals(body, loop.Latch) || !ReferenceEquals(branch.IfTrue, body)
        || body.Terminator is not IrBr back || !ReferenceEquals(back.Target, loop.Header))
      return false;

    var exitPredecessors = loop.Exit.Predecessors.ToList();
    if (exitPredecessors.Count != 1 || !ReferenceEquals(exitPredecessors[0], loop.Header))
      return false;

    // Nothing observable may be pulled across the allocation when it moves to the preheader. The
    // counted-loop matcher already made the comparison/counter exact; keep this slice to that pure
    // header rather than trying to classify arbitrary loads or calls here.
    if (loop.Header.Instructions.Any(instruction => instruction is not IrPhi and not IrCmp and not IrBinary
          and not IrCast and not IrSelect and not IrCondBr))
      return false;

    var calls = body.Instructions.OfType<IrCall>().ToList();
    var allocations = calls.Where(call => IsCall(call, _ALLOC, 1)).ToList();
    var frees = calls.Where(call => IsCall(call, _FREE, 2)).ToList();
    if (calls.Count != 2 || allocations.Count != 1 || frees.Count != 1)
      return false;                         // another call could allocate, escape, or observe heap state

    var allocation = allocations[0];
    var free = frees[0];
    if (!allocation.Type.IsFarPointer || allocation.GetOperand(1) is not IrConstantInt { Value: > 0 } size
        || !ReferenceEquals(free.GetOperand(1), allocation)
        || free.GetOperand(2) is not IrConstantInt freedSize || freedSize.Value != size.Value)
      return false;

    var positions = body.Instructions.Select((instruction, index) => (instruction, index))
      .ToDictionary(pair => pair.instruction, pair => pair.index, ReferenceEqualityComparer.Instance);
    var allocationIndex = positions[allocation];
    var freeIndex = positions[free];
    if (allocationIndex >= freeIndex || !PrivateWithinLifetime(allocation, free, body, positions, allocationIndex, freeIndex)
        || !ReadsSeeCurrentIterationWrites(allocation, body, allocationIndex, freeIndex, size.Value))
      return false;

    body.Remove(allocation);
    loop.Preheader.InsertBefore(allocation, preBranch);

    body.Remove(free);
    var exitAnchor = loop.Exit.Instructions.FirstOrDefault(instruction => instruction is not IrPhi);
    if (exitAnchor is null)
      return false;                          // valid IR normally has a terminator; decline malformed input
    loop.Exit.InsertBefore(free, exitAnchor);
    return true;
  }

  private static bool IsCall(IrCall call, string name, int arguments)
    => call.Callee is IrFunction { Name: var callee } && callee == name && call.ArgCount == arguments;

  /// <summary>
  /// Proves the allocation address cannot survive an iteration or be observed as an identity. Derived
  /// byte/element addresses may only feed more GEPs or direct loads/stores; the sole call user is the
  /// matching free. Keeping every such user between allocation and free also rejects use-after-free
  /// shapes rather than turning an already-invalid lifetime into a different one.
  /// </summary>
  private static bool PrivateWithinLifetime(IrCall allocation, IrCall free, IrBasicBlock body,
      Dictionary<IrInstruction, int> positions, int allocationIndex, int freeIndex) {
    var queue = new Queue<IrValue>([allocation]);
    var seen = new HashSet<IrValue>(ReferenceEqualityComparer.Instance);

    while (queue.Count > 0) {
      var value = queue.Dequeue();
      if (!seen.Add(value))
        continue;

      foreach (var user in value.Users) {
        if (ReferenceEquals(user, free)) {
          if (!ReferenceEquals(value, allocation) || !ReferenceEquals(free.GetOperand(1), allocation))
            return false;
          continue;
        }
        if (!ReferenceEquals(user.Parent, body) || !positions.TryGetValue(user, out var at)
            || at <= allocationIndex || at >= freeIndex)
          return false;

        switch (user) {
          case IrGep gep when ReferenceEquals(gep.BasePtr, value):
            queue.Enqueue(gep);
            break;
          case IrLoad load when ReferenceEquals(load.Pointer, value):
            break;
          case IrStore store when ReferenceEquals(store.Pointer, value) && !ReferenceEquals(store.Value, value):
            break;
          default:
            return false;                    // call/return/store-as-value/cast/compare => escape or identity use
        }
      }
    }
    return true;
  }

  /// <summary>
  /// Fresh <c>rt_arr_alloc</c> storage is zeroed. Reuse is equivalent when each load sees bytes written
  /// earlier in the SAME iteration. Stores whose address cannot be resolved are harmless by themselves
  /// but cannot establish coverage for a later read.
  /// </summary>
  private static bool ReadsSeeCurrentIterationWrites(IrCall allocation, IrBasicBlock body,
      int allocationIndex, int freeIndex, long allocationBytes) {
    var written = new List<(long Start, long End)>();

    for (var i = allocationIndex + 1; i < freeIndex; ++i) {
      switch (body.Instructions[i]) {
        case IrStore store when DerivedFrom(store.Pointer, allocation):
          if (TryRange(store.Pointer, store.Value.Type, allocation, allocationBytes, out var stored))
            AddCoverage(written, stored);
          break;

        case IrLoad load when DerivedFrom(load.Pointer, allocation):
          if (!TryRange(load.Pointer, load.Type, allocation, allocationBytes, out var read)
              || !Covered(written, read))
            return false;
          break;
      }
    }
    return true;
  }

  private static bool DerivedFrom(IrValue pointer, IrCall allocation) {
    var at = pointer;
    while (at is IrGep gep)
      at = gep.BasePtr;
    return ReferenceEquals(at, allocation);
  }

  private static bool TryRange(IrValue pointer, IrType valueType, IrCall allocation, long allocationBytes,
      out (long Start, long End) range) {
    range = default;
    if (!TryStorageBytes(valueType, out var width) || !TryOffset(pointer, allocation, out var offset))
      return false;
    try {
      var end = checked(offset + width);
      if (offset < 0 || end > allocationBytes)
        return false;
      range = (offset, end);
      return true;
    } catch (OverflowException) {
      return false;
    }
  }

  private static bool TryOffset(IrValue pointer, IrCall allocation, out long offset) {
    if (ReferenceEquals(pointer, allocation)) {
      offset = 0;
      return true;
    }
    if (pointer is not IrGep { ByteOffset: IrConstantInt displacement } gep
        || !TryOffset(gep.BasePtr, allocation, out var baseOffset)) {
      offset = 0;
      return false;
    }

    var scale = 1;
    if (gep.ElementType is { } element && !TryStorageBytes(element, out scale)) {
      offset = 0;
      return false;
    }
    try {
      offset = checked(baseOffset + checked(displacement.Value * scale));
      return true;
    } catch (OverflowException) {
      offset = 0;
      return false;
    }
  }

  private static bool TryStorageBytes(IrType type, out int bytes) {
    if ((type.IsInteger || type.IsFloat) && type.Bits >= 8 && type.Bits % 8 == 0) {
      bytes = type.Bits / 8;
      return true;
    }
    bytes = 0;                                 // pointer width is target-dependent; this pass is target-neutral
    return false;
  }

  private static void AddCoverage(List<(long Start, long End)> ranges, (long Start, long End) added) {
    var start = added.Start;
    var end = added.End;
    for (var i = ranges.Count - 1; i >= 0; --i) {
      var current = ranges[i];
      if (current.End < start || end < current.Start)
        continue;
      start = Math.Min(start, current.Start);
      end = Math.Max(end, current.End);
      ranges.RemoveAt(i);
    }
    ranges.Add((start, end));
  }

  private static bool Covered(List<(long Start, long End)> ranges, (long Start, long End) required)
    => ranges.Any(range => range.Start <= required.Start && range.End >= required.End);
}
