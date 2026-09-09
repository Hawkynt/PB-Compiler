using System.Runtime.CompilerServices;

namespace PowerBasic.Compiler.Ir;

/// <summary>
/// Observed execution counts for one loop, keyed by the number of body iterations completed on an
/// invocation. The histogram is intentionally the primitive rather than an average: two loops can
/// both average four trips while one is almost always four and the other alternates between zero and
/// eight, and those are different optimization decisions.
/// </summary>
public sealed class IrLoopTripCountProfile {

  private readonly Dictionary<long, ulong> _histogram = [];

  public IrLoopTripCountProfile(IEnumerable<(long Trips, ulong Samples)> histogram) {
    ArgumentNullException.ThrowIfNull(histogram);

    ulong total = 0;
    foreach (var (trips, samples) in histogram) {
      if (trips < 0)
        throw new ArgumentOutOfRangeException(nameof(histogram), trips, "loop trip counts cannot be negative");
      if (samples == 0)
        continue;

      this._histogram[trips] = checked(this._histogram.GetValueOrDefault(trips) + samples);
      total = checked(total + samples);
    }

    this.TotalSamples = total;
  }

  /// <summary>The copied trip-count histogram. Zero-sample buckets are omitted.</summary>
  public IReadOnlyDictionary<long, ulong> Histogram => this._histogram;

  /// <summary>The total number of loop invocations represented by the histogram.</summary>
  public ulong TotalSamples { get; }

  /// <summary>
  /// Returns the modal trip count when it owns at least <paramref name="minimumShare"/> of all
  /// samples, else null. Ties choose the smaller count deterministically, but a genuine tie cannot
  /// satisfy a threshold above 0.5.
  /// </summary>
  public long? DominantTripCount(double minimumShare) {
    if (double.IsNaN(minimumShare) || minimumShare is < 0 or > 1)
      throw new ArgumentOutOfRangeException(nameof(minimumShare));
    if (this.TotalSamples == 0)
      return null;

    var mode = this._histogram
      .OrderByDescending(static pair => pair.Value)
      .ThenBy(static pair => pair.Key)
      .First();
    return mode.Value / (double)this.TotalSamples >= minimumShare ? mode.Key : null;
  }
}

/// <summary>
/// Side metadata attached to IR identities. Profiles are deliberately not operands and do not take
/// part in SSA/use-lists: they are profitability evidence, never semantics. Keeping them in a weak
/// side table also means a cloned block starts unprofiled instead of accidentally inheriting the
/// source loop's observations.
///
/// <para>
/// O0268 will eventually populate this table from instrumented/profile files. O0272 only consumes
/// the representation, so tests and future loaders can attach measured histograms without coupling
/// the middle-end to profile collection or file formats.
/// </para>
/// </summary>
public static class IrProfileMetadata {

  private sealed class Box(IrLoopTripCountProfile value) {
    public IrLoopTripCountProfile Value { get; } = value;
  }

  private static readonly ConditionalWeakTable<IrBasicBlock, Box> _loopTrips = new();
  private static readonly object _gate = new();

  /// <summary>Associates an observed trip-count distribution with a loop header block.</summary>
  public static void SetLoopTripCounts(IrBasicBlock header, IrLoopTripCountProfile profile) {
    ArgumentNullException.ThrowIfNull(header);
    ArgumentNullException.ThrowIfNull(profile);
    lock (_gate) {
      _loopTrips.Remove(header);
      _loopTrips.Add(header, new(profile));
    }
  }

  /// <summary>Gets the trip-count distribution associated with <paramref name="header"/>.</summary>
  public static bool TryGetLoopTripCounts(IrBasicBlock header, out IrLoopTripCountProfile profile) {
    ArgumentNullException.ThrowIfNull(header);
    if (_loopTrips.TryGetValue(header, out var box)) {
      profile = box.Value;
      return true;
    }

    profile = null!;
    return false;
  }

  /// <summary>Consumes profile metadata after a transform so a fixpoint sweep does not apply it twice.</summary>
  internal static void RemoveLoopTripCounts(IrBasicBlock header) {
    lock (_gate)
      _loopTrips.Remove(header);
  }
}
