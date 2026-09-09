namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0272 — consumes a loop trip-count histogram and specializes a canonical hot small-trip loop by
/// peeling its dominant prefix. The profile decides PROFITABILITY only: every peeled iteration keeps
/// the original loop test and every non-profiled/stale outcome falls through to the untouched loop,
/// so profile mismatch cannot change program behaviour.
///
/// <para>
/// This is the first independent O0272 slice. It deliberately does not collect profiles (O0268),
/// invent vector widths, or assume the bimodal versioning machinery from O0130 exists. A future
/// profile loader only has to attach <see cref="IrLoopTripCountProfile"/> to the loop header through
/// <see cref="IrProfileMetadata"/>; this pass is otherwise independent of the profile file format.
/// </para>
/// </summary>
public static class ProfileGuidedLoopOptimization {

  private const ulong _MIN_SAMPLES = 32;
  private const double _DOMINANT_SHARE = 0.90;
  private const int _MAX_PEEL_TRIPS = 8;
  private const int _MAX_CLONED_INSTRUCTIONS = 96;

  /// <summary>Optimizes profiled loops in <paramref name="fn"/>; returns the number transformed.</summary>
  public static int Run(IrFunction fn) {
    if (fn.HasErrorHandler || fn.HasInlineAsm)
      return 0;

    var transformed = 0;
    foreach (var header in fn.Blocks.ToList()) {
      if (!IrProfileMetadata.TryGetLoopTripCounts(header, out var profile)
          || ChoosePeelCount(profile) is not { } peelCount
          || Match(fn, header) is not { } loop
          || !Profitable(loop, peelCount))
        continue;

      PeelPrefix(fn, loop, peelCount);
      IrProfileMetadata.RemoveLoopTripCounts(header);
      ++transformed;
    }
    return transformed;
  }

  /// <summary>
  /// A high-confidence small mode is actionable. Zero trips means there is no body to peel; a broad
  /// or bimodal histogram deliberately has no answer — using the average here is exactly the mistake
  /// O0272 exists to avoid.
  /// </summary>
  private static int? ChoosePeelCount(IrLoopTripCountProfile profile) {
    if (profile.TotalSamples < _MIN_SAMPLES)
      return null;
    return profile.DominantTripCount(_DOMINANT_SHARE) switch {
      > 0 and <= _MAX_PEEL_TRIPS and var trips => (int)trips,
      _ => null,
    };
  }

  private sealed record Loop(
    IrBasicBlock Header,
    IrBasicBlock Preheader,
    IReadOnlyList<IrBasicBlock> Body,
    IrBasicBlock Latch,
    IrCmp Test,
    IReadOnlyList<IrPhi> Phis);

  /// <summary>
  /// Recognizes the same conservative FOR-like shape as the full unroller, except the bound and trip
  /// count may be runtime values. Header work is only phis + compare + branch, and the body is one
  /// straight-line block chain ending at a single latch. That makes cloning one iteration mechanical
  /// instead of speculative CFG surgery.
  /// </summary>
  private static Loop? Match(IrFunction fn, IrBasicBlock header) {
    if (header.Terminator is not IrCondBr { Condition: IrCmp test } branch)
      return null;

    var phis = header.Instructions.OfType<IrPhi>().ToList();
    if (phis.Count == 0
        || header.Instructions.Count != phis.Count + 2
        || !ReferenceEquals(header.Instructions[^2], test)
        || !ReferenceEquals(header.Instructions[^1], branch))
      return null;

    var exit = branch.IfFalse;
    var body = new List<IrBasicBlock>();
    IrBasicBlock? latch = null;
    for (var at = branch.IfTrue; latch is null;) {
      if (ReferenceEquals(at, header) || ReferenceEquals(at, exit) || body.Contains(at))
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
    var preheader = predecessors.SingleOrDefault(block => !ReferenceEquals(block, latch));
    if (preheader?.Terminator is not IrBr preBranch || !ReferenceEquals(preBranch.Target, header))
      return null;

    foreach (var phi in phis)
      if (phi.IncomingBlocks.Count != 2
          || phi.IncomingFrom(preheader) is null
          || phi.IncomingFrom(latch) is null)
        return null;

    // No block other than the header may enter the body from outside. A cloned straight-line prefix
    // otherwise bypasses an edge whose value/side effects it did not reproduce.
    foreach (var block in fn.Blocks)
      if (!body.Contains(block) && !ReferenceEquals(block, header) && block.Terminator is { } terminator)
        foreach (var successor in terminator.Successors)
          if (body.Contains(successor))
            return null;

    var addressTaken = fn.AddressTakenBlocks();
    if (addressTaken.Contains(preheader) || addressTaken.Contains(header) || addressTaken.Overlaps(body))
      return null;

    return new(header, preheader, body, latch, test, phis);
  }

  private static bool Profitable(Loop loop, int peelCount) {
    var bodyInstructions = loop.Body.Sum(static block => block.Instructions.Count);
    // Each copy also gets one compare + conditional branch. Keep a hard cap so a hot-but-large loop
    // does not turn one histogram bucket into a code-size accident.
    return checked((bodyInstructions + 2) * peelCount) <= _MAX_CLONED_INSTRUCTIONS;
  }

  /// <summary>
  /// Builds N guarded copies in front of the original loop. A guard that says "not another trip"
  /// enters the original header with the current phi values; the header repeats the pure comparison
  /// and exits normally. After N successful copies the last cloned latch also enters the original
  /// header, which executes every remaining iteration. The fallback is therefore exact for zero,
  /// shorter, modal, longer, and stale-profile trip counts alike.
  /// </summary>
  private static void PeelPrefix(IrFunction fn, Loop loop, int peelCount) {
    var current = new Dictionary<IrPhi, IrValue>(ReferenceEqualityComparer.Instance);
    var latchValues = new Dictionary<IrPhi, IrValue>(ReferenceEqualityComparer.Instance);
    foreach (var phi in loop.Phis) {
      current[phi] = phi.IncomingFrom(loop.Preheader)!;
      latchValues[phi] = phi.IncomingFrom(loop.Latch)!;
    }

    var guardIncomings = new List<(IrBasicBlock Guard, Dictionary<IrPhi, IrValue> Values)>(peelCount);
    IrBasicBlock? firstGuard = null;
    IrBasicBlock? previousLatch = null;

    for (var trip = 0; trip < peelCount; ++trip) {
      var guard = fn.CreateBlock($"pgo.peel{trip}.guard");
      firstGuard ??= guard;
      if (previousLatch is not null)
        Retarget(previousLatch, guard);

      var before = new Dictionary<IrPhi, IrValue>(current, ReferenceEqualityComparer.Instance);
      var seed = new Dictionary<IrValue, IrValue>(ReferenceEqualityComparer.Instance);
      foreach (var phi in loop.Phis)
        seed[phi] = current[phi];

      var clones = IrCloner.Clone(fn, loop.Body, seed, $"pgo.peel{trip}.");
      var clonedEntry = clones[loop.Body[0]];
      var clonedLatch = clones[loop.Latch];

      var cmp = guard.Append(new IrCmp(
        loop.Test.Pred,
        MapHeaderValue(loop.Test.Lhs, current),
        MapHeaderValue(loop.Test.Rhs, current)) {
          IsSourceCondition = loop.Test.IsSourceCondition,
          FastMathFlags = loop.Test.FastMathFlags,
        });
      guard.Append(new IrCondBr(cmp, clonedEntry, loop.Header));
      guardIncomings.Add((guard, before));

      var next = new Dictionary<IrPhi, IrValue>(ReferenceEqualityComparer.Instance);
      foreach (var phi in loop.Phis) {
        var carried = latchValues[phi];
        next[phi] = seed.GetValueOrDefault(carried, carried);
      }
      current = next;
      previousLatch = clonedLatch;
    }

    // peelCount is positive by policy, so both are established.
    Retarget(previousLatch!, loop.Header);
    Retarget(loop.Preheader, firstGuard!);

    foreach (var phi in loop.Phis) {
      phi.RemoveIncoming(loop.Preheader);
      foreach (var (guard, values) in guardIncomings)
        phi.AddIncoming(values[phi], guard);
      phi.AddIncoming(current[phi], previousLatch!);
    }
  }

  private static IrValue MapHeaderValue(IrValue value, IReadOnlyDictionary<IrPhi, IrValue> current)
    => value is IrPhi phi && current.TryGetValue(phi, out var mapped) ? mapped : value;

  private static void Retarget(IrBasicBlock block, IrBasicBlock target) {
    if (block.Terminator is { } old)
      old.EraseFromParent();
    block.Append(new IrBr(target));
  }
}
