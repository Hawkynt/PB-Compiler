namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0306 — loop versioning. Runtime predicates are collected in one preheader guard and select a
/// specialized clone while the original loop remains the byte-faithful checked fallback.
/// </summary>
public static class LoopVersioning {

  private const int _MAX_INSTRUCTIONS = 96;
  private const int _MAX_RUNTIME_ALIAS_ROOTS = 3;

  private enum Direction {
    Ascending,
    Descending,
    Runtime,
  }

  private enum BoundKind {
    Lower,
    Upper,
  }

  private sealed record LinearTerm(IrValue Value, long Coefficient);

  private sealed record LinearForm(
    long CounterCoefficient,
    long Constant,
    IReadOnlyList<LinearTerm> Terms);

  private sealed record Bound(BoundKind Kind, LinearForm Index, IrValue Value);

  private sealed record BoundsCheck(IrCondBr Branch, IReadOnlyList<Bound> Bounds);

  private sealed record OverflowCheck(IrCondBr Branch, LinearForm Expression, IrType Type);

  private sealed record MemoryAccess(IrInstruction Instruction, IrValue Root, LinearForm Offset, int Bytes);

  private sealed record MemoryRoot(IrValue Root, IReadOnlyList<MemoryAccess> Accesses, int Group, int Alignment);

  private sealed record MemoryPlan(IReadOnlyList<MemoryRoot> Roots);

  private sealed record Loop(
    IrBasicBlock Header,
    List<IrBasicBlock> Region,
    IrBasicBlock Latch,
    IrBasicBlock Preheader,
    IrBasicBlock Exit,
    IrPhi Counter,
    IrValue Initial,
    IrValue Limit,
    IrValue Step,
    Direction Direction,
    IReadOnlyList<BoundsCheck> BoundsChecks,
    IReadOnlyList<OverflowCheck> OverflowChecks,
    MemoryPlan? Memory);

  private sealed record LoopTest(IrPhi Counter, IrValue Limit, IrValue? Step, Direction Direction);

  /// <summary>Versions at most one loop; the pass manager reaches further loops on its next sweep.</summary>
  public static int Run(IrFunction fn) {
    ArgumentNullException.ThrowIfNull(fn);
    if (fn.HasErrorHandler || fn.HasInlineAsm)
      return 0;

    foreach (var header in fn.Blocks.ToList())
      if (Match(fn, header) is { } loop && Version(fn, loop))
        return 1;
    return 0;
  }

  private static Loop? Match(IrFunction fn, IrBasicBlock header) {
    if (header.Terminator is not IrCondBr headerBranch
        || MatchLoopTest(headerBranch.Condition) is not { } test
        || !test.Counter.Type.IsInteger
        || !test.Counter.Type.Signed
        || test.Counter.Type.Bits is <= 1 or > 64
        || !ReferenceEquals(test.Counter.Parent, header)
        || !test.Limit.Type.Equals(test.Counter.Type))
      return null;

    var counter = test.Counter;
    var predecessors = header.Predecessors.ToList();
    if (predecessors.Count != 2)
      return null;

    IrBasicBlock? latch = null;
    IrValue? step = null;
    foreach (var predecessor in predecessors)
      if (TryStep(counter.IncomingFrom(predecessor), counter, out var candidate)) {
        if (latch is not null)
          return null;
        latch = predecessor;
        step = candidate;
      }

    if (latch is null || step is null || !step.Type.Equals(counter.Type))
      return null;
    if (test.Step is { } headerStep && !ReferenceEquals(headerStep, step))
      return null;

    var preheader = predecessors.Single(p => !ReferenceEquals(p, latch));
    if (preheader.Terminator is not IrBr enter || !ReferenceEquals(enter.Target, header))
      return null;

    var initial = counter.IncomingFrom(preheader);
    if (initial is null)
      return null;

    var exit = headerBranch.IfFalse;
    if (ReferenceEquals(exit, header))
      return null;

    var body = new List<IrBasicBlock>();
    var queue = new Queue<IrBasicBlock>([headerBranch.IfTrue]);
    while (queue.Count > 0) {
      var at = queue.Dequeue();
      if (ReferenceEquals(at, header) || body.Contains(at))
        continue;
      if (ReferenceEquals(at, exit))
        return null;
      body.Add(at);

      switch (at.Terminator) {
        case IrBr branch when ReferenceEquals(branch.Target, header):
          if (!ReferenceEquals(at, latch))
            return null;
          break;
        case IrBr branch:
          if (ReferenceEquals(branch.Target, exit))
            return null;
          queue.Enqueue(branch.Target);
          break;
        case IrCondBr branch:
          if (ReferenceEquals(branch.IfTrue, exit) || ReferenceEquals(branch.IfFalse, exit))
            return null;
          queue.Enqueue(branch.IfTrue);
          queue.Enqueue(branch.IfFalse);
          break;
        default:
          return null;
      }
    }

    if (!body.Contains(latch)
        || latch.Terminator is not IrBr back
        || !ReferenceEquals(back.Target, header))
      return null;

    var region = new List<IrBasicBlock> { header };
    region.AddRange(body);
    if (region.Sum(b => b.Instructions.Count) > _MAX_INSTRUCTIONS)
      return null;
    if (!Invariant(initial, region) || !Invariant(test.Limit, region) || !Invariant(step, region))
      return null;

    if (step is IrConstantInt stepConstant) {
      var signedStep = Signed(stepConstant);
      if (signedStep == 0)
        return null;
      if (test.Direction == Direction.Ascending && signedStep < 0
          || test.Direction == Direction.Descending && signedStep > 0)
        return null;
    } else if (counter.Type.Bits > 32) {
      // Runtime no-wrap arithmetic is evaluated one width up. There is no integer wider than i64 in
      // this IR, so the 64-bit runtime-step case is refused instead of being proved with wrapping math.
      return null;
    }

    // No control may enter the duplicated body from outside, and the loop has one structural exit.
    foreach (var block in fn.Blocks)
      if (!region.Contains(block))
        foreach (var successor in block.Successors)
          if (body.Contains(successor))
            return null;
    if (exit.Predecessors.Count() != 1 || !ReferenceEquals(exit.Predecessors.Single(), header))
      return null;
    if (exit.Phis.Any(phi => phi.IncomingFrom(header) is null))
      return null;

    var boundsChecks = new List<BoundsCheck>();
    var overflowChecks = new List<OverflowCheck>();
    foreach (var block in region) {
      if (block.Terminator is not IrCondBr branch)
        continue;

      if (TryErrorGuard(branch, 9, out var boundsCondition)) {
        var bounds = new List<Bound>();
        if (CollectBounds(boundsCondition, counter, region, bounds) && bounds.Count != 0)
          boundsChecks.Add(new(branch, bounds));
        continue;
      }

      if (TryErrorGuard(branch, 6, out var overflowCondition)
          && TryOverflowCheck(branch, overflowCondition, counter, region, out var overflow))
        overflowChecks.Add(overflow);
    }

    var memory = BuildMemoryPlan(counter, region);
    if (boundsChecks.Count == 0 && overflowChecks.Count == 0 && memory is null)
      return null;

    var loop = new Loop(
      header, region, latch, preheader, exit, counter, initial, test.Limit, step, test.Direction,
      boundsChecks, overflowChecks, memory);
    return FastPathCanRun(loop) ? loop : null;
  }

  private static LoopTest? MatchLoopTest(IrValue condition) {
    if (condition is IrCmp {
          Lhs: IrPhi counter,
          Rhs: { } limit,
          Pred: IrCmpPred.Sle or IrCmpPred.Sge,
        } simple)
      return new(
        counter,
        limit,
        null,
        simple.Pred == IrCmpPred.Sle ? Direction.Ascending : Direction.Descending);

    if (condition is not IrBinary { Op: IrBinaryOp.Or } disjunction)
      return null;

    return TryRuntimeLoopTest(disjunction.Lhs, disjunction.Rhs, out var runtime)
      || TryRuntimeLoopTest(disjunction.Rhs, disjunction.Lhs, out runtime)
        ? runtime
        : null;
  }

  private static bool TryRuntimeLoopTest(IrValue ascendingArm, IrValue descendingArm, out LoopTest test) {
    test = null!;
    if (ascendingArm is not IrBinary { Op: IrBinaryOp.And } asc
        || descendingArm is not IrBinary { Op: IrBinaryOp.And } desc)
      return false;

    if (asc.Lhs is not IrCmp { Pred: IrCmpPred.Sge } direction
        || direction.Rhs is not IrConstantInt { IsZero: true }
        || asc.Rhs is not IrCmp { Pred: IrCmpPred.Sle, Lhs: IrPhi counter } upper
        || !direction.Lhs.Type.Equals(counter.Type))
      return false;

    var notDirection = desc.Lhs is IrBinary { Op: IrBinaryOp.Xor } leftNot ? leftNot
      : desc.Rhs is IrBinary { Op: IrBinaryOp.Xor } rightNot ? rightNot
      : null;
    var lower = ReferenceEquals(desc.Lhs, notDirection) ? desc.Rhs as IrCmp : desc.Lhs as IrCmp;
    if (notDirection is null
        || !IsBooleanNotOf(notDirection, direction)
        || lower is not { Pred: IrCmpPred.Sge }
        || !ReferenceEquals(lower.Lhs, counter)
        || !ReferenceEquals(lower.Rhs, upper.Rhs))
      return false;

    test = new(counter, upper.Rhs, direction.Lhs, Direction.Runtime);
    return true;
  }

  private static bool IsBooleanNotOf(IrBinary value, IrValue original)
    => ReferenceEquals(value.Lhs, original) && value.Rhs is IrConstantInt { ZeroExtended: 1 }
       || ReferenceEquals(value.Rhs, original) && value.Lhs is IrConstantInt { ZeroExtended: 1 };

  private static bool TryStep(IrValue? value, IrPhi counter, out IrValue step) {
    step = null!;
    if (value is not IrBinary { Op: IrBinaryOp.Add } add)
      return false;
    if (ReferenceEquals(add.Lhs, counter)) {
      step = add.Rhs;
      return true;
    }
    if (ReferenceEquals(add.Rhs, counter)) {
      step = add.Lhs;
      return true;
    }
    return false;
  }

  private static bool Invariant(IrValue value, IReadOnlyCollection<IrBasicBlock> region)
    => value is IrConstant or IrArgument or IrGlobalValue
       || value is IrInstruction instruction
       && instruction.Parent is { } parent
       && !region.Contains(parent);

  /// <summary>Matches the exact RaiseWhen trap shell, not an arbitrary source branch reaching ERROR.</summary>
  private static bool TryErrorGuard(IrCondBr branch, int errorCode, out IrValue condition) {
    condition = null!;
    if (!IsErrorTrap(branch.IfTrue, branch.IfFalse, errorCode))
      return false;
    condition = branch.Condition;
    return true;
  }

  private static bool IsErrorTrap(IrBasicBlock trap, IrBasicBlock continuation, int errorCode) {
    if (trap.Instructions.Count != 2
        || trap.Instructions[0] is not IrCall call
        || call.Callee is not IrFunction callee
        || !callee.Name.Equals("rt_error", StringComparison.OrdinalIgnoreCase)
        || call.Args.SingleOrDefault() is not IrConstantInt code
        || code.ZeroExtended != (ulong)errorCode
        || trap.Terminator is not IrBr branch
        || !ReferenceEquals(branch.Target, continuation)
        || trap.Predecessors.Count() != 1)
      return false;
    return true;
  }

  private static bool CollectBounds(
      IrValue condition,
      IrPhi counter,
      IReadOnlyCollection<IrBasicBlock> region,
      List<Bound> result) {
    switch (condition) {
      case IrConstantInt constant:
        return constant.IsZero;

      case IrBinary { Op: IrBinaryOp.Or } disjunction:
        return CollectBounds(disjunction.Lhs, counter, region, result)
               && CollectBounds(disjunction.Rhs, counter, region, result);

      case IrCmp comparison when !comparison.IsSourceCondition:
        return TryBound(comparison, counter, region, out var bound) && Add(result, bound);

      default:
        return false;
    }

    static bool Add(List<Bound> bounds, Bound bound) {
      bounds.Add(bound);
      return true;
    }
  }

  private static bool TryBound(
      IrCmp comparison,
      IrPhi counter,
      IReadOnlyCollection<IrBasicBlock> region,
      out Bound bound) {
    bound = null!;

    if (TryLinear(comparison.Lhs, counter, region, out var index)
        && IsGuardScalar(comparison.Rhs)
        && Invariant(comparison.Rhs, region))
      switch (comparison.Pred) {
        case IrCmpPred.Slt:
          bound = new(BoundKind.Lower, index, comparison.Rhs);
          return true;
        case IrCmpPred.Sgt:
          bound = new(BoundKind.Upper, index, comparison.Rhs);
          return true;
      }

    if (TryLinear(comparison.Rhs, counter, region, out index)
        && IsGuardScalar(comparison.Lhs)
        && Invariant(comparison.Lhs, region))
      switch (comparison.Pred) {
        case IrCmpPred.Sgt:
          bound = new(BoundKind.Lower, index, comparison.Lhs);
          return true;
        case IrCmpPred.Slt:
          bound = new(BoundKind.Upper, index, comparison.Lhs);
          return true;
      }

    return false;
  }

  private static bool TryOverflowCheck(
      IrCondBr branch,
      IrValue condition,
      IrPhi counter,
      IReadOnlyCollection<IrBasicBlock> region,
      out OverflowCheck check) {
    check = null!;
    if (condition is IrConstantInt { IsZero: true })
      return false;

    var candidates = branch.Parent!.Instructions
      .OfType<IrBinary>()
      .Where(binary => binary.Op is IrBinaryOp.Add or IrBinaryOp.Sub
                       && binary.Type.IsInteger
                       && binary.Type.Signed
                       && binary.Type.Bits is > 1 and <= 32
                       && DependsOn(condition, binary))
      .ToList();
    if (candidates.Count != 1)
      return false;

    var operation = candidates[0];
    if (!TryLinear(operation.Lhs, counter, region, out var left)
        || !TryLinear(operation.Rhs, counter, region, out var right)
        || !TryCombine(left, right, operation.Op == IrBinaryOp.Add ? 1 : -1, out var expression)
        || !CanEvaluateWide(expression, counter.Type))
      return false;

    check = new(branch, expression, operation.Type);
    return true;
  }

  private static bool DependsOn(IrValue value, IrValue needle) {
    var seen = new HashSet<IrValue>(ReferenceEqualityComparer.Instance);
    var stack = new Stack<IrValue>();
    stack.Push(value);
    while (stack.Count > 0) {
      var current = stack.Pop();
      if (ReferenceEquals(current, needle))
        return true;
      if (!seen.Add(current) || current is not IrInstruction instruction)
        continue;
      foreach (var operand in instruction.Operands)
        stack.Push(operand);
    }
    return false;
  }

  private static bool TryLinear(
      IrValue value,
      IrPhi counter,
      IReadOnlyCollection<IrBasicBlock> region,
      out LinearForm form) {
    form = null!;

    if (!IsGuardScalar(value))
      return false;

    if (ReferenceEquals(value, counter)) {
      form = new(1, 0, []);
      return true;
    }

    if (value is IrConstantInt constant) {
      form = new(0, Signed(constant), []);
      return true;
    }

    if (value is IrCast { Op: IrCastOp.SExt } widening
        && widening.Type.Bits <= 32
        && TryLinear(widening.Value, counter, region, out var widened)) {
      form = widened;
      return true;
    }

    if (value is IrBinary { Op: IrBinaryOp.Add or IrBinaryOp.Sub } binary
        && TryLinear(binary.Lhs, counter, region, out var left)
        && TryLinear(binary.Rhs, counter, region, out var right)
        && TryCombine(left, right, binary.Op == IrBinaryOp.Add ? 1 : -1, out form))
      return CanEvaluateWide(form, counter.Type);

    if (value is IrBinary { Op: IrBinaryOp.Mul } multiply) {
      if (multiply.Lhs is IrConstantInt leftConstant
          && TryLinear(multiply.Rhs, counter, region, out var rightLinear)
          && TryScale(rightLinear, Signed(leftConstant), out form))
        return CanEvaluateWide(form, counter.Type);
      if (multiply.Rhs is IrConstantInt rightConstant
          && TryLinear(multiply.Lhs, counter, region, out var leftLinear)
          && TryScale(leftLinear, Signed(rightConstant), out form))
        return CanEvaluateWide(form, counter.Type);
    }

    if (Invariant(value, region)) {
      form = new(0, 0, [new(value, 1)]);
      return CanEvaluateWide(form, counter.Type);
    }

    return false;
  }

  private static bool TryCombine(LinearForm left, LinearForm right, long rightSign, out LinearForm result) {
    result = null!;
    try {
      var terms = new Dictionary<IrValue, long>(ReferenceEqualityComparer.Instance);
      AddTerms(terms, left.Terms, 1);
      AddTerms(terms, right.Terms, rightSign);
      result = new(
        checked(left.CounterCoefficient + checked(right.CounterCoefficient * rightSign)),
        checked(left.Constant + checked(right.Constant * rightSign)),
        terms.Where(pair => pair.Value != 0).Select(pair => new LinearTerm(pair.Key, pair.Value)).ToList());
      return true;
    } catch (OverflowException) {
      return false;
    }

    static void AddTerms(Dictionary<IrValue, long> into, IReadOnlyList<LinearTerm> terms, long scale) {
      foreach (var term in terms)
        into[term.Value] = checked(into.GetValueOrDefault(term.Value) + checked(term.Coefficient * scale));
    }
  }

  private static bool TryScale(LinearForm value, long scale, out LinearForm result) {
    result = null!;
    try {
      result = new(
        checked(value.CounterCoefficient * scale),
        checked(value.Constant * scale),
        value.Terms.Select(term => new LinearTerm(term.Value, checked(term.Coefficient * scale))).ToList());
      return true;
    } catch (OverflowException) {
      return false;
    }
  }

  private static bool CanEvaluateWide(LinearForm form, IrType counterType) {
    if (!IsGuardScalar(counterType))
      return false;
    Int128 magnitude = Int128.Abs((Int128)form.Constant);
    magnitude += Int128.Abs((Int128)form.CounterCoefficient) * SignedMagnitude(counterType.Bits);
    foreach (var term in form.Terms) {
      if (!IsGuardScalar(term.Value))
        return false;
      magnitude += Int128.Abs((Int128)term.Coefficient) * SignedMagnitude(term.Value.Type.Bits);
      if (magnitude > long.MaxValue - 65536)
        return false;
    }
    return magnitude <= long.MaxValue - 65536;
  }

  private static Int128 SignedMagnitude(int bits) => (Int128)1 << (bits - 1);

  private static bool IsGuardScalar(IrValue value) => IsGuardScalar(value.Type);

  private static bool IsGuardScalar(IrType type)
    => type.IsInteger && type.Signed && type.Bits is > 1 and <= 32;

  private static MemoryPlan? BuildMemoryPlan(
      IrPhi counter,
      IReadOnlyCollection<IrBasicBlock> region) {
    var accesses = new List<MemoryAccess>();
    foreach (var block in region)
      foreach (var instruction in block.Instructions)
        switch (instruction) {
          case IrLoad load when StorageBytes(load.Type) is { } bytes
                                && TryPointerOffset(load.Pointer, counter, region, out var root, out var offset):
            accesses.Add(new(load, root, offset, bytes));
            break;
          case IrStore store when StorageBytes(store.Value.Type) is { } bytes
                                  && TryPointerOffset(store.Pointer, counter, region, out var root, out var offset):
            accesses.Add(new(store, root, offset, bytes));
            break;
        }

    var ambiguous = accesses
      .Where(access => IsRuntimeAliasRoot(access.Root))
      .GroupBy(access => access.Root, ReferenceEqualityComparer.Instance)
      .Select(group => group.ToList())
      .Where(group => group.Count != 0)
      .ToList();
    if (ambiguous.Count is < 2 or > _MAX_RUNTIME_ALIAS_ROOTS)
      return null;
    if (!ambiguous.SelectMany(group => group).Any(access => access.Instruction is IrStore))
      return null;

    var roots = new List<MemoryRoot>();
    for (var i = 0; i < ambiguous.Count; ++i) {
      var group = ambiguous[i];
      var alignment = CommonAlignment(group);
      roots.Add(new(group[0].Root, group, i + 1, alignment));
    }

    return new(roots);
  }

  private static bool IsRuntimeAliasRoot(IrValue root)
    => root.Type.IsPointer
       && root.Type.AddressSpace == 0
       && root is not (IrAlloca or IrGlobalVariable or IrFarPtr);

  private static int CommonAlignment(IReadOnlyList<MemoryAccess> accesses) {
    var alignment = accesses
      .Select(access => LargestPowerOfTwoAtMost(access.Bytes))
      .DefaultIfEmpty(1)
      .Min();
    while (alignment > 1 && accesses.Any(access => !AlwaysMultipleOf(access.Offset, alignment)))
      alignment >>= 1;
    return alignment;
  }

  private static int LargestPowerOfTwoAtMost(int value) {
    var result = 1;
    while (result <= value / 2)
      result <<= 1;
    return result;
  }

  private static bool AlwaysMultipleOf(LinearForm form, int alignment)
    => form.Constant % alignment == 0
       && form.CounterCoefficient % alignment == 0
       && form.Terms.All(term => term.Coefficient % alignment == 0);

  private static bool TryPointerOffset(
      IrValue pointer,
      IrPhi counter,
      IReadOnlyCollection<IrBasicBlock> region,
      out IrValue root,
      out LinearForm offset) {
    root = null!;
    offset = null!;

    if (pointer is IrFarPtr || !pointer.Type.IsPointer || pointer.Type.AddressSpace != 0)
      return false;

    if (pointer is IrGep gep) {
      if (!TryPointerOffset(gep.BasePtr, counter, region, out root, out var baseOffset)
          || !TryLinear(gep.ByteOffset, counter, region, out var displacement))
        return false;
      if (gep.ElementType is { } elementType) {
        if (StorageBytes(elementType) is not { } elementBytes
            || !TryScale(displacement, elementBytes, out displacement))
          return false;
      }
      return TryCombine(baseOffset, displacement, 1, out offset)
             && CanEvaluateWide(offset, counter.Type);
    }

    if (!Invariant(pointer, region))
      return false;

    root = pointer;
    offset = new(0, 0, []);
    return true;
  }

  private static int? StorageBytes(IrType type) {
    if (type.IsVoid || type.IsPointer || type.Bits <= 0)
      return null;
    try {
      return checked((type.Bits + 7) / 8);
    } catch (OverflowException) {
      return null;
    }
  }

  /// <summary>
  /// Rejects versions whose combined guard is statically impossible, preventing dead clone churn at
  /// the pass-manager fixpoint.
  /// </summary>
  private static bool FastPathCanRun(Loop loop) {
    foreach (var check in loop.BoundsChecks)
      foreach (var bound in check.Bounds)
        foreach (var endpoint in new[] { loop.Initial, loop.Limit })
          if (TryEvaluateLinearConstant(bound.Index, endpoint, out var index)
              && TrySignedConstant(bound.Value, out var boundary)
              && (bound.Kind == BoundKind.Lower ? index < boundary : index > boundary))
            return false;

    foreach (var check in loop.OverflowChecks)
      foreach (var endpoint in new[] { loop.Initial, loop.Limit })
        if (TryEvaluateLinearConstant(check.Expression, endpoint, out var value)
            && (value < SignedMin(check.Type.Bits) || value > SignedMax(check.Type.Bits)))
          return false;

    if (loop.Step is not IrConstantInt step || loop.Limit is not IrConstantInt limit)
      return true;
    var signedStep = Signed(step);
    var limitValue = Signed(limit);
    if (signedStep > 0)
      return (Int128)limitValue <= (Int128)SignedMax(loop.Counter.Type.Bits) - signedStep;
    return (Int128)limitValue >= (Int128)SignedMin(loop.Counter.Type.Bits) - signedStep;
  }

  private static bool TryEvaluateLinearConstant(LinearForm form, IrValue endpoint, out long result) {
    result = 0;
    if (!TrySignedConstant(endpoint, out var counter))
      return false;
    Int128 value = (Int128)form.CounterCoefficient * counter + form.Constant;
    foreach (var term in form.Terms) {
      if (!TrySignedConstant(term.Value, out var constant))
        return false;
      value += (Int128)term.Coefficient * constant;
    }
    if (value < long.MinValue || value > long.MaxValue)
      return false;
    result = (long)value;
    return true;
  }

  private static bool TrySignedConstant(IrValue value, out long result) {
    if (value is IrConstantInt constant) {
      result = Signed(constant);
      return true;
    }
    result = 0;
    return false;
  }

  private static long Signed(IrConstantInt constant) {
    if (constant.Type.Bits >= 64)
      return constant.Value;
    var bits = constant.Type.Bits;
    var sign = 1UL << (bits - 1);
    var value = constant.ZeroExtended;
    return unchecked((long)((value ^ sign) - sign));
  }

  private static bool Version(IrFunction fn, Loop loop) {
    var escaping = loop.Region
      .SelectMany(block => block.Instructions)
      .Where(value => value.Users.Any(user =>
        user.Parent is { } parent
        && !loop.Region.Contains(parent)
        && !(ReferenceEquals(parent, loop.Exit) && user is IrPhi)))
      .ToList();
    var exitPhis = loop.Exit.Phis.ToList();

    var fastBlocks = IrCloner.Clone(
      fn,
      loop.Region,
      new Dictionary<IrValue, IrValue>(ReferenceEqualityComparer.Instance),
      "ver.fast.",
      out var fastValues);
    var fastHeader = fastBlocks[loop.Header];

    foreach (var check in loop.BoundsChecks)
      SpecializeFalse(fastBlocks, check.Branch);
    foreach (var check in loop.OverflowChecks)
      SpecializeFalse(fastBlocks, check.Branch);

    ApplyMemoryFacts(loop.Memory, fastValues);

    var anchor = loop.Preheader.Terminator!;
    var guard = BuildGuard(loop, anchor);

    loop.Preheader.Remove(anchor);
    loop.Preheader.Append(new IrCondBr(guard, fastHeader, loop.Header));

    foreach (var phi in exitPhis) {
      var fallbackValue = phi.IncomingFrom(loop.Header)!;
      phi.AddIncoming(fastValues.GetValueOrDefault(fallbackValue, fallbackValue), fastHeader);
    }

    foreach (var value in escaping) {
      var joined = loop.Exit.AppendPhi(new IrPhi(value.Type) { Name = value.Name });
      joined.AddIncoming(value, loop.Header);
      joined.AddIncoming(fastValues.GetValueOrDefault(value, value), fastHeader);

      foreach (var user in value.Users.ToList())
        if (!ReferenceEquals(user, joined)
            && user.Parent is { } parent
            && !loop.Region.Contains(parent)
            && !(ReferenceEquals(parent, loop.Exit) && user is IrPhi))
          user.ReplaceOperand(value, joined);
    }

    return true;
  }

  private static void SpecializeFalse(
      IReadOnlyDictionary<IrBasicBlock, IrBasicBlock> fastBlocks,
      IrCondBr branch) {
    var fastBlock = fastBlocks[branch.Parent!];
    var fastBranch = (IrCondBr)fastBlock.Terminator!;
    fastBranch.SetOperand(0, new IrConstantInt(IrType.I1, 0));
  }

  private static void ApplyMemoryFacts(
      MemoryPlan? plan,
      IReadOnlyDictionary<IrValue, IrValue> fastValues) {
    if (plan is null)
      return;
    foreach (var root in plan.Roots)
      foreach (var access in root.Accesses)
        if (fastValues.GetValueOrDefault(access.Instruction) is IrInstruction clone) {
          LoopVersioningMemoryFacts.SetNoAliasGroup(clone, root.Group);
          if (root.Alignment > 1)
            LoopVersioningMemoryFacts.SetAlignment(clone, root.Alignment);
        }
  }

  private static IrValue BuildGuard(Loop loop, IrInstruction anchor) {
    var conditions = new List<IrValue>();

    foreach (var check in loop.BoundsChecks)
      foreach (var bound in check.Bounds) {
        var boundary = ProjectSignedWide(loop.Preheader, anchor, bound.Value);
        foreach (var endpoint in new[] { loop.Initial, loop.Limit }) {
          var index = BuildLinear(loop.Preheader, anchor, bound.Index, endpoint);
          var predicate = bound.Kind == BoundKind.Lower ? IrCmpPred.Sge : IrCmpPred.Sle;
          conditions.Add(loop.Preheader.InsertBefore(new IrCmp(predicate, index, boundary), anchor));
        }
      }

    foreach (var check in loop.OverflowChecks) {
      var minimum = new IrConstantInt(IrType.I64, SignedMin(check.Type.Bits));
      var maximum = new IrConstantInt(IrType.I64, SignedMax(check.Type.Bits));
      foreach (var endpoint in new[] { loop.Initial, loop.Limit }) {
        var value = BuildLinear(loop.Preheader, anchor, check.Expression, endpoint);
        conditions.Add(loop.Preheader.InsertBefore(new IrCmp(IrCmpPred.Sge, value, minimum), anchor));
        conditions.Add(loop.Preheader.InsertBefore(new IrCmp(IrCmpPred.Sle, value, maximum), anchor));
      }
    }

    conditions.Add(BuildNoWrapGuard(loop, anchor));
    AddMemoryGuards(loop, anchor, conditions);

    var result = conditions[0];
    foreach (var condition in conditions.Skip(1))
      result = loop.Preheader.InsertBefore(new IrBinary(IrBinaryOp.And, result, condition), anchor);
    return result;
  }

  private static IrValue BuildNoWrapGuard(Loop loop, IrInstruction anchor) {
    if (loop.Step is IrConstantInt constant) {
      var step = Signed(constant);
      var threshold = step > 0
        ? (long)((Int128)SignedMax(loop.Counter.Type.Bits) - step)
        : (long)((Int128)SignedMin(loop.Counter.Type.Bits) - step);
      var predicate = step > 0 ? IrCmpPred.Sle : IrCmpPred.Sge;
      return loop.Preheader.InsertBefore(
        new IrCmp(predicate, loop.Limit, new IrConstantInt(loop.Counter.Type, threshold)), anchor);
    }

    var stepWide = ProjectSignedWide(loop.Preheader, anchor, loop.Step);
    var limitWide = ProjectSignedWide(loop.Preheader, anchor, loop.Limit);
    var zero = new IrConstantInt(IrType.I64, 0);
    var positive = loop.Preheader.InsertBefore(new IrCmp(IrCmpPred.Sgt, stepWide, zero), anchor);
    var negative = loop.Preheader.InsertBefore(new IrCmp(IrCmpPred.Slt, stepWide, zero), anchor);

    var upperThreshold = loop.Preheader.InsertBefore(
      new IrBinary(IrBinaryOp.Sub,
        new IrConstantInt(IrType.I64, SignedMax(loop.Counter.Type.Bits)), stepWide), anchor);
    var lowerThreshold = loop.Preheader.InsertBefore(
      new IrBinary(IrBinaryOp.Sub,
        new IrConstantInt(IrType.I64, SignedMin(loop.Counter.Type.Bits)), stepWide), anchor);
    var ascendingRange = loop.Preheader.InsertBefore(new IrCmp(IrCmpPred.Sle, limitWide, upperThreshold), anchor);
    var descendingRange = loop.Preheader.InsertBefore(new IrCmp(IrCmpPred.Sge, limitWide, lowerThreshold), anchor);
    var ascending = loop.Preheader.InsertBefore(new IrBinary(IrBinaryOp.And, positive, ascendingRange), anchor);
    var descending = loop.Preheader.InsertBefore(new IrBinary(IrBinaryOp.And, negative, descendingRange), anchor);

    return loop.Direction switch {
      Direction.Ascending => ascending,
      Direction.Descending => descending,
      _ => loop.Preheader.InsertBefore(new IrBinary(IrBinaryOp.Or, ascending, descending), anchor),
    };
  }

  private static void AddMemoryGuards(Loop loop, IrInstruction anchor, List<IrValue> conditions) {
    if (loop.Memory is not { Roots.Count: >= 2 } plan)
      return;

    var ranges = new Dictionary<IrValue, (IrValue Start, IrValue End, IrValue Valid)>(
      ReferenceEqualityComparer.Instance);
    foreach (var root in plan.Roots) {
      ranges[root.Root] = BuildMemoryRange(loop, root, anchor);
      if (root.Alignment > 1) {
        var address = loop.Preheader.InsertBefore(new IrCast(IrCastOp.PtrToInt, root.Root, IrType.U16), anchor);
        var masked = loop.Preheader.InsertBefore(
          new IrBinary(IrBinaryOp.And, address, new IrConstantInt(IrType.U16, root.Alignment - 1)), anchor);
        conditions.Add(loop.Preheader.InsertBefore(
          new IrCmp(IrCmpPred.Eq, masked, new IrConstantInt(IrType.U16, 0)), anchor));
      }
    }

    for (var i = 0; i < plan.Roots.Count; ++i)
      for (var j = i + 1; j < plan.Roots.Count; ++j) {
        var left = ranges[plan.Roots[i].Root];
        var right = ranges[plan.Roots[j].Root];
        var leftBefore = loop.Preheader.InsertBefore(new IrCmp(IrCmpPred.Sle, left.End, right.Start), anchor);
        var rightBefore = loop.Preheader.InsertBefore(new IrCmp(IrCmpPred.Sle, right.End, left.Start), anchor);
        var disjoint = loop.Preheader.InsertBefore(new IrBinary(IrBinaryOp.Or, leftBefore, rightBefore), anchor);
        var valid = loop.Preheader.InsertBefore(new IrBinary(IrBinaryOp.And, left.Valid, right.Valid), anchor);
        conditions.Add(loop.Preheader.InsertBefore(new IrBinary(IrBinaryOp.And, valid, disjoint), anchor));
      }
  }

  private static (IrValue Start, IrValue End, IrValue Valid) BuildMemoryRange(
      Loop loop,
      MemoryRoot root,
      IrInstruction anchor) {
    var base16 = loop.Preheader.InsertBefore(new IrCast(IrCastOp.PtrToInt, root.Root, IrType.U16), anchor);
    var baseAddress = loop.Preheader.InsertBefore(new IrCast(IrCastOp.ZExt, base16, IrType.I64), anchor);

    IrValue? start = null;
    IrValue? end = null;
    foreach (var access in root.Accesses) {
      var first = BuildLinear(loop.Preheader, anchor, access.Offset, loop.Initial);
      var last = BuildLinear(loop.Preheader, anchor, access.Offset, loop.Limit);
      var firstIsLower = loop.Preheader.InsertBefore(new IrCmp(IrCmpPred.Sle, first, last), anchor);
      var low = loop.Preheader.InsertBefore(new IrSelect(firstIsLower, first, last), anchor);
      var high = loop.Preheader.InsertBefore(new IrSelect(firstIsLower, last, first), anchor);
      var accessStart = loop.Preheader.InsertBefore(new IrBinary(IrBinaryOp.Add, baseAddress, low), anchor);
      var accessEnd0 = loop.Preheader.InsertBefore(new IrBinary(IrBinaryOp.Add, baseAddress, high), anchor);
      var accessEnd = loop.Preheader.InsertBefore(
        new IrBinary(IrBinaryOp.Add, accessEnd0, new IrConstantInt(IrType.I64, access.Bytes)), anchor);

      if (start is null) {
        start = accessStart;
        end = accessEnd;
        continue;
      }

      var startsEarlier = loop.Preheader.InsertBefore(new IrCmp(IrCmpPred.Sle, start, accessStart), anchor);
      start = loop.Preheader.InsertBefore(new IrSelect(startsEarlier, start, accessStart), anchor);
      var endsLater = loop.Preheader.InsertBefore(new IrCmp(IrCmpPred.Sge, end!, accessEnd), anchor);
      end = loop.Preheader.InsertBefore(new IrSelect(endsLater, end!, accessEnd), anchor);
    }

    var nonNegative = loop.Preheader.InsertBefore(
      new IrCmp(IrCmpPred.Sge, start!, new IrConstantInt(IrType.I64, 0)), anchor);
    var inSegment = loop.Preheader.InsertBefore(
      new IrCmp(IrCmpPred.Sle, end!, new IrConstantInt(IrType.I64, 65536)), anchor);
    var valid = loop.Preheader.InsertBefore(new IrBinary(IrBinaryOp.And, nonNegative, inSegment), anchor);
    return (start!, end!, valid);
  }

  private static IrValue BuildLinear(
      IrBasicBlock block,
      IrInstruction anchor,
      LinearForm form,
      IrValue counterValue) {
    IrValue result = new IrConstantInt(IrType.I64, form.Constant);
    result = AddScaled(block, anchor, result, ProjectSignedWide(block, anchor, counterValue), form.CounterCoefficient);
    foreach (var term in form.Terms)
      result = AddScaled(block, anchor, result, ProjectSignedWide(block, anchor, term.Value), term.Coefficient);
    return result;
  }

  private static IrValue AddScaled(
      IrBasicBlock block,
      IrInstruction anchor,
      IrValue accumulated,
      IrValue value,
      long coefficient) {
    if (coefficient == 0)
      return accumulated;
    var scaled = coefficient == 1
      ? value
      : block.InsertBefore(
        new IrBinary(IrBinaryOp.Mul, value, new IrConstantInt(IrType.I64, coefficient)), anchor);
    if (accumulated is IrConstantInt { IsZero: true })
      return scaled;
    return block.InsertBefore(new IrBinary(IrBinaryOp.Add, accumulated, scaled), anchor);
  }

  private static IrValue ProjectSignedWide(IrBasicBlock block, IrInstruction anchor, IrValue value) {
    if (value.Type.Equals(IrType.I64))
      return value;
    if (value is IrConstantInt constant)
      return new IrConstantInt(IrType.I64, Signed(constant));
    return block.InsertBefore(new IrCast(IrCastOp.SExt, value, IrType.I64), anchor);
  }

  private static long SignedMax(int bits)
    => bits >= 64 ? long.MaxValue : (1L << (bits - 1)) - 1;

  private static long SignedMin(int bits)
    => bits >= 64 ? long.MinValue : -(1L << (bits - 1));
}
