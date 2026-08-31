namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Coalesces consecutive Error 6 branches when the code made speculative by delaying the first
/// branch is pure and non-trapping.
///
/// <para>
/// Checked arithmetic is lowered as <c>condbr overflow, trap, next</c>. For a straight-line
/// expression such as <c>a + b + c</c>, the first continuation computes the next wrapped arithmetic
/// result and immediately performs another Error 6 check. Integer arithmetic in the IR is wrapping,
/// so those pure instructions are safe to execute even when the first operation overflowed. Delaying
/// the branch and testing <c>firstOverflow OR secondOverflow</c> at the second check removes one hot
/// control-flow edge without changing the result on the successful path.
/// </para>
///
/// <para>
/// The transform is deliberately narrower than a generic trap merger. Both trap blocks must be the
/// exact <c>rt_error(6)</c> shape emitted by <c>IrLowering.RaiseWhen</c>, and every instruction made
/// speculative must be pure and non-trapping. Stores, loads, calls, division/remainder and arbitrary
/// control flow stop the chain. Functions with an ON ERROR handler never reach this pass because the
/// pass manager excludes them; that is essential, since otherwise moving the point at which Error 6
/// is raised would change RESUME semantics.
/// </para>
/// </summary>
public static class OverflowCheckCoalescing {

  private const int _OVERFLOW_ERROR = 6;

  /// <summary>Coalesces qualifying adjacent overflow guards; returns the number of removed branches.</summary>
  public static int Run(IrFunction fn) {
    ArgumentNullException.ThrowIfNull(fn);
    if (fn.Entry is null || fn.HasErrorHandler)
      return 0;

    var changed = 0;
    bool progress;
    do {
      progress = false;
      foreach (var block in fn.Blocks.ToList()) {
        if (!TryMatchGuard(block, out var first))
          continue;
        var middle = first.Continuation;
        if (!TryMatchGuard(middle, out var second))
          continue;
        if (ErrorCode(first.Trap, middle) != _OVERFLOW_ERROR
            || ErrorCode(second.Trap, second.Continuation) != _OVERFLOW_ERROR)
          continue;
        if (!SafeToSpeculate(middle, second.Branch))
          continue;

        var combined = middle.InsertBefore(
          new IrBinary(IrBinaryOp.Or, first.Branch.Condition, second.Branch.Condition), second.Branch);
        second.Branch.SetOperand(0, combined);

        first.Branch.EraseFromParent();
        block.Append(new IrBr(middle));
        ++changed;
        progress = true;
        break; // the CFG changed; restart from a fresh structural view
      }
    } while (progress);

    return changed;
  }

  private readonly record struct Guard(IrCondBr Branch, IrBasicBlock Trap, IrBasicBlock Continuation);

  private static bool TryMatchGuard(IrBasicBlock block, out Guard guard) {
    guard = default;
    if (block.Terminator is not IrCondBr branch)
      return false;

    // RaiseWhen always branches TRUE to the trap. Requiring the trap shape here instead of accepting
    // either arm prevents an ordinary source IF that happens to lead to rt_error from being rewritten.
    if (ErrorCode(branch.IfTrue, branch.IfFalse) is null)
      return false;
    guard = new(branch, branch.IfTrue, branch.IfFalse);
    return true;
  }

  private static int? ErrorCode(IrBasicBlock trap, IrBasicBlock continuation) {
    if (trap.Terminator is not IrBr tail || !ReferenceEquals(tail.Target, continuation))
      return null;
    var body = trap.Instructions.Where(i => !i.IsTerminator).ToArray();
    return body is [IrCall {
        Callee: IrFunction { Name: "rt_error" },
        ArgCount: 1,
      } call]
      && call.GetOperand(1) is IrConstantInt code
        ? checked((int)code.Value)
        : null;
  }

  private static bool SafeToSpeculate(IrBasicBlock block, IrInstruction terminator) {
    foreach (var instruction in block.Instructions) {
      if (ReferenceEquals(instruction, terminator) || instruction is IrPhi)
        continue;
      if (instruction is IrBinary { Op: IrBinaryOp.SDiv or IrBinaryOp.UDiv or IrBinaryOp.SRem or IrBinaryOp.URem or IrBinaryOp.FDiv })
        return false;
      if (instruction is not (IrBinary or IrCmp or IrCast or IrGep or IrSelect))
        return false;
    }
    return true;
  }
}
