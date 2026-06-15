using PowerBasic.Compiler.CodeGen.Ssa;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

/// <summary>
/// pb36 copy propagation over the SSA. A copy <c>y = x</c> (the right-hand side a bare
/// read of another tracked scalar of the same type) lets every read of <c>y</c> read
/// <c>x</c>'s storage instead, after which the copy assignment is dead and dropped.
/// Soundness bound: the source must be assigned at most once in the body, so its cell is
/// stable across the whole live range and the redirected reads see exactly the copied
/// value; copy chains are resolved to the root whose cell is actually written, so a
/// removed copy never leaves a read pointing at an unwritten cell. Output is unchanged
/// (the same values flow), so the differential harness stays byte-identical.
/// </summary>
public static class Pb36CopyProp {

  public static (IReadOnlyDictionary<NameExpr, VariableSymbol> Reads, IReadOnlySet<Statement> DeadCopies) Analyze(SsaForm ssa) {
    var noReads = new Dictionary<NameExpr, VariableSymbol>(ReferenceEqualityComparer.Instance);
    var noDead = new HashSet<Statement>(ReferenceEqualityComparer.Instance);

    // how many real (assign/incr) definitions each tracked variable has
    var defCount = new Dictionary<VariableSymbol, int>(ReferenceEqualityComparer.Instance);
    foreach (var v in ssa.Values)
      if (v.Kind is SsaDefKind.Assign or SsaDefKind.IncrDecr)
        defCount[v.Variable] = defCount.GetValueOrDefault(v.Variable) + 1;

    // reads grouped by the version they resolve to
    var readsByVersion = new Dictionary<SsaValue, List<NameExpr>>(ReferenceEqualityComparer.Instance);
    foreach (var (read, version) in ssa.UseVersions)
      (readsByVersion.TryGetValue(version, out var list) ? list : readsByVersion[version] = []).Add(read);

    // candidate copies: the copy version, its immediate source variable, and its reads
    var candidates = new List<(SsaValue Copy, VariableSymbol Source)>();
    var copySource = new Dictionary<VariableSymbol, VariableSymbol>(ReferenceEqualityComparer.Instance);
    foreach (var copy in ssa.Values) {
      if (copy.Kind != SsaDefKind.Assign || copy.DefStmt is null || copy.DefExpr is not NameExpr sourceRead)
        continue;
      if (!ssa.UseVersions.TryGetValue(sourceRead, out var sourceVersion))
        continue;                                       // RHS is not a tracked scalar read
      var source = sourceVersion.Variable;
      var target = copy.Variable;
      if (ReferenceEquals(source, target) || !source.Type.Equals(target.Type))
        continue;
      if (defCount.GetValueOrDefault(source) > 1 || defCount.GetValueOrDefault(target) != 1)
        continue;                                       // source cell must be stable; target defined only here
      candidates.Add((copy, source));
      copySource[target] = source;
    }
    if (candidates.Count == 0)
      return (noReads, noDead);

    var reads = new Dictionary<NameExpr, VariableSymbol>(ReferenceEqualityComparer.Instance);
    var deadCopies = new HashSet<Statement>(ReferenceEqualityComparer.Instance);
    foreach (var (copy, source) in candidates) {
      var root = ResolveRoot(source, copySource);
      if (root is null)
        continue;                                       // pathological (cycle) - leave the copy in place
      if (readsByVersion.TryGetValue(copy, out var copyReads))
        foreach (var r in copyReads)
          reads[r] = root;
      deadCopies.Add(copy.DefStmt!);
    }
    return (reads, deadCopies);
  }

  /// <summary>Follows the copy chain to the variable whose cell is actually written (not itself a removed copy).</summary>
  private static VariableSymbol? ResolveRoot(VariableSymbol source, Dictionary<VariableSymbol, VariableSymbol> copySource) {
    var seen = new HashSet<VariableSymbol>(ReferenceEqualityComparer.Instance);
    var current = source;
    while (copySource.TryGetValue(current, out var next)) {
      if (!seen.Add(current))
        return null;                                    // cycle guard
      current = next;
    }
    return current;
  }
}
