namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0340-O0345 — transformations legal only under the floating freedoms granted by
/// <c>$OPTIMIZE SPEED</c>. The individual flags remain explicit so every rewrite states exactly what
/// semantic relaxation it consumes.
/// </summary>
public static class FpFastMath {

  private const int _MAX_REASSOC_LEAVES = 32;
  private const IrFastMathFlags _ARITHMETIC_FLAGS = IrFastMathFlags.Reassociate
    | IrFastMathFlags.NoNaNs | IrFastMathFlags.NoInfs | IrFastMathFlags.NoSignedZeros
    | IrFastMathFlags.AllowContract;

  public static int Run(IrFunction function, IrFastMathFlags flags) {
    if (flags == IrFastMathFlags.None || function.HasErrorHandler || function.HasInlineAsm)
      return 0;

    var changes = 0;
    if ((flags & IrFastMathFlags.Reassociate) != 0)
      changes += Reassociate(function, flags);
    if ((flags & IrFastMathFlags.AllowReciprocal) != 0)
      changes += FactorCommonDenominators(function, flags);
    changes += Annotate(function, flags);
    return changes;
  }

  private static int Reassociate(IrFunction function, IrFastMathFlags flags) {
    var changes = 0;
    foreach (var block in function.Blocks)
      foreach (var root in block.Instructions.OfType<IrBinary>().Reverse().ToList()) {
        if (root.Parent is null || root.Op is not (IrBinaryOp.FAdd or IrBinaryOp.FMul)
            || !root.Type.IsIeeeFloat || !IsChainRoot(root))
          continue;
        var leaves = new List<IrValue>();
        if (!Flatten(root, root.Op, block, leaves, isRoot: true)
            || leaves.Count < 4 || leaves.Count > _MAX_REASSOC_LEAVES
            || ChainDepth(root, root.Op, block, isRoot: true) <= BalancedDepth(leaves.Count))
          continue;
        var replacement = BuildBalanced(block, root, root.Op, leaves, 0, leaves.Count,
          ArithmeticFlags(root.FastMathFlags | flags));
        root.ReplaceAllUsesWith(replacement);
        root.EraseFromParent();
        ++changes;
      }
    return changes;
  }

  private static bool IsChainRoot(IrBinary node)
    => node.Users.Count != 1 || node.Users[0] is not IrBinary parent || parent.Op != node.Op
       || !ReferenceEquals(parent.Parent, node.Parent);

  private static bool Flatten(IrValue value, IrBinaryOp op, IrBasicBlock block, List<IrValue> leaves,
      bool isRoot = false) {
    if (leaves.Count > _MAX_REASSOC_LEAVES)
      return false;
    if (value is IrBinary inner && inner.Op == op && ReferenceEquals(inner.Parent, block)
        && (isRoot || inner.Users.Count == 1))
      return Flatten(inner.Lhs, op, block, leaves) && Flatten(inner.Rhs, op, block, leaves);
    leaves.Add(value);
    return true;
  }

  private static int ChainDepth(IrValue value, IrBinaryOp op, IrBasicBlock block, bool isRoot = false) {
    if (value is not IrBinary inner || inner.Op != op || !ReferenceEquals(inner.Parent, block)
        || (!isRoot && inner.Users.Count != 1))
      return 0;
    return 1 + Math.Max(ChainDepth(inner.Lhs, op, block), ChainDepth(inner.Rhs, op, block));
  }

  private static int BalancedDepth(int leaves) {
    var depth = 0;
    for (var capacity = 1; capacity < leaves; capacity <<= 1)
      ++depth;
    return depth;
  }

  private static IrValue BuildBalanced(IrBasicBlock block, IrInstruction anchor, IrBinaryOp op,
      IReadOnlyList<IrValue> leaves, int start, int count, IrFastMathFlags flags) {
    if (count == 1)
      return leaves[start];
    var leftCount = count / 2;
    var left = BuildBalanced(block, anchor, op, leaves, start, leftCount, flags);
    var right = BuildBalanced(block, anchor, op, leaves, start + leftCount, count - leftCount, flags);
    return block.InsertBefore(new IrBinary(op, left, right) { FastMathFlags = flags }, anchor);
  }

  private static int FactorCommonDenominators(IrFunction function, IrFastMathFlags flags) {
    var changes = 0;
    foreach (var block in function.Blocks) {
      var groups = new Dictionary<IrValue, List<IrBinary>>(ReferenceEqualityComparer.Instance);
      void Flush() {
        foreach (var values in groups.Values)
          if (values.Count >= 2)
            changes += FactorGroup(block, values, flags);
        groups.Clear();
      }

      foreach (var instruction in block.Instructions.ToList()) {
        if (instruction is IrCall) {
          Flush();
          continue;
        }
        if (instruction is not IrBinary { Op: IrBinaryOp.FDiv, Type.IsIeeeFloat: true } division)
          continue;
        if (!groups.TryGetValue(division.Rhs, out var values))
          groups[division.Rhs] = values = [];
        values.Add(division);
      }
      Flush();
    }
    return changes;
  }

  private static int FactorGroup(IrBasicBlock block, IReadOnlyList<IrBinary> divisions, IrFastMathFlags flags) {
    var first = divisions[0];
    IrValue reciprocal;
    IrBinary? retained = null;
    if (IsOne(first.Lhs)) {
      reciprocal = retained = first;
      retained.FastMathFlags |= FlagsForBinary(retained, flags);
    } else {
      reciprocal = block.InsertBefore(new IrBinary(IrBinaryOp.FDiv, new IrConstantFloat(first.Type, 1.0), first.Rhs) {
        FastMathFlags = FlagsForBinary(first, flags),
      }, first);
    }

    var changes = 0;
    foreach (var division in divisions) {
      if (ReferenceEquals(division, retained))
        continue;
      var product = block.InsertBefore(new IrBinary(IrBinaryOp.FMul, division.Lhs, reciprocal) {
        FastMathFlags = ArithmeticFlags(division.FastMathFlags | flags),
      }, division);
      division.ReplaceAllUsesWith(product);
      division.EraseFromParent();
      ++changes;
    }
    return changes;
  }

  private static bool IsOne(IrValue value) => value is IrConstantFloat { Value: 1.0 };

  private static int Annotate(IrFunction function, IrFastMathFlags flags) {
    var changes = 0;
    foreach (var instruction in function.AllInstructions) {
      var applicable = instruction switch {
        IrBinary binary when binary.IsFloatOp && binary.Type.IsIeeeFloat => FlagsForBinary(binary, flags),
        IrCmp cmp when cmp.Pred is >= IrCmpPred.Foeq and <= IrCmpPred.Foge => FlagsForCompare(flags),
        IrCall call when IrFpMath.TryGet(call, out _) => FlagsForMathCall(flags),
        _ => IrFastMathFlags.None,
      };
      var missing = applicable & ~instruction.FastMathFlags;
      if (missing == IrFastMathFlags.None)
        continue;
      instruction.FastMathFlags |= missing;
      ++changes;
    }
    return changes;
  }

  internal static IrFastMathFlags ArithmeticFlags(IrFastMathFlags flags) => flags & _ARITHMETIC_FLAGS;

  private static IrFastMathFlags FlagsForBinary(IrBinary binary, IrFastMathFlags flags) {
    var common = ArithmeticFlags(flags);
    return binary.Op == IrBinaryOp.FDiv ? common | (flags & IrFastMathFlags.AllowReciprocal) : common;
  }

  private static IrFastMathFlags FlagsForCompare(IrFastMathFlags flags)
    => flags & (IrFastMathFlags.NoNaNs | IrFastMathFlags.NoInfs | IrFastMathFlags.NoSignedZeros);

  private static IrFastMathFlags FlagsForMathCall(IrFastMathFlags flags)
    => flags & (IrFastMathFlags.NoNaNs | IrFastMathFlags.NoInfs | IrFastMathFlags.NoSignedZeros
      | IrFastMathFlags.ApproxFunc);
}
