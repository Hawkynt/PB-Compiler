namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Turns a function that calls ITSELF in tail position into a loop, so the recursion runs in constant
/// stack instead of a frame per level.
///
/// <para>
/// This is the IR half of what the direct emitter does as pb36 O14, and it is not a size or speed
/// optimization however much it looks like one: without it a deep recursion OVERFLOWS. The two tests
/// that measure it - <c>Execute_GivenDeepTailRecursion_WhenPb36_ThenConstantStack</c> and its
/// mutual-recursion twin - assert a behavioural promise, which is why this is the first of the direct
/// emitter's optimizations the routed path had to earn rather than inherit.
/// </para>
///
/// <para>
/// The transform is the standard one, and in SSA it is mostly about where the parameters come from.
/// A new ENTRY block is pushed in front of the old one, which thereby becomes a loop header with a
/// predecessor; each parameter is replaced by a phi in that header, taking the original argument on
/// the way in and the call's argument on the way round. The call and the return that follows it are
/// then replaced by a branch back to the header.
/// </para>
///
/// <code>
///   FUNCTION F(n, acc)              header:  n' = phi [n, entry], [n - 1, latch]
///     IF n = 0 THEN F = acc : EXIT             acc' = phi [acc, entry], [acc * n', latch]
///     F = F(n - 1, acc * n)         latch:   br header
///   END FUNCTION
/// </code>
///
/// <para>
/// What it will not touch, and why each one would be wrong rather than merely unprofitable:
/// </para>
/// <list type="bullet">
///   <item>a call that is not in TAIL position - anything between it and the return, or a return of
///   some other value, means the frame is still needed after the call;</item>
///   <item>a function whose frame ADDRESS escapes. A recursion reusing one frame is only equivalent
///   when no level can still be holding a pointer into the one before it, and an alloca whose address
///   is taken is exactly that possibility;</item>
///   <item>a function with an error handler or inline assembly, which the pass manager already keeps
///   whole for its own reasons.</item>
/// </list>
///
/// <para>
/// Mutual recursion between two functions is NOT handled here and does not need to be: the inliner
/// turns <c>A calls B calls A</c> into a self-call first, and this pass runs after it. That ordering
/// is what makes the mutual test pass by the same mechanism as the direct one.
/// </para>
/// </summary>
public static class TailRecursion {

  /// <summary>Rewrites self tail calls in <paramref name="function"/> as a loop; the number rewritten.</summary>
  public static int Run(IrFunction function) {
    ArgumentNullException.ThrowIfNull(function);
    if (function.IsDeclaration || function.HasErrorHandler || function.HasInlineAsm)
      return 0;

    // A frame that outlives its level cannot be reused. Only the address ESCAPING matters - an alloca
    // the function merely loads and stores through is dead by the time the branch is taken.
    if (function.AllInstructions.Any(i => i is IrAlloca alloca && Escapes(alloca)))
      return 0;

    var tails = function.Blocks
      .Select(block => (Block: block, Call: TailSelfCall(function, block)))
      .Where(pair => pair.Call is not null)
      .ToList();
    if (tails.Count == 0)
      return 0;

    var header = function.Entry!;
    var entry = function.CreateEntryBlock(function.Name + ".tailrec");
    entry.Append(new IrBr(header));

    // Each parameter becomes a phi at the head of the loop. The phis are created FIRST and populated
    // afterwards, because a call's argument may itself read a parameter - `F(n - 1, acc * n)` reads
    // both - and those reads have to be redirected to the phi as well.
    var phis = new List<IrPhi>(function.Parameters.Count);
    foreach (var parameter in function.Parameters) {
      var phi = new IrPhi(parameter.Type) { Name = parameter.Name };
      header.AppendPhi(phi);
      phis.Add(phi);
    }
    for (var i = 0; i < phis.Count; ++i) {
      function.Parameters[i].ReplaceAllUsesWith(phis[i]);
      phis[i].AddIncoming(function.Parameters[i], entry);   // ...after the replacement, so it keeps the argument
    }

    foreach (var (block, call) in tails) {
      for (var i = 0; i < phis.Count; ++i)
        phis[i].AddIncoming(call!.Operands[i + 1], block);  // operand 0 is the callee
      var terminator = block.Terminator!;
      terminator.EraseFromParent();
      call!.EraseFromParent();
      block.Append(new IrBr(header));
    }
    return tails.Count;
  }

  /// <summary>
  /// The self-call in tail position at the end of <paramref name="block"/>, or null.
  ///
  /// Tail position means the call is the instruction immediately before the return, and the return
  /// either yields the call's own result or yields nothing. Anything else - a use of the result, a
  /// store after it, a different value returned - needs the frame to still be there.
  /// </summary>
  private static IrCall? TailSelfCall(IrFunction function, IrBasicBlock block) {
    if (block.Instructions.Count < 2 || block.Terminator is null)
      return null;
    if (block.Instructions[^2] is not IrCall call || !ReferenceEquals(call.Callee, function))
      return null;
    if (call.ArgCount != function.Parameters.Count)
      return null;
    if (ReturnAfter(block) is not { } ret)
      return null;
    if (ret.HasValue && !ReferenceEquals(ret.Operands[0], call))
      return null;
    // The result may be read by the return and by nothing else; a second reader outlives the frame.
    return call.Users.Count(u => !ReferenceEquals(u, ret)) == 0 ? call : null;
  }

  /// <summary>
  /// The return this block falls into, following branches through blocks that ADD NOTHING, or null.
  ///
  /// The adjacency this looks for is almost never literal in lowered BASIC. `IF n &gt; 0 THEN CountDown
  /// n - 1` puts the call in a THEN block that branches to the statement after the IF, and the return
  /// is over there - so a pass that only matched a call sitting immediately before a `ret` fired on
  /// nothing at all, which is exactly how the first version of this behaved.
  ///
  /// An intervening block is skipped only when it is empty of everything but its terminator. A phi
  /// stops the walk rather than being reasoned about: its value depends on which edge arrived, and
  /// that is a question about the whole CFG rather than about this path.
  /// </summary>
  private static IrRet? ReturnAfter(IrBasicBlock block) {
    var current = block;
    for (var hops = 0; hops < 8; ++hops) {
      switch (current.Terminator) {
        case IrRet ret:
          return ret;
        case IrBr br:
          var next = br.Target;
          if (next.Instructions.Any(i => !i.IsTerminator))
            return null;
          current = next;
          continue;
        default:
          return null;
      }
    }
    return null;
  }

  /// <summary>Whether the alloca's ADDRESS reaches anything but a load or a store through it.</summary>
  private static bool Escapes(IrAlloca alloca)
    => alloca.Users.Any(user => user switch {
      IrLoad load => !ReferenceEquals(load.Pointer, alloca),
      IrStore store => !ReferenceEquals(store.Pointer, alloca),
      _ => true,
    });
}
