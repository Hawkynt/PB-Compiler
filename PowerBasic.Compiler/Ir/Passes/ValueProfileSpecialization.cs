namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0270 — profile-guided specialization of hot procedure arguments.
///
/// <para>
/// A value profile is deliberately an INPUT to this pass. O0268 owns profile collection and stable
/// profile identities; until that infrastructure exists, callers can feed histograms directly without
/// teaching the IR a temporary profile format that would have to be removed later.
/// </para>
/// <para>
/// For every sufficiently hot profiled value the pass clones the callee, binds the selected formal
/// parameter to that constant while cloning, and guards direct call sites with an exact value test.
/// The original call is the fallback, so indirect calls, external callers and profile misses retain
/// the unspecialized semantics. The clone keeps the original signature to avoid inventing a second ABI.
/// </para>
/// </summary>
public static class ValueProfileSpecialization {

  /// <summary>One observed value and its execution count.</summary>
  public readonly record struct ValueCount(IrConstant Value, long Count);

  /// <summary>A value histogram for one zero-based formal argument.</summary>
  public sealed record ArgumentProfile(
    IrFunction Function,
    int ArgumentIndex,
    long TotalCount,
    IReadOnlyList<ValueCount> Values
  );

  /// <summary>The profitability/code-growth policy for specialization.</summary>
  public sealed record Options {
    public static Options Default { get; } = new();

    /// <summary>The minimum fraction of executions on which one profiled value must occur.</summary>
    public double MinimumShare { get; init; } = 0.90;

    /// <summary>The largest callee body that may be cloned once.</summary>
    public int MaxCloneInstructions { get; init; } = 64;

    /// <summary>The maximum number of distinct clones materialized for one source function.</summary>
    public int MaxSpecializationsPerFunction { get; init; } = 2;
  }

  private sealed record Candidate(
    IrFunction Function,
    int ArgumentIndex,
    IrConstant Value,
    long Count,
    long TotalCount
  ) {
    public double Share => (double)this.Count / this.TotalCount;
  }

  /// <summary>Specializes from <paramref name="profiles"/> using the conservative default policy.</summary>
  public static int Run(IrModule module, IEnumerable<ArgumentProfile> profiles)
    => Run(module, profiles, Options.Default);

  /// <summary>
  /// Specializes hot profiled argument values and returns the number of specialized function clones created.
  /// </summary>
  public static int Run(IrModule module, IEnumerable<ArgumentProfile> profiles, Options options) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(profiles);
    ArgumentNullException.ThrowIfNull(options);
    Validate(options);

    // Only calls that existed when the pass began are candidates. A second specialization may wrap the
    // fallback left by the first one, but calls inside newly materialized clones must not recursively
    // specialize themselves during this sweep.
    var originalFunctions = module.Functions.ToHashSet<IrFunction>(ReferenceEqualityComparer.Instance);
    var candidates = CollectCandidates(module, profiles, options)
      .OrderByDescending(candidate => candidate.Share)
      .ThenByDescending(candidate => candidate.Count)
      .ToList();

    var perFunction = new Dictionary<IrFunction, int>(ReferenceEqualityComparer.Instance);
    var created = 0;
    var ordinal = 0;

    foreach (var candidate in candidates) {
      var source = candidate.Function;
      var sourceCount = perFunction.GetValueOrDefault(source);
      if (sourceCount >= options.MaxSpecializationsPerFunction)
        continue;

      var calls = DirectCallsTo(source, originalFunctions, candidate.ArgumentIndex).ToList();
      if (calls.Count == 0)
        continue;

      var specialized = CloneSpecialized(module, source, candidate.ArgumentIndex, candidate.Value, ordinal++);
      var rewritten = 0;
      foreach (var call in calls)
        if (RewriteCall(call, specialized, candidate.ArgumentIndex, candidate.Value, ordinal++))
          ++rewritten;

      if (rewritten == 0) {
        module.RemoveFunction(specialized);
        continue;
      }

      perFunction[source] = sourceCount + 1;
      ++created;
    }

    return created;
  }

  private static void Validate(Options options) {
    if (double.IsNaN(options.MinimumShare) || options.MinimumShare <= 0 || options.MinimumShare > 1)
      throw new ArgumentOutOfRangeException(nameof(options), "minimum share must be in (0, 1]");
    if (options.MaxCloneInstructions <= 0)
      throw new ArgumentOutOfRangeException(nameof(options), "clone instruction budget must be positive");
    if (options.MaxSpecializationsPerFunction <= 0)
      throw new ArgumentOutOfRangeException(nameof(options), "specialization count must be positive");
  }

  private static IEnumerable<Candidate> CollectCandidates(
      IrModule module, IEnumerable<ArgumentProfile> profiles, Options options) {
    foreach (var profile in profiles) {
      var function = profile.Function;
      if (!module.Functions.Contains(function)
          || function.IsDeclaration
          || function.HasErrorHandler
          || function.HasInlineAsm
          || profile.ArgumentIndex < 0
          || profile.ArgumentIndex >= function.Parameters.Count
          || profile.TotalCount <= 0
          || profile.Values is null
          || function.AllInstructions.Count() > options.MaxCloneInstructions)
        continue;

      var parameter = function.Parameters[profile.ArgumentIndex];
      foreach (var observed in profile.Values) {
        if (observed.Value is null
            || observed.Count <= 0
            || observed.Count > profile.TotalCount
            || !Equals(observed.Value.Type, parameter.Type)
            || !CanGuard(observed.Value)
            || (double)observed.Count / profile.TotalCount < options.MinimumShare)
          continue;

        yield return new Candidate(
          function,
          profile.ArgumentIndex,
          observed.Value,
          observed.Count,
          profile.TotalCount
        );
      }
    }
  }

  private static bool CanGuard(IrConstant value) => value switch {
    IrConstantInt => true,
    IrNullPtr => true,
    IrConstantFloat { Type: { IsIeeeFloat: true, Bits: 32 or 64 } } => true,
    _ => false,
  };

  private static IEnumerable<IrCall> DirectCallsTo(
      IrFunction function, HashSet<IrFunction> originalFunctions, int argumentIndex) {
    foreach (var call in function.Users.OfType<IrCall>().ToList()) {
      if (!ReferenceEquals(call.Callee, function)
          || call.Parent?.Parent is not { } caller
          || !originalFunctions.Contains(caller)
          || caller.HasErrorHandler
          || caller.HasInlineAsm
          || argumentIndex >= call.ArgCount
          || call.GetOperand(argumentIndex + 1) is IrUndef)
        continue;
      yield return call;
    }
  }

  private static IrFunction CloneSpecialized(
      IrModule module, IrFunction source, int argumentIndex, IrConstant value, int ordinal) {
    var parameters = source.Parameters
      .Select(parameter => new IrArgument(parameter.Type, parameter.Index, parameter.Name))
      .ToArray();
    var name = UniqueName(module, $"{source.Name}__vp{argumentIndex}_{ordinal}");
    var clone = new IrFunction(name, source.ReturnType, parameters) {
      IsVarArgs = source.IsVarArgs,
      NoInline = source.NoInline,
    };

    var seed = new Dictionary<IrValue, IrValue>(ReferenceEqualityComparer.Instance);
    for (var i = 0; i < source.Parameters.Count; ++i)
      seed[source.Parameters[i]] = i == argumentIndex ? value : parameters[i];

    IrCloner.Clone(clone, source.Blocks, seed, "");
    module.AddFunction(clone);
    return clone;
  }

  private static string UniqueName(IrModule module, string basis) {
    if (module.FindFunction(basis) is null)
      return basis;
    for (var suffix = 1;; ++suffix) {
      var candidate = $"{basis}_{suffix}";
      if (module.FindFunction(candidate) is null)
        return candidate;
    }
  }

  private static bool RewriteCall(
      IrCall call, IrFunction specialized, int argumentIndex, IrConstant value, int ordinal) {
    if (call.Parent is not { Parent: { } caller } host || host.Terminator is null)
      return false;

    var argument = call.GetOperand(argumentIndex + 1);
    if (!Equals(argument.Type, value.Type))
      return false;

    var callIndex = host.Instructions.TakeWhile(instruction => !ReferenceEquals(instruction, call)).Count();
    if (callIndex >= host.Instructions.Count - 1)
      return false;

    var continuation = caller.CreateBlock($"vp{ordinal}.cont");
    var specializedBlock = caller.CreateBlock($"vp{ordinal}.specialized");
    var fallbackBlock = caller.CreateBlock($"vp{ordinal}.fallback");

    var after = host.Instructions.Skip(callIndex + 1).ToList();
    foreach (var instruction in after) {
      host.Remove(instruction);
      continuation.Append(instruction);
    }
    foreach (var successor in continuation.Successors)
      foreach (var phi in successor.Phis)
        phi.RenameIncomingBlock(host, continuation);

    host.Remove(call);
    var guard = AppendExactGuard(host, argument, value);
    host.Append(new IrCondBr(guard, specializedBlock, fallbackBlock));

    var specializedArguments = call.Args.ToArray();
    specializedArguments[argumentIndex] = value;
    var specializedCall = specializedBlock.Append(
      new IrCall(call.Type, specialized, specializedArguments, call.Convention) {
        FastMathFlags = call.FastMathFlags,
      }
    );
    specializedBlock.Append(new IrBr(continuation));

    fallbackBlock.Append(call);
    fallbackBlock.Append(new IrBr(continuation));

    if (!call.Type.IsVoid && !call.HasNoUsers) {
      var result = continuation.AppendPhi(new IrPhi(call.Type));
      call.ReplaceAllUsesWith(result);
      result.AddIncoming(specializedCall, specializedBlock);
      result.AddIncoming(call, fallbackBlock);
    }

    return true;
  }

  private static IrValue AppendExactGuard(IrBasicBlock block, IrValue argument, IrConstant value) {
    if (value is not IrConstantFloat floating)
      return block.Append(new IrCmp(IrCmpPred.Eq, argument, value));

    var bitsType = floating.Type.Bits == 32 ? IrType.U32 : IrType.U64;
    var bits = floating.Type.Bits == 32
      ? BitConverter.SingleToInt32Bits((float)floating.Value)
      : BitConverter.DoubleToInt64Bits(floating.Value);
    var asBits = block.Append(new IrCast(IrCastOp.BitCast, argument, bitsType));
    return block.Append(new IrCmp(IrCmpPred.Eq, asBits, new IrConstantInt(bitsType, bits)));
  }
}
