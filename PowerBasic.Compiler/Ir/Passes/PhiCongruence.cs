namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0111 — redundant induction-variable elimination. Equal loop-carried values are merged outright;
/// same-width integer recurrences with the same constant step and a constant start offset are reduced
/// to one carried phi plus a derived <c>leader + delta</c> value.
///
/// <para>
/// It is <see cref="Gvn"/> for phis, which GVN itself cannot do. GVN numbers an instruction from its
/// operands, and a loop phi's operands include the value coming back round the latch - which is
/// derived from the phi itself. Numbering it needs the answer before it can start, so GVN skips phis
/// entirely and two identical induction variables survive it untouched.
/// </para>
/// <para>
/// The way out for equal phis is to assume congruence and then break it. Every phi in a block starts
/// in one class with the others of its type; a class splits whenever two of its members disagree on
/// some predecessor, where "disagree" means their incoming values are neither identical nor in the
/// same class. Repeating until nothing splits leaves classes whose members are provably equal —
/// including the cyclic cases, which is the whole point. Starting pessimistically (nothing congruent
/// until proven) can never conclude anything about a cycle, because the proof is circular.
/// </para>
/// <para>
/// The offset case is deliberately narrower than a general SCEV analysis. If two integer phis have
/// constant starts and recurrences <c>next = phi + step</c> on the same two edges, with the same
/// constant step, then their bit-pattern difference is invariant under the IR's wrapping arithmetic:
/// <c>right[n] = left[n] + (right[0] - left[0]) mod 2^width</c>. Replacing the second phi by that
/// derived value removes one loop-carried register and lets InstCombine cancel common offsets.
/// </para>
/// </summary>
public static class PhiCongruence {

  private sealed record AffineIv(
    IrPhi Phi,
    IrBasicBlock Entry,
    IrBasicBlock Latch,
    IrConstantInt Start,
    IrConstantInt Step);

  /// <summary>Merges congruent/constant-offset induction phis in <paramref name="fn"/>; returns how many were eliminated.</summary>
  public static int Run(IrFunction fn) {
    if (fn.HasErrorHandler || fn.HasInlineAsm)
      return 0;                                  // control can arrive where the CFG does not say

    var eliminated = 0;
    foreach (var block in fn.Blocks.ToList()) {
      eliminated += MergeIn(block);
      eliminated += MergeOffsetInductions(block);
    }
    return eliminated;
  }

  private static int MergeIn(IrBasicBlock block) {
    var phis = block.Instructions.OfType<IrPhi>().ToList();
    if (phis.Count < 2)
      return 0;

    // optimistic start: same type means same class until some predecessor proves otherwise
    var classOf = new Dictionary<IrPhi, int>(ReferenceEqualityComparer.Instance);
    var byType = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var phi in phis) {
      var key = phi.Type.ToString();
      if (!byType.TryGetValue(key, out var id))
        byType[key] = id = byType.Count;
      classOf[phi] = id;
    }

    for (var splitting = true; splitting;) {
      splitting = false;
      foreach (var group in classOf.GroupBy(p => p.Value).Select(g => g.Select(p => p.Key).ToList()).ToList()) {
        if (group.Count < 2)
          continue;
        var leader = group[0];
        var split = group.Skip(1).Where(other => !Agree(leader, other, classOf)).ToList();
        if (split.Count == 0)
          continue;

        var fresh = classOf.Values.Max() + 1;    // everything that disagreed with the leader leaves
        foreach (var phi in split)
          classOf[phi] = fresh;
        splitting = true;
      }
    }

    var eliminated = 0;
    foreach (var group in classOf.GroupBy(p => p.Value).Select(g => g.Select(p => p.Key).ToList())) {
      for (var i = 1; i < group.Count; ++i) {
        group[i].ReplaceAllUsesWith(group[0]);
        group[i].EraseFromParent();
        ++eliminated;
      }
    }
    return eliminated;
  }

  /// <summary>
  /// Reduces two affine recurrences with the same step to one carried phi plus a constant offset.
  /// Phi users are left alone: an incoming value is evaluated on a predecessor edge, and moving a
  /// header-defined derived value onto an arbitrary phi edge needs a dominance/LCSSA rewrite rather
  /// than this local canonicalization.
  /// </summary>
  private static int MergeOffsetInductions(IrBasicBlock block) {
    var phis = block.Instructions.OfType<IrPhi>().ToList();
    if (phis.Count < 2)
      return 0;

    var eliminated = 0;
    for (var i = 0; i < phis.Count; ++i) {
      var leader = phis[i];
      if (leader.Parent is null || MatchAffineIv(leader) is not { } left)
        continue;

      for (var j = i + 1; j < phis.Count; ++j) {
        var candidate = phis[j];
        if (candidate.Parent is null || MatchAffineIv(candidate) is not { } right)
          continue;
        if (!Equals(left.Phi.Type, right.Phi.Type)
            || !ReferenceEquals(left.Entry, right.Entry)
            || !ReferenceEquals(left.Latch, right.Latch)
            || left.Step.ZeroExtended != right.Step.ZeroExtended)
          continue;
        if (candidate.Users.Any(user => user is IrPhi))
          continue;

        var deltaValue = IrConstFold.Wrap(unchecked(right.Start.Value - left.Start.Value), candidate.Type);
        var delta = new IrConstantInt(candidate.Type, deltaValue);
        IrValue replacement = leader;
        if (!delta.IsZero) {
          var derived = new IrBinary(IrBinaryOp.Add, leader, delta);
          var firstNonPhi = block.Instructions.First(inst => inst is not IrPhi);
          block.InsertBefore(derived, firstNonPhi);
          replacement = derived;
        }

        candidate.ReplaceAllUsesWith(replacement);
        candidate.EraseFromParent();
        ++eliminated;
      }
    }
    return eliminated;
  }

  private static AffineIv? MatchAffineIv(IrPhi phi) {
    if (!phi.Type.IsInteger || phi.IncomingBlocks.Count != 2)
      return null;

    IrBasicBlock? entry = null;
    IrBasicBlock? latch = null;
    IrConstantInt? start = null;
    IrConstantInt? step = null;

    foreach (var predecessor in phi.IncomingBlocks) {
      var incoming = phi.IncomingFrom(predecessor)!;
      if (TryMatchStep(incoming, phi, predecessor) is { } incomingStep) {
        if (latch is not null)
          return null;
        latch = predecessor;
        step = incomingStep;
        continue;
      }

      if (incoming is not IrConstantInt incomingStart || entry is not null || !Equals(incomingStart.Type, phi.Type))
        return null;
      entry = predecessor;
      start = incomingStart;
    }

    return entry is not null && latch is not null && start is not null && step is not null && !step.IsZero
      ? new(phi, entry, latch, start, step)
      : null;
  }

  private static IrConstantInt? TryMatchStep(IrValue value, IrPhi phi, IrBasicBlock predecessor) {
    if (value is not IrBinary { Op: IrBinaryOp.Add } add || !ReferenceEquals(add.Parent, predecessor))
      return null;

    IrConstantInt? step = null;
    if (ReferenceEquals(add.Lhs, phi) && add.Rhs is IrConstantInt rhs)
      step = rhs;
    else if (ReferenceEquals(add.Rhs, phi) && add.Lhs is IrConstantInt lhs)
      step = lhs;

    return step is not null && Equals(step.Type, phi.Type) ? step : null;
  }

  /// <summary>
  /// Whether two phis carry the same value on every edge. Incoming values match when they are the same
  /// value, or when they are two phis currently believed congruent — which is what lets a cycle be
  /// proved: each phi's latch value is the other's, and neither is settled until both are.
  /// </summary>
  private static bool Agree(IrPhi left, IrPhi right, Dictionary<IrPhi, int> classOf) {
    if (!Equals(left.Type, right.Type) || left.IncomingBlocks.Count != right.IncomingBlocks.Count)
      return false;

    foreach (var predecessor in left.IncomingBlocks) {
      var a = left.IncomingFrom(predecessor);
      var b = right.IncomingFrom(predecessor);
      if (a is null || b is null)
        return false;
      if (!SameValue(a, b, classOf, depth: 4))
        return false;
    }
    return true;
  }

  /// <summary>
  /// Whether two values are the same, given what is currently believed about the phis.
  ///
  /// The derived case is what makes this an INDUCTION-VARIABLE pass rather than a duplicate-phi one.
  /// Two counters in lockstep do not carry each other directly - each carries its own <c>i + 1</c>,
  /// and those are separate instructions. They are the same value exactly when the operation matches
  /// and the operands are the same, which for the operand that IS the phi is the assumption being
  /// tested. The recursion terminates on phis, and the depth bound is only there so a long chain of
  /// arithmetic cannot cost more to compare than it could ever save.
  /// </summary>
  private static bool SameValue(IrValue a, IrValue b, Dictionary<IrPhi, int> classOf, int depth) {
    if (ReferenceEquals(a, b))
      return true;
    if (a is IrConstantInt x && b is IrConstantInt y)
      return x.Value == y.Value && Equals(x.Type, y.Type);
    if (a is IrPhi pa && b is IrPhi pb)
      return classOf.TryGetValue(pa, out var ca) && classOf.TryGetValue(pb, out var cb) && ca == cb;
    if (depth <= 0)
      return false;
    return (a, b) switch {
      (IrBinary ba, IrBinary bb) when ba.Op == bb.Op
        => SameValue(ba.Lhs, bb.Lhs, classOf, depth - 1) && SameValue(ba.Rhs, bb.Rhs, classOf, depth - 1),
      (IrCast ca2, IrCast cb2) when ca2.Op == cb2.Op && Equals(ca2.Type, cb2.Type)
        => SameValue(ca2.Value, cb2.Value, classOf, depth - 1),
      _ => false,
    };
  }
}
