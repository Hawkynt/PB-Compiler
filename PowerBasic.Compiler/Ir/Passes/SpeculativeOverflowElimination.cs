using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0308 — speculative overflow-check elimination. When a checked integer operation in a counted
/// loop is safe for an entire interval of the loop-varying operand under one loop-invariant bound,
/// test that bound once before the loop and run a cloned unchecked version on the fast path. The
/// original checked loop remains the fallback, so Error 6 is still raised at the first operation that
/// overflows whenever the guard does not prove the whole loop safe.
///
/// <para>
/// This deliberately implements the cheap form of loop versioning: one operand must be loop-invariant
/// and the other must already have an SSA interval. It does not pre-scan array memory just to learn a
/// range; that would add an O(n) pass over the data before an O(n) loop and is only profitable once a
/// downstream transform can reliably recover more than the scan costs. A FOR counter, a narrowed
/// value or a dominating range test supplies the useful intervals without extra memory traffic.
/// </para>
/// <para>
/// Signed add/sub are recognized in the exact overflow predicate emitted by
/// <c>IrLowering.CheckedArithmetic</c>. O0308 runs before InstCombine for that reason. Multiplication
/// remains checked: proving a whole-loop multiply safe needs product bounds (or a wider exact type)
/// and is better handled separately than approximated here.
/// </para>
/// </summary>
public static class SpeculativeOverflowElimination {

  private const int _OVERFLOW_ERROR = 6;
  private const int _MIN_TRIPS = 2;
  private const int _MAX_LOOP_INSTRUCTIONS = 128;
  private const string _FAST_PREFIX = "ovf.fast.";
  private const string _SLOW_PREFIX = "ovf.slow.";

  private readonly record struct OverflowCheck(
    IrBasicBlock Guard,
    IrCondBr Branch,
    IrBasicBlock Trap,
    IrBasicBlock Continuation,
    IrBinary Arithmetic);

  private readonly record struct SafeInterval(IrValue Invariant, long Lo, long Hi);

  /// <summary>Versions one qualifying loop; the pass manager fixpoint reaches subsequent loops.</summary>
  public static int Run(IrFunction fn) {
    ArgumentNullException.ThrowIfNull(fn);
    if (fn.Entry is null || fn.HasErrorHandler || fn.HasInlineAsm)
      return 0;
    if (IrRangeAnalysis.Build(fn) is not { } ranges)
      return 0;

    var addressed = fn.AddressTakenBlocks();
    foreach (var header in fn.Blocks.ToList()) {
      if (header.Label.StartsWith(_FAST_PREFIX, StringComparison.Ordinal)
          || header.Label.StartsWith(_SLOW_PREFIX, StringComparison.Ordinal))
        continue;
      if (CountedLoop.Match(fn, header) is not { } loop
          || loop.Trips < _MIN_TRIPS
          || loop.Region.Sum(b => b.Instructions.Count) > _MAX_LOOP_INSTRUCTIONS
          || loop.Region.Any(addressed.Contains)
          || !HasSingleEntryAndExit(fn, loop)
          || !ExitPhisCanBeExtended(loop))
        continue;

      var checks = loop.Region
        .Select(block => TryMatchOverflowCheck(block, out var check) ? check : (OverflowCheck?)null)
        .Where(check => check is not null)
        .Select(check => check!.Value)
        .ToList();
      if (checks.Count == 0)
        continue;

      var eliminable = new List<OverflowCheck>();
      var intervals = new Dictionary<IrValue, SafeInterval>(ReferenceEqualityComparer.Instance);
      foreach (var check in checks) {
        if (TrySafeInterval(loop, check, ranges) is not { } safe)
          continue;
        if (intervals.TryGetValue(safe.Invariant, out var previous)) {
          var lo = Math.Max(previous.Lo, safe.Lo);
          var hi = Math.Min(previous.Hi, safe.Hi);
          if (lo > hi) {
            eliminable.Clear();
            break;                                  // no runtime value can make all selected checks safe
          }
          intervals[safe.Invariant] = safe with { Lo = lo, Hi = hi };
        } else
          intervals.Add(safe.Invariant, safe);
        eliminable.Add(check);
      }
      if (eliminable.Count == 0)
        continue;

      Version(fn, loop, eliminable, intervals.Values.ToList());
      return 1;
    }
    return 0;
  }

  /// <summary>
  /// Requires precisely the region boundary the rewrite models: preheader -> header is the only
  /// external entry, and header -> exit is the only edge leaving the loop. EXIT LOOP and computed
  /// entries therefore decline rather than creating a clone with missing phi operands.
  /// </summary>
  private static bool HasSingleEntryAndExit(IrFunction fn, CountedLoop loop) {
    if (loop.Preheader.Terminator is not IrBr preheaderBranch
        || !ReferenceEquals(preheaderBranch.Target, loop.Header))
      return false;

    foreach (var block in fn.Blocks) {
      if (block.Terminator is not { } terminator)
        continue;
      foreach (var successor in terminator.Successors) {
        var sourceInside = loop.Region.Contains(block);
        var targetInside = loop.Region.Contains(successor);
        if (!sourceInside && targetInside
            && !(ReferenceEquals(block, loop.Preheader) && ReferenceEquals(successor, loop.Header)))
          return false;
        if (sourceInside && !targetInside
            && !(ReferenceEquals(block, loop.Header) && ReferenceEquals(successor, loop.Exit)))
          return false;
      }
    }
    return true;
  }

  /// <summary>Every existing exit phi needs a value for the new fast-header predecessor.</summary>
  private static bool ExitPhisCanBeExtended(CountedLoop loop)
    => loop.Exit.Instructions.OfType<IrPhi>().All(phi => phi.IncomingFrom(loop.Header) is not null);

  private static bool TryMatchOverflowCheck(IrBasicBlock block, out OverflowCheck check) {
    check = default;
    if (block.Terminator is not IrCondBr branch
        || ErrorCode(branch.IfTrue, branch.IfFalse) != _OVERFLOW_ERROR
        || !TryMatchSignedOverflow(branch.Condition, out var arithmetic))
      return false;
    check = new(block, branch, branch.IfTrue, branch.IfFalse, arithmetic);
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

  /// <summary>
  /// Matches the signed add/sub overflow identity emitted by lowering:
  /// <c>(~(l^r) &amp; (sum^l)) &lt; 0</c> for add and <c>((l^r) &amp; (sum^l)) &lt; 0</c> for sub.
  /// </summary>
  private static bool TryMatchSignedOverflow(IrValue condition, out IrBinary arithmetic) {
    arithmetic = null!;
    if (condition is not IrCmp {
          Pred: IrCmpPred.Slt,
          Lhs: IrBinary { Op: IrBinaryOp.And } mask,
          Rhs: IrConstantInt { Value: 0 },
        })
      return false;

    return TryMatchMask(mask.Lhs, mask.Rhs, out arithmetic)
           || TryMatchMask(mask.Rhs, mask.Lhs, out arithmetic);
  }

  private static bool TryMatchMask(IrValue interesting, IrValue movedAway, out IrBinary arithmetic) {
    arithmetic = null!;
    if (movedAway is not IrBinary {
          Op: IrBinaryOp.Xor,
          Lhs: IrBinary { Op: IrBinaryOp.Add or IrBinaryOp.Sub } sum,
          Rhs: { } left,
        }
        || !ReferenceEquals(left, sum.Lhs)
        || !sum.Type.IsInteger
        || !sum.Type.Signed
        || sum.Type.Bits is <= 1 or >= 64)
      return false;

    if (sum.Op == IrBinaryOp.Add) {
      if (interesting is not IrBinary {
            Op: IrBinaryOp.Xor,
            Lhs: IrBinary { Op: IrBinaryOp.Xor } operandSigns,
            Rhs: IrConstantInt { Value: -1 },
          }
          || !SameOperands(operandSigns, sum.Lhs, sum.Rhs))
        return false;
    } else if (interesting is not IrBinary { Op: IrBinaryOp.Xor } operandSigns
               || !SameOperands(operandSigns, sum.Lhs, sum.Rhs))
      return false;

    arithmetic = sum;
    return true;
  }

  private static bool SameOperands(IrBinary binary, IrValue left, IrValue right)
    => ReferenceEquals(binary.Lhs, left) && ReferenceEquals(binary.Rhs, right)
       || ReferenceEquals(binary.Lhs, right) && ReferenceEquals(binary.Rhs, left);

  private static SafeInterval? TrySafeInterval(CountedLoop loop, OverflowCheck check, IrRangeAnalysis ranges) {
    var arithmetic = check.Arithmetic;
    var leftInvariant = IsInvariant(arithmetic.Lhs, loop.Region);
    var rightInvariant = IsInvariant(arithmetic.Rhs, loop.Region);
    if (leftInvariant == rightInvariant)
      return null;                                  // either no hoistable fact, or no loop-varying work

    var invariant = leftInvariant ? arithmetic.Lhs : arithmetic.Rhs;
    var varying = leftInvariant ? arithmetic.Rhs : arithmetic.Lhs;
    if (invariant is IrConstant)
      return null;                                  // a constant guard folds to always-fast or always-slow; O0219 handles the former

    var limits = ValueRange.OfType(arithmetic.Type);
    var varyingRange = ranges.RangeAt(varying, check.Guard);
    var invariantRange = ranges.RangeAt(invariant, check.Guard);
    if (limits.IsTop || limits.IsEmpty || varyingRange.IsTop || varyingRange.IsEmpty
        || invariantRange.IsTop || invariantRange.IsEmpty)
      return null;

    var resultRange = arithmetic.Op == IrBinaryOp.Add
      ? varyingRange.Add(invariantRange)
      : leftInvariant
        ? invariantRange.Subtract(varyingRange)
        : varyingRange.Subtract(invariantRange);
    if (!resultRange.IsTop && !resultRange.IsEmpty
        && resultRange.Lo >= limits.Lo && resultRange.Hi <= limits.Hi)
      return null;                                  // statically safe already; the ordinary range pass removes this check

    long rawLo, rawHi;
    if (arithmetic.Op == IrBinaryOp.Add) {
      rawLo = limits.Lo - varyingRange.Lo;
      rawHi = limits.Hi - varyingRange.Hi;
    } else if (leftInvariant) {                     // invariant - varying
      rawLo = limits.Lo + varyingRange.Hi;
      rawHi = limits.Hi + varyingRange.Lo;
    } else {                                        // varying - invariant
      rawLo = varyingRange.Hi - limits.Hi;
      rawHi = varyingRange.Lo - limits.Lo;
    }

    var lo = Math.Max(rawLo, limits.Lo);
    var hi = Math.Min(rawHi, limits.Hi);
    return lo <= hi ? new(invariant, lo, hi) : null;
  }

  private static bool IsInvariant(IrValue value, HashSet<IrBasicBlock> region)
    => value is not IrInstruction instruction
       || instruction.Parent is null
       || !region.Contains(instruction.Parent);

  private static void Version(IrFunction fn, CountedLoop loop, IReadOnlyList<OverflowCheck> checks,
      IReadOnlyList<SafeInterval> intervals) {
    var exitPhis = loop.Exit.Instructions.OfType<IrPhi>().ToList();
    var seed = new Dictionary<IrValue, IrValue>(ReferenceEqualityComparer.Instance);
    var fastBlocks = IrCloner.Clone(fn, loop.Region.ToList(), seed, _FAST_PREFIX, out var fastValues);
    var fastHeader = fastBlocks[loop.Header];

    // Existing boundary phis already describe the slow header. Give them the corresponding value for
    // the new fast predecessor before any direct outside uses are converted to loop-closed SSA below.
    foreach (var phi in exitPhis) {
      var slowValue = phi.IncomingFrom(loop.Header)!;
      phi.AddIncoming(fastValues.GetValueOrDefault(slowValue, slowValue), fastHeader);
    }

    // The cloned fast loop bypasses only the overflow branches whose safety the preheader proves.
    // Their trap blocks remain unreachable until SimplifyCfg removes them; retaining them here keeps
    // cloning and specialization separate and makes the rewrite mechanically small.
    foreach (var check in checks) {
      var fastGuard = fastBlocks[check.Guard];
      var fastContinuation = fastBlocks[check.Continuation];
      fastGuard.Terminator!.EraseFromParent();
      fastGuard.Append(new IrBr(fastContinuation));
    }

    // Values defined in the loop and read directly after it need one definition per version. Existing
    // exit phis were handled above; all other outside users get a new LCSSA join in the common exit.
    var escaping = loop.Region
      .SelectMany(block => block.Instructions)
      .Where(value => value.Users.Any(user =>
        user.Parent is { } where
        && !loop.Region.Contains(where)
        && !(ReferenceEquals(where, loop.Exit) && user is IrPhi)))
      .ToList();

    foreach (var value in escaping) {
      var joined = loop.Exit.AppendPhi(new IrPhi(value.Type) { Name = value.Name });
      joined.AddIncoming(value, loop.Header);
      joined.AddIncoming(fastValues.GetValueOrDefault(value, value), fastHeader);
      foreach (var user in value.Users.ToList())
        if (user.Parent is { } where
            && !loop.Region.Contains(where)
            && !(ReferenceEquals(where, loop.Exit) && user is IrPhi)
            && !ReferenceEquals(user, joined))
          user.ReplaceOperand(value, joined);
    }

    var preheaderTerminator = loop.Preheader.Terminator!;
    IrValue? safe = null;
    foreach (var interval in intervals) {
      var limits = ValueRange.OfType(interval.Invariant.Type);
      if (interval.Lo > limits.Lo) {
        var lower = loop.Preheader.InsertBefore(
          new IrCmp(IrCmpPred.Sge, interval.Invariant, new IrConstantInt(interval.Invariant.Type, interval.Lo)),
          preheaderTerminator);
        safe = AppendAnd(loop.Preheader, safe, lower, preheaderTerminator);
      }
      if (interval.Hi < limits.Hi) {
        var upper = loop.Preheader.InsertBefore(
          new IrCmp(IrCmpPred.Sle, interval.Invariant, new IrConstantInt(interval.Invariant.Type, interval.Hi)),
          preheaderTerminator);
        safe = AppendAnd(loop.Preheader, safe, upper, preheaderTerminator);
      }
    }

    // A selected check always contributes at least one proper bound; a missing guard here would mean
    // the safety interval equals the whole type, which the static-range test above already rejected.
    if (safe is null)
      throw new InvalidOperationException("O0308 selected a loop without a runtime safety condition");

    loop.Header.Label = _SLOW_PREFIX + loop.Header.Label; // prevents the fixpoint from versioning the fallback again
    preheaderTerminator.EraseFromParent();
    loop.Preheader.Append(new IrCondBr(safe, fastHeader, loop.Header));
  }

  private static IrValue AppendAnd(IrBasicBlock block, IrValue? accumulated, IrValue condition, IrInstruction before)
    => accumulated is null
      ? condition
      : block.InsertBefore(new IrBinary(IrBinaryOp.And, accumulated, condition), before);
}
