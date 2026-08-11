namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0134 — closed forms for loop-carried recurrences. An accumulator whose only work is adding a
/// constant each time round is <c>start + step * trips</c>, and the loop does not have to run to find
/// that out.
///
/// <para>
/// This is not unrolling with extra steps. <see cref="LoopUnroll"/> replaces a loop with copies of its
/// body and is capped at a handful of iterations, because the copies are the cost; a closed form
/// replaces the loop with ONE multiply and does not care whether the trip count is four or forty
/// thousand. The two therefore cover different loops, and this one runs after unrolling has declined.
/// </para>
/// <para>
/// It is restricted to INTEGER accumulators, and that restriction is the whole soundness argument.
/// Two's-complement addition is associative across wrapping, so accumulating <c>n</c> times and
/// multiplying by <c>n</c> reach the same value even when the intermediate steps overflow. Floating
/// point is not: each addition rounds, and a sum of forty roundings is not one multiplication. A float
/// accumulator is left alone rather than made faster and wrong.
/// </para>
/// <para>
/// The accumulator must also be UNREAD inside the loop apart from its own increment. If the body looks
/// at the running total - prints it, branches on it - then the intermediate values are observable and
/// only the final one is being replaced here.
/// </para>
/// </summary>
public static class RecurrenceClosedForm {

  /// <summary>Rewrites what it can in <paramref name="fn"/>; returns how many recurrences closed.</summary>
  public static int Run(IrFunction fn) {
    if (fn.HasErrorHandler || fn.HasInlineAsm)
      return 0;                                  // control can arrive where the CFG does not say

    var closed = 0;
    foreach (var header in fn.Blocks.ToList())
      if (header.Parent is not null)
        closed += CloseIn(fn, header);
    return closed;
  }

  private static int CloseIn(IrFunction fn, IrBasicBlock header) {
    if (CountedLoop.Match(fn, header) is not { } loop)
      return 0;
    var (_, preheader, latch, exit, region, _, _, trips) = loop;

    var closed = 0;
    foreach (var phi in header.Instructions.OfType<IrPhi>().ToList()) {
      if (phi.Type.Kind != IrTypeKind.Int)
        continue;                                // a float accumulator rounds every step; see the note above
      if (phi.IncomingFrom(preheader) is not { } start)
        continue;
      if (phi.IncomingFrom(latch) is not IrBinary { Op: IrBinaryOp.Add } increment)
        continue;
      if (!ReferenceEquals(increment.Lhs, phi) || increment.Rhs is not IrConstantInt step)
        continue;

      // the accumulator's only use inside the loop is its own increment - otherwise the running
      // total is observable and the final value is not the only thing being replaced
      if (phi.Users.Any(u => u.Parent is { } where && region.Contains(where) && !ReferenceEquals(u, increment)))
        continue;
      // and the increment feeds nothing but the phi
      if (increment.Users.Count != 1)
        continue;

      var total = unchecked(step.Value * trips);
      var finalValue = start is IrConstantInt from
        ? (IrValue)new IrConstantInt(phi.Type, CountedLoop.Truncate(phi.Type, unchecked(from.Value + total)))
        : exit.InsertAt(0, new IrBinary(IrBinaryOp.Add, start, new IrConstantInt(phi.Type, CountedLoop.Truncate(phi.Type, total))));

      foreach (var user in phi.Users.ToList())
        if (user.Parent is { } where && !region.Contains(where) && !ReferenceEquals(user, finalValue))
          user.ReplaceOperand(phi, finalValue);
      ++closed;
    }
    return closed;
  }
}
