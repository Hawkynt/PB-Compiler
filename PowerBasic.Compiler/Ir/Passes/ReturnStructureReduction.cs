namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0281 — removes writes to scalar regions of a hidden structure-return buffer that no known caller
/// can observe.
///
/// <para>
/// PB 3.6 lowers a UDT-returning FUNCTION to a void IR function with a hidden trailing BYREF
/// <c>$sret</c> pointer. This pass keeps that ABI intact and performs the return-side counterpart of
/// argument structure reduction: it enumerates every direct call, proves each result buffer is a
/// non-escaping byte-backed local aggregate, unions the regions callers read, and removes callee
/// stores to regions outside that union.
/// </para>
///
/// <para>
/// The proof is deliberately conservative. Dynamic offsets, pointer-sized fields, whole-object
/// operations, pointer escape, hidden control flow, inline assembly, mismatched result sizes, or an
/// address-taken callee make the candidate ineligible. Removing only the store is also deliberate:
/// ordinary DCE removes a now-unused pure producer chain, while calls remain because their side
/// effects are observable.
/// </para>
///
/// <para>
/// Returning the surviving fields in registers is O0282's calling-convention specialization. O0281
/// only establishes which result regions are actually demanded and stops computing/storing the rest.
/// </para>
/// </summary>
public static class ReturnStructureReduction {

  private const string StructReturnParameterName = "$sret";

  private readonly record struct Region(long Offset, int Size) {
    public long End => this.Offset + this.Size;

    public bool Overlaps(Region other) => this.Offset < other.End && other.Offset < this.End;
  }

  private readonly record struct Write(IrStore Store, Region Region);

  /// <summary>Reduces every eligible structure-returning function; returns the number of stores removed.</summary>
  public static int Run(IrModule module) {
    var removed = 0;
    foreach (var function in module.Functions.ToList()) {
      if (function.IsDeclaration || function.HasErrorHandler || function.HasInlineAsm)
        continue;
      var sret = StructReturnParameter(function);
      if (sret is null || !IsFullyVisible(module, function))
        continue;

      var calls = CallsTo(function).ToList();
      if (calls.Count == 0
          || !TryDescribeCallers(function, sret, calls, out var resultSize, out var observed)
          || !TryDescribeCallee(sret, resultSize, out var writes, out var internallyRead))
        continue;

      foreach (var write in writes)
        if (!observed.Any(write.Region.Overlaps) && !internallyRead.Any(write.Region.Overlaps)) {
          write.Store.EraseFromParent();
          ++removed;
        }
    }
    return removed;
  }

  private static IrArgument? StructReturnParameter(IrFunction function) {
    if (!function.ReturnType.IsVoid || function.Parameters.Count == 0)
      return null;
    var parameter = function.Parameters[^1];
    return parameter.Type.IsPointer && parameter.Name == StructReturnParameterName ? parameter : null;
  }

  /// <summary>
  /// The transform needs a closed call graph for the candidate. An address-taken function, the entry
  /// point, or a call from outside this module invalidates the caller census.
  /// </summary>
  private static bool IsFullyVisible(IrModule module, IrFunction function) {
    if (function.Name.Equals("main", StringComparison.OrdinalIgnoreCase))
      return false;
    foreach (var user in function.Users)
      if (user is not IrCall call || !ReferenceEquals(call.Callee, function))
        return false;
    foreach (var user in function.Users) {
      var owner = user.Parent?.Parent;
      if (owner is null || !module.Functions.Contains(owner))
        return false;
    }
    return true;
  }

  private static IEnumerable<IrCall> CallsTo(IrFunction function)
    => function.Users.OfType<IrCall>().Where(call => ReferenceEquals(call.Callee, function));

  /// <summary>
  /// Proves every call writes into a same-sized local byte aggregate and collects every scalar region
  /// read from those aggregates anywhere in their owning functions. Counting reads before a call or
  /// after a later overwrite can only retain extra stores, never remove a required one.
  /// </summary>
  private static bool TryDescribeCallers(
      IrFunction function,
      IrArgument sret,
      IReadOnlyList<IrCall> calls,
      out int resultSize,
      out List<Region> observed) {
    resultSize = 0;
    observed = [];
    var allocations = new HashSet<IrAlloca>(ReferenceEqualityComparer.Instance);

    foreach (var call in calls) {
      var owner = call.Parent?.Parent;
      if (owner is null || owner.HasErrorHandler || owner.HasInlineAsm || sret.Index >= call.ArgCount)
        return false;
      if (call.Operands[sret.Index + 1] is not IrAlloca { Allocated: var allocated, Count: > 0 } alloca
          || allocated != IrType.I8)
        return false;
      if (resultSize == 0)
        resultSize = alloca.Count;
      else if (resultSize != alloca.Count)
        return false;
      allocations.Add(alloca);
    }

    if (resultSize == 0)
      return false;
    foreach (var allocation in allocations)
      if (!TryCollectCallerReads(allocation, function, sret.Index, resultSize, observed))
        return false;
    return true;
  }

  private static bool TryCollectCallerReads(
      IrAlloca allocation,
      IrFunction callee,
      int sretIndex,
      int resultSize,
      List<Region> reads) {
    var visited = new HashSet<IrValue>(ReferenceEqualityComparer.Instance);
    return Walk(allocation, 0);

    bool Walk(IrValue pointer, long offset) {
      if (!visited.Add(pointer))
        return true;

      foreach (var user in pointer.Users.ToList())
        switch (user) {
          case IrGep { ElementType: null, ByteOffset: IrConstantInt constant } gep
              when ReferenceEquals(gep.BasePtr, pointer):
            if (!TryOffset(offset, constant.Value, resultSize, gep.HasNoUsers, out var indexed)
                || !Walk(gep, indexed))
              return false;
            break;

          case IrLoad load when ReferenceEquals(load.Pointer, pointer):
            if (!TryRegion(offset, load.Type, resultSize, out var read))
              return false;
            reads.Add(read);
            break;

          case IrStore store when ReferenceEquals(store.Pointer, pointer):
            if (!TryRegion(offset, store.Value.Type, resultSize, out _))
              return false;
            break;                                      // caller writes do not make a result field demanded

          case IrCall call when ReferenceEquals(pointer, allocation)
              && IsOnlyStructReturnUse(call, allocation, callee, sretIndex):
            break;

          default:
            return false;                               // escape, dynamic GEP, whole-object use, phi, ...
        }
      return true;
    }
  }

  private static bool IsOnlyStructReturnUse(
      IrCall call,
      IrAlloca allocation,
      IrFunction callee,
      int sretIndex) {
    if (!ReferenceEquals(call.Callee, callee)
        || sretIndex >= call.ArgCount
        || !ReferenceEquals(call.Operands[sretIndex + 1], allocation))
      return false;

    for (var i = 1; i < call.Operands.Count; ++i)
      if (i != sretIndex + 1 && ReferenceEquals(call.Operands[i], allocation))
        return false;                                  // the result aliases another argument of this call
    return true;
  }

  /// <summary>Describes scalar reads/writes reached from the callee's hidden result pointer.</summary>
  private static bool TryDescribeCallee(
      IrArgument sret,
      int resultSize,
      out List<Write> writes,
      out List<Region> reads) {
    // A local function cannot capture an out parameter, so the lists are built through locals and
    // handed to the caller before the walk begins.
    var collectedWrites = new List<Write>();
    var collectedReads = new List<Region>();
    writes = collectedWrites;
    reads = collectedReads;
    var visited = new HashSet<IrValue>(ReferenceEqualityComparer.Instance);
    return Walk(sret, 0);

    bool Walk(IrValue pointer, long offset) {
      if (!visited.Add(pointer))
        return true;

      foreach (var user in pointer.Users.ToList())
        switch (user) {
          case IrGep { ElementType: null, ByteOffset: IrConstantInt constant } gep
              when ReferenceEquals(gep.BasePtr, pointer):
            if (!TryOffset(offset, constant.Value, resultSize, gep.HasNoUsers, out var indexed)
                || !Walk(gep, indexed))
              return false;
            break;

          case IrLoad load when ReferenceEquals(load.Pointer, pointer):
            if (!TryRegion(offset, load.Type, resultSize, out var read))
              return false;
            collectedReads.Add(read);
            break;

          case IrStore store when ReferenceEquals(store.Pointer, pointer):
            if (!TryRegion(offset, store.Value.Type, resultSize, out var write))
              return false;
            collectedWrites.Add(new Write(store, write));
            break;

          default:
            return false;                               // result address escaped or was used opaquely
        }
      return true;
    }
  }

  private static bool TryOffset(long baseOffset, long delta, int resultSize, bool unused, out long offset) {
    try {
      offset = checked(baseOffset + delta);
    } catch (OverflowException) {
      offset = 0;
      return false;
    }

    // An unused address calculation has no observable access; permit it so stale DCE residue does not
    // defeat an otherwise valid proof. Any used pointer must stay inside (or one byte past) the object.
    return unused || offset >= 0 && offset <= resultSize;
  }

  private static bool TryRegion(long offset, IrType type, int resultSize, out Region region) {
    var size = SizeOf(type);
    if (size == 0 || offset < 0 || offset > (long)resultSize - size) {
      region = default;
      return false;
    }
    region = new Region(offset, size);
    return true;
  }

  private static int SizeOf(IrType type) => type.Kind switch {
    IrTypeKind.Int or IrTypeKind.Float => Math.Max(1, (type.Bits + 7) / 8),
    _ => 0,                                            // pointer storage width is target-dependent
  };
}
