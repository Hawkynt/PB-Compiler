using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0122 — interchange a rectangular two-level counted-loop nest when doing so makes the innermost
/// memory walk cheaper and the current IR proves reordering safe.
///
/// <para>
/// This first slice intentionally recognizes a small canonical shape: both loops are
/// <see cref="CountedLoop"/>s with constant start/limit/step, the inner loop is the only body of the
/// outer loop, and the innermost body is one basic block. The CFG topology itself is already the
/// topology an interchanged nest needs, so the transform keeps every block and swaps only the loop
/// control SSA: the old inner counter becomes the outer counter and vice versa. Body instructions stay
/// in place and their counter operands are rewired.
/// </para>
/// <para>
/// Memory legality is deliberately stronger than necessary. Read/read pairs do not constrain order;
/// distinct objects are dismissed by <see cref="IrAliasAnalysis"/>. If a write may alias a different
/// access site, this version declines. A write's own affine two-dimensional address must also be
/// injective over the rectangular iteration domain, with adjacent starts separated by at least its
/// access width. That proves distinct iterations cannot overlap without needing to guess a missing
/// dependence direction. O0172 can later replace this conservative boundary with full nested
/// direction vectors.
/// </para>
/// <para>
/// Profitability is target-independent and layout-independent: sum the absolute byte displacement of
/// every memory access when the current inner counter advances and compare it with the displacement
/// the current outer counter would have after interchange. The pass fires only when the latter is
/// strictly smaller. It therefore follows the address arithmetic actually present in IR rather than
/// baking a row/column-major policy into the middle end.
/// </para>
/// </summary>
public static class LoopInterchange {

  private const int _MAX_AFFINE_DEPTH = 20;

  private sealed record Nest(
    CountedLoop Outer,
    CountedLoop Inner,
    IrCondBr OuterBranch,
    IrCondBr InnerBranch,
    IrBinary OuterNext,
    IrBinary InnerNext,
    CounterInfo OuterCounter,
    CounterInfo InnerCounter,
    IReadOnlyList<IrPhi> InnerExitCarriers,
    IReadOnlyList<MemoryAccess> Accesses);

  private readonly record struct CounterInfo(
    IrConstantInt Start,
    IrConstantInt StepConstant,
    long Step,
    long Minimum,
    long Maximum,
    IrConstantInt Final);

  private readonly record struct Affine2(long Outer, long Inner, long Constant);

  private sealed record MemoryAccess(
    IrInstruction Instruction,
    IrValue Pointer,
    IrType AccessType,
    bool Writes,
    Address2 Address);

  private readonly record struct Address2(
    IrValue Root,
    long OuterStride,
    long InnerStride,
    int Bytes);

  /// <summary>Interchanges all profitable canonical nests in <paramref name="fn"/>; returns the number changed.</summary>
  public static int Run(IrFunction fn) {
    ArgumentNullException.ThrowIfNull(fn);
    if (fn.HasErrorHandler || fn.HasInlineAsm)
      return 0;

    var changed = 0;
    foreach (var header in fn.Blocks.ToList())
      if (header.Parent is not null && TryMatch(fn, header) is { } nest && IsLegal(nest) && IsProfitable(nest)) {
        Apply(nest);
        ++changed;
      }
    return changed;
  }

  private static Nest? TryMatch(IrFunction fn, IrBasicBlock outerHeader) {
    if (CountedLoop.Match(fn, outerHeader) is not { } outer
        || outer.Header.Terminator is not IrCondBr outerBranch
        || outerBranch.IfTrue.Instructions is not { Count: 1 } innerPreheaderInstructions
        || innerPreheaderInstructions[0] is not IrBr innerEntry
        || CountedLoop.Match(fn, innerEntry.Target) is not { } inner
        || !ReferenceEquals(inner.Preheader, outerBranch.IfTrue)
        || inner.Header.Terminator is not IrCondBr innerBranch
        || !TryPerfectBody(outer, inner, innerBranch, out var bodyBlock, out var bodySharesLatch))
      return null;

    if (!TryCounter(outer, out var outerCounter, out var outerNext)
        || !TryCounter(inner, out var innerCounter, out var innerNext))
      return null;

    if (!HeaderIsControlOnly(outer)
        || !HeaderIsControlOnly(inner)
        || !LatchIsControlOnly(outer.Latch, outerNext)
        || !ReferenceEquals(innerNext.Parent, inner.Latch)
        || inner.Latch.Terminator is not IrBr innerBack
        || !ReferenceEquals(innerBack.Target, inner.Header)
        || (!bodySharesLatch && !LatchIsControlOnly(inner.Latch, innerNext)))
      return null;

    var carriers = outer.Header.Phis
      .Where(phi => !ReferenceEquals(phi, outer.Counter))
      .ToList();
    if (carriers.Any(phi => !IsInnerExitCarrier(phi, outer, inner)))
      return null;

    var body = bodyBlock.Instructions
      .Where(instruction => !instruction.IsTerminator && !ReferenceEquals(instruction, innerNext))
      .ToList();
    if (body.Any(instruction => !IsReorderableBodyInstruction(instruction)))
      return null;

    // A body value escaping the nest can denote a different LAST iteration after interchange. A
    // proper LCSSA/value-permutation layer can preserve it later; this first slice simply declines.
    if (body.Where(instruction => !instruction.Type.IsVoid)
        .Any(instruction => instruction.Users.Any(user => user.Parent is not { } parent || !outer.Region.Contains(parent))))
      return null;

    if (!CollectMemoryAccesses(body, outer, inner, outerCounter, innerCounter, out var accesses))
      return null;

    return new(outer, inner, outerBranch, innerBranch, outerNext, innerNext,
      outerCounter, innerCounter, carriers, accesses);
  }

  /// <summary>
  /// Matches either the compact hand-built test form where the inner body is also its latch, or the
  /// shape <see cref="IrLowering"/> actually emits: preheader -> header -> body -> increment/latch,
  /// with the inner exit forwarding to the outer increment block. Keeping both forms makes the
  /// legality tests small without accidentally testing a CFG the front end never produces.
  /// </summary>
  private static bool TryPerfectBody(CountedLoop outer, CountedLoop inner, IrCondBr innerBranch,
      out IrBasicBlock body, out bool bodySharesLatch) {
    body = innerBranch.IfTrue;
    bodySharesLatch = ReferenceEquals(body, inner.Latch);

    if (ReferenceEquals(inner.Exit, outer.Latch)) {
      return bodySharesLatch
        && inner.Region.Count == 2
        && inner.Region.SetEquals([inner.Header, inner.Latch])
        && outer.Region.Count == 5
        && outer.Region.SetEquals([outer.Header, inner.Preheader, inner.Header, inner.Latch, outer.Latch]);
    }

    if (bodySharesLatch
        || inner.Region.Count != 3
        || !inner.Region.SetEquals([inner.Header, body, inner.Latch])
        || body.Terminator is not IrBr bodyToLatch
        || !ReferenceEquals(bodyToLatch.Target, inner.Latch)
        || inner.Exit.Instructions.Count != 1
        || inner.Exit.Instructions[0] is not IrBr exitToOuterLatch
        || !ReferenceEquals(exitToOuterLatch.Target, outer.Latch)
        || outer.Region.Count != 7
        || !outer.Region.SetEquals([
          outer.Header, inner.Preheader, inner.Header, body, inner.Latch, inner.Exit, outer.Latch,
        ]))
      return false;

    return true;
  }

  private static bool HeaderIsControlOnly(CountedLoop loop) {
    var ordinary = loop.Header.Instructions
      .Where(instruction => instruction is not IrPhi && !instruction.IsTerminator)
      .ToList();
    return ordinary.Count == 1 && ReferenceEquals(ordinary[0], loop.Test);
  }

  private static bool LatchIsControlOnly(IrBasicBlock latch, IrBinary next) {
    var ordinary = latch.Instructions.Where(instruction => !instruction.IsTerminator).ToList();
    return ordinary.Count == 1 && ReferenceEquals(ordinary[0], next)
      && latch.Terminator is IrBr;
  }

  private static bool IsInnerExitCarrier(IrPhi phi, CountedLoop outer, CountedLoop inner) {
    if (!Equals(phi.Type, inner.Counter.Type)
        || phi.IncomingBlocks.Count != 2
        || phi.IncomingFrom(outer.Latch) is not { } fromLatch
        || !ReferenceEquals(fromLatch, inner.Counter))
      return false;

    // The carrier exists only to make the inner FOR counter available after the OUTER loop. If it
    // participates in the loop body, replacing it by the final constant would change an iteration.
    return phi.Users.All(user => ReferenceEquals(user, phi)
      || user.Parent is not { } parent
      || !outer.Region.Contains(parent));
  }

  private static bool TryCounter(CountedLoop loop, out CounterInfo counter, out IrBinary next) {
    counter = default;
    next = null!;
    if (!loop.Counter.Type.IsInteger || !loop.Counter.Type.Signed || loop.Counter.Type.Bits >= 64
        || loop.Counter.IncomingFrom(loop.Preheader) is not IrConstantInt start
        || loop.Counter.IncomingFrom(loop.Latch) is not IrBinary { Op: IrBinaryOp.Add } recurrence
        || !ReferenceEquals(recurrence.Lhs, loop.Counter)
        || recurrence.Rhs is not IrConstantInt stepConstant
        || !Equals(stepConstant.Type, loop.Counter.Type))
      return false;

    var first = Signed(start);
    var step = Signed(stepConstant);
    if (step == 0)
      return false;

    long final;
    try {
      final = checked(first + checked(step * loop.Trips));
    } catch (OverflowException) {
      return false;
    }

    // A constant-step recurrence is monotone in mathematical integers. If both endpoints fit the
    // signed machine type, every intermediate value fits too; no million-iteration re-simulation is
    // needed just to establish the same fact CountedLoop already proved about the trip count.
    var allowed = ValueRange.OfType(loop.Counter.Type);
    if (!allowed.Contains(first) || !allowed.Contains(final))
      return false;

    counter = new(start, stepConstant, step, Math.Min(first, final), Math.Max(first, final),
      new IrConstantInt(loop.Counter.Type, final));
    next = recurrence;
    return true;
  }

  private static bool IsReorderableBodyInstruction(IrInstruction instruction) => instruction switch {
    IrBinary { Op: not (IrBinaryOp.SDiv or IrBinaryOp.UDiv or IrBinaryOp.SRem or IrBinaryOp.URem or IrBinaryOp.FDiv) } => true,
    IrCmp => true,
    IrCast { Op: IrCastOp.Trunc or IrCastOp.ZExt or IrCastOp.SExt or IrCastOp.FPTrunc or IrCastOp.FPExt or IrCastOp.BitCast } => true,
    IrGep => true,
    IrFarPtr => true,
    IrSelect => true,
    IrLoad => true,
    IrStore => true,
    _ => false,
  };

  private static bool CollectMemoryAccesses(
      IReadOnlyList<IrInstruction> body,
      CountedLoop outer,
      CountedLoop inner,
      CounterInfo outerCounter,
      CounterInfo innerCounter,
      out IReadOnlyList<MemoryAccess> accesses) {
    var result = new List<MemoryAccess>();
    foreach (var instruction in body) {
      IrValue pointer;
      IrType type;
      var writes = false;
      switch (instruction) {
        case IrLoad load:
          pointer = load.Pointer;
          type = load.Type;
          break;
        case IrStore store:
          pointer = store.Pointer;
          type = store.Value.Type;
          writes = true;
          break;
        default:
          continue;
      }

      if (!TryAddress(pointer, type, outer, inner, outerCounter, innerCounter, out var address)) {
        accesses = [];
        return false;
      }
      result.Add(new(instruction, pointer, type, writes, address));
    }
    accesses = result;
    return true;
  }

  private static bool TryAddress(
      IrValue pointer,
      IrType accessType,
      CountedLoop outer,
      CountedLoop inner,
      CounterInfo outerCounter,
      CounterInfo innerCounter,
      out Address2 address) {
    address = default;
    if (IrAliasAnalysis.StorageBytes(accessType) is not { } bytes || bytes <= 0)
      return false;

    var root = pointer;
    var total = new Affine2(0, 0, 0);
    while (root is IrGep gep) {
      if (!TryAffine(gep.ByteOffset, outer, inner, outerCounter, innerCounter, _MAX_AFFINE_DEPTH, out var displacement))
        return false;
      if (gep.ElementType is { } elementType) {
        if (IrAliasAnalysis.StorageBytes(elementType) is not { } elementBytes
            || !TryScale(displacement, elementBytes, out displacement))
          return false;
      }
      if (!TryAdd(total, displacement, out total))
        return false;
      root = gep.BasePtr;
    }

    if (root is IrInstruction definition && definition.Parent is { } parent && outer.Region.Contains(parent))
      return false;

    try {
      address = new(root,
        checked(total.Outer * outerCounter.Step),
        checked(total.Inner * innerCounter.Step),
        bytes);
      return true;
    } catch (OverflowException) {
      return false;
    }
  }

  private static bool TryAffine(
      IrValue value,
      CountedLoop outer,
      CountedLoop inner,
      CounterInfo outerCounter,
      CounterInfo innerCounter,
      int depth,
      out Affine2 affine) {
    affine = default;
    if (depth <= 0 || !value.Type.IsInteger || !value.Type.Signed)
      return false;

    if (ReferenceEquals(value, outer.Counter)) {
      affine = new(1, 0, 0);
      return Fits(affine, value.Type, outerCounter, innerCounter);
    }
    if (ReferenceEquals(value, inner.Counter)) {
      affine = new(0, 1, 0);
      return Fits(affine, value.Type, outerCounter, innerCounter);
    }
    if (value is IrConstantInt constant) {
      affine = new(0, 0, Signed(constant));
      return Fits(affine, value.Type, outerCounter, innerCounter);
    }

    if (value is IrCast cast) {
      if (!TryAffine(cast.Value, outer, inner, outerCounter, innerCounter, depth - 1, out var source))
        return false;
      if (cast.Op == IrCastOp.SExt && cast.Value.Type.Signed) {
        affine = source;
        return Fits(affine, cast.Type, outerCounter, innerCounter);
      }
      if (cast.Op == IrCastOp.Trunc && cast.Type.Signed && Fits(source, cast.Type, outerCounter, innerCounter)) {
        affine = source;
        return true;
      }
      return false;
    }

    if (value is not IrBinary binary
        || !TryAffine(binary.Lhs, outer, inner, outerCounter, innerCounter, depth - 1, out var left)
        || !TryAffine(binary.Rhs, outer, inner, outerCounter, innerCounter, depth - 1, out var right))
      return false;

    switch (binary.Op) {
      case IrBinaryOp.Add:
        if (!TryAdd(left, right, out affine)) return false;
        break;
      case IrBinaryOp.Sub:
        if (!TrySubtract(left, right, out affine)) return false;
        break;
      case IrBinaryOp.Mul when IsConstant(left):
        if (!TryScale(right, left.Constant, out affine)) return false;
        break;
      case IrBinaryOp.Mul when IsConstant(right):
        if (!TryScale(left, right.Constant, out affine)) return false;
        break;
      case IrBinaryOp.Shl when IsConstant(right) && right.Constant is >= 0 and <= 62:
        if (!TryScale(left, 1L << (int)right.Constant, out affine)) return false;
        break;
      default:
        return false;
    }

    return Fits(affine, binary.Type, outerCounter, innerCounter);
  }

  private static bool IsLegal(Nest nest) {
    var accesses = nest.Accesses;
    for (var i = 0; i < accesses.Count; ++i)
      for (var j = i; j < accesses.Count; ++j) {
        var first = accesses[i];
        var second = accesses[j];
        if (!first.Writes && !second.Writes)
          continue;
        if (IrAliasAnalysis.Alias(first.Pointer, first.AccessType, second.Pointer, second.AccessType)
            == IrAliasResult.NoAlias)
          continue;
        if (!ReferenceEquals(first.Address.Root, second.Address.Root))
          return false;                           // may-alias roots with unknown provenance
        if (i != j)
          return false;                           // nested direction vectors are not implemented yet
        if (!SelfAccessIsDisjoint(first.Address, nest.Outer.Trips, nest.Inner.Trips))
          return false;
      }
    return true;
  }

  private static bool SelfAccessIsDisjoint(Address2 address, long outerTrips, long innerTrips) {
    if (outerTrips <= 1 && innerTrips <= 1)
      return true;
    if (outerTrips > 1 && address.OuterStride == 0)
      return false;
    if (innerTrips > 1 && address.InnerStride == 0)
      return false;
    if (!TryGcd(address.OuterStride, address.InnerStride, out var gcd) || gcd <= 0 || address.Bytes > gcd)
      return false;

    if (address.OuterStride == 0 || address.InnerStride == 0)
      return true;                                // the varying dimension is separated by >= width

    var outerDelta = Math.Abs(address.InnerStride / gcd);
    var innerDelta = Math.Abs(address.OuterStride / gcd);
    return outerDelta > outerTrips - 1 || innerDelta > innerTrips - 1;
  }

  private static bool IsProfitable(Nest nest) {
    ulong current = 0;
    ulong interchanged = 0;
    foreach (var access in nest.Accesses) {
      current = SaturatingAdd(current, Magnitude(access.Address.InnerStride));
      interchanged = SaturatingAdd(interchanged, Magnitude(access.Address.OuterStride));
    }
    return current > 0 && interchanged < current;
  }

  private static void Apply(Nest nest) {
    var outerHeader = nest.Outer.Header;
    var innerHeader = nest.Inner.Header;
    var outerTerminator = nest.OuterBranch;
    var innerTerminator = nest.InnerBranch;

    var newOuterCounter = outerHeader.AppendPhi(new IrPhi(nest.Inner.Counter.Type) { Name = nest.Inner.Counter.Name });
    var newInnerCounter = innerHeader.AppendPhi(new IrPhi(nest.Outer.Counter.Type) { Name = nest.Outer.Counter.Name });

    var newOuterTest = outerHeader.InsertBefore(
      new IrCmp(nest.Inner.Test.Pred, newOuterCounter, nest.Inner.Test.Rhs) {
        IsSourceCondition = nest.Inner.Test.IsSourceCondition,
      }, outerTerminator);
    var newInnerTest = innerHeader.InsertBefore(
      new IrCmp(nest.Outer.Test.Pred, newInnerCounter, nest.Outer.Test.Rhs) {
        IsSourceCondition = nest.Outer.Test.IsSourceCondition,
      }, innerTerminator);
    outerTerminator.SetOperand(0, newOuterTest);
    innerTerminator.SetOperand(0, newInnerTest);

    var newInnerNext = nest.Inner.Latch.InsertBefore(
      new IrBinary(IrBinaryOp.Add, newInnerCounter, nest.OuterCounter.StepConstant),
      nest.Inner.Latch.Terminator!);
    var newOuterNext = nest.Outer.Latch.InsertBefore(
      new IrBinary(IrBinaryOp.Add, newOuterCounter, nest.InnerCounter.StepConstant),
      nest.Outer.Latch.Terminator!);

    newOuterCounter.AddIncoming(nest.InnerCounter.Start, nest.Outer.Preheader);
    newOuterCounter.AddIncoming(newOuterNext, nest.Outer.Latch);
    newInnerCounter.AddIncoming(nest.OuterCounter.Start, nest.Inner.Preheader);
    newInnerCounter.AddIncoming(newInnerNext, nest.Inner.Latch);

    RewireCounter(nest.Outer.Counter, newInnerCounter, nest.OuterCounter.Final, nest.Outer.Region,
      nest.Outer.Test, nest.OuterNext);
    RewireCounter(nest.Inner.Counter, newOuterCounter, nest.InnerCounter.Final, nest.Outer.Region,
      nest.Inner.Test, nest.InnerNext);

    foreach (var carrier in nest.InnerExitCarriers) {
      carrier.ReplaceAllUsesWith(nest.InnerCounter.Final);
      carrier.EraseFromParent();
    }

    nest.Outer.Test.EraseFromParent();
    nest.Inner.Test.EraseFromParent();
    nest.Outer.Counter.EraseFromParent();
    nest.Inner.Counter.EraseFromParent();
    nest.OuterNext.EraseFromParent();
    nest.InnerNext.EraseFromParent();
  }

  private static void RewireCounter(
      IrPhi oldCounter,
      IrPhi insideReplacement,
      IrConstantInt outsideReplacement,
      HashSet<IrBasicBlock> region,
      params IrInstruction[] ignored) {
    var skip = ignored.ToHashSet(ReferenceEqualityComparer.Instance);
    foreach (var user in oldCounter.Users.ToArray()) {
      if (skip.Contains(user))
        continue;
      var replacement = user.Parent is { } parent && region.Contains(parent)
        ? (IrValue)insideReplacement
        : outsideReplacement;
      user.ReplaceOperand(oldCounter, replacement);
    }
  }

  private static bool Fits(Affine2 affine, IrType type, CounterInfo outer, CounterInfo inner) {
    if (!type.IsInteger || !type.Signed || type.Bits >= 64 || !TryRange(affine, outer, inner, out var lo, out var hi))
      return false;
    var allowed = ValueRange.OfType(type);
    return lo >= allowed.Lo && hi <= allowed.Hi;
  }

  private static bool TryRange(Affine2 affine, CounterInfo outer, CounterInfo inner, out long lo, out long hi) {
    lo = long.MaxValue;
    hi = long.MinValue;
    foreach (var outerValue in new[] { outer.Minimum, outer.Maximum })
      foreach (var innerValue in new[] { inner.Minimum, inner.Maximum }) {
        long value;
        try {
          value = checked(checked(affine.Outer * outerValue)
            + checked(affine.Inner * innerValue)
            + affine.Constant);
        } catch (OverflowException) {
          return false;
        }
        lo = Math.Min(lo, value);
        hi = Math.Max(hi, value);
      }
    return true;
  }

  private static bool TryAdd(Affine2 left, Affine2 right, out Affine2 result) {
    result = default;
    try {
      result = new(checked(left.Outer + right.Outer), checked(left.Inner + right.Inner), checked(left.Constant + right.Constant));
      return true;
    } catch (OverflowException) {
      return false;
    }
  }

  private static bool TrySubtract(Affine2 left, Affine2 right, out Affine2 result) {
    result = default;
    try {
      result = new(checked(left.Outer - right.Outer), checked(left.Inner - right.Inner), checked(left.Constant - right.Constant));
      return true;
    } catch (OverflowException) {
      return false;
    }
  }

  private static bool TryScale(Affine2 value, long factor, out Affine2 result) {
    result = default;
    try {
      result = new(checked(value.Outer * factor), checked(value.Inner * factor), checked(value.Constant * factor));
      return true;
    } catch (OverflowException) {
      return false;
    }
  }

  private static bool IsConstant(Affine2 value) => value.Outer == 0 && value.Inner == 0;

  private static bool TryGcd(long left, long right, out long gcd) {
    gcd = 0;
    if (left == long.MinValue || right == long.MinValue)
      return false;
    var a = Math.Abs(left);
    var b = Math.Abs(right);
    while (b != 0)
      (a, b) = (b, a % b);
    gcd = a;
    return true;
  }

  private static ulong Magnitude(long value)
    => value == long.MinValue ? 1UL << 63 : (ulong)Math.Abs(value);

  private static ulong SaturatingAdd(ulong left, ulong right)
    => ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

  private static long Signed(IrConstantInt constant) {
    if (constant.Type.Bits >= 64)
      return constant.Value;
    var bits = constant.Type.Bits;
    var pattern = constant.ZeroExtended;
    var sign = 1UL << (bits - 1);
    return unchecked((long)((pattern ^ sign) - sign));
  }
}
