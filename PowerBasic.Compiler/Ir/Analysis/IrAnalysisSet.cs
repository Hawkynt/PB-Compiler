namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>
/// Named category of analyses that share an invalidation contract. Analysis keys opt into sets explicitly;
/// preserving a set preserves every member currently or subsequently registered through its key declaration.
/// </summary>
public sealed class IrAnalysisSet {

  public IrAnalysisSet(string name) {
    if (string.IsNullOrWhiteSpace(name))
      throw new ArgumentException("Analysis-set name cannot be empty.", nameof(name));
    this.Name = name;
  }

  /// <summary>Diagnostic name of this set.</summary>
  public string Name { get; }

  internal bool Contains(IrAnalysisKey analysis) => analysis.BelongsTo(this);

  public override string ToString() => this.Name;
}

/// <summary>Shared analysis categories used by preservation contracts.</summary>
public static class IrAnalysisSets {

  /// <summary>Analyses derived only from CFG topology, not instruction/value contents.</summary>
  public static IrAnalysisSet Cfg { get; } = new("cfg");
}
