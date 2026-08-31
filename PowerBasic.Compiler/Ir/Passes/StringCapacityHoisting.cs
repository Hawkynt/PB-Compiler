namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Removes the per-iteration growth checks from a side-effect-free counted string-build loop by
/// constructing its complete suffix once in the preheader.
///
/// <para>
/// The DOS string heap has no spare-capacity field, so a literal implementation of "reserve N bytes"
/// would require changing the runtime representation. For the loops where the middle end knows the
/// exact trip count, it can do better without a new ABI: <c>s = s + piece</c> repeated N times is
/// <c>s = s + REPEAT$(N, piece)</c>. The existing <c>rt_str_repeat</c> and <c>rt_str_concat</c>
/// perform the two length/capacity decisions once, and the loop carries the already-built handle
/// unchanged. This is the same hoisting objective with no runtime representation debt.
/// </para>
///
/// <para>
/// The matcher is intentionally strict. The FOR-shaped loop must be a single-entry straight-line
/// natural loop with a compile-time trip count, and its body may have no observable operation other
/// than the one append. The string phi may not be read in the loop except by that append. Therefore
/// moving the construction to the preheader cannot make a partially built value visible, skip an
/// intervening side effect, or change an early-exit result. A variable piece is accepted only when its
/// SSA value is loop-invariant; it is duplicated once before <c>rt_str_repeat</c> so the variable's
/// owner is not consumed.
/// </para>
/// </summary>
public static class StringCapacityHoisting {

  private const string _APPEND_LIT = "rt_str_append_lit";
  private const string _APPEND_VAR = "rt_str_append_var";
  private const string _CONST = "rt_str_const";
  private const string _DUP = "rt_str_dup";
  private const string _REPEAT = "rt_str_repeat";
  private const string _CONCAT = "rt_str_concat";

  /// <summary>Hoists qualifying counted builders; returns the number of loops rewritten.</summary>
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    var changed = 0;
    foreach (var function in module.Functions.ToList()) {
      if (function.IsDeclaration || function.HasErrorHandler || function.HasInlineAsm)
        continue;
      foreach (var header in function.Blocks.ToList()) {
        if (Match(header) is not { } loop || !TryRewrite(module, loop))
          continue;
        ++changed;
      }
    }
    return changed;
  }

  private sealed record Loop(
    IrBasicBlock Header,
    IReadOnlyList<IrBasicBlock> Body,
    IrBasicBlock Latch,
    IrBasicBlock Preheader,
    int Trips,
    IrCall Append,
    IrPhi StringPhi);

  private static Loop? Match(IrBasicBlock header) {
    if (header.Parent is not { } function
        || header.Terminator is not IrCondBr { Condition: IrCmp test } branch)
      return null;

    var body = new List<IrBasicBlock>();
    IrBasicBlock? latch = null;
    for (var at = branch.IfTrue; latch is null;) {
      if (ReferenceEquals(at, header) || body.Contains(at))
        return null;
      body.Add(at);
      if (at.Terminator is not IrBr next)
        return null;
      if (ReferenceEquals(next.Target, header))
        latch = at;
      else
        at = next.Target;
    }

    var predecessors = header.Predecessors.ToList();
    if (predecessors.Count != 2)
      return null;
    var preheader = predecessors.SingleOrDefault(p => !ReferenceEquals(p, latch));
    if (preheader?.Terminator is not IrBr preheaderBranch || !ReferenceEquals(preheaderBranch.Target, header))
      return null;

    if (test.Lhs is not IrPhi counter || test.Rhs is not IrConstantInt limit)
      return null;
    if (counter.IncomingFrom(preheader) is not IrConstantInt initial)
      return null;
    if (counter.IncomingFrom(latch) is not IrBinary { Op: IrBinaryOp.Add } nextCounter
        || !ReferenceEquals(nextCounter.Lhs, counter) || nextCounter.Rhs is not IrConstantInt step)
      return null;
    if (TripCount(initial.Value, step.Value, limit.Value, test.Pred) is not { } trips || trips <= 1)
      return null;

    var appends = body.SelectMany(b => b.Instructions).OfType<IrCall>()
      .Where(c => c.Callee is IrFunction { Name: _APPEND_LIT or _APPEND_VAR })
      .ToList();
    if (appends.Count != 1)
      return null;
    var append = appends[0];
    if (append.GetOperand(1) is not IrPhi stringPhi
        || !ReferenceEquals(stringPhi.Parent, header)
        || !ReferenceEquals(stringPhi.IncomingFrom(latch), append)
        || stringPhi.IncomingFrom(preheader) is null)
      return null;

    var bodySet = new HashSet<IrBasicBlock>(body, ReferenceEqualityComparer.Instance);
    if (stringPhi.Users.Any(user => user.Parent is { } where && bodySet.Contains(where) && !ReferenceEquals(user, append)))
      return null;

    // The loop is moved in TIME, not merely rewritten in place. Anything observable between its
    // iterations would make that movement visible, so only arithmetic/address plumbing is admitted.
    foreach (var instruction in body.SelectMany(b => b.Instructions)) {
      if (ReferenceEquals(instruction, append) || instruction is IrBr or IrPhi)
        continue;
      if (instruction is not (IrBinary or IrCmp or IrCast or IrGep or IrSelect))
        return null;
      if (instruction is IrBinary { Op: IrBinaryOp.SDiv or IrBinaryOp.UDiv or IrBinaryOp.SRem or IrBinaryOp.URem or IrBinaryOp.FDiv })
        return null;
    }

    if (append.Callee is IrFunction { Name: _APPEND_LIT }) {
      if (append.ArgCount != 3 || append.GetOperand(3) is not IrConstantInt { Value: > 0 })
        return null;
    } else {
      if (append.ArgCount != 2 || !IsLoopInvariant(append.GetOperand(2), header, bodySet))
        return null;
      if (ReferenceEquals(append.GetOperand(2), stringPhi))
        return null;
    }

    // Nothing may enter the straight-line body from outside its header.
    foreach (var block in function.Blocks)
      if (!bodySet.Contains(block) && !ReferenceEquals(block, header))
        foreach (var successor in block.Successors)
          if (bodySet.Contains(successor))
            return null;

    return new(header, body, latch, preheader, trips, append, stringPhi);
  }

  private static bool TryRewrite(IrModule module, Loop loop) {
    var initial = loop.StringPhi.IncomingFrom(loop.Preheader)!;
    var anchor = loop.Preheader.Terminator!;
    IrValue piece;

    if (loop.Append.Callee is IrFunction { Name: _APPEND_LIT }) {
      var make = Declare(module, _CONST, IrType.Ptr, IrType.Ptr, IrType.I32);
      piece = loop.Preheader.InsertBefore(new IrCall(IrType.Ptr, make,
        [loop.Append.GetOperand(2), loop.Append.GetOperand(3)]), anchor);
    } else {
      var dup = Declare(module, _DUP, IrType.Ptr, IrType.Ptr);
      piece = loop.Preheader.InsertBefore(new IrCall(IrType.Ptr, dup, [loop.Append.GetOperand(2)]), anchor);
    }

    var repeat = Declare(module, _REPEAT, IrType.Ptr, IrType.I32, IrType.Ptr);
    var suffix = loop.Preheader.InsertBefore(new IrCall(IrType.Ptr, repeat,
      [IrBuilder.ConstI32(loop.Trips), piece]), anchor);
    var concat = Declare(module, _CONCAT, IrType.Ptr, IrType.Ptr, IrType.Ptr);
    var built = loop.Preheader.InsertBefore(new IrCall(IrType.Ptr, concat, [initial, suffix]), anchor);

    for (var i = 0; i < loop.StringPhi.IncomingBlocks.Count; ++i)
      if (ReferenceEquals(loop.StringPhi.IncomingBlocks[i], loop.Preheader)) {
        loop.StringPhi.SetOperand(i, built);
        break;
      }

    // The phi now starts with the final value. Carry that same value around the back edge; no append
    // remains in the loop, so all per-iteration top-block/$STRING checks disappear.
    loop.Append.ReplaceAllUsesWith(loop.StringPhi);
    loop.Append.EraseFromParent();
    return true;
  }

  private static bool IsLoopInvariant(IrValue value, IrBasicBlock header, HashSet<IrBasicBlock> body)
    => value is not IrInstruction instruction
       || instruction.Parent is not { } block
       || (!ReferenceEquals(block, header) && !body.Contains(block));

  private static int? TripCount(long initial, long step, long limit, IrCmpPred predicate) {
    if (step == 0)
      return null;
    Int128 count;
    switch (predicate) {
      case IrCmpPred.Sle when step > 0:
        if (initial > limit) return 0;
        count = ((Int128)limit - initial) / step + 1;
        break;
      case IrCmpPred.Slt when step > 0:
        if (initial >= limit) return 0;
        count = ((Int128)limit - initial - 1) / step + 1;
        break;
      case IrCmpPred.Sge when step < 0:
        if (initial < limit) return 0;
        count = ((Int128)initial - limit) / -(Int128)step + 1;
        break;
      case IrCmpPred.Sgt when step < 0:
        if (initial <= limit) return 0;
        count = ((Int128)initial - limit - 1) / -(Int128)step + 1;
        break;
      default:
        return null;
    }
    // Operators, not a relational pattern: a pattern needs its constants to already be Int128,
    // and neither 0 nor int.MaxValue is one, which is CS9135.
    return count > 0 && count <= int.MaxValue ? (int)count : null;
  }

  private static IrFunction Declare(IrModule module, string name, IrType returnType, params IrType[] parameters)
    => module.FindFunction(name)
       ?? module.AddFunction(new IrFunction(name, returnType,
         parameters.Select((type, index) => new IrArgument(type, index))));
}
