namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0283 — context-sensitive cloning by caller identity.
///
/// <para>
/// Interprocedural analyses normally join facts from every call site. That is correct but imprecise:
/// one caller passing an unknown value destroys a constant fact another caller supplies on every call.
/// This pass preserves the sharper context by cloning the callee for caller groups whose direct calls
/// agree on useful compile-time argument values, substituting those values into the clone, and rebinding
/// only that caller's calls. The original body remains the general entry for every other or unknown
/// caller, so address-taken/exported procedures do not require a closed-world assumption.
/// </para>
/// <para>
/// Code growth is deliberately bounded. At most three clones are kept per source function and at most
/// 1024 cloned IR instructions across the module. Without profile edge counts the static priority is
/// intentionally simple and deterministic: contexts with more proven arguments and more direct call
/// sites win. O0268/O0269 can replace that ranking with measured hotness without changing legality.
/// </para>
/// </summary>
public static class ContextSensitiveCloning {

  private const int _MAX_CLONES_PER_FUNCTION = 3;
  private const int _MAX_CLONED_INSTRUCTIONS_PER_MODULE = 1024;
  private const string _CLONE_MARKER = "__o0283_ctx";

  private sealed record CallerGroup(int Order, IrFunction Caller, List<IrCall> Calls);

  private sealed record Candidate(CallerGroup Group, Dictionary<int, IrValue> Facts) {
    public int Score => this.Group.Calls.Count * this.Facts.Count;
  }

  /// <summary>
  /// Clones profitable caller contexts in <paramref name="module"/> and returns the number of clones
  /// created. Calls keep their original signature and calling convention; only their callee operand is
  /// rebound.
  /// </summary>
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);

    var remainingInstructions = _MAX_CLONED_INSTRUCTIONS_PER_MODULE
      - module.Functions.Where(IsGeneratedClone).Sum(function => function.AllInstructions.Count());
    if (remainingInstructions <= 0)
      return 0;

    var cloned = 0;
    foreach (var source in module.Functions.Where(IsEligibleSource).ToList()) {
      var bodySize = source.AllInstructions.Count();
      if (bodySize == 0 || bodySize > remainingInstructions)
        continue;

      var groups = FindCallerGroups(module, source);
      if (groups.Count < 2)
        continue;

      var allCalls = groups.SelectMany(group => group.Calls).ToList();
      var candidates = groups
        .Where(group => !ReferenceEquals(group.Caller, source) && !IsGeneratedClone(group.Caller))
        .Select(group => new Candidate(group, FindCallerFacts(source, group.Calls, allCalls)))
        .Where(candidate => candidate.Facts.Count > 0)
        .OrderByDescending(candidate => candidate.Score)
        .ThenBy(candidate => candidate.Group.Order)
        .ToList();
      if (candidates.Count == 0)
        continue;

      var cloneSlots = _MAX_CLONES_PER_FUNCTION - CountExistingClones(module, source);
      if (cloneSlots <= 0)
        continue;

      // When every visible caller has a distinct useful context, leaving the least-profitable one on
      // the original saves one whole body. If any caller is already general, the original is its body
      // and every profitable context may consume a clone slot.
      var contextsToClone = candidates.Count == groups.Count ? candidates.Count - 1 : candidates.Count;
      contextsToClone = Math.Min(contextsToClone, cloneSlots);
      if (contextsToClone <= 0)
        continue;

      foreach (var candidate in candidates.Take(contextsToClone)) {
        if (bodySize > remainingInstructions)
          break;

        var clone = CloneForContext(module, source, candidate.Facts);
        foreach (var call in candidate.Group.Calls)
          call.SetOperand(0, clone);

        remainingInstructions -= bodySize;
        ++cloned;
      }
    }

    return cloned;
  }

  private static bool IsEligibleSource(IrFunction function)
    => !function.IsDeclaration
       && !function.IsVarArgs
       && !function.NoInline
       && !function.HasErrorHandler
       && !function.HasInlineAsm
       && !IsGeneratedClone(function);

  /// <summary>Whether a function was synthesized by O0283 rather than present in the source module.</summary>
  internal static bool IsGeneratedClone(IrFunction function)
    => function.Name.Contains(_CLONE_MARKER, StringComparison.Ordinal);

  /// <summary>The original source function an O0283 clone was copied from, or null for any other function.</summary>
  internal static IrFunction? SourceOfGeneratedClone(IrModule module, IrFunction clone) {
    if (!IsGeneratedClone(clone))
      return null;
    var marker = clone.Name.IndexOf(_CLONE_MARKER, StringComparison.Ordinal);
    if (marker <= 0)
      return null;
    var sourceName = clone.Name[..marker];
    return module.Functions.FirstOrDefault(function => !IsGeneratedClone(function)
      && function.Name.Equals(sourceName, StringComparison.Ordinal));
  }

  private static int CountExistingClones(IrModule module, IrFunction source) {
    var prefix = source.Name + _CLONE_MARKER;
    return module.Functions.Count(function => function.Name.StartsWith(prefix, StringComparison.Ordinal));
  }

  private static List<CallerGroup> FindCallerGroups(IrModule module, IrFunction callee) {
    var result = new List<CallerGroup>();
    for (var order = 0; order < module.Functions.Count; ++order) {
      var caller = module.Functions[order];
      if (caller.IsDeclaration)
        continue;
      var calls = caller.AllInstructions.OfType<IrCall>()
        .Where(call => ReferenceEquals(call.Callee, callee))
        .ToList();
      if (calls.Count > 0)
        result.Add(new CallerGroup(order, caller, calls));
    }
    return result;
  }

  private static Dictionary<int, IrValue> FindCallerFacts(
      IrFunction callee, IReadOnlyList<IrCall> callerCalls, IReadOnlyList<IrCall> allCalls) {
    var result = new Dictionary<int, IrValue>();

    for (var parameterIndex = 0; parameterIndex < callee.Parameters.Count; ++parameterIndex) {
      IrValue? agreed = null;
      foreach (var call in callerCalls) {
        if (parameterIndex >= call.ArgCount) {
          agreed = null;
          break;
        }

        var argument = call.GetOperand(parameterIndex + 1);
        if (!Equals(argument.Type, callee.Parameters[parameterIndex].Type)
            || !IsContextValue(argument)
            || (agreed is not null && !SameContextValue(agreed, argument))) {
          agreed = null;
          break;
        }
        agreed ??= argument;
      }

      if (agreed is null)
        continue;

      // A fact shared by every caller is not caller context. IpConstantProp can propagate it without
      // duplicating a body, so cloning for it would spend size for no additional precision.
      if (allCalls.All(call => parameterIndex < call.ArgCount
          && SameContextValue(agreed, call.GetOperand(parameterIndex + 1))))
        continue;

      result[parameterIndex] = agreed;
    }

    return result;
  }

  private static bool IsContextValue(IrValue value)
    => value is IrConstantInt or IrConstantFloat or IrNullPtr or IrGlobalValue;

  private static bool SameContextValue(IrValue left, IrValue right) {
    if (!Equals(left.Type, right.Type))
      return false;

    return (left, right) switch {
      (IrConstantInt x, IrConstantInt y) => x.ZeroExtended == y.ZeroExtended,
      (IrConstantFloat x, IrConstantFloat y)
        => BitConverter.DoubleToInt64Bits(x.Value) == BitConverter.DoubleToInt64Bits(y.Value),
      (IrNullPtr, IrNullPtr) => true,
      (IrGlobalValue x, IrGlobalValue y) => ReferenceEquals(x, y),
      _ => false,
    };
  }

  private static IrFunction CloneForContext(
      IrModule module, IrFunction source, IReadOnlyDictionary<int, IrValue> facts) {
    var parameters = source.Parameters
      .Select(parameter => new IrArgument(parameter.Type, parameter.Index, parameter.Name))
      .ToList();
    var clone = module.AddFunction(new IrFunction(NextCloneName(module, source), source.ReturnType, parameters) {
      Convention = source.Convention,
      IsVarArgs = source.IsVarArgs,
      NoInline = source.NoInline,
    });
    clone.HasErrorHandler = source.HasErrorHandler;
    clone.HasInlineAsm = source.HasInlineAsm;

    var seed = new Dictionary<IrValue, IrValue>(ReferenceEqualityComparer.Instance);
    for (var i = 0; i < source.Parameters.Count; ++i)
      seed[source.Parameters[i]] = facts.TryGetValue(i, out var fact) ? fact : clone.Parameters[i];

    // Deliberately do not map source -> clone. A recursive call may pass a different argument and has
    // not proved the entry facts of this clone; keeping recursion on the general body is conservative.
    IrCloner.Clone(clone, source.Blocks, seed, "");
    return clone;
  }

  private static string NextCloneName(IrModule module, IrFunction source) {
    for (var ordinal = 1; ; ++ordinal) {
      var candidate = $"{source.Name}{_CLONE_MARKER}{ordinal}";
      if (module.FindFunction(candidate) is null)
        return candidate;
    }
  }
}
