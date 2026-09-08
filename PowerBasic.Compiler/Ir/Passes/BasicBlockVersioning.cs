using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0305 — static basic-block versioning for profitable path contexts at a reconvergence.
///
/// <para>
/// A branch establishes facts on each outgoing edge. When control later reconverges, ordinary SSA
/// must represent every incoming context in one block and those facts can disappear. This pass
/// materializes useful contexts in the CFG: it clones a small join block for an outcome that makes a
/// later guard decidable, routes that edge to the specialized copy, and leaves the original block as
/// the fully general fallback for every context that was not worth versioning.
/// </para>
/// <para>
/// The specialization domain is deliberately small and proof-driven. Besides the branch condition
/// itself, integer comparisons against constants consume the existing <see cref="IrRangeAnalysis"/>
/// fact at the guard and refine it with the selected edge. Pointer-alignment checks recognize the
/// canonical <c>(ptrtoint p AND (2^k-1)) == 0</c> / <c>!= 0</c> form and propagate subset/superset
/// low-bit implications. No load/store alignment is asserted: the IR does not carry such metadata
/// yet, so O0305 only folds checks whose truth is proved.
/// </para>
/// </summary>
public static class BasicBlockVersioning {

  /// <summary>Hard code-growth budget for each specialized block copy.</summary>
  private const int _MAX_INSTRUCTIONS = 32;

  /// <summary>
  /// Versions at most one reconvergence per invocation. A reconvergence may gain one true and one
  /// false copy; the pass manager reaches further candidates at fixpoint.
  /// </summary>
  public static int Run(IrFunction fn) {
    if (fn.Entry is null || fn.HasErrorHandler || fn.HasInlineAsm)
      return 0;
    if (IrRangeAnalysis.Build(fn) is not { } ranges)
      return 0;

    var addressed = fn.AddressTakenBlocks();
    var dominators = ranges.Dominators;
    foreach (var guard in fn.Blocks.ToList()) {
      if (!dominators.IsReachable(guard))
        continue;
      if (guard.Terminator is not IrCondBr branch || ReferenceEquals(branch.IfTrue, branch.IfFalse))
        continue;
      if (TryMatch(guard, branch, addressed, dominators, ranges) is not { } match)
        continue;

      Version(fn, branch, match);
      return 1;
    }
    return 0;
  }

  private sealed record IncomingEdge(IrBasicBlock From, bool IsDirect);

  private sealed record ComparisonFact(IrCmp Comparison, bool? WhenTrue, bool? WhenFalse);

  private sealed record Candidate(
    IrBasicBlock Join,
    IncomingEdge WhenTrue,
    IncomingEdge WhenFalse,
    IReadOnlyList<ComparisonFact> Comparisons,
    bool VersionTrue,
    bool VersionFalse);

  private sealed record ScalarComparison(IrValue Value, IrCmpPred Pred, IrConstantInt Constant);

  private sealed record AlignmentCheck(IrValue Pointer, ulong Mask, bool IsZero);

  private static Candidate? TryMatch(
      IrBasicBlock guard,
      IrCondBr branch,
      IReadOnlySet<IrBasicBlock> addressed,
      IrDominators dominators,
      IrRangeAnalysis ranges) {
    foreach (var (trueJoin, trueEdge) in JoinCandidates(guard, branch.IfTrue))
      foreach (var (falseJoin, falseEdge) in JoinCandidates(guard, branch.IfFalse)) {
        if (!ReferenceEquals(trueJoin, falseJoin))
          continue;

        var join = trueJoin;
        if (ReferenceEquals(join, guard) || ReferenceEquals(trueEdge.From, falseEdge.From)
            || ReferenceEquals(join, guard.Parent?.Entry) || addressed.Contains(join))
          continue;
        if (join.Instructions.Count > _MAX_INSTRUCTIONS)
          continue;

        var predecessors = join.Predecessors.ToList();
        if (!predecessors.Any(p => ReferenceEquals(p, trueEdge.From))
            || !predecessors.Any(p => ReferenceEquals(p, falseEdge.From)))
          continue;
        if (predecessors.Any(predecessor => dominators.Dominates(join, predecessor)))
          continue;                                  // no loop-header/back-edge versioning in this slice
        if (join.Successors.Any(successor => successor.Phis.Any()))
          continue;                                  // successor phis need version-aware incoming expansion
        if (join.Instructions.Any(value => value.Users.Any(user => !ReferenceEquals(user.Parent, join))))
          continue;                                  // escaping definitions need an explicit merge after versions

        var conditionUsedAsGuard = branch.Condition.Users.Any(user =>
          ReferenceEquals(user.Parent, join) && user is IrCondBr or IrSelect);
        var comparisons = new List<ComparisonFact>();
        foreach (var comparison in join.Instructions.OfType<IrCmp>()) {
          if (!IsGuardUse(comparison, join))
            continue;
          var whenTrue = Decide(branch, comparison, guard, ranges, outcome: true);
          var whenFalse = Decide(branch, comparison, guard, ranges, outcome: false);
          if (whenTrue is not null || whenFalse is not null)
            comparisons.Add(new(comparison, whenTrue, whenFalse));
        }

        var versionTrue = conditionUsedAsGuard || comparisons.Any(fact => fact.WhenTrue is not null);
        var versionFalse = conditionUsedAsGuard || comparisons.Any(fact => fact.WhenFalse is not null);
        if (!versionTrue && !versionFalse)
          continue;                                  // every copy must buy a concrete guard simplification

        return new(join, trueEdge, falseEdge, comparisons, versionTrue, versionFalse);
      }
    return null;
  }

  private static bool IsGuardUse(IrCmp comparison, IrBasicBlock block)
    => comparison.Users.Any(user => ReferenceEquals(user.Parent, block) && user is IrCondBr or IrSelect);

  private static IEnumerable<(IrBasicBlock Join, IncomingEdge Edge)> JoinCandidates(
      IrBasicBlock guard, IrBasicBlock target) {
    yield return (target, new(guard, IsDirect: true));

    var predecessors = target.Predecessors.ToList();
    if (predecessors.Count != 1 || !ReferenceEquals(predecessors[0], guard)
        || target.Terminator is not IrBr bridge || ReferenceEquals(bridge.Target, target))
      yield break;
    yield return (bridge.Target, new(target, IsDirect: false));
  }

  private static bool? Decide(
      IrCondBr branch,
      IrCmp comparison,
      IrBasicBlock guard,
      IrRangeAnalysis ranges,
      bool outcome) {
    if (branch.Condition is not IrCmp branchComparison)
      return null;
    if (SameComparison(branchComparison, comparison))
      return outcome;
    return DecideRange(branchComparison, comparison, guard, ranges, outcome)
           ?? DecideAlignment(branchComparison, comparison, outcome);
  }

  private static bool SameComparison(IrCmp left, IrCmp right)
    => left.Pred == right.Pred && SameOperand(left.Lhs, right.Lhs) && SameOperand(left.Rhs, right.Rhs);

  private static bool SameOperand(IrValue left, IrValue right) {
    if (ReferenceEquals(left, right))
      return true;
    return left is IrConstantInt l && right is IrConstantInt r
      && l.Type.SameStorage(r.Type) && l.ZeroExtended == r.ZeroExtended;
  }

  #region integer range facts

  private static bool? DecideRange(
      IrCmp branchComparison,
      IrCmp comparison,
      IrBasicBlock guard,
      IrRangeAnalysis ranges,
      bool outcome) {
    if (NormalizeScalar(branchComparison) is not { } branchFact
        || NormalizeScalar(comparison) is not { } candidate
        || !ReferenceEquals(branchFact.Value, candidate.Value))
      return null;
    var branchConstant = Singleton(ranges.RangeOf(branchFact.Constant));
    var candidateConstant = Singleton(ranges.RangeOf(candidate.Constant));
    if (branchConstant is null || candidateConstant is null)
      return null;

    var pred = outcome ? branchFact.Pred : Negate(branchFact.Pred);
    if (pred is null)
      return null;
    var pathRange = Constrain(ranges.RangeAt(branchFact.Value, guard), pred.Value, branchConstant.Value);
    return pathRange is { IsEmpty: false }
      ? Decide(candidate.Pred, pathRange.Value, candidateConstant.Value)
      : null;
  }

  private static ScalarComparison? NormalizeScalar(IrCmp comparison) {
    if (!comparison.Lhs.Type.IsInteger || !comparison.Rhs.Type.IsInteger || IsFloat(comparison.Pred))
      return null;
    if (comparison.Rhs is IrConstantInt right && comparison.Lhs is not IrConstant)
      return new(comparison.Lhs, comparison.Pred, right);
    if (comparison.Lhs is IrConstantInt left && comparison.Rhs is not IrConstant)
      return new(comparison.Rhs, Swap(comparison.Pred), left);
    return null;
  }

  private static long? Singleton(ValueRange range)
    => !range.IsEmpty && !range.IsTop && range.Lo == range.Hi ? range.Lo : null;

  private static ValueRange? Constrain(ValueRange own, IrCmpPred pred, long constant) {
    if (own.IsEmpty || own.IsTop)
      return null;
    if (IsUnsigned(pred) && (own.Lo < 0 || constant < 0))
      return null;

    ValueRange bound;
    switch (pred) {
      case IrCmpPred.Eq:
        bound = ValueRange.Of(constant);
        break;
      case IrCmpPred.Ne when own.Lo == constant && own.Lo < own.Hi:
        bound = new(own.Lo + 1, own.Hi);
        break;
      case IrCmpPred.Ne when own.Hi == constant && own.Lo < own.Hi:
        bound = new(own.Lo, own.Hi - 1);
        break;
      case IrCmpPred.Ne:
        return own.Contains(constant) ? null : own;
      case IrCmpPred.Slt or IrCmpPred.Ult when constant > long.MinValue:
        bound = new(long.MinValue, constant - 1);
        break;
      case IrCmpPred.Sle or IrCmpPred.Ule:
        bound = new(long.MinValue, constant);
        break;
      case IrCmpPred.Sgt or IrCmpPred.Ugt when constant < long.MaxValue:
        bound = new(constant + 1, long.MaxValue);
        break;
      case IrCmpPred.Sge or IrCmpPred.Uge:
        bound = new(constant, long.MaxValue);
        break;
      default:
        return null;
    }
    return own.Meet(bound);
  }

  private static bool? Decide(IrCmpPred pred, ValueRange own, long constant) {
    if (own.IsEmpty || own.IsTop || (IsUnsigned(pred) && (own.Lo < 0 || constant < 0)))
      return null;
    return pred switch {
      IrCmpPred.Eq => !own.Contains(constant) ? false : own.Lo == own.Hi ? true : null,
      IrCmpPred.Ne => !own.Contains(constant) ? true : own.Lo == own.Hi ? false : null,
      IrCmpPred.Slt or IrCmpPred.Ult => own.Hi < constant ? true : own.Lo >= constant ? false : null,
      IrCmpPred.Sle or IrCmpPred.Ule => own.Hi <= constant ? true : own.Lo > constant ? false : null,
      IrCmpPred.Sgt or IrCmpPred.Ugt => own.Lo > constant ? true : own.Hi <= constant ? false : null,
      IrCmpPred.Sge or IrCmpPred.Uge => own.Lo >= constant ? true : own.Hi < constant ? false : null,
      _ => null,
    };
  }

  #endregion

  #region pointer alignment facts

  private static bool? DecideAlignment(IrCmp branchComparison, IrCmp comparison, bool outcome) {
    if (NormalizeAlignment(branchComparison) is not { } branchFact
        || NormalizeAlignment(comparison) is not { } candidate
        || !ReferenceEquals(branchFact.Pointer, candidate.Pointer))
      return null;

    var knownZero = branchFact.IsZero == outcome;
    if (knownZero) {
      if ((candidate.Mask & ~branchFact.Mask) != 0)
        return null;                                 // only a subset of known-zero low bits is guaranteed zero
      return candidate.IsZero;
    }

    if ((branchFact.Mask & ~candidate.Mask) != 0)
      return null;                                   // a known non-zero bit must remain inside the candidate mask
    return !candidate.IsZero;
  }

  private static AlignmentCheck? NormalizeAlignment(IrCmp comparison) {
    if (comparison.Pred is not (IrCmpPred.Eq or IrCmpPred.Ne))
      return null;

    IrValue masked;
    if (comparison.Rhs is IrConstantInt { IsZero: true })
      masked = comparison.Lhs;
    else if (comparison.Lhs is IrConstantInt { IsZero: true })
      masked = comparison.Rhs;
    else
      return null;

    if (masked is not IrBinary { Op: IrBinaryOp.And } and)
      return null;

    IrValue bits;
    IrConstantInt maskConstant;
    if (and.Rhs is IrConstantInt right) {
      bits = and.Lhs;
      maskConstant = right;
    } else if (and.Lhs is IrConstantInt left) {
      bits = and.Rhs;
      maskConstant = left;
    } else {
      return null;
    }

    if (bits is not IrCast { Op: IrCastOp.PtrToInt } ptrToInt)
      return null;
    var mask = maskConstant.ZeroExtended;
    if (mask == 0 || mask == ulong.MaxValue || (mask & (mask + 1)) != 0)
      return null;                                   // alignment masks are exactly 2^k - 1
    return new(ptrToInt.Value, mask, comparison.Pred == IrCmpPred.Eq);
  }

  #endregion

  private static void Version(IrFunction fn, IrCondBr branch, Candidate match) {
    IrBasicBlock? trueBlock = null;
    IrBasicBlock? falseBlock = null;

    if (match.VersionTrue) {
      var clones = IrCloner.Clone(fn, [match.Join], Seed(branch.Condition, true), "bbv.t.", out var values);
      trueBlock = clones[match.Join];
      Specialize(trueBlock, values, match.Comparisons, true, match.WhenTrue.From);
    }
    if (match.VersionFalse) {
      var clones = IrCloner.Clone(fn, [match.Join], Seed(branch.Condition, false), "bbv.f.", out var values);
      falseBlock = clones[match.Join];
      Specialize(falseBlock, values, match.Comparisons, false, match.WhenFalse.From);
    }

    if (trueBlock is not null)
      Retarget(branch, true, match.WhenTrue, trueBlock);
    if (falseBlock is not null)
      Retarget(branch, false, match.WhenFalse, falseBlock);

    foreach (var phi in match.Join.Phis.ToList()) {
      if (trueBlock is not null)
        phi.RemoveIncoming(match.WhenTrue.From);
      if (falseBlock is not null)
        phi.RemoveIncoming(match.WhenFalse.From);
    }

    if (!match.Join.Predecessors.Any())
      fn.RemoveBlock(match.Join);                    // every incoming context received a specialized version
  }

  private static Dictionary<IrValue, IrValue> Seed(IrValue condition, bool outcome)
    => new(ReferenceEqualityComparer.Instance) {
      [condition] = IrBuilder.ConstBool(outcome),
    };

  private static void Specialize(
      IrBasicBlock block,
      IReadOnlyDictionary<IrValue, IrValue> values,
      IReadOnlyList<ComparisonFact> comparisons,
      bool outcome,
      IrBasicBlock keepIncoming) {
    foreach (var phi in block.Phis.ToList())
      foreach (var incoming in phi.IncomingBlocks.Where(candidate => !ReferenceEquals(candidate, keepIncoming)).ToList())
        phi.RemoveIncoming(incoming);

    foreach (var fact in comparisons) {
      var decided = outcome ? fact.WhenTrue : fact.WhenFalse;
      if (decided is { } value && values.TryGetValue(fact.Comparison, out var clone))
        clone.ReplaceAllUsesWith(IrBuilder.ConstBool(value));
    }
  }

  private static void Retarget(IrCondBr branch, bool outcome, IncomingEdge edge, IrBasicBlock target) {
    if (edge.IsDirect) {
      if (outcome)
        branch.IfTrue = target;
      else
        branch.IfFalse = target;
      return;
    }

    if (edge.From.Terminator is not IrBr bridge)
      throw new InvalidOperationException("prechecked versioning edge is no longer an unconditional branch");
    bridge.Target = target;
  }

  private static bool IsUnsigned(IrCmpPred pred)
    => pred is IrCmpPred.Ult or IrCmpPred.Ule or IrCmpPred.Ugt or IrCmpPred.Uge;

  private static bool IsFloat(IrCmpPred pred) => pred is >= IrCmpPred.Foeq;

  private static IrCmpPred? Negate(IrCmpPred pred) => pred switch {
    IrCmpPred.Eq => IrCmpPred.Ne,
    IrCmpPred.Ne => IrCmpPred.Eq,
    IrCmpPred.Slt => IrCmpPred.Sge,
    IrCmpPred.Sle => IrCmpPred.Sgt,
    IrCmpPred.Sgt => IrCmpPred.Sle,
    IrCmpPred.Sge => IrCmpPred.Slt,
    IrCmpPred.Ult => IrCmpPred.Uge,
    IrCmpPred.Ule => IrCmpPred.Ugt,
    IrCmpPred.Ugt => IrCmpPred.Ule,
    IrCmpPred.Uge => IrCmpPred.Ult,
    _ => null,
  };

  private static IrCmpPred Swap(IrCmpPred pred) => pred switch {
    IrCmpPred.Slt => IrCmpPred.Sgt,
    IrCmpPred.Sle => IrCmpPred.Sge,
    IrCmpPred.Sgt => IrCmpPred.Slt,
    IrCmpPred.Sge => IrCmpPred.Sle,
    IrCmpPred.Ult => IrCmpPred.Ugt,
    IrCmpPred.Ule => IrCmpPred.Uge,
    IrCmpPred.Ugt => IrCmpPred.Ult,
    IrCmpPred.Uge => IrCmpPred.Ule,
    _ => pred,
  };
}
