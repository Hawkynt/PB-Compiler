namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0284 — merges structurally congruent internal functions that differ in exactly one materializable
/// operand by turning that operand into one extra parameter and redirecting every visible direct call.
///
/// <para>
/// This is deliberately a whole-module, SIZE-only transform. Changing a signature is sound only when
/// every use of the function is a direct call in this module: an escaped procedure address or an
/// externally supplied caller would still use the old ABI. <see cref="IsFullyVisible"/> is therefore
/// the same ownership fence used by interprocedural constant propagation, with the extra requirement
/// that the function has a caller outside itself so dead recursive islands are left to global DCE.
/// </para>
/// <para>
/// The structural comparison is conservative. Blocks and instructions must correspond positionally,
/// all instruction metadata and control-flow edges must agree, and exactly one operand may differ.
/// That varying operand must be a literal/global address; when it is a call target both alternatives
/// must be direct functions with the same signature. Constants, field offsets and call targets are
/// consequently covered without parameterizing values computed inside either body.
/// </para>
/// </summary>
public static partial class SemanticFunctionMerging {

  private const int _DEFAULT_MINIMUM_BODY_INSTRUCTIONS = 8;

  private readonly record struct DifferenceLocation(int Block, int Instruction, int Operand);
  private readonly record struct Difference(DifferenceLocation Location, IrValue RepresentativeValue, IrValue CandidateValue);
  private sealed record Variant(IrFunction Function, IrValue ContextValue);
  private sealed record MergePlan(
    IrFunction Representative,
    DifferenceLocation Location,
    IrValue RepresentativeValue,
    IReadOnlyList<Variant> Variants,
    long EstimatedSaving);

  /// <summary>Runs with the target-neutral minimum body-size gate used by the standard SIZE pipeline.</summary>
  public static int Run(IrModule module) => Run(module, _DEFAULT_MINIMUM_BODY_INSTRUCTIONS);

  /// <summary>
  /// Runs with an explicit minimum body size. The overload exists so focused tests and future target
  /// cost models can exercise the transformation independently of the conservative default threshold.
  /// Returns the number of duplicate function bodies removed.
  /// </summary>
  public static int Run(IrModule module, int minimumBodyInstructions) {
    ArgumentOutOfRangeException.ThrowIfNegative(minimumBodyInstructions);

    var merged = 0;
    while (FindBestPlan(module, minimumBodyInstructions) is { } plan) {
      Apply(module, plan);
      merged += plan.Variants.Count;
    }
    return merged;
  }

  private static MergePlan? FindBestPlan(IrModule module, int minimumBodyInstructions) {
    var candidates = module.Functions.Where(fn => IsCandidate(module, fn)).ToList();
    MergePlan? best = null;

    for (var i = 0; i < candidates.Count; ++i) {
      var representative = candidates[i];
      var byLocation = new Dictionary<DifferenceLocation, List<Variant>>();

      for (var j = i + 1; j < candidates.Count; ++j) {
        var candidate = candidates[j];
        if (!TryFindSingleDifference(representative, candidate, out var difference))
          continue;

        if (!byLocation.TryGetValue(difference.Location, out var variants))
          byLocation[difference.Location] = variants = [];
        variants.Add(new Variant(candidate, difference.CandidateValue));
      }

      foreach (var (location, variants) in byLocation) {
        if (variants.Count == 0)
          continue;
        if (!TryFindSingleDifference(representative, variants[0].Function, out var first))
          continue;

        var bodyInstructions = representative.AllInstructions.Count();
        if (bodyInstructions < minimumBodyInstructions)
          continue;

        // Target-neutral size estimate: removing each duplicate saves one complete body. The merged
        // form adds one argument setup at every call plus one context access in the survivor. It must
        // be strictly smaller before a target-specific cost model gets involved.
        var callCount = CallsTo(representative).Count()
          + variants.Sum(variant => CallsTo(variant.Function).Count());
        var saving = (long)bodyInstructions * variants.Count - callCount - 1L;
        if (saving <= 0 || best is not null && best.EstimatedSaving >= saving)
          continue;

        best = new MergePlan(representative, location, first.RepresentativeValue, [.. variants], saving);
      }
    }

    return best;
  }

  private static bool IsCandidate(IrModule module, IrFunction function) {
    if (function.IsDeclaration || function.HasErrorHandler || function.HasInlineAsm || function.IsVarArgs || function.NoInline)
      return false;
    if (!IsFullyVisible(module, function))
      return false;
    if (function.AllInstructions.SelectMany(instruction => instruction.Operands).Any(operand => operand is IrBlockAddress))
      return false;
    return CallsTo(function).Any(call => !ReferenceEquals(call.Parent?.Parent, function));
  }

  /// <summary>
  /// A signature-changing transform may touch a function only when the module owns every use. A use
  /// anywhere except the callee slot is an escaping address; a call from a detached/foreign owner is
  /// equally invisible to the rewrite.
  /// </summary>
  private static bool IsFullyVisible(IrModule module, IrFunction function) {
    if (function.Name.Equals("main", StringComparison.OrdinalIgnoreCase))
      return false;
    foreach (var user in function.Users)
      if (user is not IrCall call || !ReferenceEquals(call.Callee, function))
        return false;
    foreach (var user in function.Users) {
      var owner = user.Parent?.Parent;
      if (owner is null || !module.Functions.Contains(owner))
        return false;
    }
    return true;
  }

  private static IEnumerable<IrCall> CallsTo(IrFunction function)
    => function.Users.OfType<IrCall>().Where(call => ReferenceEquals(call.Callee, function));

  private static bool TryFindSingleDifference(IrFunction representative, IrFunction candidate, out Difference difference) {
    difference = default;
    if (!Equals(representative.ReturnType, candidate.ReturnType)
        || representative.Parameters.Count != candidate.Parameters.Count
        || representative.Blocks.Count != candidate.Blocks.Count)
      return false;

    var map = new Dictionary<IrValue, IrValue>(ReferenceEqualityComparer.Instance) {
      [representative] = candidate,
    };

    for (var i = 0; i < representative.Parameters.Count; ++i) {
      var left = representative.Parameters[i];
      var right = candidate.Parameters[i];
      if (!Equals(left.Type, right.Type))
        return false;
      map[left] = right;
    }

    for (var blockIndex = 0; blockIndex < representative.Blocks.Count; ++blockIndex) {
      var left = representative.Blocks[blockIndex];
      var right = candidate.Blocks[blockIndex];
      if (left.Instructions.Count != right.Instructions.Count)
        return false;
      map[left] = right;
      for (var instructionIndex = 0; instructionIndex < left.Instructions.Count; ++instructionIndex)
        map[left.Instructions[instructionIndex]] = right.Instructions[instructionIndex];
    }

    var differences = 0;
    for (var blockIndex = 0; blockIndex < representative.Blocks.Count; ++blockIndex) {
      var leftBlock = representative.Blocks[blockIndex];
      var rightBlock = candidate.Blocks[blockIndex];
      for (var instructionIndex = 0; instructionIndex < leftBlock.Instructions.Count; ++instructionIndex) {
        var left = leftBlock.Instructions[instructionIndex];
        var right = rightBlock.Instructions[instructionIndex];
        if (!SameInstructionShape(left, right, map) || left.Operands.Count != right.Operands.Count)
          return false;

        for (var operandIndex = 0; operandIndex < left.Operands.Count; ++operandIndex) {
          var leftOperand = left.GetOperand(operandIndex);
          var rightOperand = right.GetOperand(operandIndex);
          if (SameOperand(leftOperand, rightOperand, map))
            continue;
          if (++differences > 1 || !CanParameterize(left, operandIndex, leftOperand, rightOperand))
            return false;
          difference = new Difference(
            new DifferenceLocation(blockIndex, instructionIndex, operandIndex),
            leftOperand,
            rightOperand);
        }
      }
    }

    return differences == 1;
  }

  private static bool SameInstructionShape(
      IrInstruction left, IrInstruction right, IReadOnlyDictionary<IrValue, IrValue> map) {
    if (left.GetType() != right.GetType() || !Equals(left.Type, right.Type) || left.FastMathFlags != right.FastMathFlags)
      return false;

    return (left, right) switch {
      (IrBinary x, IrBinary y) => x.Op == y.Op,
      (IrCmp x, IrCmp y) => x.Pred == y.Pred && x.IsSourceCondition == y.IsSourceCondition,
      (IrCast x, IrCast y) => x.Op == y.Op,
      (IrAlloca x, IrAlloca y) => Equals(x.Allocated, y.Allocated) && x.Count == y.Count && x.IsSourceVariable == y.IsSourceVariable,
      (IrLoad, IrLoad) => true,
      (IrStore, IrStore) => true,
      (IrGep x, IrGep y) => Equals(x.ElementType, y.ElementType),
      (IrFarPtr, IrFarPtr) => true,
      (IrPhi x, IrPhi y) => SameBlocks(x.IncomingBlocks, y.IncomingBlocks, map),
      (IrSelect, IrSelect) => true,
      (IrCall x, IrCall y) => x.Convention == y.Convention,
      (IrRet, IrRet) => true,
      (IrBr x, IrBr y) => SameBlock(x.Target, y.Target, map),
      (IrCondBr x, IrCondBr y) => SameBlock(x.IfTrue, y.IfTrue, map) && SameBlock(x.IfFalse, y.IfFalse, map),
      (IrSwitch x, IrSwitch y) => SameSwitch(x, y, map),
      (IrIndirectBr x, IrIndirectBr y) => SameBlocks(x.Targets, y.Targets, map),
      (IrUnreachable, IrUnreachable) => true,
      _ => false,
    };
  }

  private static bool SameSwitch(IrSwitch left, IrSwitch right, IReadOnlyDictionary<IrValue, IrValue> map) {
    if (!SameBlock(left.DefaultTarget, right.DefaultTarget, map) || left.Cases.Count != right.Cases.Count)
      return false;
    for (var i = 0; i < left.Cases.Count; ++i) {
      var x = left.Cases[i];
      var y = right.Cases[i];
      if (x.Value != y.Value || !SameBlock(x.Target, y.Target, map))
        return false;
    }
    return true;
  }

  private static bool SameBlocks(
      IReadOnlyList<IrBasicBlock> left, IReadOnlyList<IrBasicBlock> right, IReadOnlyDictionary<IrValue, IrValue> map) {
    if (left.Count != right.Count)
      return false;
    for (var i = 0; i < left.Count; ++i)
      if (!SameBlock(left[i], right[i], map))
        return false;
    return true;
  }

  private static bool SameBlock(IrBasicBlock left, IrBasicBlock right, IReadOnlyDictionary<IrValue, IrValue> map)
    => map.TryGetValue(left, out var mapped) && ReferenceEquals(mapped, right);

  private static bool SameOperand(IrValue left, IrValue right, IReadOnlyDictionary<IrValue, IrValue> map) {
    if (map.TryGetValue(left, out var mapped))
      return ReferenceEquals(mapped, right);

    return (left, right) switch {
      (IrConstantInt x, IrConstantInt y) => Equals(x.Type, y.Type) && x.ZeroExtended == y.ZeroExtended,
      (IrConstantFloat x, IrConstantFloat y) => Equals(x.Type, y.Type) && FloatBits(x) == FloatBits(y),
      (IrNullPtr x, IrNullPtr y) => Equals(x.Type, y.Type),
      (IrUndef x, IrUndef y) => Equals(x.Type, y.Type),
      (IrBlockAddress x, IrBlockAddress y) => SameBlock(x.Block, y.Block, map),
      _ => ReferenceEquals(left, right),
    };
  }

  private static long FloatBits(IrConstantFloat value)
    => value.Type.Bits == 32
      ? BitConverter.SingleToInt32Bits((float)value.Value)
      : BitConverter.DoubleToInt64Bits(value.Value);

  private static bool CanParameterize(IrInstruction instruction, int operandIndex, IrValue left, IrValue right) {
    if (!Equals(left.Type, right.Type))
      return false;
    if (instruction is IrCall && operandIndex == 0)
      return left is IrFunction leftTarget && right is IrFunction rightTarget && SameSignature(leftTarget, rightTarget);
    return IsContextValue(left) && IsContextValue(right);
  }

  private static bool IsContextValue(IrValue value)
    => value is IrConstantInt or IrConstantFloat or IrNullPtr or IrGlobalValue;

  private static bool SameSignature(IrFunction left, IrFunction right) {
    if (!Equals(left.ReturnType, right.ReturnType) || left.IsVarArgs != right.IsVarArgs
        || left.Parameters.Count != right.Parameters.Count)
      return false;
    for (var i = 0; i < left.Parameters.Count; ++i)
      if (!Equals(left.Parameters[i].Type, right.Parameters[i].Type))
        return false;
    return true;
  }

  private static void Apply(IrModule module, MergePlan plan) {
    var representative = plan.Representative;
    var duplicates = new HashSet<IrFunction>(ReferenceEqualityComparer.Instance);
    foreach (var variant in plan.Variants)
      duplicates.Add(variant.Function);
    var varyingInstruction = representative.Blocks[plan.Location.Block].Instructions[plan.Location.Instruction];
    var context = representative.AddParameter(new IrArgument(
      plan.RepresentativeValue.Type, representative.Parameters.Count, "merge.context"));
    varyingInstruction.SetOperand(plan.Location.Operand, context);

    // Calls from a duplicate body disappear with that body. A recursive call in the survivor is
    // different: it must forward the CURRENT context, otherwise entering through a former duplicate
    // would silently switch back to the representative's specialization on the second recursion.
    foreach (var call in CallsTo(representative).ToList()) {
      var owner = call.Parent?.Parent;
      if (owner is not null && duplicates.Contains(owner))
        continue;
      Redirect(call, representative, ReferenceEquals(owner, representative) ? context : plan.RepresentativeValue);
    }

    foreach (var variant in plan.Variants)
      foreach (var call in CallsTo(variant.Function).ToList()) {
        var owner = call.Parent?.Parent;
        if (owner is not null && duplicates.Contains(owner))
          continue;
        Redirect(call, representative, variant.ContextValue);
      }

    foreach (var duplicate in duplicates) {
      duplicate.ClearBody();
      module.RemoveFunction(duplicate);
    }
  }

  private static void Redirect(IrCall call, IrFunction target, IrValue context) {
    var block = call.Parent ?? throw new InvalidOperationException("a visible call must belong to a block");
    var args = call.Args.ToList();
    args.Add(context);
    var replacement = new IrCall(call.Type, target, args, call.Convention) {
      FastMathFlags = call.FastMathFlags,
      Name = call.Name,
    };
    block.InsertBefore(replacement, call);
    call.ReplaceAllUsesWith(replacement);
    call.EraseFromParent();
  }
}
