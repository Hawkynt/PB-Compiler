namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Batches redundant dynamic-string ownership traffic.
///
/// <para>
/// PowerBASIC string ownership is not reference counting: <c>rt_str_dup</c> allocates a real copy and
/// <c>rt_str_free</c> destroys one owned handle. That makes ordinary ARC retain/release motion too
/// permissive for this IR. This pass therefore recognizes only two ownership shapes whose balance is
/// explicit in SSA.
/// </para>
///
/// <list type="bullet">
///   <item>
///   In a straight-line block, repeated assignment of the same immutable handle with no observation
///   of the intermediate owner keeps the first duplicate and removes the later duplicate/free pair.
///   </item>
///   <item>
///   In a compile-time non-empty counted loop, an invariant assignment carried by a pointer phi is
///   acquired once in the preheader and the value replaced on the first iteration is released there
///   once. The loop must have no side exit and the assignment must execute at the body entry before
///   any observable operation.
///   </item>
/// </list>
///
/// <para>
/// The restrictions are deliberately stronger than generic LICM. A zero-trip loop cannot free the
/// incoming owner, an early exit can invalidate a post-loop balance proof, and a consuming use of the
/// source can make a previously invariant raw handle dangle. Those cases are left untouched.
/// </para>
/// </summary>
public static class OwnershipBatching {

  private const string _DUP = "rt_str_dup";
  private const string _FREE = "rt_str_free";

  /// <summary>Batches ownership operations in <paramref name="fn"/>; returns the number of rewrites.</summary>
  public static int Run(IrFunction fn) {
    ArgumentNullException.ThrowIfNull(fn);
    if (fn.Entry is null || fn.HasErrorHandler || fn.HasInlineAsm)
      return 0;

    var changed = BatchStraightLine(fn);
    foreach (var header in fn.Blocks.ToList())
      if (CountedLoop.Match(fn, header) is { } loop)
        changed += BatchLoop(fn, loop);
    return changed;
  }

  /// <summary>
  /// Collapses <c>dup(s); ...; dup(s); free(first-copy)</c> when the first copy has no intervening
  /// reader. Keeping the first owned copy preserves the value while avoiding a second allocation.
  /// </summary>
  private static int BatchStraightLine(IrFunction fn) {
    var changed = 0;
    foreach (var block in fn.Blocks) {
      bool progress;
      do {
        progress = false;
        foreach (var second in block.Instructions.OfType<IrCall>().ToList()) {
          if (!IsDup(second, out var source) || second.Parent is null)
            continue;
          var secondAt = IndexIn(block, second);
          if (secondAt < 0 || secondAt + 1 >= block.Instructions.Count)
            continue;
          if (block.Instructions[secondAt + 1] is not IrCall free || !IsFree(free, out var previous))
            continue;
          if (previous is not IrCall first || !IsDup(first, out var firstSource)
              || !ReferenceEquals(firstSource, source) || !ReferenceEquals(first.Parent, block))
            continue;
          if (first.Users.Count != 1 || !ReferenceEquals(first.Users[0], free))
            continue;                              // the intermediate assignment was observed
          var firstAt = IndexIn(block, first);
          if (firstAt < 0 || firstAt >= secondAt)
            continue;
          if (!StraightLineSourceRemainsStable(block, firstAt, secondAt, source))
            continue;                              // an opaque operation could mutate/consume the source
          if (!HasOnlyOwnershipUses(second))
            continue;                              // preserve any observable raw-handle identity

          free.EraseFromParent();
          second.ReplaceAllUsesWith(first);
          second.EraseFromParent();
          ++changed;
          progress = true;
          break;                                   // instruction list changed; restart the block
        }
      } while (progress);
    }
    return changed;
  }

  /// <summary>
  /// Between two equal-source duplicates only pure plumbing and explicit dup/free ownership traffic
  /// may occur. An unknown call or memory operation can mutate a handle behind the same SSA pointer;
  /// in that case the first copy and the second copy need not contain the same bytes.
  /// </summary>
  private static bool StraightLineSourceRemainsStable(
      IrBasicBlock block, int firstAt, int secondAt, IrValue source) {
    for (var i = firstAt + 1; i < secondAt; ++i) {
      var instruction = block.Instructions[i];
      if (IsTransparent(instruction))
        continue;
      if (instruction is IrCall call) {
        if (IsDup(call, out _))
          continue;                                // deep-copy reads but does not modify its source
        if (IsFree(call, out var released) && !ReferenceEquals(released, source))
          continue;                                // a distinct owned block dies; source ownership survives
      }
      return false;
    }
    return true;
  }

  private static int BatchLoop(IrFunction fn, CountedLoop loop) {
    if (!HasCanonicalLifetime(fn, loop))
      return 0;

    var branch = (IrCondBr)loop.Header.Terminator!;
    var bodyEntry = branch.IfTrue;
    var changed = 0;
    foreach (var owner in loop.Header.Phis.ToList()) {
      if (ReferenceEquals(owner, loop.Counter) || owner.Type != IrType.Ptr)
        continue;
      if (owner.IncomingFrom(loop.Preheader) is not { } initial
          || owner.IncomingFrom(loop.Latch) is not IrCall duplicate
          || !IsDup(duplicate, out var source)
          || !ReferenceEquals(duplicate.Parent, bodyEntry))
        continue;
      if (!IsInvariant(source, loop) || !SourceSurvivesLoop(source, duplicate, loop))
        continue;

      var loopUses = owner.Users
        .Where(user => user.Parent is { } block && loop.Region.Contains(block))
        .ToList();
      if (loopUses.Count != 1 || loopUses[0] is not IrCall release
          || !IsFreeOf(release, owner) || !ReferenceEquals(release.Parent, bodyEntry))
        continue;
      if (!AssignmentStartsBeforeEffects(bodyEntry, duplicate, release))
        continue;
      if (!InitialOwnerCanDieAtPreheader(initial, owner))
        continue;
      if (!HasOnlyOwnershipUses(duplicate))
        continue;
      if (!DefinitionDominatesPreheader(fn, source, loop.Preheader))
        continue;

      // The loop is known to execute at least once. Preserve assignment evaluation order: make the
      // replacement first, then destroy the value it replaces, then enter the original pure header.
      duplicate.Parent!.Remove(duplicate);
      release.Parent!.Remove(release);
      var anchor = loop.Preheader.Terminator!;
      loop.Preheader.InsertBefore(duplicate, anchor);
      release.SetOperand(1, initial);
      loop.Preheader.InsertBefore(release, anchor);

      owner.ReplaceAllUsesWith(duplicate);
      owner.EraseFromParent();
      ++changed;
    }
    return changed;
  }

  /// <summary>
  /// A lifetime can be moved across the loop header only when normal completion is the sole way out.
  /// <see cref="CountedLoop"/> deliberately admits richer regions for recurrence analysis, including
  /// side exits; ownership may not.
  /// </summary>
  private static bool HasCanonicalLifetime(IrFunction fn, CountedLoop loop) {
    if (loop.Preheader.Terminator is not IrBr { Target: var target } || !ReferenceEquals(target, loop.Header))
      return false;
    if (loop.Header.Terminator is not IrCondBr branch || !ReferenceEquals(branch.IfFalse, loop.Exit))
      return false;

    // No work in the header except SSA selection and the pure counted-loop test: moving allocation
    // and release from the body entry into the preheader must not cross an observable operation.
    if (loop.Header.Instructions.Any(instruction => instruction is not IrPhi
        && !ReferenceEquals(instruction, loop.Test)
        && !ReferenceEquals(instruction, loop.Header.Terminator)))
      return false;

    foreach (var block in loop.Region) {
      if (ReferenceEquals(block, loop.Header))
        continue;
      var successors = block.Successors.ToList();
      if (successors.Count == 0 || successors.Any(successor => !loop.Region.Contains(successor)))
        return false;                               // EXIT/GOTO/RETURN or another side exit
    }

    // CountedLoop is a recurrence matcher, not a structured-control-flow proof. Reject a GOTO into a
    // body block even if the original SSA happens not to use a header value on that path: the moved
    // acquisition in the preheader would not dominate such an entry.
    foreach (var outside in fn.Blocks.Where(block => !loop.Region.Contains(block)))
      foreach (var successor in outside.Successors)
        if (loop.Region.Contains(successor)
            && !(ReferenceEquals(outside, loop.Preheader) && ReferenceEquals(successor, loop.Header)))
          return false;

    return true;
  }

  private static bool AssignmentStartsBeforeEffects(IrBasicBlock block, IrCall duplicate, IrCall release) {
    var duplicateAt = IndexIn(block, duplicate);
    var releaseAt = IndexIn(block, release);
    if (duplicateAt < 0 || releaseAt <= duplicateAt)
      return false;

    for (var i = 0; i < releaseAt; ++i) {
      var instruction = block.Instructions[i];
      if (ReferenceEquals(instruction, duplicate))
        continue;
      if (!IsTransparent(instruction))
        return false;
    }
    return true;
  }

  private static bool IsTransparent(IrInstruction instruction) => instruction switch {
    IrBinary { Op: not (IrBinaryOp.SDiv or IrBinaryOp.UDiv or IrBinaryOp.SRem or IrBinaryOp.URem or IrBinaryOp.FDiv) } => true,
    IrCmp or IrCast or IrGep or IrSelect => true,
    _ => false,
  };

  private static bool IsInvariant(IrValue value, CountedLoop loop)
    => value is not IrInstruction instruction
       || instruction.Parent is not { } block
       || !loop.Region.Contains(block);

  /// <summary>
  /// The raw source handle must remain owned while the loop runs. Ordinary <c>rt_str_dup</c> readers
  /// do not consume it; anything else is conservatively a possible ownership transfer.
  /// </summary>
  private static bool SourceSurvivesLoop(IrValue source, IrCall duplicate, CountedLoop loop)
    => source.Users.All(user => ReferenceEquals(user, duplicate)
      || user.Parent is not { } block
      || !loop.Region.Contains(block)
      || IsDupOf(user, source));

  private static bool InitialOwnerCanDieAtPreheader(IrValue initial, IrPhi owner)
    => initial is not (IrInstruction or IrArgument)
       || initial.Users.All(user => ReferenceEquals(user, owner));

  private static bool DefinitionDominatesPreheader(IrFunction fn, IrValue value, IrBasicBlock preheader) {
    if (value is not IrInstruction { Parent: { } definition })
      return true;
    if (ReferenceEquals(definition, preheader))
      return true;                                  // insertion is at the terminator, after all definitions
    var dominators = IrDominators.Build(fn);
    return dominators is not null && dominators.Dominates(definition, preheader);
  }

  /// <summary>
  /// Proves transitively that a value is used only as an owned dynamic-string handle. A phi is merely
  /// a transport node, not proof by itself: if the phi is later converted to an integer or passed to
  /// an opaque call, raw heap identity is observable and replacing one allocation with another is not
  /// legal.
  /// </summary>
  private static bool HasOnlyOwnershipUses(IrValue value) {
    var visited = new HashSet<IrValue>(ReferenceEqualityComparer.Instance);
    return Visit(value);

    bool Visit(IrValue current) {
      if (!visited.Add(current))
        return true;
      foreach (var user in current.Users) {
        if (IsDupOf(user, current) || IsFreeOf(user, current))
          continue;
        if (user is IrPhi phi && Visit(phi))
          continue;
        return false;
      }
      return true;
    }
  }

  private static bool IsDup(IrCall call, out IrValue source) {
    source = null!;
    if (call.Callee is not IrFunction { Name: _DUP } || call.ArgCount != 1)
      return false;
    source = call.GetOperand(1);
    return true;
  }

  private static bool IsDupOf(IrInstruction instruction, IrValue source)
    => instruction is IrCall call && IsDup(call, out var argument) && ReferenceEquals(argument, source);

  private static bool IsFree(IrCall call, out IrValue owner) {
    owner = null!;
    if (call.Callee is not IrFunction { Name: _FREE } || call.ArgCount != 1)
      return false;
    owner = call.GetOperand(1);
    return true;
  }

  private static bool IsFreeOf(IrInstruction instruction, IrValue owner)
    => instruction is IrCall call && IsFree(call, out var argument) && ReferenceEquals(argument, owner);

  private static int IndexIn(IrBasicBlock block, IrInstruction instruction) {
    for (var i = 0; i < block.Instructions.Count; ++i)
      if (ReferenceEquals(block.Instructions[i], instruction))
        return i;
    return -1;
  }
}
