namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Decomposes fixed-size whole-record copies and equality tests into scalar memory operations when
/// the surrounding typed accesses prove a complete, independent byte partition of the record.
///
/// <para>
/// Whole UDT assignment and BYVAL lowering deliberately use <c>llvm.memcpy</c>, and UDT equality uses
/// <c>rt_mem_compare</c>, because those operations preserve the record's observable bytes without
/// inventing field semantics. That conservative representation also makes an otherwise non-escaping
/// record look escaped to <see cref="ScalarReplaceAggregates"/>. This pass removes that barrier only
/// when the record's ordinary field accesses prove exactly how every byte is partitioned.
/// </para>
///
/// <para>
/// The proof is deliberately stricter than "these fields look useful". Regions must cover the whole
/// copied/compared extent without gaps or overlap, every region must have one storage type, and every
/// other use of a local backing allocation must be either a typed scalar access or another exact
/// whole-object copy/comparison. Padding, UNION type-punning, dynamic offsets, nested addresses,
/// pointer-width fields and other escapes therefore keep the original byte operation.
/// </para>
///
/// <para>
/// Equality remains byte equality. Integer field equality is bit equality, so a complete integer-only
/// partition can replace <c>rt_mem_compare(...) == 0</c> with the conjunction of per-region equality
/// tests. Floating regions are intentionally left to <c>rt_mem_compare</c>: IEEE equality does not
/// preserve raw-bit semantics for signed zero or NaNs, and inventing a numeric comparison there would
/// be a miscompile disguised as scalarization.
/// </para>
/// </summary>
public static class AggregateBlockScalarization {

  private const string _MEMCPY = "llvm.memcpy.p0.p0.i32";
  private const string _MEM_COMPARE = "rt_mem_compare";

  private readonly record struct Region(long Offset, IrType Type, int Size) {
    public long End => this.Offset + this.Size;
  }

  /// <summary>Scalarizes qualifying block operations; returns the number of calls removed.</summary>
  public static int Run(IrFunction fn) {
    if (fn.HasErrorHandler || fn.HasInlineAsm)
      return 0;

    var changes = 0;
    foreach (var call in fn.AllInstructions.OfType<IrCall>().ToList()) {
      if (TryScalarizeCopy(call) || TryScalarizeEquality(call))
        ++changes;
    }
    return changes;
  }

  private static bool TryScalarizeCopy(IrCall call) {
    if (call.Callee is not IrFunction { Name: _MEMCPY } || call.ArgCount != 4)
      return false;
    var args = call.Args.ToArray();
    if (args[2] is not IrConstantInt { Value: > 0 and <= int.MaxValue } sizeConstant
        || args[3] is not IrConstantInt { Value: 0 })
      return false;                                  // dynamic-size or volatile copy

    var bytes = (int)sizeConstant.Value;
    var destination = args[0];
    var source = args[1];
    if (destination is not IrAlloca { Allocated: var allocated, Count: var count }
        || allocated != IrType.I8 || count != bytes)
      return false;                                  // only a whole local packed record can disappear

    if (ReferenceEquals(destination, source)) {
      call.EraseFromParent();                        // source-level self-assignment is a byte-for-byte no-op
      return true;
    }

    if (source is not IrAlloca and not IrArgument)
      return false;                                  // keep address arithmetic / globals / unknown storage conservative
    if (source is IrAlloca sourceAlloca
        && (sourceAlloca.Allocated != IrType.I8 || sourceAlloca.Count != bytes))
      return false;

    if (!TryCompleteLayout(bytes, destination as IrAlloca, source as IrAlloca, out var layout))
      return false;

    var block = call.Parent;
    if (block is null)
      return false;
    var at = IndexOf(block, call);
    if (at < 0)
      return false;

    // Load the complete source value before writing any destination region. memcpy requires
    // non-overlap, but this ordering also preserves the intuitive whole-copy snapshot if a malformed
    // producer ever hands us aliases the intrinsic itself would not permit.
    var values = new List<(Region Region, IrValue Value)>(layout.Count);
    foreach (var region in layout) {
      var sourceAddress = InsertAddress(block, ref at, source, region.Offset);
      var value = block.InsertAt(at++, new IrLoad(region.Type, sourceAddress));
      values.Add((region, value));
    }
    foreach (var (region, value) in values) {
      var destinationAddress = InsertAddress(block, ref at, destination, region.Offset);
      block.InsertAt(at++, new IrStore(value, destinationAddress));
    }

    call.EraseFromParent();
    return true;
  }

  private static bool TryScalarizeEquality(IrCall call) {
    if (call.Callee is not IrFunction { Name: _MEM_COMPARE } || call.ArgCount != 3)
      return false;
    var args = call.Args.ToArray();
    if (args[2] is not IrConstantInt { Value: > 0 and <= int.MaxValue } sizeConstant)
      return false;
    if (call.Users.Count == 0 || !call.Users.All(IsZeroEqualityTest))
      return false;                                  // ordering users still need the three-way memcmp result

    var bytes = (int)sizeConstant.Value;
    var left = args[0];
    var right = args[1];
    if (!IsComparablePointer(left, bytes) || !IsComparablePointer(right, bytes))
      return false;
    var leftAlloca = left as IrAlloca;
    var rightAlloca = right as IrAlloca;
    if (leftAlloca is null && rightAlloca is null)
      return false;                                  // no local observations from which to recover a layout
    if (!TryCompleteLayout(bytes, leftAlloca, rightAlloca, out var layout))
      return false;
    if (layout.Any(region => region.Type.Kind != IrTypeKind.Int))
      return false;                                  // float equality is not raw-bit equality

    var block = call.Parent;
    if (block is null)
      return false;
    var at = IndexOf(block, call);
    if (at < 0)
      return false;

    IrValue? allEqual = null;
    foreach (var region in layout) {
      var leftAddress = InsertAddress(block, ref at, left, region.Offset);
      var leftValue = block.InsertAt(at++, new IrLoad(region.Type, leftAddress));
      var rightAddress = InsertAddress(block, ref at, right, region.Offset);
      var rightValue = block.InsertAt(at++, new IrLoad(region.Type, rightAddress));
      var equal = block.InsertAt(at++, new IrCmp(IrCmpPred.Eq, leftValue, rightValue));
      allEqual = allEqual is null
        ? equal
        : block.InsertAt(at++, new IrBinary(IrBinaryOp.And, allEqual, equal));
    }

    if (allEqual is null)
      return false;

    IrValue? notEqual = null;
    foreach (var user in call.Users.ToList()) {
      var cmp = (IrCmp)user;
      var replacement = cmp.Pred == IrCmpPred.Eq
        ? allEqual
        : notEqual ??= block.InsertAt(at++, new IrBinary(IrBinaryOp.Xor, allEqual, new IrConstantInt(IrType.I1, 1)));
      cmp.ReplaceAllUsesWith(replacement);
      cmp.EraseFromParent();
    }
    call.EraseFromParent();
    return true;
  }

  private static bool IsComparablePointer(IrValue pointer, int bytes) => pointer switch {
    IrAlloca { Allocated: var allocated, Count: var count } => allocated == IrType.I8 && count == bytes,
    IrArgument => true,
    _ => false,
  };

  private static bool IsZeroEqualityTest(IrInstruction user)
    => user is IrCmp { Pred: IrCmpPred.Eq or IrCmpPred.Ne } cmp
       && (cmp.Lhs is IrConstantInt { Value: 0 } || cmp.Rhs is IrConstantInt { Value: 0 });

  /// <summary>
  /// Merges the typed observations of up to two local records into one exact byte layout. A record may
  /// contribute no fields itself (a BYVAL source parameter, for example); the other side can prove the
  /// layout for both because the block operation already establishes that exactly <paramref name="bytes"/>
  /// bytes are transferred/compared.
  /// </summary>
  private static bool TryCompleteLayout(int bytes, IrAlloca? first, IrAlloca? second, out List<Region> layout) {
    layout = [];
    var allocas = new List<IrAlloca>(2);
    if (first is not null)
      allocas.Add(first);
    if (second is not null && !allocas.Any(existing => ReferenceEquals(existing, second)))
      allocas.Add(second);

    foreach (var alloca in allocas) {
      if (!TryObservedRegions(alloca, out var observed))
        return false;
      layout.AddRange(observed);
    }

    var distinct = layout.Distinct().OrderBy(region => region.Offset).ThenBy(region => region.End).ToList();
    if (distinct.Count == 0)
      return false;

    long cursor = 0;
    foreach (var region in distinct) {
      if (region.Offset != cursor || region.End > bytes)
        return false;                                // gap, overlap, conflicting type, or out of bounds
      cursor = region.End;
    }
    if (cursor != bytes)
      return false;

    layout = distinct;
    return true;
  }

  /// <summary>
  /// Recovers typed scalar regions from one byte-backed local. Exact whole-object operations are
  /// transparent to this analysis because they contribute no field type; every other non-scalar use
  /// is an escape and rejects the layout.
  /// </summary>
  private static bool TryObservedRegions(IrAlloca alloca, out List<Region> regions) {
    regions = [];

    foreach (var user in alloca.Users)
      switch (user) {
        case IrLoad or IrStore:
          if (!TryAccessRegion(user, alloca, 0, alloca.Count, out var direct))
            return false;
          regions.Add(direct);
          break;

        case IrGep { ElementType: null, ByteOffset: IrConstantInt constant } gep:
          if (gep.Users.Count == 0)
            return false;
          foreach (var indexed in gep.Users) {
            if (!TryAccessRegion(indexed, gep, constant.Value, alloca.Count, out var region))
              return false;
            regions.Add(region);
          }
          break;

        case IrCall call when IsExactWholeObjectCall(call, alloca):
          break;

        default:
          return false;
      }

    var distinct = regions.Distinct().OrderBy(region => region.Offset).ThenBy(region => region.End).ToList();
    for (var i = 0; i < distinct.Count; ++i)
      for (var j = i + 1; j < distinct.Count && distinct[j].Offset < distinct[i].End; ++j)
        if (distinct[i] != distinct[j])
          return false;                              // overlapping UNION/type-pun views

    regions = distinct;
    return true;
  }

  private static bool IsExactWholeObjectCall(IrCall call, IrAlloca alloca) {
    var args = call.Args.ToArray();
    return call.Callee switch {
      IrFunction { Name: _MEMCPY } when args.Length == 4
        => (ReferenceEquals(args[0], alloca) || ReferenceEquals(args[1], alloca))
           && args[2] is IrConstantInt { Value: var bytes } && bytes == alloca.Count
           && args[3] is IrConstantInt { Value: 0 },
      IrFunction { Name: _MEM_COMPARE } when args.Length == 3
        => (ReferenceEquals(args[0], alloca) || ReferenceEquals(args[1], alloca))
           && args[2] is IrConstantInt { Value: var bytes } && bytes == alloca.Count,
      _ => false,
    };
  }

  private static bool TryAccessRegion(
      IrInstruction instruction,
      IrValue pointer,
      long offset,
      int allocationSize,
      out Region region) {
    IrType? type = instruction switch {
      IrLoad load when ReferenceEquals(load.Pointer, pointer) => load.Type,
      IrStore store when ReferenceEquals(store.Pointer, pointer) && !ReferenceEquals(store.Value, pointer) => store.Value.Type,
      _ => null,
    };

    var size = type is null ? 0 : SizeOf(type);
    if (type is null || size == 0 || offset < 0 || offset > allocationSize - size) {
      region = default;
      return false;
    }

    region = new Region(offset, type, size);
    return true;
  }

  private static int SizeOf(IrType type) => type.Kind switch {
    IrTypeKind.Int or IrTypeKind.Float => Math.Max(1, (type.Bits + 7) / 8),
    _ => 0,                                          // pointer width is target-dependent
  };

  private static IrValue InsertAddress(IrBasicBlock block, ref int at, IrValue basePointer, long offset)
    => offset == 0
      ? basePointer
      : block.InsertAt(at++, new IrGep(basePointer, new IrConstantInt(IrType.I32, offset)));

  private static int IndexOf(IrBasicBlock block, IrInstruction instruction) {
    for (var i = 0; i < block.Instructions.Count; ++i)
      if (ReferenceEquals(block.Instructions[i], instruction))
        return i;
    return -1;
  }
}
