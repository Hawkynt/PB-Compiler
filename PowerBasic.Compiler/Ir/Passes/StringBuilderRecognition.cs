namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0294: recognizes a loop-carried string accumulator before a multi-concatenation chain is collapsed
/// to <c>rt_str_concat_n</c>, and turns its safe variable/literal suffix into in-place append calls.
///
/// <para>
/// The important case is <c>out$ = out$ + parts$(i) + ","</c>. Lowering borrows both string-variable
/// reads with <c>rt_str_dup</c>. Those temporary blocks land above <c>out$</c>, so even though O0208's
/// append runtime can grow a topmost target in place, the accumulator is no longer topmost when the
/// concatenation runs. O0024 then makes the problem permanent by flattening the whole expression into
/// <c>rt_str_concat_n</c>, which allocates a new result and recopies the growing prefix every iteration.
/// </para>
///
/// <para>
/// This pass recognizes the recurrence in SSA: a pointer phi whose backedge value is a left-associated
/// concatenation rooted in a borrow of that same phi. If the only in-loop uses of the old accumulator
/// are that borrow and the assignment's matching <c>rt_str_free</c>, consuming the original handle in
/// the first append is exactly the ownership transfer the borrow+free pair represented. Right-hand
/// variable borrows become raw borrowed handles and literals become raw bytes, so neither creates a
/// heap block that can displace the accumulator.
/// </para>
///
/// <para>
/// This is intentionally the allocation-free-suffix slice, not a spare-capacity implementation. A
/// string-producing call or any other accumulator observation inside the loop declines. General
/// geometric growth still needs a runtime representation whose logical length is distinct from its
/// allocated capacity, plus a decision about observable heap queries.
/// </para>
/// </summary>
public static class StringBuilderRecognition {

  private const string _CONCAT = "rt_str_concat";
  private const string _DUP = "rt_str_dup";
  private const string _FREE = "rt_str_free";
  private const string _CONST = "rt_str_const";
  private const string _APPEND_VAR = "rt_str_append_var";
  private const string _APPEND_LIT = "rt_str_append_lit";

  private readonly record struct AppendStep(IrCall Concat, IrCall Suffix);

  /// <summary>Rewrites qualifying loop-carried builders; returns the number of accumulator recurrences changed.</summary>
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    var changed = 0;

    foreach (var function in module.Functions.ToList()) {
      if (function.IsDeclaration || function.HasErrorHandler || function.HasInlineAsm)
        continue;
      var dominators = IrDominators.Build(function);
      if (dominators is null)
        continue;

      foreach (var accumulator in function.AllInstructions.OfType<IrPhi>()
                 .Where(phi => phi.Type.IsPointer).ToList()) {
        var header = accumulator.Parent;
        if (header is null)
          continue;

        for (var i = 0; i < accumulator.IncomingBlocks.Count; ++i) {
          var latch = accumulator.IncomingBlocks[i];
          // A predecessor edge is a natural-loop backedge only when its header dominates the latch.
          if (!dominators.Dominates(header, latch)
              || !latch.Successors.Any(successor => ReferenceEquals(successor, header)))
            continue;
          if (accumulator.GetOperand(i) is not IrCall root || !IsConcat(root))
            continue;

          var loop = NaturalLoop(header, latch, dominators);
          if (!TryMatch(root, accumulator, loop, out var accumulatorBorrow, out var release, out var steps))
            continue;

          Rewrite(module, accumulator, accumulatorBorrow, release, steps);
          ++changed;
          break; // one rewritten backedge completely defines this recurrence
        }
      }
    }

    return changed;
  }

  /// <summary>Collects the natural loop by walking predecessors from a dominated latch up to its header.</summary>
  private static HashSet<IrBasicBlock> NaturalLoop(IrBasicBlock header, IrBasicBlock latch, IrDominators dominators) {
    var result = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance) { header };
    var work = new Stack<IrBasicBlock>();
    if (result.Add(latch))
      work.Push(latch);

    while (work.Count > 0) {
      var block = work.Pop();
      foreach (var predecessor in block.Predecessors) {
        if (!dominators.IsReachable(predecessor) || !dominators.Dominates(header, predecessor))
          continue;
        if (result.Add(predecessor) && !ReferenceEquals(predecessor, header))
          work.Push(predecessor);
      }
    }

    return result;
  }

  private static bool TryMatch(
      IrCall root,
      IrPhi accumulator,
      HashSet<IrBasicBlock> loop,
      out IrCall accumulatorBorrow,
      out IrCall release,
      out List<AppendStep> steps) {
    accumulatorBorrow = null!;
    release = null!;
    steps = [];

    // Walk only a LEFT-associated chain. Rewriting a right subtree would move an append before work
    // that originally completed while evaluating that subtree, which can change which runtime fault
    // happens first. The source spelling motivating O0294 naturally lowers to this left spine.
    IrValue cursor = root;
    while (cursor is IrCall concat && IsConcat(concat)) {
      if (concat.Parent is not { } block || !loop.Contains(block))
        return false;
      if (!TrySuffix(concat.GetOperand(2), concat, loop, out var suffix))
        return false;
      steps.Add(new AppendStep(concat, suffix));
      cursor = concat.GetOperand(1);
    }

    // Two concatenations means at least three operands. The ordinary two-operand self-append remains
    // O0208/StringAppendInPlace territory; O0294 exists specifically to stop O0024 swallowing builders.
    if (steps.Count < 2)
      return false;

    if (cursor is not IrCall { Callee: IrFunction { Name: _DUP }, ArgCount: 1 } borrow
        || !ReferenceEquals(borrow.GetOperand(1), accumulator)
        || borrow.Parent is not { } borrowBlock || !loop.Contains(borrowBlock))
      return false;
    accumulatorBorrow = borrow;

    steps.Reverse(); // inner -> outer, which is execution/dataflow order along the left spine
    if (accumulatorBorrow.Users.Count != 1
        || !ReferenceEquals(accumulatorBorrow.Users[0], steps[0].Concat))
      return false;

    // Every concat must be private to the next concat, except the root, which is precisely the phi's
    // backedge value. Shared intermediates have another owner and cannot be consumed in-place.
    for (var i = 0; i < steps.Count; ++i) {
      var expectedUser = i + 1 < steps.Count ? (IrInstruction)steps[i + 1].Concat : accumulator;
      if (steps[i].Concat.Users.Count != 1 || !ReferenceEquals(steps[i].Concat.Users[0], expectedUser))
        return false;
    }

    // Mutating the accumulator earlier is invisible only when nobody else inside this iteration reads
    // the old handle. Outside-loop users are fine: they observe the final phi value after the loop.
    var inLoopUsers = accumulator.Users
      .Where(user => user.Parent is { } parent && loop.Contains(parent))
      .ToList();
    if (inLoopUsers.Count != 2 || !inLoopUsers.Contains(accumulatorBorrow))
      return false;
    var otherUser = ReferenceEquals(inLoopUsers[0], accumulatorBorrow) ? inLoopUsers[1] : inLoopUsers[0];
    if (otherUser is not IrCall { Callee: IrFunction { Name: _FREE }, ArgCount: 1 } free
        || !ReferenceEquals(free.GetOperand(1), accumulator))
      return false;

    // This is the lowering's assignment free, not an unrelated lifetime end: it follows the completed
    // expression in the same block. The first append takes over that ownership transfer.
    if (!ReferenceEquals(free.Parent, root.Parent)
        || IndexIn(root.Parent!, free) <= IndexIn(root.Parent!, root))
      return false;
    release = free;
    return true;
  }

  private static bool TrySuffix(
      IrValue value,
      IrCall concat,
      HashSet<IrBasicBlock> loop,
      out IrCall suffix) {
    suffix = null!;
    if (value is not IrCall leaf || leaf.Parent is not { } block || !loop.Contains(block)
        || leaf.Users.Count != 1 || !ReferenceEquals(leaf.Users[0], concat))
      return false;
    if (leaf.Callee is not IrFunction callee)
      return false;
    if (callee.Name == _DUP && leaf.ArgCount == 1 || callee.Name == _CONST && leaf.ArgCount == 2) {
      suffix = leaf;
      return true;
    }
    return false;
  }

  private static void Rewrite(
      IrModule module,
      IrPhi accumulator,
      IrCall accumulatorBorrow,
      IrCall release,
      List<AppendStep> steps) {
    IrValue target = accumulator;
    foreach (var (concat, suffix) in steps) {
      IrCall append;
      if (suffix.Callee is IrFunction { Name: _DUP }) {
        var entry = Declare(module, _APPEND_VAR, IrType.Ptr, IrType.Ptr, IrType.Ptr);
        append = new IrCall(IrType.Ptr, entry, [target, suffix.GetOperand(1)]);
      } else {
        var entry = Declare(module, _APPEND_LIT, IrType.Ptr, IrType.Ptr, IrType.Ptr, IrType.I32);
        append = new IrCall(IrType.Ptr, entry, [target, suffix.GetOperand(1), suffix.GetOperand(2)]);
      }

      concat.Parent!.InsertBefore(append, concat);
      concat.ReplaceAllUsesWith(append);
      concat.EraseFromParent();
      suffix.EraseFromParent();
      target = append;
    }

    accumulatorBorrow.EraseFromParent();
    release.EraseFromParent();
  }

  private static bool IsConcat(IrValue value)
    => value is IrCall { Callee: IrFunction { Name: _CONCAT }, ArgCount: 2 };

  private static int IndexIn(IrBasicBlock block, IrInstruction instruction) {
    for (var i = 0; i < block.Instructions.Count; ++i)
      if (ReferenceEquals(block.Instructions[i], instruction))
        return i;
    return -1;
  }

  private static IrFunction Declare(IrModule module, string name, IrType returnType, params IrType[] parameters)
    => module.FindFunction(name)
       ?? module.AddFunction(new IrFunction(name, returnType,
         parameters.Select((type, index) => new IrArgument(type, index))));
}
