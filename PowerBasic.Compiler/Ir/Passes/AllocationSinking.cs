namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Sinks a string-literal allocation into the single-entry conditional arm that is its only reader.
///
/// <para>
/// This is intentionally narrower than generic code sinking. A PowerBASIC string allocation changes
/// the compacting string heap, so moving it across another call, load or store can change observable
/// allocation order. The first O0288 slice therefore accepts only <c>rt_str_const</c>, crosses only
/// pure non-trapping scalar work (plus the lowering's provable <c>rt_str_free(null)</c> no-op), and
/// requires the matching lifetime-ending free to be the first real instruction at the arm's join.
/// On the taken path, no heap operation is reordered; on the untaken path the allocation/free pair is
/// avoided entirely.
/// </para>
///
/// <para>
/// The CFG shape is the ordinary no-else IF produced by lowering: the allocation is before a
/// conditional branch, one successor is a single-entry body whose non-phi users consume the value,
/// that body branches directly to the other successor, and the join immediately frees the owned
/// handle. Restricting the destination to one predecessor also keeps the sunk allocation singly
/// executed and avoids introducing a value that would fail to dominate a phi edge.
/// </para>
/// </summary>
public static class AllocationSinking {

  private const string _ALLOC = "rt_str_const";
  private const string _FREE = "rt_str_free";

  /// <summary>Sinks qualifying allocations; returns the number moved.</summary>
  public static int Run(IrFunction fn) {
    ArgumentNullException.ThrowIfNull(fn);
    if (fn.Entry is null || fn.HasErrorHandler || fn.HasInlineAsm)
      return 0;

    var changed = 0;
    bool progress;
    do {
      progress = false;
      foreach (var block in fn.Blocks.ToList()) {
        if (block.Terminator is not IrCondBr branch)
          continue;

        // Work backwards. If several allocations precede one IF, moving a later one can expose the
        // earlier one without ever moving an allocation across another allocation.
        foreach (var allocation in block.Instructions.OfType<IrCall>().Reverse().ToArray()) {
          if (!TrySink(block, branch, allocation))
            continue;
          ++changed;
          progress = true;
          break;
        }
        if (progress)
          break; // ownership and CFG placement changed; restart from a fresh structural view
      }
    } while (progress);

    return changed;
  }

  private static bool TrySink(IrBasicBlock source, IrCondBr branch, IrCall allocation) {
    if (allocation.Callee is not IrFunction { Name: _ALLOC } || allocation.ArgCount != 2)
      return false;
    if (!SafeToCross(source, allocation, branch))
      return false;

    var users = allocation.Users.Distinct().ToArray();
    var frees = users.Where(user => IsFreeOf(user, allocation)).Cast<IrCall>().ToArray();
    if (frees.Length != 1)
      return false;

    var free = frees[0];
    var readers = users.Where(user => !ReferenceEquals(user, free)).ToArray();
    if (readers.Length == 0 || readers.Any(user => user is IrPhi || user.Parent is null))
      return false;

    var destination = readers[0].Parent!;
    if (readers.Any(user => !ReferenceEquals(user.Parent, destination)))
      return false;
    if (!ReferenceEquals(destination, branch.IfTrue) && !ReferenceEquals(destination, branch.IfFalse))
      return false;

    var predecessors = destination.Predecessors.ToArray();
    if (predecessors.Length != 1 || !ReferenceEquals(predecessors[0], source))
      return false;
    if (destination.Terminator is not IrBr tail)
      return false;

    var other = ReferenceEquals(destination, branch.IfTrue) ? branch.IfFalse : branch.IfTrue;
    var merge = tail.Target;
    if (!ReferenceEquals(other, merge) || !ReferenceEquals(free.Parent, merge))
      return false;

    var firstRealInstruction = merge.Instructions.FirstOrDefault(instruction => instruction is not IrPhi);
    if (!ReferenceEquals(firstRealInstruction, free))
      return false;

    source.Remove(allocation);
    destination.InsertAt(destination.Phis.Count(), allocation);
    merge.Remove(free);
    destination.InsertBefore(free, destination.Terminator!);
    return true;
  }

  private static bool SafeToCross(IrBasicBlock block, IrCall allocation, IrCondBr branch) {
    var start = IndexOf(block.Instructions, allocation);
    var end = IndexOf(block.Instructions, branch);
    if (start < 0 || end <= start)
      return false;

    for (var i = start + 1; i < end; ++i) {
      var instruction = block.Instructions[i];
      if (IsFreeNull(instruction))
        continue;
      if (instruction is IrBinary {
          Op: IrBinaryOp.SDiv or IrBinaryOp.UDiv or IrBinaryOp.SRem or IrBinaryOp.URem or IrBinaryOp.FDiv,
        })
        return false;
      if (instruction is not (IrBinary or IrCmp or IrCast or IrGep or IrSelect))
        return false;
    }
    return true;
  }

  private static int IndexOf(IReadOnlyList<IrInstruction> instructions, IrInstruction target) {
    for (var i = 0; i < instructions.Count; ++i)
      if (ReferenceEquals(instructions[i], target))
        return i;
    return -1;
  }

  private static bool IsFreeNull(IrInstruction instruction)
    => instruction is IrCall {
        Callee: IrFunction { Name: _FREE },
        ArgCount: 1,
      } call
      && call.GetOperand(1) is IrNullPtr;

  private static bool IsFreeOf(IrInstruction instruction, IrValue allocation)
    => instruction is IrCall {
        Callee: IrFunction { Name: _FREE },
        ArgCount: 1,
      } call
      && ReferenceEquals(call.GetOperand(1), allocation);
}
