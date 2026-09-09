using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0309 — speculative integer narrowing. A cheap loop-invariant scalar range guard versions a
/// canonical loop into a narrow fast path and the untouched wide loop as its fallback.
/// </summary>
public static class SpeculativeIntegerNarrowing {

  private const int _MAX_INSTRUCTIONS = 96;
  private const int _MAX_GUARDS = 2;

  /// <summary>Versions at most one loop; the pass manager's fixpoint reaches further candidates.</summary>
  public static int Run(IrFunction fn) {
    ArgumentNullException.ThrowIfNull(fn);
    if (fn.Entry is null || fn.HasErrorHandler || fn.HasInlineAsm
        || IrRangeAnalysis.Build(fn) is not { } ranges)
      return 0;

    var addressTaken = fn.AddressTakenBlocks();
    foreach (var header in fn.Blocks.ToList())
      if (Match(fn, header, addressTaken) is { } loop
          && BuildPlan(loop, ranges) is { } plan) {
        Version(fn, plan, ranges);
        return 1;
      }

    return 0;
  }

  private sealed record Loop(
    IrBasicBlock Header,
    List<IrBasicBlock> Body,
    IrBasicBlock Latch,
    IrBasicBlock Preheader,
    IrBasicBlock Exit) {

    public IReadOnlyList<IrBasicBlock> Region => [this.Header, .. this.Body];
  }

  private sealed record NarrowingPlan(Loop Loop, List<IrInstruction> Candidates, List<IrValue> Guards);

  private static Loop? Match(IrFunction fn, IrBasicBlock header, HashSet<IrBasicBlock> addressTaken) {
    if (header.Terminator is not IrCondBr headerBranch)
      return null;

    var predecessors = header.Predecessors.ToList();
    if (predecessors.Count != 2)
      return null;

    var exit = headerBranch.IfFalse;
    var body = new List<IrBasicBlock>();
    var queue = new Queue<IrBasicBlock>([headerBranch.IfTrue]);
    IrBasicBlock? latch = null;

    while (queue.Count > 0) {
      var block = queue.Dequeue();
      if (ReferenceEquals(block, header) || ReferenceEquals(block, exit) || body.Contains(block))
        continue;

      body.Add(block);
      switch (block.Terminator) {
        case IrBr branch when ReferenceEquals(branch.Target, header):
          if (latch is not null && !ReferenceEquals(latch, block))
            return null;
          latch = block;
          break;

        case IrBr branch when ReferenceEquals(branch.Target, exit):
          return null;

        case IrBr branch:
          queue.Enqueue(branch.Target);
          break;

        case IrCondBr branch:
          if (ReferenceEquals(branch.IfTrue, header) || ReferenceEquals(branch.IfFalse, header)
              || ReferenceEquals(branch.IfTrue, exit) || ReferenceEquals(branch.IfFalse, exit))
            return null;
          queue.Enqueue(branch.IfTrue);
          queue.Enqueue(branch.IfFalse);
          break;

        default:
          return null;
      }
    }

    if (latch is null)
      return null;

    var preheader = predecessors.SingleOrDefault(block => !ReferenceEquals(block, latch));
    if (preheader?.Terminator is not IrBr preheaderBranch
        || !ReferenceEquals(preheaderBranch.Target, header))
      return null;

    var region = new List<IrBasicBlock>(body.Count + 1) { header };
    region.AddRange(body);
    var regionSet = new HashSet<IrBasicBlock>(region, ReferenceEqualityComparer.Instance);

    if (ReferenceEquals(exit, header) || regionSet.Contains(exit)
        || region.Sum(block => block.Instructions.Count) > _MAX_INSTRUCTIONS
        || region.Any(addressTaken.Contains)
        || region.SelectMany(block => block.Instructions).Any(instruction => !IsCloneable(instruction))
        || exit.Predecessors.Any(block => !ReferenceEquals(block, header)))
      return null;

    // Nothing outside may enter the middle of the cloned region. The header itself already has the
    // two predecessor proof above, so this only needs to fence the body.
    foreach (var block in fn.Blocks)
      if (!regionSet.Contains(block) && block.Successors.Any(body.Contains))
        return null;

    return new(header, body, latch, preheader, exit);
  }

  private static bool IsCloneable(IrInstruction instruction) => instruction is
    IrPhi or IrBinary or IrCmp or IrCast or IrAlloca or IrLoad or IrStore or IrGep or IrFarPtr
    or IrSelect or IrCall or IrRet or IrBr or IrCondBr or IrSwitch or IrIndirectBr or IrUnreachable;

  private static NarrowingPlan? BuildPlan(Loop loop, IrRangeAnalysis ranges) {
    var region = new HashSet<IrBasicBlock>(loop.Region, ReferenceEqualityComparer.Instance);
    var candidates = new List<IrInstruction>();
    var guards = new List<IrValue>();
    var guardSet = new HashSet<IrValue>(ReferenceEqualityComparer.Instance);

    foreach (var instruction in loop.Region.SelectMany(block => block.Instructions)) {
      if (!TryPlanCandidate(instruction, loop, region, ranges, out var candidateGuards))
        continue;

      var additional = candidateGuards.Count(value => !guardSet.Contains(value));
      if (guardSet.Count + additional > _MAX_GUARDS)
        continue;

      candidates.Add(instruction);
      foreach (var value in candidateGuards)
        if (guardSet.Add(value))
          guards.Add(value);
    }

    if (guards.Count == 0 || candidates.Count == 0)
      return null;

    // One cheap multiply can already repay the guard on a 16-bit target. For the cheaper operation
    // classes require at least two wins, which is the point of guarding a region rather than a value.
    if (candidates.Count < 2
        && !candidates.Any(instruction => instruction is IrBinary { Op: IrBinaryOp.Mul }))
      return null;

    return new(loop, candidates, guards);
  }

  private static bool TryPlanCandidate(
      IrInstruction instruction,
      Loop loop,
      HashSet<IrBasicBlock> region,
      IrRangeAnalysis ranges,
      out List<IrValue> candidateGuards) {
    candidateGuards = [];
    var guardSet = new HashSet<IrValue>(ReferenceEqualityComparer.Instance);
    var visiting = new HashSet<IrValue>(ReferenceEqualityComparer.Instance);

    switch (instruction) {
      case IrBinary binary when !binary.HasNoUsers && IsNarrowable(binary):
        if (RangeUnderGuard(binary.Lhs, binary.Parent!, loop, region, ranges, candidateGuards, guardSet, visiting)
              is not { } lhs
            || RangeUnderGuard(binary.Rhs, binary.Parent!, loop, region, ranges, candidateGuards, guardSet, visiting)
              is not { } rhs
            || !FitsWord(lhs, binary.Lhs.Type)
            || !FitsWord(rhs, binary.Rhs.Type))
          return false;

        return FitsWord(Evaluate(binary.Op, lhs, rhs).Fit(binary.Type), binary.Type);

      case IrCmp cmp when !cmp.HasNoUsers && IsNarrowable(cmp):
        return RangeUnderGuard(cmp.Lhs, cmp.Parent!, loop, region, ranges, candidateGuards, guardSet, visiting)
                 is { } cmpLhs
               && RangeUnderGuard(cmp.Rhs, cmp.Parent!, loop, region, ranges, candidateGuards, guardSet, visiting)
                 is { } cmpRhs
               && FitsWord(cmpLhs, cmp.Lhs.Type)
               && FitsWord(cmpRhs, cmp.Rhs.Type);

      default:
        return false;
    }
  }

  private static bool IsNarrowable(IrBinary binary)
    => binary.Type.IsInteger
       && binary.Type.Bits == 32
       && binary.Op is IrBinaryOp.Add or IrBinaryOp.Sub or IrBinaryOp.Mul
       && binary.Lhs.Type.IsInteger
       && binary.Rhs.Type.IsInteger
       && binary.Lhs.Type.Bits == 32
       && binary.Rhs.Type.Bits == 32
       && binary.Lhs.Type.Signed == binary.Type.Signed
       && binary.Rhs.Type.Signed == binary.Type.Signed;

  private static bool IsNarrowable(IrCmp cmp) {
    if (!cmp.Lhs.Type.IsInteger || !cmp.Rhs.Type.IsInteger
        || cmp.Lhs.Type.Bits != 32 || cmp.Rhs.Type.Bits != 32
        || cmp.Lhs.Type.Signed != cmp.Rhs.Type.Signed)
      return false;

    return cmp.Pred switch {
      IrCmpPred.Eq or IrCmpPred.Ne => true,
      IrCmpPred.Slt or IrCmpPred.Sle or IrCmpPred.Sgt or IrCmpPred.Sge => cmp.Lhs.Type.Signed,
      IrCmpPred.Ult or IrCmpPred.Ule or IrCmpPred.Ugt or IrCmpPred.Uge => !cmp.Lhs.Type.Signed,
      _ => false,
    };
  }

  private static ValueRange? RangeUnderGuard(
      IrValue value,
      IrBasicBlock at,
      Loop loop,
      HashSet<IrBasicBlock> region,
      IrRangeAnalysis ranges,
      List<IrValue> guards,
      HashSet<IrValue> guardSet,
      HashSet<IrValue> visiting) {
    if (!value.Type.IsInteger)
      return null;

    var known = ranges.RangeAt(value, at);
    if (FitsWord(known, value.Type))
      return known;
    if (value.Type.Bits != 32)
      return null;

    if (CanGuard(value, loop, region, ranges.Dominators)) {
      if (guardSet.Add(value))
        guards.Add(value);
      return known.Meet(WordRange(value.Type));
    }

    if (value is not IrBinary { Parent: { } parent } binary
        || !region.Contains(parent)
        || !IsNarrowable(binary)
        || !visiting.Add(value))
      return null;

    try {
      if (RangeUnderGuard(binary.Lhs, parent, loop, region, ranges, guards, guardSet, visiting) is not { } lhs
          || RangeUnderGuard(binary.Rhs, parent, loop, region, ranges, guards, guardSet, visiting) is not { } rhs)
        return null;
      return Evaluate(binary.Op, lhs, rhs).Fit(binary.Type);
    } finally {
      visiting.Remove(value);
    }
  }

  private static bool CanGuard(
      IrValue value,
      Loop loop,
      HashSet<IrBasicBlock> region,
      IrDominators dominators)
    => value switch {
      IrArgument => true,
      IrInstruction { Parent: { } block } => !region.Contains(block) && dominators.Dominates(block, loop.Preheader),
      _ => false,
    };

  private static ValueRange Evaluate(IrBinaryOp op, ValueRange lhs, ValueRange rhs) => op switch {
    IrBinaryOp.Add => lhs.Add(rhs),
    IrBinaryOp.Sub => lhs.Subtract(rhs),
    IrBinaryOp.Mul => lhs.Multiply(rhs),
    _ => ValueRange.Top,
  };

  private static ValueRange WordRange(IrType type)
    => type.Signed ? new(short.MinValue, short.MaxValue) : new(0, ushort.MaxValue);

  private static bool FitsWord(ValueRange range, IrType type) {
    if (range.IsEmpty || range.IsTop)
      return false;
    var word = WordRange(type);
    return range.Lo >= word.Lo && range.Hi <= word.Hi;
  }

  private static void Version(IrFunction fn, NarrowingPlan plan, IrRangeAnalysis ranges) {
    var loop = plan.Loop;
    var region = loop.Region;
    var regionSet = new HashSet<IrBasicBlock>(region, ReferenceEqualityComparer.Instance);
    var valueMap = new Dictionary<IrValue, IrValue>(ReferenceEqualityComparer.Instance);
    var fastBlocks = IrCloner.Clone(fn, region, valueMap, "narrow.", out _);
    var fastHeader = fastBlocks[loop.Header];

    foreach (var original in plan.Candidates)
      valueMap[original] = RewriteCandidate((IrInstruction)valueMap[original]);

    // Existing exit phis need one incoming for the new fast predecessor.
    foreach (var phi in loop.Exit.Phis.ToList())
      for (var i = 0; i < phi.IncomingBlocks.Count; ++i)
        if (ReferenceEquals(phi.IncomingBlocks[i], loop.Header)) {
          phi.AddIncoming(MapFast(phi.GetOperand(i), valueMap), fastHeader);
          break;
        }

    // Values used directly after the loop were valid before because the original header dominated
    // the exit. Versioning creates a second definition, so form the missing LCSSA join explicitly.
    foreach (var value in region.SelectMany(block => block.Instructions).Where(value => !value.Type.IsVoid)) {
      var outsideUsers = value.Users
        .Where(user => user.Parent is { } block
                       && !regionSet.Contains(block)
                       && !(ReferenceEquals(block, loop.Exit) && user is IrPhi))
        .ToList();
      if (outsideUsers.Count == 0)
        continue;

      var joined = loop.Exit.AppendPhi(new IrPhi(value.Type) { Name = value.Name });
      joined.AddIncoming(value, loop.Header);
      joined.AddIncoming(MapFast(value, valueMap), fastHeader);
      foreach (var user in outsideUsers)
        user.ReplaceOperand(value, joined);
    }

    var guard = BuildGuard(plan.Guards, loop.Preheader, ranges);
    loop.Preheader.Terminator!.EraseFromParent();
    loop.Preheader.Append(new IrCondBr(guard, fastHeader, loop.Header));
  }

  private static IrValue MapFast(IrValue value, Dictionary<IrValue, IrValue> valueMap)
    => valueMap.GetValueOrDefault(value, value);

  private static IrValue BuildGuard(IReadOnlyList<IrValue> roots, IrBasicBlock preheader, IrRangeAnalysis ranges) {
    var terminator = preheader.Terminator
      ?? throw new InvalidOperationException("speculative narrowing preheader lost its terminator");
    var checks = new List<IrValue>();

    foreach (var root in roots) {
      var known = ranges.RangeAt(root, preheader);
      var word = WordRange(root.Type);

      if (root.Type.Signed && known.Lo < word.Lo)
        checks.Add(preheader.InsertBefore(
          new IrCmp(IrCmpPred.Sge, root, new IrConstantInt(root.Type, word.Lo)), terminator));

      if (known.Hi > word.Hi)
        checks.Add(preheader.InsertBefore(
          new IrCmp(root.Type.Signed ? IrCmpPred.Sle : IrCmpPred.Ule,
            root, new IrConstantInt(root.Type, word.Hi)), terminator));
    }

    if (checks.Count == 0)
      throw new InvalidOperationException("speculative narrowing plan has no runtime guard");

    var guard = checks[0];
    for (var i = 1; i < checks.Count; ++i)
      guard = preheader.InsertBefore(new IrBinary(IrBinaryOp.And, guard, checks[i]), terminator);
    return guard;
  }

  private static IrValue RewriteCandidate(IrInstruction instruction) => instruction switch {
    IrBinary binary => RewriteBinary(binary),
    IrCmp cmp => RewriteComparison(cmp),
    _ => throw new InvalidOperationException($"cannot narrow {instruction.GetType().Name}"),
  };

  private static IrValue RewriteBinary(IrBinary binary) {
    var block = binary.Parent
      ?? throw new InvalidOperationException("cannot narrow a detached binary instruction");
    var narrowType = IrType.Integer(16, binary.Type.Signed);
    var lhs = block.InsertBefore(new IrCast(IrCastOp.Trunc, binary.Lhs, narrowType), binary);
    var rhs = block.InsertBefore(new IrCast(IrCastOp.Trunc, binary.Rhs, narrowType), binary);
    var narrow = block.InsertBefore(new IrBinary(binary.Op, lhs, rhs), binary);
    var widened = block.InsertBefore(
      new IrCast(binary.Type.Signed ? IrCastOp.SExt : IrCastOp.ZExt, narrow, binary.Type) { Name = binary.Name },
      binary);

    binary.ReplaceAllUsesWith(widened);
    binary.EraseFromParent();
    return widened;
  }

  private static IrValue RewriteComparison(IrCmp cmp) {
    var block = cmp.Parent
      ?? throw new InvalidOperationException("cannot narrow a detached comparison");
    var narrowType = IrType.Integer(16, cmp.Lhs.Type.Signed);
    var lhs = block.InsertBefore(new IrCast(IrCastOp.Trunc, cmp.Lhs, narrowType), cmp);
    var rhs = block.InsertBefore(new IrCast(IrCastOp.Trunc, cmp.Rhs, narrowType), cmp);
    var narrow = block.InsertBefore(new IrCmp(cmp.Pred, lhs, rhs) {
      IsSourceCondition = cmp.IsSourceCondition,
      Name = cmp.Name,
    }, cmp);

    cmp.ReplaceAllUsesWith(narrow);
    cmp.EraseFromParent();
    return narrow;
  }
}
