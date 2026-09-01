namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Scalar-replaces non-escaping byte-backed aggregates whose observed accesses name independent,
/// statically known regions.
///
/// <para>
/// UDT lowering deliberately starts from a representation that is always correct: one packed
/// <c>alloca i8, N</c> plus typed loads/stores at byte offsets. That representation also hides field
/// independence from <see cref="Mem2Reg"/>, because a GEP makes the backing allocation escape that
/// pass's direct-load/store model. This pass proves the missing fact and replaces each independent
/// byte region with a typed scalar slot. The following mem2reg sweep can then erase those slots into
/// SSA values entirely.
/// </para>
///
/// <para>
/// <see cref="AggregateBlockScalarization"/> runs first to decompose exact whole-record copies and
/// equality tests whose surrounding typed accesses prove a complete byte layout. Anything it cannot
/// prove remains a whole-object user and therefore keeps the aggregate materialized here.
/// </para>
///
/// <para>
/// Overlap is a hard boundary rather than an optimization opportunity. UNION fields deliberately
/// alias the same bytes, so two distinct accessed regions that intersect keep their shared backing
/// store. Likewise, a dynamic offset, an escaping pointer, a remaining whole-object operation, or an
/// access outside the allocation makes the object ineligible.
/// </para>
/// </summary>
public static class ScalarReplaceAggregates {

  private readonly record struct Region(long Offset, IrType Type, int Size) {
    public long End => this.Offset + this.Size;
  }

  public static int Run(IrFunction fn) {
    if (fn.HasErrorHandler)
      return 0;

    var split = AggregateBlockScalarization.Run(fn);
    foreach (var block in fn.Blocks.ToList())
      foreach (var instruction in block.Instructions.ToList())
        if (instruction is IrAlloca { Allocated: var allocated, Count: > 1 } alloca
            && allocated == IrType.I8
            && TryDescribe(alloca, out var regions)
            && Split(alloca, regions))
          ++split;
    return split;
  }

  /// <summary>
  /// Describes every typed byte region reached from the backing allocation and rejects anything that
  /// would make independent scalar slots unsound.
  /// </summary>
  private static bool TryDescribe(IrAlloca alloca, out List<Region> regions) {
    regions = [];

    foreach (var user in alloca.Users)
      switch (user) {
        case IrLoad or IrStore:
          if (!TryAccessRegion(user, alloca, 0, alloca.Count, out var direct))
            return false;
          regions.Add(direct);
          break;

        case IrGep { ElementType: null, ByteOffset: IrConstantInt constant } gep:
          // A preceding fold can leave the address calculation behind after all of its memory users
          // disappeared. The GEP itself has no side effect and Split removes it with the aggregate,
          // so an empty use-list says nothing about aliasing and must not make an otherwise-local
          // record look as though its address escaped.
          foreach (var indexed in gep.Users) {
            if (!TryAccessRegion(indexed, gep, constant.Value, alloca.Count, out var region))
              return false;
            regions.Add(region);
          }
          break;

        default:
          return false;                           // escape, whole-object use, phi, nested address, ...
      }

    if (regions.Count == 0)
      return false;

    var distinct = regions.Distinct().OrderBy(r => r.Offset).ThenBy(r => r.End).ToList();
    for (var i = 0; i < distinct.Count; ++i)
      for (var j = i + 1; j < distinct.Count && distinct[j].Offset < distinct[i].End; ++j)
        if (distinct[i] != distinct[j])
          return false;                           // UNION/type-pun overlap must stay shared storage

    regions = distinct;
    return true;
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
    _ => 0,                                      // pointer width is target-dependent
  };

  private static bool Split(IrAlloca alloca, IReadOnlyList<Region> regions) {
    var parent = alloca.Parent;
    if (parent is null)
      return false;

    var at = -1;
    for (var i = 0; i < parent.Instructions.Count; ++i)
      if (ReferenceEquals(parent.Instructions[i], alloca)) {
        at = i;
        break;
      }
    if (at < 0)
      return false;

    var slots = new Dictionary<Region, IrAlloca>();
    for (var i = 0; i < regions.Count; ++i) {
      var region = regions[i];
      var slot = new IrAlloca(region.Type) {
        Name = $"{alloca.Name ?? "agg"}.@{region.Offset}",
        IsSourceVariable = alloca.IsSourceVariable,
      };
      slots[region] = parent.InsertAt(at + i + 1, slot);
    }

    foreach (var user in alloca.Users.ToList())
      switch (user) {
        case IrLoad load:
          load.SetOperand(0, slots[RegionFor(load, 0)]);
          break;
        case IrStore store:
          store.SetOperand(1, slots[RegionFor(store, 0)]);
          break;
        case IrGep gep:
          var offset = ((IrConstantInt)gep.ByteOffset).Value;
          foreach (var indexed in gep.Users.ToList())
            switch (indexed) {
              case IrLoad load:
                load.SetOperand(0, slots[RegionFor(load, offset)]);
                break;
              case IrStore store:
                store.SetOperand(1, slots[RegionFor(store, offset)]);
                break;
            }
          gep.EraseFromParent();
          break;
      }

    if (!alloca.HasNoUsers)
      return false;                               // TryDescribe makes this unreachable; fail closed
    alloca.EraseFromParent();
    return true;
  }

  private static Region RegionFor(IrInstruction access, long offset) {
    var type = access switch {
      IrLoad load => load.Type,
      IrStore store => store.Value.Type,
      _ => throw new InvalidOperationException("aggregate region requested for a non-memory access"),
    };
    return new Region(offset, type, SizeOf(type));
  }
}
