using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0057 — stores private scalar values in the narrowest representation proven to preserve every
/// reaching value, while keeping all computation at the source width.
///
/// <para>
/// Direct memory is deliberately confined to mem2reg-promotable slots. An address escape, a GEP, an
/// incompatible memory view, or any other observable storage shape makes the pass decline the slot.
/// After promotion, bounded integer phis and losslessly-single-representable floating phis are the SSA
/// form of the same storage decision and are narrowed at their incoming edges, then widened once at the
/// phi boundary. Ordinary users therefore keep the source width and its arithmetic/rounding semantics.
/// </para>
/// </summary>
public static class StorageNarrowing {

  private const long _SINGLE_EXACT_INTEGER = 1L << 24;

  /// <summary>
  /// Narrows eligible scalar slots and promoted phis; <paramref name="minimumIntegerBits"/> is the
  /// backend's integer profitability floor (16 for x86-16, 8 where byte storage is worthwhile).
  /// Floating storage narrows only from wider IEEE formats to binary32 and only when the value is
  /// proven to survive that round trip exactly.
  /// </summary>
  public static int Run(IrFunction fn, int minimumIntegerBits = 16) {
    ArgumentNullException.ThrowIfNull(fn);
    if (minimumIntegerBits is not (8 or 16 or 32 or 64))
      throw new ArgumentOutOfRangeException(nameof(minimumIntegerBits), minimumIntegerBits,
        "integer storage width must be 8, 16, 32, or 64 bits");
    if (fn.Entry is null || IrRangeAnalysis.Build(fn) is not { } ranges)
      return 0;

    var changed = NarrowIntegerSlots(fn, ranges, minimumIntegerBits);
    changed += NarrowFloatSlots(fn, ranges);
    changed += NarrowIntegerPhis(fn, ranges, minimumIntegerBits);
    changed += NarrowFloatPhis(fn, ranges);
    return changed;
  }

  private static int NarrowIntegerSlots(IrFunction fn, IrRangeAnalysis ranges, int minimumIntegerBits) {
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

      var storageType = NarrowestInteger(slot.Allocated, range, minimumIntegerBits);
      if (storageType.SameStorage(slot.Allocated))
        continue;

      RewriteSlot(slot, storageType);
      ++changed;
    }
    return changed;
  }

  private static int NarrowFloatSlots(IrFunction fn, IrRangeAnalysis ranges) {
    var changed = 0;
    foreach (var slot in fn.AllInstructions.OfType<IrAlloca>().ToList()) {
      if (slot.Count != 1 || !slot.Allocated.IsIeeeFloat || slot.Allocated.Bits <= 32
          || !Mem2Reg.IsPromotable(slot))
        continue;

      var exact = true;
      foreach (var store in slot.Users.OfType<IrStore>()) {
        if (store.Parent is not { } block || !ExactlyRepresentableAsSingle(store.Value, ranges, block, [])) {
          exact = false;
          break;
        }
      }
      if (!exact)
        continue;

      RewriteSlot(slot, IrType.F32);
      ++changed;
    }
    return changed;
  }

  private static int NarrowIntegerPhis(IrFunction fn, IrRangeAnalysis ranges, int minimumIntegerBits) {
    var changed = 0;
    foreach (var phi in fn.AllInstructions.OfType<IrPhi>().ToList()) {
      if (!phi.Type.IsInteger || phi.Type.Bits <= minimumIntegerBits || phi.Parent is null
          || phi.IncomingBlocks.Count == 0 || phi.IncomingBlocks.Count != phi.Operands.Count)
        continue;

      var storageType = NarrowestInteger(phi.Type, ranges.RangeOf(phi), minimumIntegerBits);
      if (storageType.SameStorage(phi.Type) || !CanRewritePhi(phi))
        continue;

      RewritePhi(phi, storageType);
      ++changed;
    }
    return changed;
  }

  private static int NarrowFloatPhis(IrFunction fn, IrRangeAnalysis ranges) {
    var changed = 0;
    foreach (var phi in fn.AllInstructions.OfType<IrPhi>().ToList()) {
      if (!phi.Type.IsIeeeFloat || phi.Type.Bits <= 32 || phi.Parent is null
          || phi.IncomingBlocks.Count == 0 || phi.IncomingBlocks.Count != phi.Operands.Count
          || !CanRewritePhi(phi))
        continue;

      var exact = true;
      for (var i = 0; i < phi.IncomingBlocks.Count; ++i) {
        var predecessor = phi.IncomingBlocks[i];
        if (!ExactlyRepresentableAsSingle(phi.GetOperand(i), ranges, predecessor, [])) {
          exact = false;
          break;
        }
      }
      if (!exact)
        continue;

      RewritePhi(phi, IrType.F32);
      ++changed;
    }
    return changed;
  }

  private static bool CanRewritePhi(IrPhi phi) {
    for (var i = 0; i < phi.IncomingBlocks.Count; ++i)
      if (phi.IncomingBlocks[i].Terminator is null || !phi.GetOperand(i).Type.SameStorage(phi.Type))
        return false;
    return true;
  }

  private static IrType NarrowestInteger(IrType original, ValueRange range, int minimumIntegerBits) {
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

  /// <summary>
  /// Proves that converting <paramref name="value"/> to IEEE binary32 and extending it back cannot
  /// change its numerical value. This is deliberately a precision-provenance proof, not a mere range
  /// test: a tiny DOUBLE result can still carry more than 24 significand bits.
  /// </summary>
  private static bool ExactlyRepresentableAsSingle(IrValue value, IrRangeAnalysis ranges,
      IrBasicBlock block, HashSet<IrValue> visiting) {
    if (!value.Type.IsIeeeFloat)
      return false;
    if (value.Type.Bits <= 32)
      return true;
    if (!visiting.Add(value))
      return false;

    try {
      switch (value) {
        case IrConstantFloat constant:
          return ExactlyRepresentableAsSingle(constant.Value);

        case IrCast { Op: IrCastOp.FPExt } cast:
          return ExactlyRepresentableAsSingle(cast.Value, ranges, block, visiting);

        case IrCast { Op: IrCastOp.SIToFP or IrCastOp.UIToFP } cast: {
          var range = ranges.RangeAt(cast.Value, block);
          return !range.IsTop && !range.IsEmpty
            && range.Lo >= -_SINGLE_EXACT_INTEGER && range.Hi <= _SINGLE_EXACT_INTEGER;
        }

        case IrSelect select:
          return ExactlyRepresentableAsSingle(select.IfTrue, ranges, block, visiting)
            && ExactlyRepresentableAsSingle(select.IfFalse, ranges, block, visiting);

        case IrPhi phi when phi.IncomingBlocks.Count > 0 && phi.IncomingBlocks.Count == phi.Operands.Count:
          for (var i = 0; i < phi.IncomingBlocks.Count; ++i)
            if (!ExactlyRepresentableAsSingle(phi.GetOperand(i), ranges, phi.IncomingBlocks[i], visiting))
              return false;
          return true;

        default:
          return false;
      }
    } finally {
      visiting.Remove(value);
    }
  }

  private static bool ExactlyRepresentableAsSingle(double value) {
    if (double.IsNaN(value))
      return false;
    var single = (float)value;
    if ((double)single != value)
      return false;
    if (value != 0.0)
      return true;

    // Equality treats +0 and -0 as the same number, but the sign of zero is observable to IEEE math.
    return (BitConverter.DoubleToInt64Bits(value) < 0) == (BitConverter.SingleToInt32Bits(single) < 0);
  }

  private static void RewriteSlot(IrAlloca slot, IrType storageType) {
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
          var extension = storageType.IsInteger
            ? storageType.Signed ? IrCastOp.SExt : IrCastOp.ZExt
            : IrCastOp.FPExt;
          var widened = loadBlock.InsertBefore(new IrCast(extension, narrow, load.Type), load);
          load.ReplaceAllUsesWith(widened);
          load.EraseFromParent();
          break;
        }
        case IrStore store when store.Parent is { } storeBlock: {
          var truncation = storageType.IsInteger ? IrCastOp.Trunc : IrCastOp.FPTrunc;
          var narrow = storeBlock.InsertBefore(new IrCast(truncation, store.Value, storageType), store);
          storeBlock.InsertBefore(new IrStore(narrow, replacement), store);
          store.EraseFromParent();
          break;
        }
      }

    slot.EraseFromParent();
  }

  private static void RewritePhi(IrPhi phi, IrType storageType) {
    var block = phi.Parent ?? throw new InvalidOperationException("phi has no parent block");
    var narrowPhi = block.AppendPhi(new IrPhi(storageType) { Name = phi.Name });
    var truncation = storageType.IsInteger ? IrCastOp.Trunc : IrCastOp.FPTrunc;

    for (var i = 0; i < phi.IncomingBlocks.Count; ++i) {
      var predecessor = phi.IncomingBlocks[i];
      var terminator = predecessor.Terminator!;
      var narrow = predecessor.InsertBefore(new IrCast(truncation, phi.GetOperand(i), storageType), terminator);
      narrowPhi.AddIncoming(narrow, predecessor);
    }

    var firstOrdinary = block.Instructions.First(i => i is not IrPhi);
    var extension = storageType.IsInteger
      ? storageType.Signed ? IrCastOp.SExt : IrCastOp.ZExt
      : IrCastOp.FPExt;
    var widened = block.InsertBefore(new IrCast(extension, narrowPhi, phi.Type), firstOrdinary);
    phi.ReplaceAllUsesWith(widened);
    phi.EraseFromParent();
  }
}
