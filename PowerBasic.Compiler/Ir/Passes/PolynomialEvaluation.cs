namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0337 — rewrites profitable one-variable integer polynomials into Horner or Estrin form. Integer
/// arithmetic is exact modulo the IR bit width, so reassociation is always legal; floating-point
/// reassociation remains owned by O0344 and its explicit fast-math contract.
/// </summary>
public static class PolynomialEvaluation {

  private const int _MAX_DEGREE = 8;

  private abstract record Plan;
  private sealed record ConstantPlan(long Value) : Plan;
  private sealed record ValuePlan(IrValue Value) : Plan;
  private sealed record BinaryPlan(IrBinaryOp Op, Plan Lhs, Plan Rhs) : Plan;
  private readonly record struct PlanCost(int Multiplies, int Operations, int MultiplyDepth);

  /// <summary>Rewrites profitable integer polynomial roots; returns the number rewritten.</summary>
  public static int Run(IrFunction fn) {
    ArgumentNullException.ThrowIfNull(fn);
    if (fn.HasErrorHandler || fn.HasInlineAsm)
      return 0;

    var rewritten = 0;
    foreach (var instruction in fn.AllInstructions.ToList())
      if (instruction is IrBinary root && IsRoot(root) && TryRewrite(root))
        ++rewritten;
    return rewritten;
  }

  private static bool IsRoot(IrBinary root) {
    if (root.Type.Kind != IrTypeKind.Int || root.Op is not (IrBinaryOp.Add or IrBinaryOp.Sub or IrBinaryOp.Mul)
        || root.HasNoUsers)
      return false;
    return !root.Users.Any(user => user is IrBinary { Op: IrBinaryOp.Add or IrBinaryOp.Sub or IrBinaryOp.Mul } parent
                                   && parent.Type.SameStorage(root.Type));
  }

  private static bool TryRewrite(IrBinary root) {
    IrValue? variable = null;
    var region = new HashSet<IrInstruction>(ReferenceEqualityComparer.Instance);
    if (!TryRead(root, root.Type, ref variable, region, out var coefficients) || variable is null)
      return false;

    var degree = Degree(coefficients, root.Type);
    if (degree < 2)
      return false;

    var removable = RemovableRegion(root, region);
    var removableMultiplies = removable.Count(instruction => instruction is IrBinary { Op: IrBinaryOp.Mul });
    if (removableMultiplies == 0)
      return false;

    var variablePlan = new ValuePlan(variable);
    var coefficientPlans = coefficients.Take(degree + 1)
      .Select(value => (Plan)new ConstantPlan(Wrap(root.Type, value)))
      .ToList();
    var horner = BuildHorner(root.Type, variablePlan, coefficientPlans);
    var hornerCost = Cost(horner);
    var estrin = BuildEstrin(root.Type, variablePlan, coefficientPlans);
    var estrinCost = Cost(estrin);

    var selected = estrinCost.Multiplies <= hornerCost.Multiplies
                   && estrinCost.Operations <= hornerCost.Operations
                   && estrinCost.MultiplyDepth < hornerCost.MultiplyDepth
      ? estrin
      : horner;
    if (Cost(selected).Multiplies >= removableMultiplies)
      return false;

    var block = root.Parent;
    if (block is null)
      return false;

    var replacement = Emit(selected, root.Type, block, root,
      new Dictionary<Plan, IrValue>(ReferenceEqualityComparer.Instance));
    root.ReplaceAllUsesWith(replacement);
    root.EraseFromParent();
    return true;
  }

  private static HashSet<IrInstruction> RemovableRegion(IrBinary root, HashSet<IrInstruction> region) {
    var removable = new HashSet<IrInstruction>(ReferenceEqualityComparer.Instance) { root };
    bool changed;
    do {
      changed = false;
      foreach (var instruction in region)
        if (!removable.Contains(instruction) && instruction.Users.All(removable.Contains))
          changed |= removable.Add(instruction);
    } while (changed);
    return removable;
  }

  private static Plan BuildHorner(IrType type, Plan variable, IReadOnlyList<Plan> coefficients) {
    Plan result = coefficients[^1];
    for (var power = coefficients.Count - 2; power >= 0; --power)
      result = Add(type, Multiply(type, result, variable), coefficients[power]);
    return result;
  }

  private static Plan BuildEstrin(IrType type, Plan variable, IReadOnlyList<Plan> coefficients) {
    if (coefficients.Count == 1)
      return coefficients[0];

    var paired = new List<Plan>((coefficients.Count + 1) / 2);
    for (var i = 0; i < coefficients.Count; i += 2) {
      var pair = coefficients[i];
      if (i + 1 < coefficients.Count)
        pair = Add(type, pair, Multiply(type, coefficients[i + 1], variable));
      paired.Add(pair);
    }

    if (paired.Count == 1)
      return paired[0];
    return BuildEstrin(type, Multiply(type, variable, variable), paired);
  }

  private static Plan Add(IrType type, Plan lhs, Plan rhs) {
    if (lhs is ConstantPlan { Value: var left } && rhs is ConstantPlan { Value: var right })
      return new ConstantPlan(Wrap(type, unchecked(left + right)));
    if (IsPlanZero(lhs, type))
      return rhs;
    if (IsPlanZero(rhs, type))
      return lhs;
    return new BinaryPlan(IrBinaryOp.Add, lhs, rhs);
  }

  private static Plan Multiply(IrType type, Plan lhs, Plan rhs) {
    if (lhs is ConstantPlan { Value: var left } && rhs is ConstantPlan { Value: var right })
      return new ConstantPlan(Wrap(type, unchecked(left * right)));
    if (IsPlanZero(lhs, type) || IsPlanZero(rhs, type))
      return new ConstantPlan(0);
    if (IsPlanOne(lhs, type))
      return rhs;
    if (IsPlanOne(rhs, type))
      return lhs;
    return new BinaryPlan(IrBinaryOp.Mul, lhs, rhs);
  }

  private static PlanCost Cost(Plan root) {
    var seen = new HashSet<Plan>(ReferenceEqualityComparer.Instance);
    var multiplies = 0;
    var operations = 0;
    Visit(root);
    return new PlanCost(multiplies, operations,
      MultiplyDepth(root, new Dictionary<Plan, int>(ReferenceEqualityComparer.Instance)));

    void Visit(Plan plan) {
      if (!seen.Add(plan) || plan is not BinaryPlan binary)
        return;
      ++operations;
      if (binary.Op == IrBinaryOp.Mul)
        ++multiplies;
      Visit(binary.Lhs);
      Visit(binary.Rhs);
    }
  }

  private static int MultiplyDepth(Plan plan, Dictionary<Plan, int> cache) {
    if (cache.TryGetValue(plan, out var known))
      return known;
    if (plan is not BinaryPlan binary)
      return cache[plan] = 0;
    var childDepth = Math.Max(MultiplyDepth(binary.Lhs, cache), MultiplyDepth(binary.Rhs, cache));
    return cache[plan] = childDepth + (binary.Op == IrBinaryOp.Mul ? 1 : 0);
  }

  private static IrValue Emit(Plan plan, IrType type, IrBasicBlock block, IrInstruction anchor,
      Dictionary<Plan, IrValue> emitted) {
    if (emitted.TryGetValue(plan, out var existing))
      return existing;

    IrValue result = plan switch {
      ConstantPlan constant => new IrConstantInt(type, Wrap(type, constant.Value)),
      ValuePlan value => value.Value,
      BinaryPlan binary => block.InsertBefore(new IrBinary(binary.Op,
        Emit(binary.Lhs, type, block, anchor, emitted),
        Emit(binary.Rhs, type, block, anchor, emitted)), anchor),
      _ => throw new InvalidOperationException($"Unknown polynomial plan {plan.GetType().Name}"),
    };
    emitted[plan] = result;
    return result;
  }

  private static bool TryRead(IrValue value, IrType type, ref IrValue? variable,
      HashSet<IrInstruction> region, out long[] coefficients) {
    coefficients = new long[_MAX_DEGREE + 1];
    switch (value) {
      case IrConstantInt constant when constant.Type.SameStorage(type):
        coefficients[0] = Wrap(type, constant.Value);
        return true;
      case IrBinary binary when binary.Type.SameStorage(type)
                                && binary.Op is IrBinaryOp.Add or IrBinaryOp.Sub or IrBinaryOp.Mul:
        region.Add(binary);
        if (!TryRead(binary.Lhs, type, ref variable, region, out var left)
            || !TryRead(binary.Rhs, type, ref variable, region, out var right))
          return false;
        coefficients = binary.Op switch {
          IrBinaryOp.Add => Add(type, left, right, subtract: false),
          IrBinaryOp.Sub => Add(type, left, right, subtract: true),
          _ => Multiply(type, left, right),
        };
        return coefficients.Length != 0;
      default:
        if (!value.Type.SameStorage(type))
          return false;
        if (variable is null)
          variable = value;
        else if (!ReferenceEquals(variable, value))
          return false;
        coefficients[1] = 1;
        return true;
    }
  }

  private static long[] Add(IrType type, long[] left, long[] right, bool subtract) {
    var result = new long[_MAX_DEGREE + 1];
    for (var i = 0; i < result.Length; ++i)
      result[i] = Wrap(type, subtract ? unchecked(left[i] - right[i]) : unchecked(left[i] + right[i]));
    return result;
  }

  private static long[] Multiply(IrType type, long[] left, long[] right) {
    var result = new long[_MAX_DEGREE + 1];
    for (var i = 0; i < left.Length; ++i) {
      if (IsZero(left[i], type))
        continue;
      for (var j = 0; j < right.Length; ++j) {
        if (IsZero(right[j], type))
          continue;
        if (i + j > _MAX_DEGREE)
          return [];
        result[i + j] = Wrap(type, unchecked(result[i + j] + unchecked(left[i] * right[j])));
      }
    }
    return result;
  }

  private static int Degree(long[] coefficients, IrType type) {
    for (var i = coefficients.Length - 1; i >= 0; --i)
      if (!IsZero(coefficients[i], type))
        return i;
    return 0;
  }

  private static bool IsPlanZero(Plan plan, IrType type)
    => plan is ConstantPlan constant && IsZero(constant.Value, type);

  private static bool IsPlanOne(Plan plan, IrType type)
    => plan is ConstantPlan constant && Wrap(type, constant.Value) == 1;

  private static bool IsZero(long value, IrType type) => Wrap(type, value) == 0;

  private static long Wrap(IrType type, long value) {
    if (type.Bits >= 64)
      return value;
    var mask = (1UL << type.Bits) - 1;
    return unchecked((long)(unchecked((ulong)value) & mask));
  }
}
