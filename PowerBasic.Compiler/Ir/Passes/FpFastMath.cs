namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0340-O0345 — transformations legal only under the floating freedoms granted by
/// <c>$OPTIMIZE SPEED</c>. The individual flags remain explicit so every rewrite states exactly what
/// semantic relaxation it consumes.
/// </summary>
public static class FpFastMath {

  private const int _MAX_REASSOC_LEAVES = 32;
  private const IrFastMathFlags _ARITHMETIC_FLAGS = IrFastMathFlags.Reassociate
    | IrFastMathFlags.NoNaNs | IrFastMathFlags.NoInfs | IrFastMathFlags.NoSignedZeros;

  private readonly record struct SignedValue(IrValue Value, bool Negative);

  public static int Run(IrFunction function, IrFastMathFlags flags) {
    if (flags == IrFastMathFlags.None || function.HasErrorHandler || function.HasInlineAsm)
      return 0;

    var changes = 0;
    if ((flags & IrFastMathFlags.Reassociate) != 0)
      changes += Reassociate(function, flags);
    if ((flags & IrFastMathFlags.AllowReciprocal) != 0)
      changes += FactorCommonDenominators(function, flags);
    changes += AnnotateContractions(function, flags);
    changes += Annotate(function, flags);
    return changes;
  }

  private static int Reassociate(IrFunction function, IrFastMathFlags flags) {
    var changes = 0;
    var allowSubtraction = (flags & IrFastMathFlags.NoSignedZeros) != 0;
    foreach (var block in function.Blocks)
      foreach (var root in block.Instructions.OfType<IrBinary>().Reverse().ToList()) {
        if (root.Parent is null || !root.Type.IsIeeeFloat)
          continue;

        if (root.Op == IrBinaryOp.FMul) {
          if (!IsProductRoot(root))
            continue;
          var leaves = new List<IrValue>();
          var nodes = new List<IrBinary>();
          if (!FlattenProduct(root, block, leaves, nodes, isRoot: true)
              || leaves.Count < 4 || leaves.Count > _MAX_REASSOC_LEAVES
              || ProductDepth(root, block, isRoot: true) <= BalancedDepth(leaves.Count))
            continue;
          var replacement = BuildBalancedProduct(block, root, leaves, 0, leaves.Count,
            ArithmeticFlags(root.FastMathFlags | flags));
          ReplaceTree(root, replacement, nodes);
          ++changes;
          continue;
        }

        if (!IsSumOp(root.Op, allowSubtraction) || !IsSumRoot(root, allowSubtraction))
          continue;
        var signedLeaves = new List<SignedValue>();
        var sumNodes = new List<IrBinary>();
        if (!FlattenSum(root, block, signedLeaves, sumNodes, negative: false,
              allowSubtraction: allowSubtraction, isRoot: true)
            || signedLeaves.Count < 4 || signedLeaves.Count > _MAX_REASSOC_LEAVES
            || SumDepth(root, block, allowSubtraction, isRoot: true) <= BalancedDepth(signedLeaves.Count))
          continue;
        var signedReplacement = BuildBalancedSum(block, root, signedLeaves, 0, signedLeaves.Count,
          ArithmeticFlags(root.FastMathFlags | flags));
        System.Diagnostics.Debug.Assert(!signedReplacement.Negative);
        ReplaceTree(root, signedReplacement.Value, sumNodes);
        ++changes;
      }
    return changes;
  }

  private static bool IsProductRoot(IrBinary node)
    => node.Users.Count != 1 || node.Users[0] is not IrBinary parent || parent.Op != IrBinaryOp.FMul
       || !ReferenceEquals(parent.Parent, node.Parent);

  private static bool IsSumRoot(IrBinary node, bool allowSubtraction)
    => node.Users.Count != 1 || node.Users[0] is not IrBinary parent
       || !ReferenceEquals(parent.Parent, node.Parent) || !IsSumOp(parent.Op, allowSubtraction);

  private static bool IsSumOp(IrBinaryOp op, bool allowSubtraction)
    => op == IrBinaryOp.FAdd || allowSubtraction && op == IrBinaryOp.FSub;

  private static bool FlattenProduct(IrValue value, IrBasicBlock block, List<IrValue> leaves,
      List<IrBinary> nodes, bool isRoot = false) {
    if (leaves.Count > _MAX_REASSOC_LEAVES)
      return false;
    if (value is IrBinary { Op: IrBinaryOp.FMul } inner && ReferenceEquals(inner.Parent, block)
        && (isRoot || inner.Users.Count == 1)) {
      nodes.Add(inner);
      return FlattenProduct(inner.Lhs, block, leaves, nodes)
        && FlattenProduct(inner.Rhs, block, leaves, nodes);
    }
    leaves.Add(value);
    return true;
  }

  private static bool FlattenSum(IrValue value, IrBasicBlock block, List<SignedValue> leaves,
      List<IrBinary> nodes, bool negative, bool allowSubtraction, bool isRoot = false) {
    if (leaves.Count > _MAX_REASSOC_LEAVES)
      return false;
    if (value is IrBinary inner && ReferenceEquals(inner.Parent, block)
        && IsSumOp(inner.Op, allowSubtraction) && (isRoot || inner.Users.Count == 1)) {
      nodes.Add(inner);
      if (!FlattenSum(inner.Lhs, block, leaves, nodes, negative, allowSubtraction))
        return false;
      var rhsNegative = inner.Op == IrBinaryOp.FSub ? !negative : negative;
      return FlattenSum(inner.Rhs, block, leaves, nodes, rhsNegative, allowSubtraction);
    }
    leaves.Add(new SignedValue(value, negative));
    return true;
  }

  private static int ProductDepth(IrValue value, IrBasicBlock block, bool isRoot = false) {
    if (value is not IrBinary { Op: IrBinaryOp.FMul } inner || !ReferenceEquals(inner.Parent, block)
        || (!isRoot && inner.Users.Count != 1))
      return 0;
    return 1 + Math.Max(ProductDepth(inner.Lhs, block), ProductDepth(inner.Rhs, block));
  }

  private static int SumDepth(IrValue value, IrBasicBlock block, bool allowSubtraction, bool isRoot = false) {
    if (value is not IrBinary inner || !ReferenceEquals(inner.Parent, block)
        || !IsSumOp(inner.Op, allowSubtraction) || (!isRoot && inner.Users.Count != 1))
      return 0;
    return 1 + Math.Max(SumDepth(inner.Lhs, block, allowSubtraction),
      SumDepth(inner.Rhs, block, allowSubtraction));
  }

  private static int BalancedDepth(int leaves) {
    var depth = 0;
    for (var capacity = 1; capacity < leaves; capacity <<= 1)
      ++depth;
    return depth;
  }

  private static IrValue BuildBalancedProduct(IrBasicBlock block, IrInstruction anchor,
      IReadOnlyList<IrValue> leaves, int start, int count, IrFastMathFlags flags) {
    if (count == 1)
      return leaves[start];
    var leftCount = count / 2;
    var left = BuildBalancedProduct(block, anchor, leaves, start, leftCount, flags);
    var right = BuildBalancedProduct(block, anchor, leaves, start + leftCount, count - leftCount, flags);
    return block.InsertBefore(new IrBinary(IrBinaryOp.FMul, left, right) { FastMathFlags = flags }, anchor);
  }

  private static SignedValue BuildBalancedSum(IrBasicBlock block, IrInstruction anchor,
      IReadOnlyList<SignedValue> leaves, int start, int count, IrFastMathFlags flags) {
    if (count == 1)
      return leaves[start];
    var leftCount = count / 2;
    var left = BuildBalancedSum(block, anchor, leaves, start, leftCount, flags);
    var right = BuildBalancedSum(block, anchor, leaves, start + leftCount, count - leftCount, flags);
    var op = left.Negative == right.Negative ? IrBinaryOp.FAdd : IrBinaryOp.FSub;
    var value = block.InsertBefore(new IrBinary(op, left.Value, right.Value) { FastMathFlags = flags }, anchor);
    return new SignedValue(value, left.Negative);
  }

  private static void ReplaceTree(IrBinary root, IrValue replacement, IReadOnlyList<IrBinary> nodes) {
    root.ReplaceAllUsesWith(replacement);
    root.EraseFromParent();
    foreach (var node in nodes)
      if (!ReferenceEquals(node, root) && node.Parent is not null && node.HasNoUsers)
        node.EraseFromParent();
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

      if (IsOne(division.Lhs)) {
        division.ReplaceAllUsesWith(reciprocal);
        division.EraseFromParent();
        ++changes;
        continue;
      }

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

  private static int AnnotateContractions(IrFunction function, IrFastMathFlags flags) {
    if ((flags & IrFastMathFlags.AllowContract) == 0)
      return 0;

    var changes = 0;
    foreach (var operation in function.AllInstructions.OfType<IrBinary>()) {
      if (operation.Op is not (IrBinaryOp.FAdd or IrBinaryOp.FSub) || !operation.Type.IsIeeeFloat)
        continue;

      var hasProduct = false;
      changes += AnnotateProduct(operation.Lhs, ref hasProduct);
      changes += AnnotateProduct(operation.Rhs, ref hasProduct);
      if (hasProduct)
        changes += AddFlag(operation, IrFastMathFlags.AllowContract);
    }
    return changes;
  }

  private static int AnnotateProduct(IrValue value, ref bool hasProduct) {
    if (value is not IrBinary { Op: IrBinaryOp.FMul, Type.IsIeeeFloat: true } product)
      return 0;
    hasProduct = true;
    return AddFlag(product, IrFastMathFlags.AllowContract);
  }

  private static int AddFlag(IrInstruction instruction, IrFastMathFlags flag) {
    if ((instruction.FastMathFlags & flag) != 0)
      return 0;
    instruction.FastMathFlags |= flag;
    return 1;
  }
  private static bool IsRsqrtDivision(IrBinary binary)
    => binary.Op == IrBinaryOp.FDiv && IsOne(binary.Lhs)
       && binary.Rhs is IrCall call && IrFpMath.TryGet(call, out var kind)
       && kind == IrFpMathFunction.Sqrt;

  private static bool IsRsqrtSqrt(IrCall call)
    => call.Users.Any(user => user is IrBinary binary && ReferenceEquals(binary.Rhs, call)
      && IsRsqrtDivision(binary));

  private static int Annotate(IrFunction function, IrFastMathFlags flags) {
    var changes = 0;
    foreach (var instruction in function.AllInstructions) {
      var applicable = instruction switch {
        IrBinary binary when binary.IsFloatOp && binary.Type.IsIeeeFloat => FlagsForBinary(binary, flags),
        IrCmp cmp when cmp.Pred is >= IrCmpPred.Foeq and <= IrCmpPred.Foge => FlagsForCompare(flags),
        IrCall call when IrFpMath.TryGet(call, out _) => FlagsForMathCall(call, flags),
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
    if (binary.Op != IrBinaryOp.FDiv)
      return common;

    var applicable = common | (flags & IrFastMathFlags.AllowReciprocal);
    // Contraction is otherwise granted only where a product actually feeds an add or subtract, but the
    // reciprocal-sqrt pair is lowered as one operation: the division has to keep the permission its
    // sqrt already gets in FlagsForMathCall, or the backend cannot form rsqrt at all.
    if (IsRsqrtDivision(binary) && (flags & IrFastMathFlags.AllowContract) != 0)
      applicable |= flags & (IrFastMathFlags.ApproxFunc | IrFastMathFlags.AllowContract);
    return applicable;
  }

  private static IrFastMathFlags FlagsForCompare(IrFastMathFlags flags)
    => flags & (IrFastMathFlags.NoNaNs | IrFastMathFlags.NoInfs | IrFastMathFlags.NoSignedZeros);

  private static IrFastMathFlags FlagsForMathCall(IrCall call, IrFastMathFlags flags) {
    var applicable = flags & (IrFastMathFlags.NoNaNs | IrFastMathFlags.NoInfs | IrFastMathFlags.NoSignedZeros
      | IrFastMathFlags.ApproxFunc);
    if (IsRsqrtSqrt(call))
      applicable |= flags & IrFastMathFlags.AllowContract;
    return applicable;
  }
}
