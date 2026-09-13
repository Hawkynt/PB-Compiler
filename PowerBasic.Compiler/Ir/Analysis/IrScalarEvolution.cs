namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>
/// Shared scalar-evolution facts for natural loops. The bootstrap domain recognizes integer additive recurrences
/// of the form <c>{start,+,step}</c> and proves exact trip counts for canonical header comparisons with constant
/// start, step and limit. It deliberately models the IR's fixed-width wrapping arithmetic instead of assuming
/// mathematical integers or no-wrap flags the IR does not carry.
/// </summary>
public sealed class IrScalarEvolution {

  private const int _MAX_SIMULATED_TRIPS = 1 << 20;

  /// <summary>One loop-carried additive recurrence.</summary>
  public sealed record AddRecurrence(
    IrLoopAnalysis.Loop Loop,
    IrPhi Phi,
    IrValue Start,
    IrValue Step,
    IrBinary Update);

  private readonly Dictionary<IrPhi, AddRecurrence> _byPhi =
    new(ReferenceEqualityComparer.Instance);
  private readonly Dictionary<IrLoopAnalysis.Loop, List<AddRecurrence>> _byLoop =
    new(ReferenceEqualityComparer.Instance);
  private readonly Dictionary<IrLoopAnalysis.Loop, long?> _tripCounts =
    new(ReferenceEqualityComparer.Instance);

  private IrScalarEvolution(IrFunction function, IrLoopAnalysis loops) {
    ArgumentNullException.ThrowIfNull(function);
    ArgumentNullException.ThrowIfNull(loops);

    foreach (var loop in loops.Loops) {
      var recurrences = this.FindRecurrences(loop);
      this._byLoop[loop] = recurrences;
      foreach (var recurrence in recurrences)
        this._byPhi[recurrence.Phi] = recurrence;
      this._tripCounts[loop] = ExactTripCount(loop, recurrences);
    }
  }

  /// <summary>Builds scalar-evolution facts from the shared natural-loop forest.</summary>
  internal static IrScalarEvolution Build(IrFunction function, IrLoopAnalysis loops)
    => new(function, loops);

  /// <summary>Returns the additive recurrence carried by a phi, or null when the phi is not a supported recurrence.</summary>
  public AddRecurrence? RecurrenceFor(IrPhi phi) {
    ArgumentNullException.ThrowIfNull(phi);
    return this._byPhi.GetValueOrDefault(phi);
  }

  /// <summary>All supported additive recurrences directly discovered for a loop header.</summary>
  public IReadOnlyList<AddRecurrence> RecurrencesFor(IrLoopAnalysis.Loop loop) {
    ArgumentNullException.ThrowIfNull(loop);
    return this._byLoop.TryGetValue(loop, out var recurrences) ? recurrences : [];
  }

  /// <summary>
  /// Exact number of body entries for the canonical header test, or null when the count is unknown or exceeds
  /// the bounded simulation budget. Zero is a valid exact result for a loop whose first header test fails.
  /// </summary>
  public long? ExactTripCount(IrLoopAnalysis.Loop loop) {
    ArgumentNullException.ThrowIfNull(loop);
    return this._tripCounts.GetValueOrDefault(loop);
  }

  /// <summary>Whether a value is structurally loop-invariant.</summary>
  public static bool IsLoopInvariant(IrValue value, IrLoopAnalysis.Loop loop) {
    ArgumentNullException.ThrowIfNull(value);
    ArgumentNullException.ThrowIfNull(loop);
    return value is not IrInstruction { Parent: { } block } || !loop.Contains(block);
  }

  private List<AddRecurrence> FindRecurrences(IrLoopAnalysis.Loop loop) {
    var result = new List<AddRecurrence>();
    if (loop.UniqueEnteringBlock is not { } preheader || loop.Latches.Count != 1)
      return result;
    var latch = loop.Latches[0];

    foreach (var phi in loop.Header.Phis) {
      if (!phi.Type.IsInteger
          || phi.IncomingFrom(preheader) is not { } start
          || phi.IncomingFrom(latch) is not IrBinary { Op: IrBinaryOp.Add } update
          || !ReferenceEquals(update.Parent, latch)
          || !Equals(update.Type, phi.Type))
        continue;

      IrValue? step = null;
      if (ReferenceEquals(update.Lhs, phi) && IsLoopInvariant(update.Rhs, loop))
        step = update.Rhs;
      else if (ReferenceEquals(update.Rhs, phi) && IsLoopInvariant(update.Lhs, loop))
        step = update.Lhs;

      if (step is null || !Equals(step.Type, phi.Type))
        continue;
      result.Add(new(loop, phi, start, step, update));
    }

    return result;
  }

  private static long? ExactTripCount(IrLoopAnalysis.Loop loop, IReadOnlyList<AddRecurrence> recurrences) {
    if (loop.Header.Terminator is not IrCondBr { Condition: IrCmp comparison } branch)
      return null;

    var trueInside = loop.Contains(branch.IfTrue);
    var falseInside = loop.Contains(branch.IfFalse);
    if (trueInside == falseInside)
      return null;

    var predicate = trueInside ? comparison.Pred : Negate(comparison.Pred);
    if (predicate is null)
      return null;

    AddRecurrence? recurrence;
    IrConstantInt limit;
    if (comparison.Lhs is IrPhi leftPhi
        && recurrences.FirstOrDefault(candidate => ReferenceEquals(candidate.Phi, leftPhi)) is { } leftRecurrence
        && comparison.Rhs is IrConstantInt rightLimit) {
      recurrence = leftRecurrence;
      limit = rightLimit;
    } else if (comparison.Rhs is IrPhi rightPhi
               && recurrences.FirstOrDefault(candidate => ReferenceEquals(candidate.Phi, rightPhi)) is { } rightRecurrence
               && comparison.Lhs is IrConstantInt leftLimit) {
      recurrence = rightRecurrence;
      limit = leftLimit;
      predicate = Swap(predicate.Value);
    } else {
      return null;
    }

    if (recurrence.Start is not IrConstantInt start
        || recurrence.Step is not IrConstantInt step
        || !Equals(start.Type, recurrence.Phi.Type)
        || !Equals(step.Type, recurrence.Phi.Type)
        || !Equals(limit.Type, recurrence.Phi.Type)
        || step.IsZero)
      return null;

    var value = Wrap(recurrence.Phi.Type, start.Value);
    var bound = Wrap(recurrence.Phi.Type, limit.Value);
    var delta = Wrap(recurrence.Phi.Type, step.Value);
    for (long trips = 0; trips <= _MAX_SIMULATED_TRIPS; ++trips) {
      if (!Holds(predicate.Value, recurrence.Phi.Type, value, bound))
        return trips;
      var advanced = Wrap(recurrence.Phi.Type, unchecked(value + delta));
      if (advanced == value)
        return null;
      value = advanced;
    }
    return null;
  }

  private static bool Holds(IrCmpPred predicate, IrType type, long left, long right) => predicate switch {
    IrCmpPred.Eq => left == right,
    IrCmpPred.Ne => left != right,
    IrCmpPred.Slt => left < right,
    IrCmpPred.Sle => left <= right,
    IrCmpPred.Sgt => left > right,
    IrCmpPred.Sge => left >= right,
    IrCmpPred.Ult => Unsigned(type, left) < Unsigned(type, right),
    IrCmpPred.Ule => Unsigned(type, left) <= Unsigned(type, right),
    IrCmpPred.Ugt => Unsigned(type, left) > Unsigned(type, right),
    IrCmpPred.Uge => Unsigned(type, left) >= Unsigned(type, right),
    _ => false,
  };

  private static IrCmpPred? Negate(IrCmpPred predicate) => predicate switch {
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

  private static IrCmpPred Swap(IrCmpPred predicate) => predicate switch {
    IrCmpPred.Slt => IrCmpPred.Sgt,
    IrCmpPred.Sle => IrCmpPred.Sge,
    IrCmpPred.Sgt => IrCmpPred.Slt,
    IrCmpPred.Sge => IrCmpPred.Sle,
    IrCmpPred.Ult => IrCmpPred.Ugt,
    IrCmpPred.Ule => IrCmpPred.Uge,
    IrCmpPred.Ugt => IrCmpPred.Ult,
    IrCmpPred.Uge => IrCmpPred.Ule,
    _ => predicate,
  };

  private static long Wrap(IrType type, long value) => type.Bits switch {
    8 => (sbyte)value,
    16 => (short)value,
    32 => (int)value,
    64 => value,
    _ => value,
  };

  private static ulong Unsigned(IrType type, long value) => type.Bits switch {
    8 => (byte)value,
    16 => (ushort)value,
    32 => (uint)value,
    _ => unchecked((ulong)value),
  };
}
