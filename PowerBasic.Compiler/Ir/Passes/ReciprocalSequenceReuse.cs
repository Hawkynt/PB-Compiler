namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0338 — reuses reciprocals across repeated IEEE divisions. Strict floating point only admits
/// exact power-of-two constant reciprocals; divisions carrying <see cref="IrFastMathFlags.AllowReciprocal"/>
/// may additionally share one runtime reciprocal across dominated, call-free CFG regions. A supplied
/// target cost model decides when that legal rewrite is actually profitable.
/// </summary>
public static class ReciprocalSequenceReuse {

  /// <summary>Rewrites profitable reciprocal groups; returns the number of divisions replaced or hoisted.</summary>
  public static int Run(IrFunction fn, IIrArithmeticCostModel? costModel = null) {
    ArgumentNullException.ThrowIfNull(fn);
    if (fn.HasErrorHandler || fn.HasInlineAsm)
      return 0;

    var changed = RewriteExactConstantGroups(fn);
    if (fn.Entry is null)
      return changed;

    var dominators = IrDominators.Build(fn)!;
    changed += RewriteRelaxedGroups(fn, dominators, costModel);
    changed += ReciprocalLoopHoisting.Run(fn);
    return changed;
  }

  private static int RewriteExactConstantGroups(IrFunction fn) {
    var groups = fn.AllInstructions
      .OfType<IrBinary>()
      .Where(binary => binary.Op == IrBinaryOp.FDiv && binary.Rhs is IrConstantFloat divisor
                       && TryReciprocal(divisor, out _))
      .GroupBy(binary => Key((IrConstantFloat)binary.Rhs))
      .Where(group => group.Count() > 1)
      .ToList();

    var replaced = 0;
    foreach (var group in groups) {
      var divisor = (IrConstantFloat)group.First().Rhs;
      if (!TryReciprocal(divisor, out var reciprocal))
        continue;

      foreach (var division in group.ToList()) {
        var block = division.Parent;
        if (block is null)
          continue;

        var multiply = block.InsertBefore(new IrBinary(IrBinaryOp.FMul, division.Lhs,
          new IrConstantFloat(division.Type, reciprocal)) {
          FastMathFlags = FpFastMath.ArithmeticFlags(division.FastMathFlags),
        }, division);
        division.ReplaceAllUsesWith(multiply);
        division.EraseFromParent();
        ++replaced;
      }
    }

    return replaced;
  }

  private static int RewriteRelaxedGroups(
      IrFunction fn, IrDominators dominators, IIrArithmeticCostModel? costModel) {
    var groups = new List<List<IrBinary>>();
    foreach (var division in fn.AllInstructions.OfType<IrBinary>().Where(IsRelaxedDivision).ToList()) {
      var group = groups.FirstOrDefault(candidate => SameDivisor(candidate[0].Rhs, division.Rhs));
      if (group is null)
        groups.Add([division]);
      else
        group.Add(division);
    }

    var replaced = 0;
    foreach (var group in groups.Where(group => group.Count > 1))
      replaced += RewriteRelaxedGroup(fn, group, dominators, costModel);
    return replaced;
  }

  private static int RewriteRelaxedGroup(
      IrFunction fn, List<IrBinary> group, IrDominators dominators, IIrArithmeticCostModel? costModel) {
    var remaining = group
      .Where(division => division.Parent is { } block && dominators.IsReachable(block))
      .ToList();
    var replaced = 0;

    while (remaining.Count > 1) {
      IrBinary? bestAnchor = null;
      List<IrBinary>? bestSequence = null;
      foreach (var candidate in remaining) {
        // Keep the candidate first even when the group's discovery order differs from dominance order.
        // The first element is the instruction RewriteRelaxedSequence will use to materialize 1/d,
        // and the guarded-hoist profitability projection needs to reason about exactly that origin.
        var sequence = new List<IrBinary> { candidate };
        sequence.AddRange(remaining.Where(division => !ReferenceEquals(division, candidate)
          && InstructionDominates(candidate, division, dominators)
          && IsCallFreeBetween(candidate, division)));
        if (sequence.Count <= (bestSequence?.Count ?? 1)
            || !IsProfitable(fn, sequence, dominators, costModel))
          continue;
        bestAnchor = candidate;
        bestSequence = sequence;
      }

      if (bestAnchor is null || bestSequence is null)
        break;

      replaced += RewriteRelaxedSequence(bestAnchor, bestSequence);
      remaining.RemoveAll(bestSequence.Contains);
    }

    return replaced;
  }

  private static bool IsProfitable(
      IrFunction fn, IReadOnlyList<IrBinary> sequence,
      IrDominators dominators, IIrArithmeticCostModel? costModel) {
    if (costModel is null)
      return true;

    var anchor = sequence[0];
    if (IsOne(anchor.Lhs))
      return costModel.PreferExistingReciprocalUse(anchor.Type, sequence.Count - 1);
    if (costModel.PreferReciprocalReuse(anchor.Type, sequence.Count))
      return true;

    // A target may reject the static shape yet accept the same rewrite once a proven counted loop lets
    // guarded hoisting pay the reciprocal only once. ProjectedDivisionCount returns a value only when
    // every priced division executes every iteration and the generated reciprocal is itself hoistable.
    return ReciprocalLoopHoisting.ProjectedDivisionCount(fn, sequence, dominators) is { } dynamicCount
      && costModel.PreferReciprocalReuse(anchor.Type, dynamicCount);
  }

  private static int RewriteRelaxedSequence(IrBinary anchor, IReadOnlyList<IrBinary> sequence) {
    var anchorBlock = anchor.Parent!;
    IrValue reciprocal;
    IrBinary? retained = null;
    if (IsOne(anchor.Lhs)) {
      reciprocal = retained = anchor;
    } else {
      reciprocal = anchorBlock.InsertBefore(new IrBinary(IrBinaryOp.FDiv,
        new IrConstantFloat(anchor.Type, 1.0), anchor.Rhs) {
        FastMathFlags = anchor.FastMathFlags,
      }, anchor);
    }

    var replaced = 0;
    foreach (var division in sequence) {
      if (ReferenceEquals(division, retained))
        continue;
      var block = division.Parent!;
      var multiply = block.InsertBefore(new IrBinary(IrBinaryOp.FMul, division.Lhs, reciprocal) {
        FastMathFlags = FpFastMath.ArithmeticFlags(division.FastMathFlags),
      }, division);
      division.ReplaceAllUsesWith(multiply);
      division.EraseFromParent();
      ++replaced;
    }

    return replaced;
  }

  private static bool IsRelaxedDivision(IrBinary binary)
    => binary is { Op: IrBinaryOp.FDiv, Type: { Kind: IrTypeKind.Float, Format: IrFloatFormat.Ieee, Bits: 32 or 64 or 80 } }
       && (binary.FastMathFlags & IrFastMathFlags.AllowReciprocal) != 0;

  private static bool SameDivisor(IrValue left, IrValue right) {
    if (ReferenceEquals(left, right))
      return true;
    return left is IrConstantFloat leftConstant
           && right is IrConstantFloat rightConstant
           && leftConstant.Type.Equals(rightConstant.Type)
           && Key(leftConstant) == Key(rightConstant);
  }

  private static bool InstructionDominates(IrInstruction first, IrInstruction second, IrDominators dominators) {
    var firstBlock = first.Parent;
    var secondBlock = second.Parent;
    if (firstBlock is null || secondBlock is null)
      return false;
    if (!ReferenceEquals(firstBlock, secondBlock))
      return dominators.Dominates(firstBlock, secondBlock);

    var firstIndex = IndexOf(firstBlock.Instructions, first);
    var secondIndex = IndexOf(firstBlock.Instructions, second);
    return firstIndex >= 0 && firstIndex <= secondIndex;
  }

  private static bool IsCallFreeBetween(IrInstruction first, IrInstruction second) {
    var firstBlock = first.Parent;
    var secondBlock = second.Parent;
    if (firstBlock is null || secondBlock is null)
      return false;

    if (ReferenceEquals(firstBlock, secondBlock)) {
      var firstIndex = IndexOf(firstBlock.Instructions, first);
      var secondIndex = IndexOf(firstBlock.Instructions, second);
      return firstIndex >= 0 && secondIndex >= firstIndex
             && !ContainsCall(firstBlock.Instructions, firstIndex + 1, secondIndex);
    }

    var forward = ReachableFrom(firstBlock);
    if (!forward.Contains(secondBlock))
      return false;
    var backward = CanReach(secondBlock);

    foreach (var block in forward.Where(backward.Contains)) {
      var start = ReferenceEquals(block, firstBlock) ? IndexOf(block.Instructions, first) + 1 : 0;
      var end = ReferenceEquals(block, secondBlock) ? IndexOf(block.Instructions, second) : block.Instructions.Count;
      if (start < 0 || end < 0 || ContainsCall(block.Instructions, start, end))
        return false;
    }

    return true;
  }

  private static HashSet<IrBasicBlock> ReachableFrom(IrBasicBlock start) {
    var result = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance);
    var pending = new Stack<IrBasicBlock>();
    pending.Push(start);
    while (pending.Count > 0) {
      var block = pending.Pop();
      if (!result.Add(block))
        continue;
      foreach (var successor in block.Successors)
        pending.Push(successor);
    }
    return result;
  }

  private static HashSet<IrBasicBlock> CanReach(IrBasicBlock target) {
    var result = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance);
    var pending = new Stack<IrBasicBlock>();
    pending.Push(target);
    while (pending.Count > 0) {
      var block = pending.Pop();
      if (!result.Add(block))
        continue;
      foreach (var predecessor in block.Predecessors)
        pending.Push(predecessor);
    }
    return result;
  }

  private static bool ContainsCall(IReadOnlyList<IrInstruction> instructions, int start, int end) {
    for (var i = start; i < end; ++i)
      if (instructions[i] is IrCall)
        return true;
    return false;
  }

  private static int IndexOf(IReadOnlyList<IrInstruction> instructions, IrInstruction instruction) {
    for (var i = 0; i < instructions.Count; ++i)
      if (ReferenceEquals(instructions[i], instruction))
        return i;
    return -1;
  }

  private static bool IsOne(IrValue value) => value is IrConstantFloat { Value: 1.0 };

  private static (int Bits, long Pattern) Key(IrConstantFloat value) => value.Type.Bits switch {
    32 => (32, BitConverter.SingleToInt32Bits((float)value.Value)),
    64 or 80 => (value.Type.Bits, BitConverter.DoubleToInt64Bits(value.Value)),
    _ => (value.Type.Bits, BitConverter.DoubleToInt64Bits(value.Value)),
  };

  private static bool TryReciprocal(IrConstantFloat divisor, out double reciprocal) {
    reciprocal = 0;
    if (divisor.Type is not { Kind: IrTypeKind.Float, Format: IrFloatFormat.Ieee, Bits: 32 or 64 or 80 }
        || divisor.Value == 0 || !double.IsFinite(divisor.Value) || !IsPowerOfTwo(divisor))
      return false;

    reciprocal = 1.0 / divisor.Value;
    if (divisor.Type.Bits == 32)
      reciprocal = (float)reciprocal;
    return double.IsFinite(reciprocal) && reciprocal != 0;
  }

  private static bool IsPowerOfTwo(IrConstantFloat value) {
    if (value.Type.Bits == 32) {
      var pattern = (uint)BitConverter.SingleToInt32Bits(MathF.Abs((float)value.Value)) & 0x7fff_ffffU;
      var exponent = pattern & 0x7f80_0000U;
      var fraction = pattern & 0x007f_ffffU;
      return exponent == 0 ? fraction != 0 && (fraction & (fraction - 1)) == 0 : fraction == 0;
    }

    // IrConstantFloat currently carries a binary64 payload even when typed f80. A power of two in that
    // payload is also an exact x87 value, and its reciprocal is exact as long as the payload can hold it.
    var bits = (ulong)BitConverter.DoubleToInt64Bits(Math.Abs(value.Value)) & 0x7fff_ffff_ffff_ffffUL;
    var doubleExponent = bits & 0x7ff0_0000_0000_0000UL;
    var doubleFraction = bits & 0x000f_ffff_ffff_ffffUL;
    return doubleExponent == 0
      ? doubleFraction != 0 && (doubleFraction & (doubleFraction - 1)) == 0
      : doubleFraction == 0;
  }
}
