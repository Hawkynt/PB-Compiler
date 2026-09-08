namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// A loop-carried integer reduction that may be split into worker-private partial results once a
/// hosted back end has proved the surrounding loop parallelizable.
/// </summary>
/// <param name="Accumulator">The loop-header phi carrying the running result.</param>
/// <param name="Update">The associative operation feeding the accumulator's latch edge.</param>
/// <param name="InitialValue">The value entering the accumulator from the loop preheader.</param>
/// <param name="Input">The per-iteration operand combined with the private accumulator.</param>
/// <param name="Identity">The operator identity used to seed each worker-private accumulator.</param>
/// <param name="Header">The counted-loop header.</param>
/// <param name="Preheader">The block entering the loop from outside.</param>
/// <param name="Latch">The unique block carrying the back edge.</param>
/// <param name="Exit">The loop exit block.</param>
/// <param name="Trips">The exact iteration count proved by <see cref="CountedLoop"/>.</param>
public sealed record IrParallelReduction(
  IrPhi Accumulator,
  IrBinary Update,
  IrValue InitialValue,
  IrValue Input,
  IrConstantInt Identity,
  IrBasicBlock Header,
  IrBasicBlock Preheader,
  IrBasicBlock Latch,
  IrBasicBlock Exit,
  long Trips);

/// <summary>
/// O0312 — recognizes reductions that a hosted parallel-loop lowering may safely privatize and
/// combine after the workers finish.
///
/// <para>
/// This pass is deliberately analysis-only. O0311 owns the proof that iterations of the surrounding
/// loop are independent and the hosted back end owns worker creation. Conflating those proofs here
/// would make a valid reduction look like permission to parallelize an otherwise dependent loop.
/// </para>
/// <para>
/// Integer <c>add</c>, <c>mul</c>, <c>and</c>, <c>or</c> and <c>xor</c> are exact associative
/// operations modulo their IR width, so their partial results may be regrouped. Floating-point
/// arithmetic is rejected: regrouping changes rounding unless a separate relaxed-FP contract permits
/// it, and O0312 does not silently invent that contract.
/// </para>
/// </summary>
public static class ParallelReduction {

  /// <summary>Finds every worker-privatizable reduction in <paramref name="fn"/>.</summary>
  public static IReadOnlyList<IrParallelReduction> Analyze(IrFunction fn) {
    ArgumentNullException.ThrowIfNull(fn);
    if (fn.HasErrorHandler || fn.HasInlineAsm)
      return [];

    var result = new List<IrParallelReduction>();
    foreach (var header in fn.Blocks)
      if (CountedLoop.Match(fn, header) is { } loop)
        foreach (var phi in header.Instructions.OfType<IrPhi>())
          if (TryMatch(loop, phi) is { } reduction)
            result.Add(reduction);
    return result;
  }

  private static IrParallelReduction? TryMatch(CountedLoop loop, IrPhi phi) {
    if (ReferenceEquals(phi, loop.Counter) || !phi.Type.IsInteger || phi.IncomingBlocks.Count != 2)
      return null;
    if (phi.IncomingFrom(loop.Preheader) is not { } initial)
      return null;
    if (phi.IncomingFrom(loop.Latch) is not IrBinary update || !IsAssociativeInteger(update.Op))
      return null;
    if (update.Parent is not { } updateBlock || !loop.Region.Contains(updateBlock))
      return null;

    var input = OtherOperand(update, phi);
    if (input is null || DependsOn(input, phi, loop.Region))
      return null;

    // A worker may only own a private accumulator when the running value is invisible inside the
    // iteration. Reads after the loop are fine: they consume the final combined result.
    if (phi.Users.Any(user => IsInside(user, loop.Region) && !ReferenceEquals(user, update)))
      return null;
    if (update.Users.Any(user => IsInside(user, loop.Region) && !ReferenceEquals(user, phi)))
      return null;

    return new(
      phi,
      update,
      initial,
      input,
      Identity(phi.Type, update.Op),
      loop.Header,
      loop.Preheader,
      loop.Latch,
      loop.Exit,
      loop.Trips);
  }

  private static IrValue? OtherOperand(IrBinary update, IrPhi accumulator) =>
    (ReferenceEquals(update.Lhs, accumulator), ReferenceEquals(update.Rhs, accumulator)) switch {
      (true, false) => update.Rhs,
      (false, true) => update.Lhs,
      _ => null,
    };

  private static bool IsAssociativeInteger(IrBinaryOp op) => op is
    IrBinaryOp.Add or IrBinaryOp.Mul or IrBinaryOp.And or IrBinaryOp.Or or IrBinaryOp.Xor;

  private static IrConstantInt Identity(IrType type, IrBinaryOp op) => new(type, op switch {
    IrBinaryOp.Mul => 1,
    IrBinaryOp.And => -1,
    _ => 0,
  });

  private static bool IsInside(IrInstruction instruction, HashSet<IrBasicBlock> region) =>
    instruction.Parent is { } block && region.Contains(block);

  /// <summary>
  /// A syntactically separate operand is not necessarily independent: <c>acc + f(acc)</c> still has
  /// a serial dependence. Follow only definitions inside the loop; an outside definition cannot
  /// depend on this loop's header phi in valid SSA.
  /// </summary>
  private static bool DependsOn(IrValue value, IrValue target, HashSet<IrBasicBlock> region) {
    var pending = new Stack<IrValue>([value]);
    var seen = new HashSet<IrValue>(ReferenceEqualityComparer.Instance);
    while (pending.TryPop(out var current)) {
      if (ReferenceEquals(current, target))
        return true;
      if (!seen.Add(current) || current is not IrInstruction { Parent: { } block } instruction || !region.Contains(block))
        continue;
      foreach (var operand in instruction.Operands)
        pending.Push(operand);
    }
    return false;
  }
}
