using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0311 — versions a large, proven-independent counted loop with a hosted parallel path.
///
/// <para>
/// The original loop is deliberately retained as the sequential version. Its preheader asks the
/// hosted runtime whether parallel execution is worthwhile; only the true edge calls the outlined
/// one-iteration helper. That makes the trip-count/thread-count decision a runtime policy and avoids
/// paying callback overhead on the sequential path.
/// </para>
/// <para>
/// This first slice is intentionally narrow. It accepts the straight-line loop shape emitted for a
/// simple constant-bound <c>FOR</c>, requires <see cref="IrLoopDependenceAnalysis"/> to be complete and
/// to prove no loop-carried dependence, accepts only signed 8/16/32-bit counters, rejects
/// calls/trapping arithmetic and rejects captures other than constants/globals. The counter-width
/// restriction keeps the hosted runtime's <c>int64_t</c> iteration reconstruction away from signed
/// overflow; 64-bit induction stays sequential until that arithmetic is explicitly overflow-safe.
/// The capture restriction keeps the outlined helper target-independent: the IR has opaque pointers
/// and therefore has no portable byte layout for an arbitrary closure object.
/// </para>
/// <para>
/// The pass is hosted-only by policy, not by IR mechanics. Callers must opt in only for C/LLVM output;
/// the native DOS pipeline never invokes it. The runtime entry points live in <c>runtime/pbc_parallel.c</c>.
/// </para>
/// </summary>
public static class ParallelLoopVersioning {

  /// <summary>Below this many iterations the sequential loop always wins the static profitability gate.</summary>
  public const int MinimumTrips = 1024;

  private sealed record Candidate(
    CountedLoop Loop,
    IrBasicBlock Body,
    IrBinary Next,
    IrConstantInt Start,
    IrConstantInt Step);

  /// <summary>Versions every eligible loop in <paramref name="module"/>; returns the number changed.</summary>
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);

    var changed = 0;
    IrFunction? shouldRun = null;
    IrFunction? parallelFor = null;
    var helperOrdinal = 0;

    foreach (var fn in module.Functions.Where(function => !function.IsDeclaration).ToList()) {
      if (fn.HasErrorHandler || fn.HasInlineAsm)
        continue;

      foreach (var header in fn.Blocks.ToList()) {
        if (header.Parent is null || TryMatch(fn, header) is not { } candidate)
          continue;

        var dependence = IrLoopDependenceAnalysis.Analyze(fn, header);
        if (dependence is not { IsComplete: true, HasLoopCarriedDependence: false }
            || dependence.Accesses.Count == 0
            || !dependence.Accesses.Any(access => access.Writes))
          continue;

        shouldRun ??= GetOrCreateShouldRun(module);
        parallelFor ??= GetOrCreateParallelFor(module);
        var helper = OutlineIteration(module, candidate, ref helperOrdinal);
        Apply(candidate, helper, shouldRun, parallelFor);
        ++changed;
      }
    }

    return changed;
  }

  private static Candidate? TryMatch(IrFunction fn, IrBasicBlock header) {
    if (CountedLoop.Match(fn, header) is not { } loop
        || !loop.Counter.Type.IsInteger
        || !loop.Counter.Type.Signed
        || loop.Counter.Type.Bits is not (8 or 16 or 32)
        || loop.Trips < MinimumTrips
        || loop.Region.Count != 2)
      return null;

    var phis = loop.Header.Phis.ToList();
    if (phis.Count != 1
        || !ReferenceEquals(phis[0], loop.Counter)
        || loop.Header.Instructions.Count != 3
        || loop.Header.Instructions[^2] is not IrCmp test
        || !ReferenceEquals(test, loop.Test)
        || loop.Header.Terminator is not IrCondBr branch
        || !ReferenceEquals(branch.IfTrue, loop.Latch)
        || loop.Latch.Terminator is not IrBr back
        || !ReferenceEquals(back.Target, loop.Header)
        || loop.Preheader.Terminator is not IrBr preheaderBranch
        || !ReferenceEquals(preheaderBranch.Target, loop.Header)
        || loop.Counter.IncomingFrom(loop.Preheader) is not IrConstantInt start
        || loop.Counter.IncomingFrom(loop.Latch) is not IrBinary { Op: IrBinaryOp.Add } next
        || !ReferenceEquals(next.Lhs, loop.Counter)
        || next.Rhs is not IrConstantInt step
        || !ReferenceEquals(next.Parent, loop.Latch)
        || next.Users.Any(user => !ReferenceEquals(user, loop.Counter))
        || loop.Exit.Phis.Any(phi => phi.IncomingFrom(loop.Header) is not null))
      return null;

    // The parallel path does not execute the original counter phi, so an observable post-loop counter
    // would need a merge carrying its mathematically final value. Leave that to the generalized slice.
    if (loop.Counter.Users.Any(user => user.Parent is not { } parent || !loop.Region.Contains(parent)))
      return null;

    var body = loop.Latch.Instructions
      .Where(instruction => !instruction.IsTerminator && !ReferenceEquals(instruction, next))
      .ToList();
    if (body.Count == 0 || body.Any(instruction => !IsParallelBodyInstruction(instruction)))
      return null;

    // A value escaping the iteration can denote a different iteration when execution is concurrent.
    // Memory is the communication channel this slice understands; SSA values are not.
    if (body.Where(instruction => !instruction.Type.IsVoid)
        .Any(instruction => instruction.Users.Any(user => user.Parent is not { } parent || !loop.Region.Contains(parent))))
      return null;

    var bodySet = body.ToHashSet<IrInstruction>(ReferenceEqualityComparer.Instance);
    foreach (var instruction in body)
      foreach (var operand in instruction.Operands)
        if (!IsAvailableInOutlinedBody(operand, loop.Counter, bodySet))
          return null;

    return new(loop, loop.Latch, next, start, step);
  }

  private static bool IsParallelBodyInstruction(IrInstruction instruction) => instruction switch {
    IrBinary { Op: not (IrBinaryOp.SDiv or IrBinaryOp.UDiv or IrBinaryOp.SRem or IrBinaryOp.URem or IrBinaryOp.FDiv) } => true,
    IrCmp => true,
    IrCast { Op: IrCastOp.Trunc or IrCastOp.ZExt or IrCastOp.SExt or IrCastOp.FPTrunc or IrCastOp.FPExt or IrCastOp.BitCast } => true,
    IrGep => true,
    IrSelect => true,
    IrLoad => true,
    IrStore => true,
    _ => false,
  };

  private static bool IsAvailableInOutlinedBody(
      IrValue value,
      IrPhi counter,
      HashSet<IrInstruction> body) => value switch {
    _ when ReferenceEquals(value, counter) => true,
    IrInstruction instruction => body.Contains(instruction),
    IrGlobalValue => true,
    IrConstantInt or IrConstantFloat or IrNullPtr or IrUndef => true,
    _ => false,
  };

  private static IrFunction OutlineIteration(IrModule module, Candidate candidate, ref int ordinal) {
    string name;
    do
      name = $"pb_parallel_body_{ordinal++}";
    while (module.FindFunction(name) is not null);

    var iteration = new IrArgument(IrType.I64, 0, "iteration");
    var helper = module.AddFunction(new IrFunction(name, IrType.Void, [iteration]) { NoInline = true });
    var entry = helper.CreateBlock("entry");
    IrValue counter = iteration;
    if (!Equals(candidate.Loop.Counter.Type, IrType.I64))
      counter = entry.Append(new IrCast(IrCastOp.Trunc, iteration, candidate.Loop.Counter.Type));

    var seed = new Dictionary<IrValue, IrValue>(ReferenceEqualityComparer.Instance) {
      [candidate.Loop.Counter] = counter,
    };
    var blocks = IrCloner.Clone(helper, [candidate.Body], seed, "body.", out var values);
    var clonedBody = blocks[candidate.Body];
    entry.Append(new IrBr(clonedBody));

    if (values.TryGetValue(candidate.Next, out var clonedNext) && clonedNext is IrInstruction next)
      next.EraseFromParent();
    clonedBody.Terminator?.EraseFromParent();
    clonedBody.Append(new IrRet());
    return helper;
  }

  private static void Apply(
      Candidate candidate,
      IrFunction helper,
      IrFunction shouldRun,
      IrFunction parallelFor) {
    var loop = candidate.Loop;
    var preheader = loop.Preheader;
    preheader.Terminator!.EraseFromParent();

    var trips = new IrConstantInt(IrType.I64, loop.Trips);
    var should = preheader.Append(new IrCall(IrType.I32, shouldRun, [trips]));
    var condition = preheader.Append(new IrCmp(IrCmpPred.Ne, should, new IrConstantInt(IrType.I32, 0)));
    var parallel = preheader.Parent!.CreateBlock(loop.Header.Label + ".parallel");
    preheader.Append(new IrCondBr(condition, parallel, loop.Header));

    parallel.Append(new IrCall(IrType.Void, parallelFor, [
      new IrConstantInt(IrType.I64, Signed(candidate.Start)),
      new IrConstantInt(IrType.I64, Signed(candidate.Step)),
      trips,
      helper,
    ]));
    parallel.Append(new IrBr(loop.Exit));
  }

  private static IrFunction GetOrCreateShouldRun(IrModule module)
    => module.FindFunction("rt_parallel_should_run") ?? module.AddFunction(new IrFunction(
      "rt_parallel_should_run",
      IrType.I32,
      [new IrArgument(IrType.I64, 0, "trips")]));

  private static IrFunction GetOrCreateParallelFor(IrModule module)
    => module.FindFunction("rt_parallel_for") ?? module.AddFunction(new IrFunction(
      "rt_parallel_for",
      IrType.Void,
      [
        new IrArgument(IrType.I64, 0, "first"),
        new IrArgument(IrType.I64, 1, "step"),
        new IrArgument(IrType.I64, 2, "trips"),
      ]) { IsVarArgs = true });

  private static long Signed(IrConstantInt value) => value.Type.Bits switch {
    8 => (sbyte)value.Value,
    16 => (short)value.Value,
    32 => (int)value.Value,
    _ => value.Value,
  };
}
