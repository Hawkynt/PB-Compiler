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
/// natural loop with a compile-time, machine-width-correct trip count, and its entire region may have
/// no observable operation other than the one append. The string phi may not be read anywhere in the
/// loop except by that append. Therefore moving the construction to the preheader cannot make a
/// partially built value visible, skip an intervening side effect, or change an early-exit result. A
/// variable piece is accepted only when its SSA value is loop-invariant; it is duplicated once before
/// <c>rt_str_repeat</c> so the variable's owner is not consumed.
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

  private sealed record Loop(CountedLoop Counted, IrCall Append, IrPhi StringPhi);

  private static Loop? Match(IrBasicBlock header) {
    if (header.Parent is not { } function
        || header.Terminator is not IrCondBr branch
        || CountedLoop.Match(function, header) is not { } counted
        || counted.Trips <= 1
        || counted.Trips > int.MaxValue)
      return null;

    if (counted.Preheader.Terminator is not IrBr preheaderBranch
        || !ReferenceEquals(preheaderBranch.Target, header))
      return null;

    // O0353 deliberately handles only one straight-line path from the header back to itself. Reuse
    // CountedLoop for the CFG/trip-count proof, then make the stricter builder topology explicit here.
    var body = new List<IrBasicBlock>();
    for (var at = branch.IfTrue;;) {
      if (ReferenceEquals(at, header) || body.Contains(at))
        return null;
      body.Add(at);
      if (at.Terminator is not IrBr next)
        return null;
      if (ReferenceEquals(next.Target, header))
        break;
      at = next.Target;
    }
    if (!ReferenceEquals(body[^1], counted.Latch)
        || !counted.Region.SetEquals(body.Append(header)))
      return null;

    // CountedLoop identifies the natural-loop region but does not make this pass's stronger
    // single-entry promise for non-header blocks. Keep that explicit: the preheader-built value must
    // dominate every route that can reach the append.
    foreach (var block in function.Blocks)
      if (!counted.Region.Contains(block))
        foreach (var successor in block.Successors)
          if (counted.Region.Contains(successor) && !ReferenceEquals(successor, header))
            return null;

    var appends = body.SelectMany(b => b.Instructions).OfType<IrCall>()
      .Where(c => c.Callee is IrFunction { Name: _APPEND_LIT or _APPEND_VAR })
      .ToList();
    if (appends.Count != 1)
      return null;
    var append = appends[0];
    if (append.GetOperand(1) is not IrPhi stringPhi
        || !ReferenceEquals(stringPhi.Parent, header)
        || !ReferenceEquals(stringPhi.IncomingFrom(counted.Latch), append)
        || stringPhi.IncomingFrom(counted.Preheader) is null)
      return null;

    // The prebuilt handle becomes the phi's value from the first header visit onwards. Any other use
    // of the old partially-built value anywhere in the loop would therefore observe the rewrite.
    if (stringPhi.Users.Any(user => user.Parent is { } where
        && counted.Region.Contains(where)
        && !ReferenceEquals(user, append)))
      return null;

    // The build is moved in TIME, not merely rewritten in place. Observable or trapping work in the
    // header is just as relevant as work in the body, so validate the complete natural-loop region.
    foreach (var instruction in counted.Region.SelectMany(b => b.Instructions)) {
      if (ReferenceEquals(instruction, append) || instruction is IrPhi || instruction.IsTerminator)
        continue;
      if (instruction is not (IrBinary or IrCmp or IrCast or IrGep or IrSelect))
        return null;
      if (instruction is IrBinary {
          Op: IrBinaryOp.SDiv or IrBinaryOp.UDiv or IrBinaryOp.SRem or IrBinaryOp.URem or IrBinaryOp.FDiv,
        })
        return null;
    }

    if (append.Callee is IrFunction { Name: _APPEND_LIT }) {
      if (append.ArgCount != 3 || append.GetOperand(3) is not IrConstantInt { Value: > 0 })
        return null;
    } else {
      if (append.ArgCount != 2 || !IsLoopInvariant(append.GetOperand(2), counted.Region))
        return null;
      if (ReferenceEquals(append.GetOperand(2), stringPhi))
        return null;
    }

    return new(counted, append, stringPhi);
  }

  private static bool TryRewrite(IrModule module, Loop loop) {
    var initial = loop.StringPhi.IncomingFrom(loop.Counted.Preheader)!;
    var anchor = loop.Counted.Preheader.Terminator!;
    IrValue piece;

    if (loop.Append.Callee is IrFunction { Name: _APPEND_LIT }) {
      var make = Declare(module, _CONST, IrType.Ptr, IrType.Ptr, IrType.I32);
      piece = loop.Counted.Preheader.InsertBefore(new IrCall(IrType.Ptr, make,
        [loop.Append.GetOperand(2), loop.Append.GetOperand(3)]), anchor);
    } else {
      var dup = Declare(module, _DUP, IrType.Ptr, IrType.Ptr);
      piece = loop.Counted.Preheader.InsertBefore(new IrCall(IrType.Ptr, dup, [loop.Append.GetOperand(2)]), anchor);
    }

    var repeat = Declare(module, _REPEAT, IrType.Ptr, IrType.I32, IrType.Ptr);
    var suffix = loop.Counted.Preheader.InsertBefore(new IrCall(IrType.Ptr, repeat,
      [IrBuilder.ConstI32((int)loop.Counted.Trips), piece]), anchor);
    var concat = Declare(module, _CONCAT, IrType.Ptr, IrType.Ptr, IrType.Ptr);
    var built = loop.Counted.Preheader.InsertBefore(new IrCall(IrType.Ptr, concat, [initial, suffix]), anchor);

    for (var i = 0; i < loop.StringPhi.IncomingBlocks.Count; ++i)
      if (ReferenceEquals(loop.StringPhi.IncomingBlocks[i], loop.Counted.Preheader)) {
        loop.StringPhi.SetOperand(i, built);
        break;
      }

    // The phi now starts with the final value. Carry that same value around the back edge; no append
    // remains in the loop, so all per-iteration top-block/$STRING checks disappear.
    loop.Append.ReplaceAllUsesWith(loop.StringPhi);
    loop.Append.EraseFromParent();
    return true;
  }

  private static bool IsLoopInvariant(IrValue value, HashSet<IrBasicBlock> region)
    => value is not IrInstruction instruction
       || instruction.Parent is not { } block
       || !region.Contains(block);

  private static IrFunction Declare(IrModule module, string name, IrType returnType, params IrType[] parameters)
    => module.FindFunction(name)
       ?? module.AddFunction(new IrFunction(name, returnType,
         parameters.Select((type, index) => new IrArgument(type, index))));
}
