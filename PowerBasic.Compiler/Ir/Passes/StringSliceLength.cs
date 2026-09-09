namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0297: measures a read-only substring without materializing the substring.
///
/// <para>
/// <c>LEN(LEFT$(s$, n))</c>, <c>LEN(RIGHT$(s$, n))</c> and <c>LEN(MID$(s$, i, n))</c>
/// are only asking for the bounds of a slice. Lowering normally allocates that slice and then hands
/// the temporary straight to <c>rt_str_len</c>, which frees it again on DOS. This pass instead calls
/// <c>rt_str_len</c> on the original owned handle at the exact point where the substring producer
/// would have consumed it, then derives the slice length with ordinary SSA arithmetic.
/// </para>
///
/// <para>
/// Keeping the consuming length call at the producer site is important. Moving it to the later LEN
/// use would extend the source handle's lifetime across arbitrary intervening string allocations and
/// would change when the DOS string heap releases it. The replacement therefore removes only the
/// allocation/copy; ownership and evaluation order stay where lowering put them.
/// </para>
///
/// <para>
/// The formulas are exactly the hosted runtime's substring rules: LEFT$/RIGHT$ clamp a negative
/// count to zero and a count past the source length to the source length; MID$ clamps start below one
/// to one, yields zero past the end or for a non-positive count, and otherwise clips the requested
/// count to the remaining suffix. Two-argument MID$ is the same suffix length with no count cap.
/// </para>
/// </summary>
public static class StringSliceLength {

  private const string _LEN = "rt_str_len";
  private const string _LEFT = "rt_str_left";
  private const string _RIGHT = "rt_str_right";
  private const string _MID = "rt_str_mid";
  private const string _MID2 = "rt_str_mid2";

  /// <summary>Rewrites immediately measured slices across the module; returns the number rewritten.</summary>
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    var rewritten = 0;
    foreach (var function in module.Functions.ToList()) {
      if (function.IsDeclaration || function.HasErrorHandler || function.HasInlineAsm)
        continue;

      foreach (var lengthCall in function.AllInstructions.OfType<IrCall>().ToList()) {
        if (lengthCall.Parent is null || lengthCall.Callee is not IrFunction { Name: _LEN } || lengthCall.ArgCount != 1)
          continue;
        if (lengthCall.GetOperand(1) is not IrCall { Callee: IrFunction sliceFn } slice
            || slice.Users.Count != 1 || slice.Parent is null)
          continue;

        IrValue? replacement = sliceFn.Name switch {
          _LEFT or _RIGHT when slice.ArgCount == 2
            => RewriteEdgeSlice(lengthCall, slice),
          _MID when slice.ArgCount == 3
            => RewriteMidSlice(lengthCall, slice, hasExplicitCount: true),
          _MID2 when slice.ArgCount == 2
            => RewriteMidSlice(lengthCall, slice, hasExplicitCount: false),
          _ => null,
        };
        if (replacement is null)
          continue;

        lengthCall.ReplaceAllUsesWith(replacement);
        lengthCall.EraseFromParent();
        slice.EraseFromParent();
        ++rewritten;
      }
    }
    return rewritten;
  }

  /// <summary>LEN(LEFT$/RIGHT$): min(max(requested, 0), sourceLength).</summary>
  private static IrValue RewriteEdgeSlice(IrCall lengthCall, IrCall slice) {
    var sourceLength = ConsumeAndMeasureSource(lengthCall, slice);
    var requested = slice.GetOperand(2);
    var zero = new IrConstantInt(requested.Type, 0);
    var block = slice.Parent!;

    var isNegative = block.InsertBefore(new IrCmp(IrCmpPred.Slt, requested, zero), slice);
    var nonNegative = block.InsertBefore(new IrSelect(isNegative, zero, requested), slice);
    var exceedsSource = block.InsertBefore(new IrCmp(IrCmpPred.Sgt, nonNegative, sourceLength), slice);
    return block.InsertBefore(new IrSelect(exceedsSource, sourceLength, nonNegative), slice);
  }

  /// <summary>
  /// LEN(MID$): remaining = max(sourceLength - max(start, 1) + 1, 0), then optionally cap it by
  /// max(requestedCount, 0).
  /// </summary>
  private static IrValue RewriteMidSlice(IrCall lengthCall, IrCall slice, bool hasExplicitCount) {
    var sourceLength = ConsumeAndMeasureSource(lengthCall, slice);
    var start = slice.GetOperand(2);
    var zero = new IrConstantInt(start.Type, 0);
    var one = new IrConstantInt(start.Type, 1);
    var block = slice.Parent!;

    var startsBeforeOne = block.InsertBefore(new IrCmp(IrCmpPred.Slt, start, one), slice);
    var normalizedStart = block.InsertBefore(new IrSelect(startsBeforeOne, one, start), slice);
    var afterStart = block.InsertBefore(new IrBinary(IrBinaryOp.Sub, sourceLength, normalizedStart), slice);
    var rawRemaining = block.InsertBefore(new IrBinary(IrBinaryOp.Add, afterStart, one), slice);
    var noRemaining = block.InsertBefore(new IrCmp(IrCmpPred.Sle, rawRemaining, zero), slice);
    IrValue remaining = block.InsertBefore(new IrSelect(noRemaining, zero, rawRemaining), slice);

    if (!hasExplicitCount)
      return remaining;

    var requested = slice.GetOperand(3);
    var requestedZero = new IrConstantInt(requested.Type, 0);
    var nonPositive = block.InsertBefore(new IrCmp(IrCmpPred.Sle, requested, requestedZero), slice);
    var positiveRequested = block.InsertBefore(new IrSelect(nonPositive, requestedZero, requested), slice);
    var exceedsRemaining = block.InsertBefore(new IrCmp(IrCmpPred.Sgt, positiveRequested, remaining), slice);
    remaining = block.InsertBefore(new IrSelect(exceedsRemaining, remaining, positiveRequested), slice);
    return remaining;
  }

  /// <summary>
  /// Replaces the substring producer's ownership consumption with rt_str_len at the same instruction
  /// position. The existing declaration is the LEN call's callee, so no new module-level ABI entry is
  /// invented.
  /// </summary>
  private static IrValue ConsumeAndMeasureSource(IrCall lengthCall, IrCall slice) {
    var source = slice.GetOperand(1);
    return slice.Parent!.InsertBefore(new IrCall(lengthCall.Type, lengthCall.Callee, [source]), slice);
  }
}
