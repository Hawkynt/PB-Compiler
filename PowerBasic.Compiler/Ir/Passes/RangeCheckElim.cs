using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Folds an integer comparison the range analysis decides, which is how a runtime trap that cannot
/// fire stops being emitted.
///
/// <para>
/// <c>$ERROR BOUNDS ON</c>, <c>$ERROR OVERFLOW ON</c> and the divide-by-zero guard all reach the IR
/// in one shape: <c>IrLowering.RaiseWhen</c> emits <c>condbr &lt;a compare&gt;, trap, ok</c>. So the
/// question "can this check be elided" is exactly the question "is this compare decided", and there
/// is no need for a pass that knows what a bounds check looks like - proving the compare and letting
/// <c>simplifycfg</c> and <c>dce</c> collect the unreachable trap is the whole transform.
/// </para>
///
/// <para>
/// <b>The overflow trap looked like it would need a special case and does not</b>, which is worth
/// recording because the special case was written first and thrown away. A bounds check and a zero
/// guard compare the value being checked, so an interval decides them outright; the signed
/// <c>+</c>/<c>-</c> trap instead asks the textbook SIGN rule - <c>(~(l^r) &amp; (sum^l)) &lt; 0</c> -
/// because a target-independent IR has no flags register. That is a fact about three CORRELATED
/// values, and interval arithmetic does lose the correlation. What rescues it is the ASYMMETRIC AND
/// rule in <see cref="ValueRange.And"/>: with <c>l</c> and <c>r</c> known non-negative and small the
/// right-hand half is bounded and non-negative on its own, that survives the <c>AND</c> whatever the
/// left-hand half is, and the comparison against zero decides. The version that matched the sign rule
/// syntactically instead was correct and useless - <c>instcombine</c> had already folded the two
/// <c>XOR</c>s into one by the time it ran.
/// </para>
///
/// <para>
/// It is not limited to traps and deliberately so. The same fold removes a dead <c>IF</c> arm the
/// front end could not see was dead, which is the direct emitter's O16 behaviour arriving on the IR
/// path by the same route rather than by a second mechanism.
/// </para>
///
/// <para>
/// <b>The direction that matters.</b> A compare is replaced only when the analysis decides it for
/// EVERY value its operands can take. Anything else is left standing: eliding a check that could fire
/// is a silent miscompile, and there is no measurement that would notice - the program simply
/// computes with a subscript outside its array. The analysis over-approximates on purpose, so an
/// undecided answer is the common one and costs only the check that was going to be emitted anyway.
/// </para>
/// </summary>
public static class RangeCheckElim {

  public static int Run(IrFunction fn) {
    ArgumentNullException.ThrowIfNull(fn);
    if (IrRangeAnalysis.Build(fn) is not { } ranges)
      return 0;

    // Decided first, replaced afterwards. The analysis reads the operand graph, and rewiring one
    // compare while still questioning the next would be asking about a graph it was not built for.
    var decided = new List<(IrCmp Cmp, bool Outcome)>();
    foreach (var block in fn.Blocks)
      foreach (var cmp in block.Instructions.OfType<IrCmp>())
        if (!cmp.HasNoUsers && ranges.Decide(cmp, block) is { } outcome)
          decided.Add((cmp, outcome));

    foreach (var (cmp, outcome) in decided)
      cmp.ReplaceAllUsesWith(IrBuilder.ConstBool(outcome));
    return decided.Count;
  }

}
