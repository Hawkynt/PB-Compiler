using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0057 — stores a private scalar integer in the narrowest representation proven to hold every
/// reaching value, while keeping all computation at the source width.
///
/// <para>
/// The representation change is deliberately confined to direct, mem2reg-promotable slots. An
/// address escape, a GEP, an incompatible memory view, or any other observable storage shape makes
/// the pass decline the slot. Stores truncate only after the original-width value has been computed;
/// loads immediately sign- or zero-extend back to the original width. PB's original wrapping and
/// numeric-error point therefore do not move to a narrower arithmetic operation.
/// </para>
/// </summary>
public static class StorageNarrowing {

  /// <summary>
  /// Narrows eligible scalar integer slots; <paramref name="minimumIntegerBits"/> is the backend's
  /// profitability floor (16 for x86-16, 8 for targets where byte storage is worthwhile).
  /// </summary>
  public static int Run(IrFunction fn, int minimumIntegerBits = 16) {
    ArgumentNullException.ThrowIfNull(fn);
    if (minimumIntegerBits is not (8 or 16 or 32 or 64))
      throw new ArgumentOutOfRangeException(nameof(minimumIntegerBits), minimumIntegerBits,
        "integer storage width must be 8, 16, 32, or 64 bits");
    if (fn.Entry is null || IrRangeAnalysis.Build(fn) is not { } ranges)
      return 0;

    var changed = 0;
    foreach (var slot in fn.AllInstructions.OfType<IrAlloca>().ToList()) {
      if (slot.Count != 1 || !slot.Allocated.IsInteger || slot.Allocated.Bits <= minimumIntegerBits
          || !Mem2Reg.IsPromotable(slot))
        continue;

      // The IR has PB's zero-initialization semantics: a read before the first explicit store is zero.
      var range = new ValueRange(0, 0);
      var valid = true;
      foreach (var store in slot.Users.OfType<IrStore>()) {
        if (store.Parent is null) {
          valid = false;
          break;
        }
        range = range.Join(ranges.RangeAt(store.Value, store.Parent));
      }
      if (!valid)
        continue;

      var storageType = Narrowest(slot.Allocated, range, minimumIntegerBits);
      if (storageType.SameStorage(slot.Allocated))
        continue;

      Rewrite(slot, storageType);
      ++changed;
    }
    return changed;
  }

  private static IrType Narrowest(IrType original, ValueRange range, int minimumIntegerBits) {
    if (range.IsTop || range.IsEmpty)
      return original;

    foreach (var bits in new[] { 8, 16, 32 }) {
      if (bits < minimumIntegerBits)
        continue;
      if (bits >= original.Bits)
        break;
      var candidate = IrType.Integer(bits, signed: range.Lo < 0);
      var bounds = ValueRange.OfType(candidate);
      if (range.Lo >= bounds.Lo && range.Hi <= bounds.Hi)
        return candidate;
    }
    return original;
  }

  private static void Rewrite(IrAlloca slot, IrType storageType) {
    var block = slot.Parent ?? throw new InvalidOperationException("storage slot has no parent block");
    var replacement = block.InsertBefore(new IrAlloca(storageType) {
      Count = slot.Count,
      IsSourceVariable = slot.IsSourceVariable,
      Name = slot.Name,
    }, slot);

    foreach (var user in slot.Users.ToList())
      switch (user) {
        case IrLoad load when load.Parent is { } loadBlock: {
          var narrow = loadBlock.InsertBefore(new IrLoad(storageType, replacement), load);
          var extension = storageType.Signed ? IrCastOp.SExt : IrCastOp.ZExt;
          var widened = loadBlock.InsertBefore(new IrCast(extension, narrow, load.Type), load);
          load.ReplaceAllUsesWith(widened);
          load.EraseFromParent();
          break;
        }
        case IrStore store when store.Parent is { } storeBlock: {
          var narrow = storeBlock.InsertBefore(new IrCast(IrCastOp.Trunc, store.Value, storageType), store);
          storeBlock.InsertBefore(new IrStore(narrow, replacement), store);
          store.EraseFromParent();
          break;
        }
      }

    slot.EraseFromParent();
  }
}
