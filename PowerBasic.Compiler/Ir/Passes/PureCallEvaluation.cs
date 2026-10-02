namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0025 - a call to a pure integer FUNCTION with constant arguments is answered at compile time.
/// <c>PRINT Fact%(7)</c> prints 5040 without a single call: the pass interprets the callee's IR on the
/// constants and replaces the call with what it returns.
///
/// <para>
/// A function qualifies when its body does nothing but integer arithmetic, compares, casts, selects,
/// phis and branches, and calls functions that qualify too - itself included, so recursion is fine.
/// Anything else is a way to observe the program: memory, the runtime, an error handler, inline
/// assembly, a float. A function declared <c>NOINLINE</c> keeps every call, as the inliner keeps it:
/// the source asked for a real procedure. Overflow checking is not an exception to rule out separately: where the dialect
/// checks, the lowering spells the check as a branch to the runtime's error raise, which is a call to
/// something that does not qualify.
/// </para>
/// <para>
/// Every operation is evaluated by <see cref="IrConstFold"/>, the same folder the rest of the middle
/// end uses, so the answer is the one the compiled code would give - wrapping included. Whatever it
/// refuses to fold (a division by zero) leaves the call where it was, as does running past the step
/// or recursion budget: a program that would not finish at run time does not get to hang the
/// compiler either.
/// </para>
/// </summary>
public static class PureCallEvaluation {

  private const int _STEP_BUDGET = 200_000;
  private const int _DEPTH_BUDGET = 256;

  /// <summary>Folds every constant-argument call to a pure function; the number folded.</summary>
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    var pure = PureFunctions(module);
    if (pure.Count == 0)
      return 0;
    var folded = 0;
    foreach (var function in module.Functions.Where(function => !function.IsDeclaration))
      foreach (var call in function.AllInstructions.OfType<IrCall>().ToList()) {
        if (call.Parent is null || call.Callee is not IrFunction callee || !pure.Contains(callee) || callee.NoInline
            || !call.Args.All(argument => argument is IrConstantInt))
          continue;
        var steps = _STEP_BUDGET;
        var arguments = call.Args.Cast<IrConstantInt>().ToArray();
        if (Evaluate(callee, arguments, ref steps, depth: 0) is not { } answer)
          continue;
        call.ReplaceAllUsesWith(answer);
        call.EraseFromParent();
        ++folded;
      }
    return folded;
  }

  /// <summary>The functions whose every instruction is pure integer work or a call to one of them.</summary>
  private static HashSet<IrFunction> PureFunctions(IrModule module) {
    var pure = module.Functions.Where(IsLocallyPure).ToHashSet();
    for (var changed = true; changed;) {
      changed = false;
      foreach (var function in pure.ToList())
        if (function.AllInstructions.OfType<IrCall>().Any(call => call.Callee is not IrFunction callee || !pure.Contains(callee))) {
          pure.Remove(function);
          changed = true;
        }
    }
    return pure;
  }

  private static bool IsLocallyPure(IrFunction function)
    => !function.IsDeclaration && !function.HasErrorHandler && !function.HasInlineAsm
       && function.ReturnType.IsInteger
       && function.Parameters.All(parameter => parameter.Type.IsInteger)
       && function.AllInstructions.All(IsPureInstruction);

  private static bool IsPureInstruction(IrInstruction instruction) => instruction switch {
    IrBinary binary => binary.Type.IsInteger,
    IrCmp compare => compare.Lhs.Type.IsInteger,
    IrCast cast => cast.Type.IsInteger && cast.Value.Type.IsInteger,
    IrSelect select => select.Type.IsInteger,
    IrPhi phi => phi.Type.IsInteger,
    IrCall call => call.Type.IsInteger,
    IrBr or IrCondBr => true,
    IrRet ret => ret.HasValue,
    _ => false,
  };

  /// <summary>Runs <paramref name="function"/> on constant arguments; its answer, or null when it cannot be known.</summary>
  private static IrConstantInt? Evaluate(IrFunction function, IrConstantInt[] arguments, ref int steps, int depth) {
    if (depth > _DEPTH_BUDGET || arguments.Length != function.Parameters.Count)
      return null;
    var values = new Dictionary<IrValue, IrConstantInt>(ReferenceEqualityComparer.Instance);
    for (var i = 0; i < arguments.Length; ++i)
      values[function.Parameters[i]] = new IrConstantInt(function.Parameters[i].Type, arguments[i].Value);

    IrConstantInt? ValueOf(IrValue value)
      => value as IrConstantInt ?? values.GetValueOrDefault(value);

    var block = function.Entry;
    IrBasicBlock? previous = null;
    while (block is not null) {
      // a block's phis all read the edge they were entered by, before any of them is assigned
      var entering = block.Phis.Select(phi => (phi, value: previous is null ? null : ValueOf(phi.IncomingFrom(previous)!))).ToList();
      foreach (var (phi, value) in entering) {
        if (value is null)
          return null;
        values[phi] = value;
      }

      IrBasicBlock? next = null;
      foreach (var instruction in block.Instructions) {
        if (instruction is IrPhi)
          continue;
        if (--steps < 0)
          return null;
        switch (instruction) {
          case IrBinary binary when ValueOf(binary.Lhs) is { } left && ValueOf(binary.Rhs) is { } right:
            if (IrConstFold.TryFold(new IrBinary(binary.Op, left, right)) is not IrConstantInt sum)
              return null;
            values[binary] = sum;
            break;
          case IrCmp compare when ValueOf(compare.Lhs) is { } left && ValueOf(compare.Rhs) is { } right:
            if (IrConstFold.TryFold(new IrCmp(compare.Pred, left, right)) is not IrConstantInt truth)
              return null;
            values[compare] = truth;
            break;
          case IrCast cast when ValueOf(cast.Value) is { } source:
            if (IrConstFold.TryFold(new IrCast(cast.Op, source, cast.Type)) is not IrConstantInt converted)
              return null;
            values[cast] = converted;
            break;
          case IrSelect select when ValueOf(select.Condition) is { } condition:
            if (ValueOf(condition.Value != 0 ? select.IfTrue : select.IfFalse) is not { } chosen)
              return null;
            values[select] = chosen;
            break;
          case IrCall { Callee: IrFunction callee } call:
            var callArguments = new IrConstantInt[call.ArgCount];
            var index = 0;
            foreach (var argument in call.Args)
              if (ValueOf(argument) is { } known)
                callArguments[index++] = known;
              else
                return null;
            if (Evaluate(callee, callArguments, ref steps, depth + 1) is not { } result)
              return null;
            values[call] = new IrConstantInt(call.Type, result.Value);
            break;
          case IrBr branch:
            next = branch.Target;
            break;
          case IrCondBr conditional when ValueOf(conditional.Condition) is { } condition:
            next = condition.Value != 0 ? conditional.IfTrue : conditional.IfFalse;
            break;
          case IrRet ret:
            return ValueOf(ret.Value!) is { } answer ? new IrConstantInt(function.ReturnType, answer.Value) : null;
          default:
            return null;
        }
      }
      previous = block;
      block = next;
    }
    return null;
  }
}
