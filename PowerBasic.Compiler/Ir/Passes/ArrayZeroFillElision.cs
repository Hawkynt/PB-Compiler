namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0068: replaces the zero-filling dynamic numeric-array allocator with its no-zero twin when the
/// canonical FOR loop immediately after the allocation provably overwrites every element before any
/// element can be observed.
///
/// <para>
/// This pass intentionally runs on freshly lowered IR, before mem2reg/unrolling erase the source
/// shape that constitutes the proof. It accepts only the rank-1 lowering produced for
/// <c>DIM/REDIM a(lo TO hi)</c> followed by <c>FOR i = lo TO hi : a(i) = value : NEXT</c>, STEP 1.
/// The loop body must be one straight-line chain, contain exactly one write through the allocated
/// buffer, contain no read through that buffer, and contain no call. Any uncertainty keeps the
/// ordinary zero-filling allocator.
/// </para>
///
/// <para>
/// String arrays never reach this pass: they use <c>rt_arr_alloc_ptr</c>, because leaving a string
/// handle uninitialised would turn arbitrary bytes into an owned heap object. Error-handler and
/// inline-assembly functions are excluded for the same reason as the rest of the middle end: their
/// observable control/memory edges are not represented completely in the CFG.
/// </para>
/// </summary>
public static class ArrayZeroFillElision {

  private const string Alloc = "rt_arr_alloc";
  private const string AllocNoZero = "rt_arr_alloc_nz";

  /// <summary>Rewrites covered numeric-array allocations in <paramref name="module"/>.</summary>
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);

    IrFunction? noZero = null;
    var rewritten = 0;
    foreach (var function in module.Functions.ToList()) {
      if (function.IsDeclaration || function.HasErrorHandler || function.HasInlineAsm)
        continue;

      foreach (var allocation in function.AllInstructions.OfType<IrCall>().ToList()) {
        if (allocation.Parent is null
            || allocation.Callee is not IrFunction { Name: Alloc } allocator
            || allocation.ArgCount != 1
            || !IsCovered(function, allocation))
          continue;

        noZero ??= module.FindFunction(AllocNoZero)
          ?? module.AddFunction(new IrFunction(AllocNoZero, allocator.ReturnType,
            [new IrArgument(allocation.GetOperand(1).Type, 0, "bytes")]));
        allocation.SetOperand(0, noZero);
        ++rewritten;
      }
    }
    return rewritten;
  }

  private static bool IsCovered(IrFunction function, IrCall allocation) {
    var preheader = allocation.Parent!;
    if (preheader.Terminator is not IrBr toHeader)
      return false;

    var dataStores = allocation.Users
      .OfType<IrStore>()
      .Where(store => ReferenceEquals(store.Value, allocation) && ReferenceEquals(store.Parent, preheader))
      .ToList();
    if (dataStores.Count != 1)
      return false;
    var dataCell = dataStores[0].Pointer;

    if (!TryMatchLoop(function, toHeader.Target, preheader, out var loop))
      return false;

    var allocationIndex = preheader.Instructions.ToList().IndexOf(allocation);
    if (allocationIndex < 0)
      return false;
    foreach (var instruction in preheader.Instructions.Skip(allocationIndex + 1)) {
      if (instruction is IrCall)
        return false;
      if (instruction is IrLoad load && PointerFromAllocation(load.Pointer, allocation, dataCell))
        return false;
      if (instruction is IrStore store && PointerFromAllocation(store.Pointer, allocation, dataCell))
        return false;
    }

    if (!TryFindStoredValue(preheader, loop.CounterSlot, out var start)
        || !TryFindStoredValue(preheader, loop.LimitSlot, out var limit)
        || !IsStepOne(loop.Latch, loop.CounterSlot))
      return false;

    IrStore? elementWrite = null;
    foreach (var block in loop.Body) {
      foreach (var instruction in block.Instructions) {
        switch (instruction) {
          case IrCall:
            return false;
          case IrLoad load when PointerFromAllocation(load.Pointer, allocation, dataCell):
            return false;
          case IrStore store when PointerFromAllocation(store.Pointer, allocation, dataCell):
            if (elementWrite is not null)
              return false;
            elementWrite = store;
            break;
        }
      }
    }
    if (elementWrite is null)
      return false;

    if (!TryElementAddress(elementWrite.Pointer, allocation, dataCell, loop.CounterSlot,
          preheader, out var lower, out var elementBytes)
        || !Equivalent(lower, start))
      return false;

    var bytes = allocation.GetOperand(1);
    return TryElementCount(bytes, elementBytes, out var count)
      && IsInclusiveCount(count, start, limit);
  }

  private sealed record Loop(
    IrValue CounterSlot,
    IrValue LimitSlot,
    IReadOnlyList<IrBasicBlock> Body,
    IrBasicBlock Latch);

  private static bool TryMatchLoop(IrFunction function, IrBasicBlock header, IrBasicBlock preheader,
      out Loop loop) {
    loop = null!;
    if (header.Terminator is not IrCondBr branch
        || branch.Condition is not IrCmp { Pred: IrCmpPred.Sle or IrCmpPred.Ule } compare
        || compare.Lhs is not IrLoad counter
        || compare.Rhs is not IrLoad limit)
      return false;

    var predecessors = function.Blocks
      .Where(block => block.Terminator is { } terminator && terminator.Successors.Contains(header))
      .ToList();
    if (predecessors.Count != 2 || !predecessors.Contains(preheader))
      return false;

    var body = new List<IrBasicBlock>();
    IrBasicBlock? latch = null;
    for (var current = branch.IfTrue; latch is null;) {
      if (ReferenceEquals(current, header) || body.Contains(current))
        return false;
      body.Add(current);
      if (current.Terminator is not IrBr next)
        return false;
      if (ReferenceEquals(next.Target, header))
        latch = current;
      else
        current = next.Target;
    }

    if (!predecessors.Contains(latch) || ReferenceEquals(branch.IfFalse, header) || body.Contains(branch.IfFalse))
      return false;

    foreach (var block in function.Blocks)
      if (!ReferenceEquals(block, header) && !body.Contains(block) && block.Terminator is { } terminator)
        foreach (var successor in terminator.Successors)
          if (body.Contains(successor))
            return false;

    loop = new(counter.Pointer, limit.Pointer, body, latch);
    return true;
  }

  private static bool IsStepOne(IrBasicBlock latch, IrValue counterSlot) {
    foreach (var store in latch.Instructions.OfType<IrStore>()) {
      if (!ReferenceEquals(store.Pointer, counterSlot))
        continue;
      if (store.Value is not IrBinary { Op: IrBinaryOp.Add } add)
        return false;
      return ValueFromSlot(add.Lhs, counterSlot) && add.Rhs is IrConstantInt { Value: 1 }
        || ValueFromSlot(add.Rhs, counterSlot) && add.Lhs is IrConstantInt { Value: 1 };
    }
    return false;
  }

  private static bool TryElementAddress(IrValue pointer, IrCall allocation, IrValue dataCell,
      IrValue counterSlot, IrBasicBlock preheader, out IrValue lower, out long elementBytes) {
    lower = null!;
    elementBytes = 0;
    if (pointer is not IrGep gep || !PointerFromAllocation(gep.BasePtr, allocation, dataCell))
      return false;

    IrValue relative = gep.ByteOffset;
    elementBytes = 1;
    if (relative is IrBinary { Op: IrBinaryOp.Mul } multiply) {
      if (multiply.Lhs is IrConstantInt left && left.Value > 0) {
        elementBytes = left.Value;
        relative = multiply.Rhs;
      } else if (multiply.Rhs is IrConstantInt right && right.Value > 0) {
        elementBytes = right.Value;
        relative = multiply.Lhs;
      }
    }

    if (relative is IrBinary { Op: IrBinaryOp.Sub } subtract
        && ValueFromSlot(subtract.Lhs, counterSlot)) {
      lower = ResolveDescriptorLoad(subtract.Rhs, preheader);
      return elementBytes > 0;
    }
    if (ValueFromSlot(relative, counterSlot)) {
      lower = new IrConstantInt(IrType.I32, 0);
      return elementBytes > 0;
    }
    return false;
  }

  private static bool TryElementCount(IrValue bytes, long elementBytes, out IrValue count) {
    count = null!;
    if (elementBytes == 1) {
      count = bytes;
      return true;
    }
    if (bytes is not IrBinary { Op: IrBinaryOp.Mul } multiply)
      return false;
    if (multiply.Lhs is IrConstantInt left && left.Value == elementBytes) {
      count = multiply.Rhs;
      return true;
    }
    if (multiply.Rhs is IrConstantInt right && right.Value == elementBytes) {
      count = multiply.Lhs;
      return true;
    }
    return false;
  }

  private static bool IsInclusiveCount(IrValue count, IrValue lower, IrValue upper) {
    if (count is IrConstantInt literal && TryConstant(lower, out var lo) && TryConstant(upper, out var hi))
      return literal.Value == hi - lo + 1;

    if (count is IrBinary { Op: IrBinaryOp.Add } add) {
      IrValue? difference = null;
      if (add.Lhs is IrConstantInt { Value: 1 })
        difference = add.Rhs;
      else if (add.Rhs is IrConstantInt { Value: 1 })
        difference = add.Lhs;
      if (difference is IrBinary { Op: IrBinaryOp.Sub } subtract)
        return Equivalent(subtract.Lhs, upper) && Equivalent(subtract.Rhs, lower);
    }

    return TryConstant(lower, out var one) && one == 1 && Equivalent(count, upper);
  }

  private static IrValue ResolveDescriptorLoad(IrValue value, IrBasicBlock preheader) {
    var stripped = StripWideningCast(value);
    if (stripped is not IrLoad load || !TryFindStoredValue(preheader, load.Pointer, out var stored))
      return stripped;
    return StripWideningCast(stored);
  }

  private static bool TryFindStoredValue(IrBasicBlock block, IrValue pointer, out IrValue value) {
    for (var index = block.Instructions.Count - 1; index >= 0; --index)
      if (block.Instructions[index] is IrStore store && ReferenceEquals(store.Pointer, pointer)) {
        value = StripWideningCast(store.Value);
        return true;
      }
    value = null!;
    return false;
  }

  private static bool PointerFromAllocation(IrValue pointer, IrCall allocation, IrValue dataCell) {
    pointer = StripPointerCasts(pointer);
    return ReferenceEquals(pointer, allocation)
      || pointer is IrLoad load && ReferenceEquals(load.Pointer, dataCell)
      || pointer is IrGep gep && PointerFromAllocation(gep.BasePtr, allocation, dataCell);
  }

  private static IrValue StripPointerCasts(IrValue value) {
    while (value is IrCast { Value: var inner } && value.Type.IsPointer)
      value = inner;
    return value;
  }

  private static bool ValueFromSlot(IrValue value, IrValue slot) {
    value = StripWideningCast(value);
    return value is IrLoad load && ReferenceEquals(load.Pointer, slot);
  }

  private static IrValue StripWideningCast(IrValue value) {
    while (value is IrCast { Op: IrCastOp.SExt or IrCastOp.ZExt, Value: var inner })
      value = inner;
    return value;
  }

  private static bool Equivalent(IrValue first, IrValue second) {
    first = StripWideningCast(first);
    second = StripWideningCast(second);
    if (ReferenceEquals(first, second))
      return true;
    if (first is IrConstantInt a && second is IrConstantInt b)
      return a.Value == b.Value;
    if (first is IrLoad al && second is IrLoad bl)
      return ReferenceEquals(al.Pointer, bl.Pointer);
    if (first is IrBinary ab && second is IrBinary bb && ab.Op == bb.Op)
      return Equivalent(ab.Lhs, bb.Lhs) && Equivalent(ab.Rhs, bb.Rhs);
    if (first is IrCast ac && second is IrCast bc && ac.Op == bc.Op)
      return Equivalent(ac.Value, bc.Value);
    return false;
  }

  private static bool TryConstant(IrValue value, out long constant) {
    value = StripWideningCast(value);
    if (value is IrConstantInt literal) {
      constant = literal.Value;
      return true;
    }
    constant = 0;
    return false;
  }
}
