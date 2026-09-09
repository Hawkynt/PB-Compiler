namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Coalesces several bounded, straight-line string allocations into one preflighted DOS heap region.
///
/// <para>
/// PowerBASIC's string heap is already a bump arena: freeing a handle marks its block dead, while
/// physical bytes are recovered by the next compaction. O0289 exploits that representation instead of
/// inventing aliasing descriptors. <c>rt_str_coalesce_begin</c> proves the whole region's worst-case
/// byte demand fits after at most one compaction; the coalesced runtime entries then carve ordinary
/// <c>[handle,length]+bytes</c> blocks from that reserved tail and keep one descriptor-table scan cursor.
/// <c>rt_str_coalesce_end</c> closes the transaction. Individual handles therefore remain completely
/// ordinary: every existing consumer, free and later compaction keeps its exact semantics.
/// </para>
///
/// <para>
/// Only allocations with a compile-time upper bound participate. A non-coalesced runtime call is a
/// hard barrier because it may allocate or inspect <c>rt_strtop</c> itself. <c>rt_str_free</c> is the
/// sole transparent call: it cannot grow the heap and may only make the preflight more conservative.
/// Regions stay within one basic block and are capped to a positive signed word so the x86-16 runtime
/// ABI can carry their reservation exactly.
/// </para>
///
/// <para>
/// String-variable reads normally lower as <c>rt_str_dup(cell)</c> because consuming string routines
/// own their operands. For LEFT$/RIGHT$/MID$ a single-use dup immediately feeding the substring is
/// unnecessary once the selected coalesced entry is allowed to BORROW its source. Profitable regions
/// therefore erase that one copy and route the substring through a borrowed entry; no standalone dup
/// is removed, and an intervening call prevents the rewrite.
/// </para>
///
/// <para>
/// Functions with PB error handlers or inline assembly are skipped, matching the rest of the string
/// middle end. Either can transfer control around the synthetic end marker. Existing coalescing calls
/// and coalesced entries are barriers, which also makes the pass idempotent across repeated module
/// sweeps.
/// </para>
/// </summary>
public static class StringAllocationCoalescing {

  private const string _BEGIN = "rt_str_coalesce_begin";
  private const string _END = "rt_str_coalesce_end";
  private const string _FREE = "rt_str_free";
  private const string _DUP = "rt_str_dup";

  private const int _MIN_ALLOCATIONS = 3;
  private const int _MAX_RESERVATION = short.MaxValue;
  private const int _BLOCK_HEADER_BYTES = 4;

  private static readonly Dictionary<string, string> _coalesced = new(StringComparer.Ordinal) {
    ["rt_str_const"] = "rt_str_const_coalesced",
    ["rt_str_left"] = "rt_str_left_coalesced",
    ["rt_str_right"] = "rt_str_right_coalesced",
    ["rt_str_mid"] = "rt_str_mid_coalesced",
    ["rt_str_space"] = "rt_str_space_coalesced",
    ["rt_str_string"] = "rt_str_string_coalesced",
    ["rt_str_chr"] = "rt_str_chr_coalesced",
  };

  private static readonly Dictionary<string, string> _borrowedSubstring = new(StringComparer.Ordinal) {
    ["rt_str_left"] = "rt_str_left_borrow_coalesced",
    ["rt_str_right"] = "rt_str_right_borrow_coalesced",
    ["rt_str_mid"] = "rt_str_mid_borrow_coalesced",
  };

  private sealed record Candidate(
    IrCall Call,
    IrInstruction Start,
    int Capacity,
    string Replacement,
    IrCall? RemovedBorrow = null,
    IrValue? BorrowedSource = null);

  private sealed record Region(
    IrBasicBlock Block,
    IrInstruction First,
    IrInstruction EndAnchor,
    IReadOnlyList<Candidate> Candidates,
    int Capacity);

  /// <summary>Wraps profitable allocation regions across the module; returns the number of batches created.</summary>
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);

    var regions = new List<Region>();
    foreach (var function in module.Functions.ToList()) {
      if (function.IsDeclaration || function.HasErrorHandler || function.HasInlineAsm)
        continue;
      foreach (var block in function.Blocks)
        FindRegions(block, regions);
    }

    if (regions.Count == 0)
      return 0;

    var begin = Declare(module, _BEGIN, IrType.Void, IrType.I16);
    var end = Declare(module, _END, IrType.Void);

    foreach (var region in regions) {
      // Insert the markers before rewriting a leading dup: an inserted instruction remains at the
      // right position when the borrow immediately following it is erased.
      region.Block.InsertBefore(
        new IrCall(IrType.Void, begin, [new IrConstantInt(IrType.I16, region.Capacity)]),
        region.First);
      region.Block.InsertBefore(new IrCall(IrType.Void, end, []), region.EndAnchor);

      foreach (var candidate in region.Candidates) {
        var original = (IrFunction)candidate.Call.Callee;
        var replacement = Declare(module, candidate.Replacement, original.ReturnType,
          [.. original.Parameters.Select(parameter => parameter.Type)]);
        candidate.Call.SetOperand(0, replacement);

        if (candidate.RemovedBorrow is not { } borrow || candidate.BorrowedSource is not { } source)
          continue;

        candidate.Call.SetOperand(1, source);
        borrow.EraseFromParent();
      }
    }

    return regions.Count;
  }

  private static void FindRegions(IrBasicBlock block, ICollection<Region> regions) {
    var candidates = block.Instructions
      .OfType<IrCall>()
      .Select(call => CandidateFor(block, call))
      .Where(candidate => candidate is not null)
      .Cast<Candidate>()
      .ToDictionary(candidate => candidate.Call, ReferenceEqualityComparer.Instance);

    var borrowedStarts = candidates.Values
      .Where(candidate => candidate.RemovedBorrow is not null)
      .ToDictionary(candidate => (IrInstruction)candidate.RemovedBorrow!, ReferenceEqualityComparer.Instance);

    var current = new List<Candidate>();
    var capacity = 0;

    void Flush(IrInstruction anchor) {
      if (current.Count >= _MIN_ALLOCATIONS)
        regions.Add(new Region(block, current[0].Start, anchor, [.. current], capacity));
      current.Clear();
      capacity = 0;
    }

    void Add(Candidate candidate) {
      if (capacity + candidate.Capacity > _MAX_RESERVATION)
        Flush(candidate.Start);
      current.Add(candidate);
      capacity += candidate.Capacity;
    }

    foreach (var instruction in block.Instructions) {
      // A single-use dup belonging to a borrowed substring is part of that candidate, not a call
      // barrier. It is counted only when its consuming substring is reached below.
      if (borrowedStarts.ContainsKey(instruction))
        continue;

      if (instruction is not IrCall call) {
        if (instruction.IsTerminator)
          Flush(instruction);
        continue;
      }

      if (candidates.TryGetValue(call, out var candidate)) {
        Add(candidate);
        continue;
      }

      if (call.Callee is IrFunction { Name: _FREE })
        continue;

      Flush(call);
    }
  }

  private static Candidate? CandidateFor(IrBasicBlock block, IrCall call) {
    if (call.Callee is not IrFunction callee || !_coalesced.TryGetValue(callee.Name, out var replacement))
      return null;

    var payload = callee.Name switch {
      "rt_str_const" when call.ArgCount == 2 => PositiveBound(call.GetOperand(2)),
      "rt_str_left" or "rt_str_right" when call.ArgCount == 2 => PositiveBound(call.GetOperand(2)),
      "rt_str_mid" when call.ArgCount == 3 => PositiveBound(call.GetOperand(3)),
      "rt_str_space" or "rt_str_string" when call.ArgCount >= 1 => PositiveBound(call.GetOperand(1)),
      "rt_str_chr" when call.ArgCount == 1 => 1,
      _ => null,
    };
    if (payload is not { } bytes || bytes + _BLOCK_HEADER_BYTES > _MAX_RESERVATION)
      return null;

    if (_borrowedSubstring.TryGetValue(callee.Name, out var borrowedReplacement)
        && call.GetOperand(1) is IrCall { Callee: IrFunction { Name: _DUP }, ArgCount: 1 } borrow
        && borrow.Users.Count == 1
        && ReferenceEquals(borrow.Parent, block)
        && NoCallBetween(block, borrow, call))
      return new Candidate(call, borrow, bytes + _BLOCK_HEADER_BYTES, borrowedReplacement,
        borrow, borrow.GetOperand(1));

    return new Candidate(call, call, bytes + _BLOCK_HEADER_BYTES, replacement);
  }

  private static int? PositiveBound(IrValue value)
    => value is IrConstantInt { Value: > 0 and <= short.MaxValue } constant
      ? (int)constant.Value
      : null;

  private static bool NoCallBetween(IrBasicBlock block, IrInstruction first, IrInstruction last) {
    var from = IndexIn(block, first);
    var to = IndexIn(block, last);
    if (from < 0 || to <= from)
      return false;
    for (var i = from + 1; i < to; ++i)
      if (block.Instructions[i] is IrCall)
        return false;
    return true;
  }

  private static int IndexIn(IrBasicBlock block, IrInstruction instruction) {
    for (var i = 0; i < block.Instructions.Count; ++i)
      if (ReferenceEquals(block.Instructions[i], instruction))
        return i;
    return -1;
  }

  private static IrFunction Declare(IrModule module, string name, IrType returnType, params IrType[] parameters)
    => module.FindFunction(name)
       ?? module.AddFunction(new IrFunction(name, returnType,
         parameters.Select((type, index) => new IrArgument(type, index))));
}
