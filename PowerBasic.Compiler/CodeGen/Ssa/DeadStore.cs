using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen.Ssa;

/// <summary>
/// SSA dead-store elimination (docs/PB36.md O2/O17). Removes assignments to
/// non-escaping tracked scalars whose SSA version is never really read - i.e.
/// the value is dead, or every read of it was already folded to a constant by
/// SCCP. Composes with SCCP: once a constant has been propagated into all its
/// uses, the assignment that produced it is dead.
///
/// Only literal / equate / variable-copy right-hand sides qualify (they cannot
/// trap and have no side effects, so removing the store is unobservable). The
/// pass is an aggressive mark-sweep: liveness is seeded from real (unfolded)
/// reads in statements that are not themselves removable, then propagated
/// through phi inputs and the right-hand sides of kept assignments, so a value
/// kept alive only by a dead copy chain still dies.
/// </summary>
public static class DeadStore {

  /// <summary>The assignment statements safe to drop. Empty when nothing is dead.</summary>
  public static HashSet<Statement> Compute(SemanticModel model, SsaForm ssa, IReadOnlyDictionary<NameExpr, long> provenReads) {
    // candidate def versions: an Assign with a pure, non-trapping RHS
    var candidateRhsReads = new Dictionary<SsaValue, List<NameExpr>>(ReferenceEqualityComparer.Instance);
    foreach (var v in ssa.Values)
      if (v is { Kind: SsaDefKind.Assign, DefExpr: { } rhs } && IsRemovableRhs(rhs, model))
        candidateRhsReads[v] = ReadsIn(rhs);

    // a read is "real" when SCCP did not fold it to a constant
    bool IsReal(NameExpr r) => !provenReads.ContainsKey(r);

    // reads that belong to a removable candidate's RHS are only conditionally
    // live (live iff that candidate is kept); every other real read is a root
    var conditionalReads = new HashSet<NameExpr>(
      candidateRhsReads.Values.SelectMany(rs => rs), ReferenceEqualityComparer.Instance);

    var live = new HashSet<SsaValue>(ReferenceEqualityComparer.Instance);
    var worklist = new Queue<SsaValue>();
    void MarkLive(SsaValue v) {
      if (live.Add(v))
        worklist.Enqueue(v);
    }

    // seed: every real read outside a candidate RHS keeps its version live
    foreach (var (read, version) in ssa.UseVersions)
      if (IsReal(read) && !conditionalReads.Contains(read))
        MarkLive(version);

    while (worklist.Count > 0) {
      var v = worklist.Dequeue();
      switch (v.Kind) {
        case SsaDefKind.Phi:
          foreach (var (_, input) in v.PhiInputs)
            MarkLive(input);
          break;
        case SsaDefKind.IncrDecr:
          if (v.IncrBase != null)
            MarkLive(v.IncrBase); // a kept INCR reads its prior version
          break;
        case SsaDefKind.Assign:
          // keeping this assignment emits its RHS, so its real reads come alive
          if (candidateRhsReads.TryGetValue(v, out var reads))
            foreach (var r in reads)
              if (IsReal(r) && ssa.UseVersions.TryGetValue(r, out var rv))
                MarkLive(rv);
          break;
      }
    }

    var dead = new HashSet<Statement>(ReferenceEqualityComparer.Instance);
    foreach (var v in candidateRhsReads.Keys)
      if (!live.Contains(v) && v.DefStmt != null)
        dead.Add(v.DefStmt);
    return dead;
  }

  /// <summary>A RHS that cannot trap and has no side effects: a literal, an integral equate, or a plain variable copy.</summary>
  private static bool IsRemovableRhs(Expression e, SemanticModel model) => e switch {
    IntegerLiteralExpr => true,
    NamedConstantExpr c => model.Equates.TryGetValue(c.Name, out var v) && v.Integer != null,
    NameExpr n => model.VariableBindings.ContainsKey(n)
      && !model.CallBindings.ContainsKey(n)
      && !model.IntrinsicBindings.ContainsKey(n), // a real variable read, not a parameterless call
    _ => false,
  };

  private static List<NameExpr> ReadsIn(Expression e) {
    var reads = new List<NameExpr>();
    if (e is NameExpr n)
      reads.Add(n);
    return reads; // removable RHS shapes have at most one variable read (a copy)
  }
}
