namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0062 — replace a repeated affine expression of a counted-loop induction variable by its own
/// loop-carried recurrence.
///
/// <para>
/// For a counter <c>i[n+1] = i[n] + step</c>, every same-width integer expression
/// <c>f(i) = scale*i + offset</c> obeys
/// <c>f(i[n+1]) = f(i[n]) + scale*step</c> in the IR's modulo-2^width arithmetic. The pass therefore
/// computes the first value once, carries it through a header phi, and advances it with one add in the
/// latch. A body such as <c>j = 2*i + 3</c> loses the multiply and add on every iteration.
/// </para>
/// <para>
/// This is deliberately not a general Scalar-Evolution implementation. The accepted expression tree
/// contains only the loop counter, same-width integer constants, add/sub, multiplication where one
/// side is constant, and a constant left shift (the canonical form InstCombine and verified arithmetic
/// lowering use for power-of-two scaling). Values from other phis, casts, division/right shifts,
/// calls and memory are rejected. A candidate that directly feeds a phi is also rejected because phi
/// operands are evaluated on predecessor edges; rewriting such an edge needs a separate LCSSA/dominance
/// transform.
/// </para>
/// <para>
/// Profitability is equally conservative: a one-add offset such as <c>i + 3</c> is left alone because
/// turning one add into another add plus a carried phi buys nothing. Non-trivial constant scaling and
/// expression trees containing at least two arithmetic operations are reduced; a scale that collapses
/// to zero modulo the type width becomes a constant instead.
/// </para>
/// </summary>
public static class InductionVariableSimplification {

  private const int _MAX_AFFINE_DEPTH = 16;

  private readonly record struct Affine(
    long Scale,
    long Offset,
    int Cost,
    bool DependsOnCounter,
    bool HasExpensiveScale);

  /// <summary>Simplifies profitable affine derived induction values in <paramref name="fn"/>.</summary>
  public static int Run(IrFunction fn) {
    ArgumentNullException.ThrowIfNull(fn);
    if (fn.Entry is null || fn.HasErrorHandler || fn.HasInlineAsm)
      return 0;

    var changed = 0;
    foreach (var header in fn.Blocks.ToList()) {
      if (header.Parent is null
          || CountedLoop.Match(fn, header) is not { } loop
          || !TryCounter(loop, out var start, out var step))
        continue;

      var candidates = loop.Region
        .SelectMany(block => block.Instructions)
        .OfType<IrBinary>()
        .Where(root => root.Parent is not null && !root.HasNoUsers && root.Users.All(user => user is not IrPhi))
        .Select(root => (Root: root, Form: TryAffine(root, loop.Counter, root.Type, _MAX_AFFINE_DEPTH, out var form) ? form : (Affine?)null))
        .Where(candidate => candidate.Form is { } form && IsProfitable(form))
        .Select(candidate => (candidate.Root, Form: candidate.Form!.Value))
        .OrderByDescending(candidate => candidate.Form.Cost)
        .ToList();

      // One recurrence per (scale, width), not one per root. Two affine values with the same scale
      // differ by a constant at every iteration, so the second is the first plus that constant - and
      // an add where the value is used costs less than a second loop-carried register. Minting a phi
      // each time and leaving O0111 to merge them also relocates the compensating add into the loop
      // HEADER, which is where LoopUnroll requires nothing but phis, the compare and the branch.
      var carried = new Dictionary<(long Scale, string Type), (IrValue Value, long First)>();
      foreach (var (root, form) in candidates) {
        // A larger affine root may have been rewritten first and erased, dropping the only use of one
        // of its children. Do not mint a recurrence for work that is already dead.
        if (root.Parent is null || root.HasNoUsers || root.Users.Any(user => user is IrPhi))
          continue;

        Rewrite(loop, root, form, start, step, carried);
        ++changed;
      }
    }

    return changed;
  }

  private static bool TryCounter(CountedLoop loop, out IrConstantInt start, out IrConstantInt step) {
    start = null!;
    step = null!;
    if (!loop.Counter.Type.IsInteger
        || loop.Counter.IncomingFrom(loop.Preheader) is not IrConstantInt initial
        || loop.Counter.IncomingFrom(loop.Latch) is not IrBinary { Op: IrBinaryOp.Add } next
        || !ReferenceEquals(next.Lhs, loop.Counter)
        || next.Rhs is not IrConstantInt increment
        || !Equals(initial.Type, loop.Counter.Type)
        || !Equals(increment.Type, loop.Counter.Type)
        || increment.IsZero)
      return false;

    start = initial;
    step = increment;
    return true;
  }

  private static bool TryAffine(IrValue value, IrPhi counter, IrType type, int depth, out Affine affine) {
    affine = default;
    if (depth <= 0 || !type.IsInteger || !Equals(value.Type, type))
      return false;

    if (ReferenceEquals(value, counter)) {
      affine = new(Scale: 1, Offset: 0, Cost: 0, DependsOnCounter: true, HasExpensiveScale: false);
      return true;
    }

    if (value is IrConstantInt constant) {
      affine = new(Scale: 0, Offset: IrConstFold.Wrap(constant.Value, type), Cost: 0,
        DependsOnCounter: false, HasExpensiveScale: false);
      return true;
    }

    if (value is not IrBinary binary
        || !TryAffine(binary.Lhs, counter, type, depth - 1, out var left)
        || !TryAffine(binary.Rhs, counter, type, depth - 1, out var right))
      return false;

    switch (binary.Op) {
      case IrBinaryOp.Add:
        affine = Add(left, right, type, subtractRight: false);
        return true;

      case IrBinaryOp.Sub:
        affine = Add(left, right, type, subtractRight: true);
        return true;

      case IrBinaryOp.Mul when !left.DependsOnCounter:
        affine = Scale(right, left.Offset, type, left.Cost + right.Cost + 1);
        return true;

      case IrBinaryOp.Mul when !right.DependsOnCounter:
        affine = Scale(left, right.Offset, type, left.Cost + right.Cost + 1);
        return true;

      case IrBinaryOp.Shl when !right.DependsOnCounter && right.Offset >= 0 && right.Offset < type.Bits:
        affine = Scale(left, unchecked(1L << (int)right.Offset), type, left.Cost + right.Cost + 1);
        return true;

      default:
        return false;
    }
  }

  private static Affine Add(Affine left, Affine right, IrType type, bool subtractRight) {
    var sign = subtractRight ? -1L : 1L;
    return new(
      Scale: WrapAdd(left.Scale, unchecked(sign * right.Scale), type),
      Offset: WrapAdd(left.Offset, unchecked(sign * right.Offset), type),
      Cost: left.Cost + right.Cost + 1,
      DependsOnCounter: left.DependsOnCounter || right.DependsOnCounter,
      HasExpensiveScale: left.HasExpensiveScale || right.HasExpensiveScale);
  }

  private static Affine Scale(Affine value, long factor, IrType type, int cost) => new(
    Scale: IrConstFold.Wrap(unchecked(value.Scale * factor), type),
    Offset: IrConstFold.Wrap(unchecked(value.Offset * factor), type),
    Cost: cost,
    DependsOnCounter: value.DependsOnCounter,
    HasExpensiveScale: value.HasExpensiveScale || (value.DependsOnCounter && factor is not (0 or 1)));

  private static bool IsProfitable(Affine form)
    => form.DependsOnCounter && (form.Scale == 0 || form.HasExpensiveScale || form.Cost >= 2);

  private static void Rewrite(
      CountedLoop loop,
      IrBinary root,
      Affine form,
      IrConstantInt start,
      IrConstantInt step,
      Dictionary<(long Scale, string Type), (IrValue Value, long First)> carried) {
    var type = root.Type;
    var first = IrConstFold.Wrap(unchecked(form.Scale * start.Value + form.Offset), type);
    var delta = IrConstFold.Wrap(unchecked(form.Scale * step.Value), type);
    var key = (form.Scale, type.ToString());

    IrValue replacement;
    if (delta == 0) {
      replacement = new IrConstantInt(type, first);
    } else if (carried.TryGetValue(key, out var existing)) {
      // Same scale, same step, so the two run in lockstep and their difference is the difference of
      // their first values. The add goes where the root was, not into the header: it belongs to the
      // iteration that reads it.
      var offset = IrConstFold.Wrap(unchecked(first - existing.First), type);
      if (offset == 0) {
        replacement = existing.Value;
      } else {
        var shifted = new IrBinary(IrBinaryOp.Add, existing.Value, new IrConstantInt(type, offset)) {
          Name = root.Name,
        };
        root.Parent!.InsertBefore(shifted, root);
        replacement = shifted;
      }
    } else {
      var derived = loop.Header.AppendPhi(new IrPhi(type) {
        Name = root.Name is null ? null : $"{root.Name}.iv",
      });
      var next = new IrBinary(IrBinaryOp.Add, derived, new IrConstantInt(type, delta));
      loop.Latch.InsertBefore(next, loop.Latch.Terminator!);
      derived.AddIncoming(new IrConstantInt(type, first), loop.Preheader);
      derived.AddIncoming(next, loop.Latch);
      carried[key] = (derived, first);
      replacement = derived;
    }

    var left = root.Lhs;
    var right = root.Rhs;
    root.ReplaceAllUsesWith(replacement);
    if (!root.HasNoUsers)
      return;

    root.EraseFromParent();
    EraseDeadArithmetic(left);
    EraseDeadArithmetic(right);
  }

  /// <summary>
  /// Removes only pure arithmetic that became unused because an affine root disappeared. This keeps
  /// nested canonical forms such as <c>(i &lt;&lt; 1) + i + 3</c> from being reconsidered as separate
  /// derived IVs later in the same candidate snapshot; ordinary DCE remains responsible for everything
  /// outside this expression tree.
  /// </summary>
  private static void EraseDeadArithmetic(IrValue value) {
    if (value is not IrBinary { Parent: not null, HasNoUsers: true } binary)
      return;
    var left = binary.Lhs;
    var right = binary.Rhs;
    binary.EraseFromParent();
    EraseDeadArithmetic(left);
    EraseDeadArithmetic(right);
  }

  private static long WrapAdd(long left, long right, IrType type)
    => IrConstFold.Wrap(unchecked(left + right), type);
}
