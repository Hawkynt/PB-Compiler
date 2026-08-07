namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0012 — float demotion. PowerBASIC types a bare variable name SINGLE, so most DOS-era loop
/// counters are floating point by accident rather than by intent. When one provably only ever holds
/// integral values in a bounded range, the whole x87 round trip can be replaced by integer arithmetic.
///
/// <para>
/// The shape accepted is the counter: a float phi whose value on entry is an integral constant, whose
/// value round the latch is itself plus an integral constant, and which is compared against an
/// integral constant. That triple is what makes the demotion SOUND rather than merely plausible - it
/// bounds the counter. Integer arithmetic wraps where float arithmetic saturates, so demoting a value
/// whose range is unknown trades one wrong answer for another; with an init, a step and a limit all
/// inside 16 bits, the counter cannot leave i32's range whatever the trip count, and the step's sign
/// must move toward the limit or the loop does not terminate in either form.
/// </para>
/// <para>
/// Every use has to survive the change too. Arithmetic and comparisons within the cluster are
/// rewritten; a conversion back to an integer becomes the identity, which is the whole saving; and
/// anything else at all - a call, a store, a return - declines the cluster, because the value would
/// escape as an integer where something expects a float.
/// </para>
/// </summary>
public static class FloatDemotion {

  /// <summary>The widest constant accepted, so the demoted counter cannot leave i32's range.</summary>
  private const long _LIMIT = short.MaxValue;

  /// <summary>Demotes what it can in <paramref name="fn"/>; returns how many counters moved.</summary>
  public static int Run(IrFunction fn) {
    if (fn.HasErrorHandler || fn.HasInlineAsm)
      return 0;

    var demoted = 0;
    foreach (var block in fn.Blocks.ToList())
      foreach (var phi in block.Instructions.OfType<IrPhi>().ToList())
        if (phi.Parent is not null && phi.Type.IsIeeeFloat && Demote(phi))
          ++demoted;
    return demoted;
  }

  /// <summary>
  /// The integer this float value certainly is, or null.
  ///
  /// Both spellings count. The lowering writes a FOR bound as <c>sitofp i16 10 to f32</c> rather than
  /// as a float literal - the source said 10, and widening an integer constant is how it gets there -
  /// so a version that only accepted <see cref="IrConstantFloat"/> would decline every counter it
  /// exists for. It did, until the IR was printed and looked at.
  /// </summary>
  private static long? Integral(IrValue value) {
    if (value is IrConstantFloat c)
      return c.Value == System.Math.Floor(c.Value) && c.Value is >= -_LIMIT and <= _LIMIT ? (long)c.Value : null;
    if (value is IrCast { Op: IrCastOp.SIToFP or IrCastOp.UIToFP } widened
        && widened.Value is IrConstantInt i && i.Value is >= -_LIMIT and <= _LIMIT)
      return i.Value;
    return null;
  }

  private static bool Demote(IrPhi phi) {
    if (phi.IncomingBlocks.Count != 2)
      return false;

    // one edge brings a constant in, the other brings the phi plus a constant step
    IrValue? entry = null;
    IrBinary? step = null;
    foreach (var predecessor in phi.IncomingBlocks) {
      var incoming = phi.IncomingFrom(predecessor);
      if (incoming is IrBinary { Op: IrBinaryOp.FAdd or IrBinaryOp.FSub } candidate
          && ReferenceEquals(candidate.Lhs, phi) && Integral(candidate.Rhs) is not null)
        step = candidate;
      else
        entry = incoming;
    }
    if (step is null || entry is null || Integral(entry) is not { } start)
      return false;
    if (step.Users.Count != 1)
      return false;                              // the increment feeds the phi and nothing else

    var stride = Integral(step.Rhs)!.Value * (step.Op == IrBinaryOp.FSub ? -1 : 1);
    if (stride == 0)
      return false;

    // the guard: a comparison against an integral constant, whose sense the step moves toward
    var guard = phi.Users.OfType<IrCmp>().FirstOrDefault(c => ReferenceEquals(c.Lhs, phi) && Integral(c.Rhs) is not null);
    if (guard is null || Integral(guard.Rhs) is not { } limit || !Bounded(start, stride, limit, guard.Pred))
      return false;

    // every other user must be one this can rewrite
    foreach (var user in phi.Users)
      if (!ReferenceEquals(user, step) && !ReferenceEquals(user, guard) && !IsRewritableUse(user, phi))
        return false;

    Rewrite(phi, step, guard, start, stride);
    return true;
  }

  /// <summary>
  /// Whether the step moves the counter toward the limit, so the loop terminates and the counter stays
  /// within one step of it. A step going the wrong way runs forever in both the float and the integer
  /// form, but only the integer one wraps - so it is refused rather than reasoned about.
  /// </summary>
  private static bool Bounded(long start, long stride, long limit, IrCmpPred pred) => pred switch {
    IrCmpPred.Folt or IrCmpPred.Fole => stride > 0 && start <= limit,
    IrCmpPred.Fogt or IrCmpPred.Foge => stride < 0 && start >= limit,
    _ => false,
  };

  /// <summary>
  /// Whether a use of the counter survives demotion. A conversion back to an integer becomes the
  /// identity, which is the saving; arithmetic against an integral constant is rewritten; anything
  /// else would let the value escape as an integer where a float is expected.
  /// </summary>
  private static bool IsRewritableUse(IrInstruction user, IrPhi phi) => user switch {
    IrCast { Op: IrCastOp.FPToSIRound or IrCastOp.FPToSI } cast => !IsWide(cast.Type) || cast.Type.Bits <= 32,
    IrBinary { Op: IrBinaryOp.FAdd or IrBinaryOp.FSub or IrBinaryOp.FMul } b
      => Integral(ReferenceEquals(b.Lhs, phi) ? b.Rhs : b.Lhs) is not null,
    IrCmp c => Integral(ReferenceEquals(c.Lhs, phi) ? c.Rhs : c.Lhs) is not null,
    _ => false,
  };

  private static bool IsWide(IrType type) => type.Bits > 16;

  private static void Rewrite(IrPhi phi, IrBinary step, IrCmp guard, long start, long stride) {
    var block = phi.Parent!;
    var integer = IrType.I32;

    var counter = block.AppendPhi(new IrPhi(integer) { Name = phi.Name });
    var increment = step.Parent!.InsertBefore(
      new IrBinary(IrBinaryOp.Add, counter, new IrConstantInt(integer, stride)), step);

    foreach (var predecessor in phi.IncomingBlocks)
      counter.AddIncoming(
        ReferenceEquals(phi.IncomingFrom(predecessor), step) ? increment : new IrConstantInt(integer, start),
        predecessor);

    foreach (var user in phi.Users.ToList()) {
      if (ReferenceEquals(user, step))
        continue;
      switch (user) {
        case IrCast { Op: IrCastOp.FPToSIRound or IrCastOp.FPToSI } cast:
          // the round trip disappears: the counter already IS the integer this was producing
          cast.ReplaceAllUsesWith(Narrowed(cast, counter));
          cast.EraseFromParent();
          break;
        case IrCmp cmp: {
          var against = new IrConstantInt(integer, Integral(Other(cmp, phi))!.Value);
          var replacement = cmp.Parent!.InsertBefore(new IrCmp(IntegerPredicate(cmp.Pred), counter, against), cmp);
          cmp.ReplaceAllUsesWith(replacement);
          cmp.EraseFromParent();
          break;
        }
        case IrBinary arithmetic: {
          var constant = new IrConstantInt(integer, Integral(Other(arithmetic, phi))!.Value);
          var rewritten = arithmetic.Parent!.InsertBefore(
            new IrBinary(IntegerOp(arithmetic.Op), counter, constant), arithmetic);
          // the result was a float and its users still expect one
          var widened = arithmetic.Parent!.InsertBefore(new IrCast(IrCastOp.SIToFP, rewritten, arithmetic.Type), arithmetic);
          arithmetic.ReplaceAllUsesWith(widened);
          arithmetic.EraseFromParent();
          break;
        }
      }
    }

    step.EraseFromParent();
    phi.EraseFromParent();
  }

  private static IrValue Other(IrInstruction instruction, IrPhi phi) => instruction switch {
    IrBinary b => ReferenceEquals(b.Lhs, phi) ? b.Rhs : b.Lhs,
    IrCmp c => ReferenceEquals(c.Lhs, phi) ? c.Rhs : c.Lhs,
    _ => throw new System.InvalidOperationException(),
  };

  /// <summary>The counter narrowed to the width the conversion it replaces was producing.</summary>
  private static IrValue Narrowed(IrCast cast, IrPhi counter)
    => cast.Type.Bits >= 32 ? counter : cast.Parent!.InsertBefore(new IrCast(IrCastOp.Trunc, counter, cast.Type), cast);

  private static IrBinaryOp IntegerOp(IrBinaryOp op) => op switch {
    IrBinaryOp.FAdd => IrBinaryOp.Add,
    IrBinaryOp.FSub => IrBinaryOp.Sub,
    _ => IrBinaryOp.Mul,
  };

  private static IrCmpPred IntegerPredicate(IrCmpPred pred) => pred switch {
    IrCmpPred.Folt => IrCmpPred.Slt,
    IrCmpPred.Fole => IrCmpPred.Sle,
    IrCmpPred.Fogt => IrCmpPred.Sgt,
    IrCmpPred.Foge => IrCmpPred.Sge,
    IrCmpPred.Foeq => IrCmpPred.Eq,
    _ => IrCmpPred.Ne,
  };
}
