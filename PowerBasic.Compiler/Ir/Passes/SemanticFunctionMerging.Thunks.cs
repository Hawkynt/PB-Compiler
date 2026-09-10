namespace PowerBasic.Compiler.Ir.Passes;

public static partial class SemanticFunctionMerging {

  /// <summary>
  /// The ABI-preserving form of O0284. Source-visible functions keep their original signature and
  /// become tiny thunks; a private helper owns the parameterized shared body. This is the form a
  /// hybrid native backend can use without requiring every possible caller to participate in an ABI
  /// rewrite.
  /// </summary>
  public sealed record EntryThunkMergeResult(int MergedBodies, IReadOnlyList<IrFunction> Helpers);

  /// <summary>
  /// Merges structurally congruent functions behind ABI-preserving entry thunks. Unlike <see cref="Run"/>,
  /// this mode does not require all uses to be visible because no source-visible signature changes.
  /// <paramref name="candidateFilter"/> lets a backend restrict the transform to functions it can own.
  /// </summary>
  public static EntryThunkMergeResult RunWithEntryThunks(
      IrModule module,
      Func<IrFunction, bool>? candidateFilter = null,
      int minimumBodyInstructions = _DEFAULT_MINIMUM_BODY_INSTRUCTIONS,
      bool allowCallTargetDifferences = true,
      Func<IrFunction, bool>? callTargetFilter = null) {
    ArgumentOutOfRangeException.ThrowIfNegative(minimumBodyInstructions);

    var helpers = new List<IrFunction>();
    var merged = 0;
    while (FindBestThunkPlan(module, candidateFilter, minimumBodyInstructions, allowCallTargetDifferences, callTargetFilter) is { } plan) {
      helpers.Add(ApplyWithEntryThunks(module, plan, helpers.Count));
      merged += plan.Variants.Count;
    }
    return new EntryThunkMergeResult(merged, helpers);
  }

  private static MergePlan? FindBestThunkPlan(
      IrModule module,
      Func<IrFunction, bool>? candidateFilter,
      int minimumBodyInstructions,
      bool allowCallTargetDifferences,
      Func<IrFunction, bool>? callTargetFilter) {
    var candidates = module.Functions
      .Where(fn => IsThunkCandidate(fn) && (candidateFilter?.Invoke(fn) ?? true))
      .ToList();
    MergePlan? best = null;

    for (var i = 0; i < candidates.Count; ++i) {
      var representative = candidates[i];
      var byLocation = new Dictionary<DifferenceLocation, List<Variant>>();

      for (var j = i + 1; j < candidates.Count; ++j) {
        var candidate = candidates[j];
        if (!TryFindSingleDifference(representative, candidate, out var difference))
          continue;
        if (IsCallTargetDifference(representative, difference.Location)) {
          if (!allowCallTargetDifferences)
            continue;
          if (callTargetFilter is not null
              && (difference.RepresentativeValue is not IrFunction representativeTarget
                  || difference.CandidateValue is not IrFunction candidateTarget
                  || !callTargetFilter(representativeTarget)
                  || !callTargetFilter(candidateTarget)))
            continue;
        }

        if (!byLocation.TryGetValue(difference.Location, out var variants))
          byLocation[difference.Location] = variants = [];
        variants.Add(new Variant(candidate, difference.CandidateValue));
      }

      foreach (var (location, variants) in byLocation) {
        if (variants.Count == 0
            || !TryFindSingleDifference(representative, variants[0].Function, out var first))
          continue;

        var bodyInstructions = representative.AllInstructions.Count();
        if (bodyInstructions < minimumBodyInstructions)
          continue;

        // N original bodies become one body plus N two-instruction thunks. Charge another operation
        // per thunk for materializing/passing the context and one access in the helper. It is an IR
        // estimate, intentionally biased toward declining marginal native merges.
        var thunkCount = variants.Count + 1L;
        var saving = bodyInstructions * (long)variants.Count - 3L * thunkCount - 1L;
        if (saving <= 0 || best is not null && best.EstimatedSaving >= saving)
          continue;

        best = new MergePlan(representative, location, first.RepresentativeValue, [.. variants], saving);
      }
    }

    return best;
  }

  private static bool IsThunkCandidate(IrFunction function) {
    if (function.Name.Equals("main", StringComparison.OrdinalIgnoreCase)
        || function.IsDeclaration || function.HasErrorHandler || function.HasInlineAsm
        || function.IsVarArgs || function.NoInline)
      return false;
    return !function.AllInstructions.SelectMany(instruction => instruction.Operands)
      .Any(operand => operand is IrBlockAddress);
  }

  private static bool IsCallTargetDifference(IrFunction function, DifferenceLocation location)
    => location.Operand == 0
       && function.Blocks[location.Block].Instructions[location.Instruction] is IrCall;

  private static IrFunction ApplyWithEntryThunks(IrModule module, MergePlan plan, int ordinal) {
    var representative = plan.Representative;
    var helperParameters = representative.Parameters
      .Select(parameter => new IrArgument(parameter.Type, parameter.Index, parameter.Name))
      .ToList();
    var context = new IrArgument(plan.RepresentativeValue.Type, helperParameters.Count, "merge.context");
    helperParameters.Add(context);

    var helper = module.AddFunction(new IrFunction(
      UniqueHelperName(module, representative.Name, ordinal), representative.ReturnType, helperParameters) {
      NoInline = true,
    });

    var seed = new Dictionary<IrValue, IrValue>(ReferenceEqualityComparer.Instance) {
      [representative] = helper,
    };
    for (var i = 0; i < representative.Parameters.Count; ++i)
      seed[representative.Parameters[i]] = helper.Parameters[i];

    IrCloner.Clone(helper, representative.Blocks, seed, "merge.");
    var varyingInstruction = helper.Blocks[plan.Location.Block].Instructions[plan.Location.Instruction];
    varyingInstruction.SetOperand(plan.Location.Operand, context);

    // A cloned self-call now targets the helper, but it still has the source signature. Forward the
    // current context so recursion stays in the specialization selected by the entry thunk.
    foreach (var recursive in helper.AllInstructions.OfType<IrCall>()
               .Where(call => ReferenceEquals(call.Callee, helper)).ToList())
      Redirect(recursive, helper, context);

    RewriteAsThunk(representative, helper, plan.RepresentativeValue);
    foreach (var variant in plan.Variants)
      RewriteAsThunk(variant.Function, helper, variant.ContextValue);

    return helper;
  }

  private static void RewriteAsThunk(IrFunction function, IrFunction helper, IrValue context) {
    function.ClearBody();
    var entry = function.CreateBlock("entry");
    var args = function.Parameters.Cast<IrValue>().Append(context).ToList();
    var call = entry.Append(new IrCall(function.ReturnType, helper, args, IrCallConvention.Basic));
    entry.Append(function.ReturnType.IsVoid ? new IrRet() : new IrRet(call));
  }

  private static string UniqueHelperName(IrModule module, string representativeName, int ordinal) {
    var stem = $"__o0284_{representativeName}_merge";
    var name = ordinal == 0 ? stem : $"{stem}_{ordinal}";
    for (var suffix = ordinal + 1; module.FindFunction(name) is not null; ++suffix)
      name = $"{stem}_{suffix}";
    return name;
  }
}
