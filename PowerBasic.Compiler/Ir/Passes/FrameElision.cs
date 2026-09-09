namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0070 — stack-frame elision eligibility.
///
/// The middle end can prove one half of frame elision without knowing anything about the target ABI:
/// after scalar replacement and mem2reg, a function with no surviving <see cref="IrAlloca"/> needs no
/// fixed local stack storage of its own. Calls do not invalidate that proof — they may move SP while
/// they execute, but they do not create persistent state in this function's frame.
///
/// <para>
/// This is deliberately only an eligibility analysis. Register allocation may still introduce spill
/// slots, and a target ABI may need a frame pointer to address incoming stack parameters. The machine
/// emitter must therefore re-check its final stack slots and parameter plan before omitting a frame.
/// Keeping the two proofs separate is what makes the analysis target-neutral instead of baking an
/// x86 addressing mode into SSA IR.
/// </para>
/// </summary>
public static class FrameElision {

  /// <summary>Whether the optimized IR has no fixed local stack state that inherently requires a frame.</summary>
  public static bool IsCandidate(IrFunction function) {
    ArgumentNullException.ThrowIfNull(function);
    if (function.IsDeclaration || function.HasErrorHandler || function.HasInlineAsm)
      return false;
    return !function.AllInstructions.OfType<IrAlloca>().Any();
  }
}
