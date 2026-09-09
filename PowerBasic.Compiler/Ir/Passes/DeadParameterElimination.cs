namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0069 — removes dead parameters from owned procedure ABIs and, for a bounded dominant literal
/// call shape, clones a specialized callee whose constant parameters disappear entirely.
///
/// <para>
/// Both transforms are whole-program operations. They run only when every use of the function is a
/// direct call owned by this module; an escaped function address, the runtime entry point, varargs,
/// opaque inline assembly or non-local error handling makes the pass decline. A call whose signature
/// does not exactly match the current function is likewise left alone rather than guessed about.
/// </para>
/// <para>
/// Removing a call operand deliberately does not remove the IR that computed it. A side-effecting
/// expression therefore still executes in the same place and order, while ordinary DCE is free to
/// collect a pure producer whose value became unused. That is the distinction O0069 needs between
/// "do not pass this value" and "do not evaluate this expression".
/// </para>
/// <para>
/// Cloning is intentionally narrow. O0160 owns range/alignment/alias versioning; this pass recognizes
/// only a strict-majority literal shape and emits at most one clone per procedure. A target-neutral
/// size proxy requires the removed call operands to pay for the duplicated IR, and a hard instruction
/// cap prevents a small call-site win from multiplying a large body.
/// </para>
/// </summary>
public static class DeadParameterElimination {

  private const string _CLONE_MARKER = "$o0069$shape";
  private const int _MAX_CLONE_INSTRUCTIONS = 24;

  /// <summary>Rewrites every safe owned procedure in <paramref name="module"/>; returns a change count.</summary>
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);

    var changed = EliminateDeadParameters(module);
    changed += CloneDominantLiteralShapes(module);
    return changed;
  }

  /// <summary>
  /// Deadness can move up a forwarding chain: after <c>sink(x)</c> stops passing <c>x</c>, the caller's
  /// own <c>x</c> may become unused. Repeat until no ABI shrinks further.
  /// </summary>
  private static int EliminateDeadParameters(IrModule module) {
    var total = 0;
    for (var progress = true; progress;) {
      progress = false;
      foreach (var function in module.Functions.ToList()) {
        if (!CanRewrite(module, function))
          continue;

        var calls = CallsTo(function).ToList();
        if (calls.Count == 0 || calls.Any(call => !HasExactSignature(function, call)))
          continue;

        var dead = function.Parameters
          .Select((parameter, index) => (parameter, index))
          .Where(item => item.parameter.HasNoUsers)
          .Select(item => item.index)
          .OrderByDescending(index => index)
          .ToArray();
        if (dead.Length == 0)
          continue;

        foreach (var index in dead) {
          foreach (var call in calls)
            call.RemoveOperandAt(index + 1);
          function.RemoveParameterAt(index);
        }

        total += dead.Length;
        progress = true;
      }
    }
    return total;
  }

  private static int CloneDominantLiteralShapes(IrModule module) {
    var changed = 0;
    foreach (var function in module.Functions.ToList()) {
      if (!CanRewrite(module, function)
          || function.NoInline
          || function.Name.Contains(_CLONE_MARKER, StringComparison.Ordinal)
          || module.Functions.Any(candidate => !ReferenceEquals(candidate, function)
            && candidate.Name.StartsWith(function.Name + _CLONE_MARKER, StringComparison.Ordinal))
          || function.AllInstructions.Count() > _MAX_CLONE_INSTRUCTIONS)
        continue;

      var calls = CallsTo(function).ToList();
      if (calls.Count < 3 || calls.Any(call => !HasExactSignature(function, call)))
        continue;

      var dominant = DominantLiteralGroup(function, calls);
      if (dominant is null)
        continue;

      var specialized = CommonLiteralParameters(function, dominant.Calls);
      if (specialized.Count == 0)
        continue;

      // Target-neutral size proxy: every omitted call operand removes argument materialization/push
      // work, while every copied IR instruction costs code. Branch folding in the clone only improves
      // this estimate, so requiring the former to cover the latter is deliberately conservative.
      var removedCallOperands = dominant.Calls.Count * specialized.Count;
      if (removedCallOperands < function.AllInstructions.Count())
        continue;

      var seed = new Dictionary<IrValue, IrValue>(ReferenceEqualityComparer.Instance);
      var cloneParameters = new List<IrArgument>();
      for (var index = 0; index < function.Parameters.Count; ++index) {
        var parameter = function.Parameters[index];
        if (specialized.TryGetValue(index, out var literal)) {
          seed[parameter] = literal;
          continue;
        }

        var cloneParameter = new IrArgument(parameter.Type, cloneParameters.Count, parameter.Name);
        cloneParameters.Add(cloneParameter);
        seed[parameter] = cloneParameter;
      }

      var clone = module.AddFunction(new IrFunction(CloneName(module, function), function.ReturnType, cloneParameters));
      IrCloner.Clone(clone, function.Blocks, seed, "");

      var removedIndexes = specialized.Keys.OrderByDescending(index => index).ToArray();
      foreach (var call in dominant.Calls) {
        call.SetOperand(0, clone);
        foreach (var index in removedIndexes)
          call.RemoveOperandAt(index + 1);
      }

      changed += dominant.Calls.Count + 1;
    }
    return changed;
  }

  private static LiteralGroup? DominantLiteralGroup(IrFunction function, IReadOnlyList<IrCall> calls) {
    LiteralGroup? best = null;
    for (var parameterIndex = 0; parameterIndex < function.Parameters.Count; ++parameterIndex) {
      var groups = new List<LiteralGroup>();
      foreach (var call in calls) {
        var argument = call.GetOperand(parameterIndex + 1);
        if (!IsLiteral(argument))
          continue;

        var group = groups.FirstOrDefault(candidate => SameLiteral(candidate.Literal, argument));
        if (group is null) {
          group = new LiteralGroup(parameterIndex, argument);
          groups.Add(group);
        }
        group.Calls.Add(call);
      }

      foreach (var group in groups) {
        if (group.Calls.Count < 2
            || group.Calls.Count == calls.Count
            || group.Calls.Count * 2 <= calls.Count)
          continue;
        if (best is null
            || group.Calls.Count > best.Calls.Count
            || group.Calls.Count == best.Calls.Count && group.ParameterIndex < best.ParameterIndex)
          best = group;
      }
    }
    return best;
  }

  private static Dictionary<int, IrValue> CommonLiteralParameters(IrFunction function, IReadOnlyList<IrCall> calls) {
    var result = new Dictionary<int, IrValue>();
    for (var index = 0; index < function.Parameters.Count; ++index) {
      var literal = calls[0].GetOperand(index + 1);
      if (!IsLiteral(literal))
        continue;
      if (calls.All(call => SameLiteral(literal, call.GetOperand(index + 1))))
        result[index] = literal;
    }
    return result;
  }

  private static bool CanRewrite(IrModule module, IrFunction function) {
    if (function.IsDeclaration
        || function.IsVarArgs
        || function.HasErrorHandler
        || function.HasInlineAsm
        || function.Name.Equals("main", StringComparison.OrdinalIgnoreCase))
      return false;

    foreach (var user in function.Users) {
      if (user is not IrCall call || !ReferenceEquals(call.Callee, function))
        return false;
      if (call.Operands.Count(operand => ReferenceEquals(operand, function)) != 1)
        return false; // the same call also carries the function as data: its address escaped
      var caller = call.Parent?.Parent;
      if (caller is null || !module.Functions.Contains(caller))
        return false;
    }
    return true;
  }

  private static bool HasExactSignature(IrFunction function, IrCall call) {
    if (call.ArgCount != function.Parameters.Count || !Equals(call.Type, function.ReturnType))
      return false;
    for (var index = 0; index < function.Parameters.Count; ++index)
      if (!Equals(call.GetOperand(index + 1).Type, function.Parameters[index].Type))
        return false;
    return true;
  }

  private static IEnumerable<IrCall> CallsTo(IrFunction function)
    => function.Users.OfType<IrCall>().Where(call => ReferenceEquals(call.Callee, function));

  private static bool IsLiteral(IrValue value) => value is IrConstantInt or IrConstantFloat;

  private static bool SameLiteral(IrValue left, IrValue right) => (left, right) switch {
    (IrConstantInt a, IrConstantInt b) => a.Value == b.Value && Equals(a.Type, b.Type),
    (IrConstantFloat a, IrConstantFloat b) => a.Value.Equals(b.Value) && Equals(a.Type, b.Type),
    _ => false,
  };

  private static string CloneName(IrModule module, IrFunction function) {
    var stem = function.Name + _CLONE_MARKER;
    if (module.FindFunction(stem) is null)
      return stem;
    for (var ordinal = 2; ; ++ordinal) {
      var candidate = stem + ordinal;
      if (module.FindFunction(candidate) is null)
        return candidate;
    }
  }

  private sealed class LiteralGroup(int parameterIndex, IrValue literal) {
    public int ParameterIndex { get; } = parameterIndex;
    public IrValue Literal { get; } = literal;
    public List<IrCall> Calls { get; } = [];
  }
}
