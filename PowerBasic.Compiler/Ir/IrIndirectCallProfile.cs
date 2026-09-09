using System.Runtime.CompilerServices;

namespace PowerBasic.Compiler.Ir;

/// <summary>One observed target of an indirect call and the number of executions that reached it.</summary>
public readonly record struct IrIndirectCallTarget(IrFunction Target, ulong Count);

/// <summary>
/// Value-profile data for one indirect <see cref="IrCall"/>. The total count includes executions of
/// targets not present in <see cref="Targets"/>, so the listed counts may sum to less than the total.
/// </summary>
public sealed class IrIndirectCallProfile {

  private readonly IrIndirectCallTarget[] _targets;

  public IrIndirectCallProfile(ulong totalCount, params IrIndirectCallTarget[] targets) {
    ArgumentNullException.ThrowIfNull(targets);
    if (totalCount == 0)
      throw new ArgumentOutOfRangeException(nameof(totalCount), "an indirect-call profile needs at least one execution");
    if (targets.Length == 0)
      throw new ArgumentException("an indirect-call profile needs at least one observed target", nameof(targets));

    var seen = new HashSet<IrFunction>(ReferenceEqualityComparer.Instance);
    ulong accounted = 0;
    foreach (var target in targets) {
      ArgumentNullException.ThrowIfNull(target.Target);
      if (target.Count == 0 || target.Count > totalCount)
        throw new ArgumentOutOfRangeException(nameof(targets), "target counts must be between one and the total count");
      if (!seen.Add(target.Target))
        throw new ArgumentException("an indirect-call profile cannot list the same target twice", nameof(targets));
      if (ulong.MaxValue - accounted < target.Count || (accounted += target.Count) > totalCount)
        throw new ArgumentException("observed target counts cannot exceed the total execution count", nameof(targets));
    }

    this.TotalCount = totalCount;
    this._targets = [.. targets];
  }

  /// <summary>Total executions observed at the call site, including unlisted targets.</summary>
  public ulong TotalCount { get; }

  /// <summary>Observed targets. Consumers must not assume the list is sorted by count.</summary>
  public IReadOnlyList<IrIndirectCallTarget> Targets => this._targets;
}

/// <summary>
/// Profile metadata attached to IR calls without making runtime emitters understand profiling data.
/// Weak keys make the metadata die with a discarded call, and cloned calls intentionally start
/// unprofiled because a clone is no longer the source call site whose execution counts were measured.
/// </summary>
public static class IrCallProfileExtensions {

  private static readonly ConditionalWeakTable<IrCall, IrIndirectCallProfile> _profiles = new();

  /// <summary>Returns the indirect-target profile attached to <paramref name="call"/>, if any.</summary>
  public static IrIndirectCallProfile? GetIndirectTargetProfile(this IrCall call) {
    ArgumentNullException.ThrowIfNull(call);
    return _profiles.TryGetValue(call, out var profile) ? profile : null;
  }

  /// <summary>Attaches profile data, or clears it when <paramref name="profile"/> is null.</summary>
  public static void SetIndirectTargetProfile(this IrCall call, IrIndirectCallProfile? profile) {
    ArgumentNullException.ThrowIfNull(call);
    _profiles.Remove(call);
    if (profile is not null)
      _profiles.Add(call, profile);
  }
}
