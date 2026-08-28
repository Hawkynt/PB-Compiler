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
    // a BYREF argument inlines only when its address IS the parameter's storage; otherwise the
    // emitter falls back to a real call, so the body must survive
    for (var i = 0; i < args.Count; ++i)
      if (!proc.Parameters[i].ByVal && !InlinableByRefArgument(args[i], proc.Parameters[i], model, isNearLValue))
        return false;
    return true;
  }

  /// <summary>
  /// Whether a BYREF argument can be bound the only way the inliner knows how - by handing the body
  /// the address of the argument's own cell.
  ///
  /// <para>
  /// Two conditions, and the second is the one that was missing. The cell has to be a NEAR lvalue,
  /// because the parameter slot the body reads through is one word. And it has to already hold the
  /// PARAMETER's type: a real call compares the two and, when they differ, evaluates the argument,
  /// coerces it and copies it into a hidden temp of the parameter's width
  /// (<c>CodeGenerator.EmitArgumentPush</c>). The inliner had no such arm, so <c>f#(i%)</c> pointed a
  /// DOUBLE parameter at a two-byte INTEGER cell and the body read six bytes of whatever followed it.
  /// </para>
  /// </summary>
  public static bool InlinableByRefArgument(Expression arg, VariableSymbol parameter, SemanticModel model,
      Func<Expression, bool> isNearLValue)
    => isNearLValue(arg) && Equals(model.TypeOf(arg), parameter.Type);

  private static IReadOnlyList<Expression> ArgsOf(object node) => node switch {
    CallStmt c => c.Arguments,
    CallOrIndexExpr c => c.Arguments,
    _ => [],                        // a bare NameExpr is a parameterless call
  };
}
