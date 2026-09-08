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
/// This first slice is deliberately strict: one canonical counted work block (plus PB lowering's
/// optional dedicated increment block), one <c>rt_arr_alloc</c>/<c>rt_arr_free</c> pair, a constant byte
/// size, no other calls, and no pointer escape. Those restrictions do two jobs. They make the
/// allocation execute on every iteration, and they keep the array block topmost in PB's bump allocator,
/// so hoisting the allocation and sinking the free changes neither the address-space lifetime nor the
/// allocator's rollback behaviour.
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
    if (loop.Preheader.Terminator is not IrBr preBranch || !ReferenceEquals(preBranch.Target, loop.Header)
        || loop.Header.Terminator is not IrCondBr branch || !ReferenceEquals(branch.IfFalse, loop.Exit)
        || !TryWorkBlock(loop, branch, out var body))
      return false;

    var exitPredecessors = loop.Exit.Predecessors.ToList();
    if (exitPredecessors.Count != 1 || !ReferenceEquals(exitPredecessors[0], loop.Header))
      return false;

    // Hoisting changes the relative order only against the header. Keep that header to phi selection,
    // the CountedLoop matcher’s own comparison and its branch; admitting arbitrary "pure-looking"
    // arithmetic here would also admit operations whose exceptional behaviour can precede OOM.
    if (loop.Header.Instructions.Any(instruction => instruction is not IrPhi
          && !ReferenceEquals(instruction, loop.Test) && instruction is not IrCondBr))
      return false;

    var calls = body.Instructions.OfType<IrCall>().ToList();
    var allocations = calls.Where(call => IsCall(call, _ALLOC, 1)).ToList();
    var frees = calls.Where(call => IsCall(call, _FREE, 2)).ToList();
    if (calls.Count != 2 || allocations.Count != 1 || frees.Count != 1)
      return false;                         // another call could allocate, escape, or observe heap state

    var allocation = allocations[0];
    var free = frees[0];
    if (!allocation.Type.IsFarPointer
        || !TryConstantInteger(allocation.GetOperand(1), out var size) || size <= 0
        || !ReferenceEquals(free.GetOperand(1), allocation)
        || !TryConstantInteger(free.GetOperand(2), out var freedSize) || freedSize != size)
      return false;

    var positions = body.Instructions.Select((instruction, index) => (instruction, index))
      .ToDictionary(pair => pair.instruction, pair => pair.index, ReferenceEqualityComparer.Instance);
    var allocationIndex = positions[allocation];
    var freeIndex = positions[free];
    if (allocationIndex >= freeIndex || !PrivateWithinLifetime(allocation, free, body, positions, allocationIndex, freeIndex)
        || !ReadsSeeCurrentIterationWrites(allocation, body, allocationIndex, freeIndex, size))
      return false;

    // Resolve every insertion point before mutating either block. Apart from being tidier, this means
    // malformed hand-built IR is declined atomically rather than being left half-hoisted.
    var exitAnchor = loop.Exit.Instructions.FirstOrDefault(instruction => instruction is not IrPhi);
    if (exitAnchor is null)
      return false;

    // Lowering intentionally leaves REDIM's constant extent/address arithmetic in explicit IR until
    // the ordinary constant folder runs. O0290 runs before unrolling and therefore before that fold;
    // make the lifetime-moved calls independent of their old body-local arithmetic before moving them.
    allocation.SetOperand(1, new IrConstantInt(allocation.GetOperand(1).Type, size));
    free.SetOperand(2, new IrConstantInt(free.GetOperand(2).Type, size));

    body.Remove(allocation);
    loop.Preheader.InsertBefore(allocation, preBranch);
    body.Remove(free);
    loop.Exit.InsertBefore(free, exitAnchor);
    return true;
  }

  /// <summary>
  /// Finds the one block that owns the temporary lifetime. Hand-built canonical loops commonly use
  /// that block as the latch itself; source FOR lowering uses a separate <c>for.inc</c> latch. In the
  /// latter case the latch may contain only the induction update and its branch, so extending the heap
  /// lifetime across it cannot hide another allocation or observable effect.
  /// </summary>
  private static bool TryWorkBlock(CountedLoop loop, IrCondBr branch, out IrBasicBlock body) {
    body = null!;
    var nonHeader = loop.Region.Where(block => !ReferenceEquals(block, loop.Header)).ToList();
    if (nonHeader.Count == 1) {
      body = nonHeader[0];
      return ReferenceEquals(body, loop.Latch) && ReferenceEquals(branch.IfTrue, body)
        && body.Terminator is IrBr back && ReferenceEquals(back.Target, loop.Header);
    }

    if (nonHeader.Count != 2)
      return false;
    body = nonHeader.SingleOrDefault(block => !ReferenceEquals(block, loop.Latch))!;
    if (body is null || !ReferenceEquals(branch.IfTrue, body)
        || body.Terminator is not IrBr toLatch || !ReferenceEquals(toLatch.Target, loop.Latch)
        || loop.Latch.Terminator is not IrBr backToHeader || !ReferenceEquals(backToHeader.Target, loop.Header))
      return false;

    var next = loop.Counter.IncomingFrom(loop.Latch);
    return next is IrBinary { Op: IrBinaryOp.Add } increment
      && ReferenceEquals(increment.Lhs, loop.Counter)
      && loop.Latch.Instructions.All(instruction => ReferenceEquals(instruction, increment) || instruction is IrBr);
  }

  private static bool IsCall(IrCall call, string name, int arguments)
    => call.Callee is IrFunction callee && callee.Name == name && call.ArgCount == arguments;

  /// <summary>
  /// Resolves the small pure integer expression family REDIM lowering uses for constant bounds and
  /// offsets. This is not a second general constant folder: it recursively supplies constant operands
  /// to the shared <see cref="IrConstFold"/> rules, so wrapping, cast and invalid-operation semantics
  /// stay identical.
  /// </summary>
  private static bool TryConstantInteger(IrValue value, out long constant) {
    switch (value) {
      case IrConstantInt immediate:
        constant = immediate.Value;
        return true;

      case IrBinary { Type.IsInteger: true } binary
          when TryConstantInteger(binary.Lhs, out var left) && TryConstantInteger(binary.Rhs, out var right): {
        var candidate = new IrBinary(binary.Op,
          new IrConstantInt(binary.Lhs.Type, left), new IrConstantInt(binary.Rhs.Type, right));
        var folded = IrConstFold.TryFold(candidate) as IrConstantInt;
        candidate.DropOperandUses();
        if (folded is not null) {
          constant = folded.Value;
          return true;
        }
        break;
      }

      case IrCast { Type.IsInteger: true } cast when TryConstantInteger(cast.Value, out var operand): {
        var candidate = new IrCast(cast.Op, new IrConstantInt(cast.Value.Type, operand), cast.Type);
        var folded = IrConstFold.TryFold(candidate) as IrConstantInt;
        candidate.DropOperandUses();
        if (folded is not null) {
          constant = folded.Value;
          return true;
        }
        break;
      }
    }

    constant = 0;
    return false;
  }

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
    if (pointer is not IrGep gep || !TryConstantInteger(gep.ByteOffset, out var displacement)
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
      offset = checked(baseOffset + checked(displacement * scale));
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
