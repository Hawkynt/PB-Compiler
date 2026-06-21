using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

/// <summary>
/// pb36 O6 reachability support: which procedures the emitter will inline at
/// <em>every</em> call site, so the reachability pruner can ignore their call
/// edges and purge the now-unreferenced procedure body from the image.
///
/// A procedure qualifies only when it is structurally inlinable (decided by the
/// emitter's <c>AnalyzeInlinableLeaf</c>, passed in here as a predicate) and every
/// reference to it in <see cref="SemanticModel.CallBindings"/> is a genuine call the
/// emitter inlines: a SUB called as a statement, or a FUNCTION used in value position
/// (a FUNCTION whose result is discarded - a <see cref="CallStmt"/> binding - does NOT
/// inline). Arity must match exactly (no defaulted/variadic call). The whole analysis
/// bails to the empty set the moment a procedure's address could be taken (any
/// CODEPTR-family intrinsic in the program), because an indirect call cannot inline and
/// the procedure must survive - conservative, sound, and identical to how IPCP guards.
/// </summary>
public static class OptInlining {

  public static HashSet<ProcedureSymbol> FullyInlinedProcedures(
      SemanticModel model,
      Func<ProcedureSymbol, bool> isInlinable,
      Func<ProcedureSymbol, bool> isFullyOwned,
      Func<Expression, bool> isNearLValue,
      bool programHasErrorHandling) {
    var result = new HashSet<ProcedureSymbol>(ReferenceEqualityComparer.Instance);

    // the emitter declines to inline into an error-handling region (RESUME latch
    // mismatch), so a call site there falls back to a real call - keep every body
    if (programHasErrorHandling)
      return result;

    // any address-taking reference makes every procedure potentially indirectly called
    foreach (var (_, intrinsic) in model.IntrinsicBindings)
      if (intrinsic.Name is "CODEPTR" or "CODESEG" or "CODEPTR32")
        return result;

    // a procedure is purgeable only if it inlines at every site; one non-inlinable
    // reference poisons it
    var poisoned = new HashSet<ProcedureSymbol>(ReferenceEqualityComparer.Instance);
    var referenced = new HashSet<ProcedureSymbol>(ReferenceEqualityComparer.Instance);
    foreach ((object node, ProcedureSymbol proc) in model.CallBindings) {
      referenced.Add(proc);
      if (poisoned.Contains(proc))
        continue;
      if (!isFullyOwned(proc) || !isInlinable(proc) || !CallInlinesHere(node, proc, model, isNearLValue))
        poisoned.Add(proc);
    }

    foreach (var proc in referenced)
      if (!poisoned.Contains(proc))
        result.Add(proc);
    return result;
  }

  /// <summary>True when the reference <paramref name="node"/> to <paramref name="proc"/> is a genuine call the emitter inlines (right arity; a FUNCTION must be in value position).</summary>
  private static bool CallInlinesHere(object node, ProcedureSymbol proc, SemanticModel model, Func<Expression, bool> isNearLValue) {
    var args = model.ReorderedArguments.GetValueOrDefault(node) ?? ArgsOf(node);
    if (args.Count != proc.Parameters.Count)
      return false;                 // defaulted / variadic / wrong arity - the inliner declines
    // a FUNCTION inlines only when its result is consumed (value position). The binder
    // keys a discarded FUNCTION-as-statement as a CallStmt; an expression-position call
    // is a CallOrIndexExpr (with args) or a bare NameExpr (parameterless).
    if (proc.IsFunction && node is CallStmt)
      return false;
    // a BYREF argument inlines only as a near lvalue (its address is passed); otherwise the
    // emitter falls back to a real call, so the body must survive
    for (var i = 0; i < args.Count; ++i)
      if (!proc.Parameters[i].ByVal && !isNearLValue(args[i]))
        return false;
    return true;
  }

  private static IReadOnlyList<Expression> ArgsOf(object node) => node switch {
    CallStmt c => c.Arguments,
    CallOrIndexExpr c => c.Arguments,
    _ => [],                        // a bare NameExpr is a parameterless call
  };
}
