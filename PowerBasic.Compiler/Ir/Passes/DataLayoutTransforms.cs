using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>Target facts required by storage transforms whose profitability/correct representation is target-dependent.</summary>
public sealed record IrDataLayoutTarget(
  int PointerBits,
  int VectorBytes = 1,
  int CacheSizeBytes = 0,
  int CacheLineBytes = 0,
  int CacheAssociativity = 1);

/// <summary>O0320 — converts private arrays of packed scalar records into one scalar array per used field.</summary>
public static class ArrayOfStructsToStructOfArrays {
  public static int Run(IrFunction fn) => DataLayoutTransformCore.RewriteRecordArrays(fn, DataLayoutTransformCore.RecordMode.Soa);
}

/// <summary>O0321 — reorders private packed-record fields by static access frequency.</summary>
public static class FieldReordering {
  public static int Run(IrFunction fn) => DataLayoutTransformCore.RewriteRecordArrays(fn, DataLayoutTransformCore.RecordMode.Reorder);
}

/// <summary>O0322 — splits infrequently accessed fields out of private packed-record arrays.</summary>
public static class HotColdFieldSplitting {
  public static int Run(IrFunction fn) => DataLayoutTransformCore.RewriteRecordArrays(fn, DataLayoutTransformCore.RecordMode.HotCold);
}

/// <summary>O0323 — packs private integer record fields to the smallest proven byte or sub-byte representation.</summary>
public static class StructurePackingByRange {
  public static int Run(IrFunction fn) => DataLayoutTransformCore.PackRecordFields(fn);
}

/// <summary>O0324 — stores same-region pointers as 16-bit encoded element indices when the target pointer is wider.</summary>
public static class PointerCompression {
  public static int Run(IrFunction fn, int pointerBits) => DataLayoutTransformCore.CompressPointerArrays(fn, pointerBits);
}

/// <summary>O0325 — rounds private scalar-array storage to a complete target vector.</summary>
public static class ArrayPaddingAlignment {
  public static int Run(IrFunction fn, int vectorBytes) => DataLayoutTransformCore.PadScalarArrays(fn, vectorBytes);
}

/// <summary>O0326 — pads two-dimensional row strides that alias one cache set across all ways.</summary>
public static class CacheConflictPadding {
  public static int Run(IrFunction fn, int cacheSizeBytes, int cacheLineBytes = 0, int cacheAssociativity = 1)
    => DataLayoutTransformCore.PadConflictingRows(fn, cacheSizeBytes, cacheLineBytes, cacheAssociativity);
}

/// <summary>O0327 — transposes private two-dimensional arrays when the innermost loop walks the strided dimension.</summary>
public static class DataTransposition {
  public static int Run(IrFunction fn) => DataLayoutTransformCore.TransposeForTraversal(fn);
}

/// <summary>O0328 — forwards a pure producer expression into a single same-index consumer and removes the temporary array traffic.</summary>
public static class TemporaryArrayFusion {
  public static int Run(IrFunction fn) => DataLayoutTransformCore.EliminateTemporaryArrays(fn);
}

/// <summary>O0329 — contracts a fixed-width sliding-window array recurrence to loop-carried SSA values.</summary>
public static class ArrayContraction {
  public static int Run(IrFunction fn) => DataLayoutTransformCore.ContractSlidingWindows(fn);
}

internal static class DataLayoutTransformCore {

  internal enum RecordMode { Soa, Reorder, HotCold }

  private sealed class Linear {
    public Dictionary<IrValue, long> Terms { get; } = new(ReferenceEqualityComparer.Instance);
    public long Constant { get; set; }

    public Linear(long constant = 0) => this.Constant = constant;

    public Linear Clone() {
      var result = new Linear(this.Constant);
      foreach (var (value, coefficient) in this.Terms)
        result.Terms[value] = coefficient;
      return result;
    }

    public bool Add(Linear other, long scale = 1) {
      try {
        this.Constant = checked(this.Constant + checked(other.Constant * scale));
        foreach (var (value, coefficient) in other.Terms) {
          var next = checked(this.Terms.GetValueOrDefault(value) + checked(coefficient * scale));
          if (next == 0)
            this.Terms.Remove(value);
          else
            this.Terms[value] = next;
        }
        return true;
      } catch (OverflowException) {
        return false;
      }
    }

    public bool Scale(long factor) {
      try {
        this.Constant = checked(this.Constant * factor);
        foreach (var key in this.Terms.Keys.ToArray()) {
          var coefficient = checked(this.Terms[key] * factor);
          if (coefficient == 0)
            this.Terms.Remove(key);
          else
            this.Terms[key] = coefficient;
        }
        return true;
      } catch (OverflowException) {
        return false;
      }
    }

    public bool DivideExact(long divisor) {
      if (divisor == 0 || this.Constant % divisor != 0 || this.Terms.Values.Any(c => c % divisor != 0))
        return false;
      this.Constant /= divisor;
      foreach (var key in this.Terms.Keys.ToArray())
        this.Terms[key] /= divisor;
      return true;
    }

    public bool SameAs(Linear other) => this.Constant == other.Constant
      && this.Terms.Count == other.Terms.Count
      && this.Terms.All(pair => other.Terms.TryGetValue(pair.Key, out var c) && c == pair.Value);
  }

  private sealed record Access(IrInstruction Instruction, IrValue Pointer, Linear Bytes, IrType ValueType, int BytesWide) {
    public bool IsLoad => this.Instruction is IrLoad;
    public bool IsStore => this.Instruction is IrStore;
  }

  private sealed record Field(long Offset, IrType Type, int Size, List<Access> Accesses) {
    public int Reads => this.Accesses.Count(a => a.IsLoad);
    public int Writes => this.Accesses.Count - this.Reads;
    public int Weight => this.Reads + this.Writes * 2;
  }

  private sealed record RecordShape(IrAlloca Root, long Stride, int Elements, List<Field> Fields);
  private readonly record struct BitField(int Width, bool Signed);

  internal static int RewriteRecordArrays(IrFunction fn, RecordMode mode) {
    if (fn.Entry is null)
      return 0;
    var changed = 0;
    foreach (var root in fn.Entry.Instructions.OfType<IrAlloca>().ToList()) {
      if (!TryRecordShape(fn, root, out var shape))
        continue;
      changed += mode switch {
        RecordMode.Soa => ToSoa(shape!),
        RecordMode.Reorder => Reorder(shape!),
        RecordMode.HotCold => SplitHotCold(shape!),
        _ => 0,
      };
    }
    return changed;
  }

  internal static int PackRecordFields(IrFunction fn) {
    if (fn.Entry is null || IrRangeAnalysis.Build(fn) is not { } ranges)
      return 0;
    var changed = 0;
    foreach (var root in fn.Entry.Instructions.OfType<IrAlloca>().ToList()) {
      if (!TryRecordShape(fn, root, out var shape) || !ExactFields(shape!))
        continue;
      var packedTypes = new Dictionary<Field, IrType>();
      var bitFields = new Dictionary<Field, BitField>();
      foreach (var field in shape!.Fields) {
        if (!field.Type.IsInteger || field.Accesses.All(a => !a.IsStore)) {
          packedTypes[field] = field.Type;
          continue;
        }
        var stores = field.Accesses.Where(a => a.Instruction is IrStore).Select(a => (IrStore)a.Instruction).ToList();
        var range = new ValueRange(0, 0); // PB variables are zero-initialized before their first source write.
        foreach (var store in stores) {
          if (store.Parent is null) {
            range = ValueRange.OfType(field.Type);
            break;
          }
          range = range.Join(ranges.RangeAt(store.Value, store.Parent));
        }
        var subByteWidth = SubByteWidth(range);
        if (subByteWidth > 0) {
          packedTypes[field] = IrType.I8;
          bitFields[field] = new BitField(subByteWidth, range.Lo < 0);
        } else
          packedTypes[field] = Narrowest(field.Type, range);
      }
      if (bitFields.Count == 0 && packedTypes.All(pair => pair.Key.Type.SameStorage(pair.Value)))
        continue;
      if (Pack(shape, packedTypes, bitFields))
        ++changed;
    }
    return changed;
  }

  internal static int CompressPointerArrays(IrFunction fn, int pointerBits) {
    if (pointerBits <= 16 || fn.Entry is null || IrRangeAnalysis.Build(fn) is not { } ranges)
      return 0;
    var changed = 0;
    foreach (var root in fn.Entry.Instructions.OfType<IrAlloca>().Where(a => a.Allocated.IsPointer && a.Count > 1).ToList()) {
      if (!PrivatePointerTree(root))
        continue;
      var geps = root.Users.OfType<IrGep>().ToList();
      if (geps.Count == 0 || root.Users.Any(user => user is not IrGep)
          || geps.Any(g => g.ElementType is not { IsPointer: true }))
        continue;
      IrValue? region = null;
      IrType? regionElement = null;
      var stores = new List<IrStore>();
      var loads = new List<IrLoad>();
      var valid = true;
      foreach (var gep in geps)
        foreach (var user in gep.Users)
          switch (user) {
            case IrStore store when ReferenceEquals(store.Pointer, gep): {
              stores.Add(store);
              if (store.Value is IrNullPtr)
                break;
              if (store.Value is not IrGep target || target.ElementType is null) { valid = false; break; }
              region ??= target.BasePtr;
              regionElement ??= target.ElementType;
              if (!ReferenceEquals(region, target.BasePtr) || !regionElement.SameStorage(target.ElementType)) { valid = false; break; }
              if (store.Parent is null) { valid = false; break; }
              var range = ranges.RangeAt(target.ByteOffset, store.Parent);
              if (range.IsTop || range.IsEmpty || range.Lo < 0 || range.Hi >= ushort.MaxValue) { valid = false; break; }
              break;
            }
            case IrLoad load when ReferenceEquals(load.Pointer, gep):
              loads.Add(load);
              break;
            default:
              valid = false;
              break;
          }
      if (!valid || region is null || regionElement is null || stores.Count == 0 || loads.Count == 0)
        continue;

      var replacement = InsertAllocaAfter(root, IrType.U16, root.Count, (root.Name ?? "ptrs") + ".compressed");
      foreach (var gep in geps.ToList()) {
        var narrowPtr = gep.Parent!.InsertBefore(new IrGep(replacement, gep.ByteOffset, IrType.U16), gep);
        foreach (var user in gep.Users.ToList())
          switch (user) {
            case IrStore store: {
              IrValue compressed;
              if (store.Value is IrNullPtr)
                compressed = new IrConstantInt(IrType.U16, 0);
              else {
                var target = (IrGep)store.Value;
                IrValue index = target.ByteOffset;
                if (!index.Type.SameStorage(IrType.U16))
                  index = store.Parent!.InsertBefore(new IrCast(
                    index.Type.Bits > 16 ? IrCastOp.Trunc : IrCastOp.ZExt, index, IrType.U16), store);
                compressed = store.Parent!.InsertBefore(new IrBinary(
                  IrBinaryOp.Add, index, new IrConstantInt(IrType.U16, 1)), store);
              }
              store.Parent!.InsertBefore(new IrStore(compressed, narrowPtr), store);
              store.EraseFromParent();
              break;
            }
            case IrLoad load: {
              var block = load.Parent!;
              var encoded = block.InsertBefore(new IrLoad(IrType.U16, narrowPtr), load);
              var isNull = block.InsertBefore(new IrCmp(IrCmpPred.Eq, encoded, new IrConstantInt(IrType.U16, 0)), load);
              var decoded = block.InsertBefore(new IrBinary(IrBinaryOp.Sub, encoded, new IrConstantInt(IrType.U16, 1)), load);
              var safeIndex = block.InsertBefore(new IrSelect(isNull, new IrConstantInt(IrType.U16, 0), decoded), load);
              var index = block.InsertBefore(new IrCast(IrCastOp.ZExt, safeIndex, IrType.I32), load);
              var target = block.InsertBefore(new IrGep(region, index, regionElement), load);
              var value = block.InsertBefore(new IrSelect(isNull, new IrNullPtr(target.Type), target), load);
              load.ReplaceAllUsesWith(value);
              load.EraseFromParent();
              break;
            }
          }
        if (gep.HasNoUsers)
          gep.EraseFromParent();
      }
      if (root.HasNoUsers)
        root.EraseFromParent();
      ++changed;
    }
    return changed;
  }

  internal static int PadScalarArrays(IrFunction fn, int vectorBytes) {
    if (vectorBytes <= 1 || fn.Entry is null)
      return 0;
    var changed = 0;
    foreach (var root in fn.Entry.Instructions.OfType<IrAlloca>().ToList()) {
      var size = IrAliasAnalysis.StorageBytes(root.Allocated);
      if (size is not { } elementBytes || root.Count < 16 || vectorBytes <= elementBytes || vectorBytes % elementBytes != 0)
        continue;
      if (!PrivatePointerTree(root))
        continue;
      var laneCount = vectorBytes / elementBytes;
      var padded = checked(((root.Count + laneCount - 1) / laneCount) * laneCount);
      if (padded == root.Count)
        continue;
      var replacement = InsertAllocaAfter(root, root.Allocated, padded, (root.Name ?? "array") + ".padded");
      root.ReplaceAllUsesWith(replacement);
      root.EraseFromParent();
      ++changed;
    }
    return changed;
  }

  internal static int PadConflictingRows(IrFunction fn, int cacheSizeBytes, int cacheLineBytes, int cacheAssociativity) {
    if (cacheSizeBytes <= 0 || cacheAssociativity <= 0 || cacheSizeBytes % cacheAssociativity != 0 || fn.Entry is null)
      return 0;
    var conflictSpanBytes = cacheSizeBytes / cacheAssociativity;
    var changed = 0;
    foreach (var root in fn.Entry.Instructions.OfType<IrAlloca>().ToList()) {
      if (root.Name?.Contains(".cachepad", StringComparison.Ordinal) == true)
        continue;
      if (IrAliasAnalysis.StorageBytes(root.Allocated) is not { } elementBytes || root.Count < 64 || !PrivatePointerTree(root))
        continue;
      var accesses = CollectAccesses(fn, root);
      if (accesses is null || accesses.Count == 0)
        continue;
      if (!TryCommonTwoDimensionalShape(accesses, elementBytes, root.Count, out var rowElements, out var rows, out var rowTerm))
        continue;
      var rowBytes = checked(rowElements * elementBytes);
      if (rowBytes % conflictSpanBytes != 0)
        continue;
      var padElements = cacheLineBytes > 0
        ? Math.Max(1L, ((long)cacheLineBytes + elementBytes - 1) / elementBytes)
        : 1L;
      var physicalRow = checked(rowElements + padElements);
      var rewrites = new Dictionary<Access, Linear>();
      foreach (var access in accesses) {
        var logical = access.Bytes.Clone();
        if (!logical.DivideExact(elementBytes)
            || !logical.Terms.TryGetValue(rowTerm!, out var rowCoefficient)
            || Math.Abs(rowCoefficient) != rowElements) {
          rewrites.Clear();
          break;
        }
        logical.Terms[rowTerm!] = Math.Sign(rowCoefficient) * physicalRow;
        var scaled = TryScaleCopy(logical, elementBytes);
        if (scaled is null || !CanBuildLinear(scaled) || access.Instruction.Parent is null) {
          rewrites.Clear();
          break;
        }
        rewrites[access] = scaled;
      }
      if (rewrites.Count != accesses.Count)
        continue;
      int paddedElements;
      try {
        paddedElements = checked((int)(rows * physicalRow));
      } catch (OverflowException) {
        continue;
      }
      var replacement = InsertAllocaAfter(root, root.Allocated, paddedElements, (root.Name ?? "array") + ".cachepad");
      foreach (var (access, bytes) in rewrites)
        _ = RewriteAccess(access, replacement, bytes); // preflight above makes this non-failing.
      CleanupDeadGeps(root);
      if (root.HasNoUsers)
        root.EraseFromParent();
      ++changed;
    }
    return changed;
  }

  internal static int TransposeForTraversal(IrFunction fn) {
    if (fn.Entry is null)
      return 0;
    var loops = fn.Blocks.Select(h => CountedLoop.Match(fn, h)).Where(l => l is not null).Cast<CountedLoop>().ToList();
    if (loops.Count == 0)
      return 0;
    var changed = 0;
    foreach (var root in fn.Entry.Instructions.OfType<IrAlloca>().ToList()) {
      if (root.Name?.Contains(".transpose", StringComparison.Ordinal) == true)
        continue;
      if (IrAliasAnalysis.StorageBytes(root.Allocated) is not { } elementBytes || root.Count < 16 || !PrivatePointerTree(root))
        continue;
      var accesses = CollectAccesses(fn, root);
      if (accesses is null || accesses.Count == 0)
        continue;
      if (!TryCommonTwoDimensionalShape(accesses, elementBytes, root.Count, out var columns, out var rows, out var rowTerm))
        continue;
      if (!loops.Any(loop => ReferenceEquals(loop.Counter, rowTerm)
          && IsInnermostTraversal(loop, loops, accesses)))
        continue;

      var replacement = InsertAllocaAfter(root, root.Allocated, root.Count, (root.Name ?? "array") + ".transpose");
      var ok = true;
      foreach (var access in accesses) {
        var elements = access.Bytes.Clone();
        if (!elements.DivideExact(elementBytes)
            || !elements.Terms.TryGetValue(rowTerm!, out var rowCoefficient)
            || Math.Abs(rowCoefficient) != columns) { ok = false; break; }
        elements.Terms[rowTerm!] = Math.Sign(rowCoefficient);
        var other = elements.Terms.FirstOrDefault(pair => !ReferenceEquals(pair.Key, rowTerm) && Math.Abs(pair.Value) == 1);
        if (other.Key is null) { ok = false; break; }
        elements.Terms[other.Key] = Math.Sign(other.Value) * rows;
        var scaled = TryScaleCopy(elements, elementBytes);
        if (scaled is null || !RewriteAccess(access, replacement, scaled)) { ok = false; break; }
      }
      if (!ok) {
        replacement.EraseFromParent();
        continue;
      }
      CleanupDeadGeps(root);
      if (root.HasNoUsers)
        root.EraseFromParent();
      ++changed;
    }
    return changed;
  }

  internal static int EliminateTemporaryArrays(IrFunction fn) {
    if (fn.Entry is null)
      return 0;
    var loops = fn.Blocks.Select(h => CountedLoop.Match(fn, h)).Where(l => l is not null).Cast<CountedLoop>().ToList();
    if (loops.Count < 2 || IrDominators.Build(fn) is not { } dom)
      return 0;
    var changed = 0;
    foreach (var root in fn.Entry.Instructions.OfType<IrAlloca>().ToList()) {
      if (root.Count < 2 || IrAliasAnalysis.StorageBytes(root.Allocated) is not { } elementBytes || !PrivatePointerTree(root))
        continue;
      var accesses = CollectAccesses(fn, root);
      if (accesses is null)
        continue;
      var stores = accesses.Where(a => a.Instruction is IrStore).ToList();
      var loads = accesses.Where(a => a.Instruction is IrLoad).ToList();
      if (stores.Count != 1 || loads.Count != 1)
        continue;
      var producerMatches = loops.Where(loop => stores[0].Instruction.Parent is { } b && loop.Region.Contains(b)).ToList();
      var consumerMatches = loops.Where(loop => loads[0].Instruction.Parent is { } b && loop.Region.Contains(b)).ToList();
      if (producerMatches.Count != 1 || consumerMatches.Count != 1)
        continue;
      var producer = producerMatches[0];
      var consumer = consumerMatches[0];
      if (ReferenceEquals(producer, consumer) || producer.Trips != consumer.Trips
          || !SameCounterSequence(producer, consumer))
        continue;
      if (!dom.Dominates(producer.Exit, consumer.Header) || !TransparentPath(producer.Exit, consumer.Header))
        continue;
      if (!SameIterationAddress(stores[0], producer.Counter, loads[0], consumer.Counter, elementBytes))
        continue;
      var store = (IrStore)stores[0].Instruction;
      var load = (IrLoad)loads[0].Instruction;
      if (store.Parent is null || !dom.Dominates(store.Parent, producer.Latch))
        continue; // every temporary element must actually be produced on every iteration.
      if (!TryClonePureValue(store.Value, producer, consumer, load, out var forwarded)) {
        Dce.Run(fn);
        continue;
      }
      load.ReplaceAllUsesWith(forwarded!);
      load.EraseFromParent();
      store.EraseFromParent();
      CleanupDeadGeps(root);
      Dce.Run(fn);
      if (root.HasNoUsers)
        root.EraseFromParent();
      ++changed;
    }
    return changed;
  }

  internal static int ContractSlidingWindows(IrFunction fn) {
    if (fn.Entry is null)
      return 0;
    var loops = fn.Blocks.Select(h => CountedLoop.Match(fn, h)).Where(l => l is not null).Cast<CountedLoop>().ToList();
    if (loops.Count == 0 || IrDominators.Build(fn) is not { } dom)
      return 0;
    var changed = 0;
    foreach (var root in fn.Entry.Instructions.OfType<IrAlloca>().ToList()) {
      if (root.Count < 2 || IrAliasAnalysis.StorageBytes(root.Allocated) is not { } elementBytes || !PrivatePointerTree(root))
        continue;
      var accesses = CollectAccesses(fn, root);
      if (accesses is null
          || accesses.Any(a => a.BytesWide != elementBytes || !a.ValueType.SameStorage(root.Allocated)))
        continue;
      foreach (var loop in loops) {
        if (IrLoopDependenceAnalysis.Analyze(fn, loop.Header) is not { IsComplete: true })
          continue;

        var insideStores = accesses.Where(a => a.Instruction is IrStore && a.Instruction.Parent is { } b && loop.Region.Contains(b)).ToList();
        var insideLoads = accesses.Where(a => a.Instruction is IrLoad && a.Instruction.Parent is { } b && loop.Region.Contains(b)).ToList();
        var outsideStores = accesses.Where(a => a.Instruction is IrStore && (a.Instruction.Parent is not { } b || !loop.Region.Contains(b))).ToList();
        var outsideLoads = accesses.Where(a => a.Instruction is IrLoad && (a.Instruction.Parent is not { } b || !loop.Region.Contains(b))).ToList();
        if (insideStores.Count != 1 || insideLoads.Count == 0 || outsideLoads.Count == 0)
          continue;
        if (!TryCounterProgression(loop, out var firstCounter, out var step) || step != 1)
          continue; // the scalar shift below models an advancing fixed-width window.

        var current = IndexOf(insideStores[0], loop.Counter, elementBytes);
        if (current is null)
          continue;
        var historyLoads = new List<(IrLoad Load, int Distance)>(insideLoads.Count);
        var width = 0;
        var valid = true;
        foreach (var access in insideLoads) {
          var previous = IndexOf(access, loop.Counter, elementBytes);
          long distance;
          try {
            distance = previous is null ? 0 : checked(current.Value - previous.Value);
          } catch (OverflowException) {
            valid = false;
            break;
          }
          if (previous is null || distance <= 0 || distance > root.Count) {
            valid = false;
            break;
          }
          var slot = checked((int)distance);
          width = Math.Max(width, slot);
          historyLoads.Add(((IrLoad)access.Instruction, slot));
        }
        if (!valid || width == 0 || outsideStores.Count != width)
          continue;

        long firstCurrent;
        long lastCurrent;
        try {
          firstCurrent = checked(firstCounter + current.Value);
          lastCurrent = checked(firstCurrent + checked((loop.Trips - 1) * step));
        } catch (OverflowException) {
          continue;
        }
        if (firstCurrent < width || lastCurrent < firstCurrent || lastCurrent >= root.Count)
          continue;

        var seeds = new IrStore?[width];
        foreach (var access in outsideStores) {
          if (!ConstantElement(access, elementBytes, out var seedIndex)) {
            valid = false;
            break;
          }
          long distance;
          try {
            distance = checked(firstCurrent - seedIndex);
          } catch (OverflowException) {
            valid = false;
            break;
          }
          if (distance is < 1 || distance > width) {
            valid = false;
            break;
          }
          var store = (IrStore)access.Instruction;
          var slot = checked((int)distance) - 1;
          if (seeds[slot] is not null || store.Parent is null || !dom.Dominates(store.Parent, loop.Preheader)) {
            valid = false;
            break;
          }
          seeds[slot] = store;
        }
        if (!valid || seeds.Any(seed => seed is null))
          continue;

        var finalLoads = new List<(IrLoad Load, int Distance)>(outsideLoads.Count);
        foreach (var access in outsideLoads) {
          if (!ConstantElement(access, elementBytes, out var finalIndex)) {
            valid = false;
            break;
          }
          long distance;
          try {
            distance = checked(checked(lastCurrent - finalIndex) + 1);
          } catch (OverflowException) {
            valid = false;
            break;
          }
          var load = (IrLoad)access.Instruction;
          if (distance is < 1 || distance > width || load.Parent is null || !dom.Dominates(loop.Exit, load.Parent)) {
            valid = false;
            break;
          }
          finalLoads.Add((load, checked((int)distance)));
        }
        if (!valid)
          continue;

        var recurrenceStore = (IrStore)insideStores[0].Instruction;
        if (recurrenceStore.Parent is null || !dom.Dominates(recurrenceStore.Parent, loop.Latch))
          continue; // every current element must be produced on every iteration.

        var history = new IrPhi[width];
        for (var slot = 0; slot < history.Length; ++slot) {
          var suffix = width == 1 ? ".window" : $".window.{slot + 1}";
          var phi = loop.Header.AppendPhi(new IrPhi(root.Allocated) { Name = (root.Name ?? "array") + suffix });
          phi.AddIncoming(seeds[slot]!.Value, loop.Preheader);
          history[slot] = phi;
        }

        foreach (var (load, distance) in historyLoads)
          load.ReplaceAllUsesWith(history[distance - 1]);
        for (var slot = 0; slot < history.Length; ++slot)
          history[slot].AddIncoming(slot == 0 ? recurrenceStore.Value : history[slot - 1], loop.Latch);
        foreach (var (load, distance) in finalLoads)
          load.ReplaceAllUsesWith(history[distance - 1]);

        foreach (var (load, _) in historyLoads)
          load.EraseFromParent();
        foreach (var (load, _) in finalLoads)
          load.EraseFromParent();
        foreach (var seed in seeds)
          seed!.EraseFromParent();
        recurrenceStore.EraseFromParent();
        CleanupDeadGeps(root);
        Dce.Run(fn);
        if (root.HasNoUsers)
          root.EraseFromParent();
        ++changed;
        break;
      }
    }
    return changed;
  }

  private static int ToSoa(RecordShape shape) {
    if (shape.Root.Name?.EndsWith(".hot", StringComparison.Ordinal) == true)
      return 0; // O0322 selected this grouping; the following O0320 pass must not immediately undo it.
    if (shape.Elements < 16 || shape.Fields.Count < 2)
      return 0;
    var entry = shape.Root.Parent!;
    var at = entry.Instructions.ToList().IndexOf(shape.Root);
    if (at < 0)
      return 0;
    var fieldArrays = new Dictionary<Field, IrAlloca>();
    var insert = at + 1;
    foreach (var field in shape.Fields.OrderBy(f => f.Offset))
      fieldArrays[field] = entry.InsertAt(insert++, new IrAlloca(field.Type) {
        Count = shape.Elements,
        Name = $"{shape.Root.Name ?? "record"}.{field.Offset}.soa",
      });

    foreach (var field in shape.Fields)
      foreach (var access in field.Accesses) {
        var index = access.Bytes.Clone();
        index.Constant -= field.Offset;
        if (!index.DivideExact(shape.Stride) || BuildLinear(access.Instruction.Parent!, access.Instruction, index) is not { } value)
          return 0;
        var pointer = access.Instruction.Parent!.InsertBefore(new IrGep(fieldArrays[field], value, field.Type), access.Instruction);
        SetPointer(access.Instruction, pointer);
      }
    CleanupDeadGeps(shape.Root);
    if (shape.Root.HasNoUsers)
      shape.Root.EraseFromParent();
    return 1;
  }

  private static int Reorder(RecordShape shape) {
    if (shape.Elements < 8 || shape.Fields.Count < 3 || !ExactFields(shape))
      return 0;
    var desired = shape.Fields.OrderByDescending(f => f.Weight).ThenByDescending(f => f.Size).ThenBy(f => f.Offset).ToList();
    var newOffsets = new Dictionary<Field, long>();
    long offset = 0;
    foreach (var field in desired) {
      newOffsets[field] = offset;
      offset += field.Size;
    }
    if (shape.Fields.All(f => newOffsets[f] == f.Offset))
      return 0;
    var rewrites = new Dictionary<Access, Linear>();
    foreach (var field in shape.Fields)
      foreach (var access in field.Accesses) {
        var index = access.Bytes.Clone();
        index.Constant -= field.Offset;
        if (!index.DivideExact(shape.Stride) || TryScaleCopy(index, shape.Stride) is not { } bytes)
          return 0;
        bytes.Constant += newOffsets[field];
        rewrites[access] = bytes;
      }
    foreach (var (access, bytes) in rewrites)
      if (!RewriteAccess(access, shape.Root, bytes))
        return 0;
    CleanupDeadGeps(shape.Root);
    return 1;
  }

  private static int SplitHotCold(RecordShape shape) {
    if (shape.Elements < 16 || shape.Fields.Count < 3 || !ExactFields(shape))
      return 0;
    var max = shape.Fields.Max(f => f.Weight);
    if (max <= 0)
      return 0;
    var cold = shape.Fields.Where(f => (long)f.Weight * 4 <= max).ToHashSet();
    var hot = shape.Fields.Where(f => !cold.Contains(f)).ToList();
    if (cold.Count == 0 || hot.Count == 0)
      return 0;
    var hotStride = hot.Sum(f => f.Size);
    var indexes = new Dictionary<Access, Linear>();
    var hotBytes = new Dictionary<Access, Linear>();
    var hotOffset = new Dictionary<Field, long>();
    long offset = 0;
    foreach (var field in hot.OrderBy(f => f.Offset)) {
      hotOffset[field] = offset;
      offset += field.Size;
    }
    foreach (var field in shape.Fields)
      foreach (var access in field.Accesses) {
        var index = access.Bytes.Clone();
        index.Constant -= field.Offset;
        if (!index.DivideExact(shape.Stride))
          return 0;
        if (cold.Contains(field))
          indexes[access] = index;
        else {
          if (TryScaleCopy(index, hotStride) is not { } bytes)
            return 0;
          bytes.Constant += hotOffset[field];
          hotBytes[access] = bytes;
        }
      }

    var hotRoot = InsertAllocaAfter(shape.Root, IrType.I8, checked(shape.Elements * hotStride), (shape.Root.Name ?? "record") + ".hot");
    var coldRoots = new Dictionary<Field, IrAlloca>();
    foreach (var field in cold)
      coldRoots[field] = InsertAllocaAfter(hotRoot, field.Type, shape.Elements, $"{shape.Root.Name ?? "record"}.{field.Offset}.cold");
    foreach (var field in shape.Fields)
      foreach (var access in field.Accesses)
        if (cold.Contains(field)) {
          if (BuildLinear(access.Instruction.Parent!, access.Instruction, indexes[access]) is not { } indexValue)
            return 0;
          var pointer = access.Instruction.Parent!.InsertBefore(new IrGep(coldRoots[field], indexValue, field.Type), access.Instruction);
          SetPointer(access.Instruction, pointer);
        } else if (!RewriteAccess(access, hotRoot, hotBytes[access]))
          return 0;
    CleanupDeadGeps(shape.Root);
    if (shape.Root.HasNoUsers)
      shape.Root.EraseFromParent();
    return 1;
  }

  private static bool Pack(RecordShape shape, IReadOnlyDictionary<Field, IrType> packedTypes,
      IReadOnlyDictionary<Field, BitField> bitFields) {
    var packedOffset = new Dictionary<Field, long>();
    var bitOffset = new Dictionary<Field, int>();
    long bitCursor = 0;
    foreach (var field in shape.Fields.OrderBy(f => f.Offset)) {
      if (bitFields.TryGetValue(field, out var bitField)) {
        var withinByte = (int)(bitCursor & 7);
        if (withinByte + bitField.Width > 8)
          bitCursor = (bitCursor + 7) & ~7L;
        packedOffset[field] = bitCursor >> 3;
        bitOffset[field] = (int)(bitCursor & 7);
        bitCursor += bitField.Width;
        continue;
      }

      bitCursor = (bitCursor + 7) & ~7L;
      var size = IrAliasAnalysis.StorageBytes(packedTypes[field]);
      if (size is null)
        return false;
      packedOffset[field] = bitCursor >> 3;
      bitCursor += checked(size.Value * 8L);
    }
    var stride = (bitCursor + 7) >> 3;
    if (stride <= 0 || stride >= shape.Stride)
      return false;
    var rewrittenOffsets = new Dictionary<Access, Linear>();
    foreach (var field in shape.Fields)
      foreach (var access in field.Accesses) {
        var index = access.Bytes.Clone();
        index.Constant -= field.Offset;
        if (!index.DivideExact(shape.Stride) || TryScaleCopy(index, stride) is not { } bytes)
          return false;
        bytes.Constant += packedOffset[field];
        rewrittenOffsets[access] = bytes;
      }

    var root = InsertAllocaAfter(shape.Root, IrType.I8, checked(shape.Elements * (int)stride), (shape.Root.Name ?? "record") + ".packed");
    foreach (var field in shape.Fields)
      foreach (var access in field.Accesses.ToList()) {
        if (BuildLinear(access.Instruction.Parent!, access.Instruction, rewrittenOffsets[access]) is not { } byteOffset)
          return false;
        var pointer = access.Instruction.Parent!.InsertBefore(new IrGep(root, byteOffset), access.Instruction);
        if (bitFields.TryGetValue(field, out var bitField)) {
          RewriteBitFieldAccess(access, pointer, bitOffset[field], bitField);
          continue;
        }

        var storedType = packedTypes[field];
        if (access.Instruction is IrLoad load) {
          if (storedType.SameStorage(load.Type)) {
            load.SetOperand(0, pointer);
            continue;
          }
          var block = load.Parent!;
          var narrow = block.InsertBefore(new IrLoad(storedType, pointer), load);
          var op = storedType.Signed ? IrCastOp.SExt : IrCastOp.ZExt;
          var widened = block.InsertBefore(new IrCast(op, narrow, load.Type), load);
          load.ReplaceAllUsesWith(widened);
          load.EraseFromParent();
        } else if (access.Instruction is IrStore store) {
          if (storedType.SameStorage(store.Value.Type)) {
            store.SetOperand(1, pointer);
            continue;
          }
          var block = store.Parent!;
          var narrow = block.InsertBefore(new IrCast(IrCastOp.Trunc, store.Value, storedType), store);
          block.InsertBefore(new IrStore(narrow, pointer), store);
          store.EraseFromParent();
        }
      }
    CleanupDeadGeps(shape.Root);
    if (shape.Root.HasNoUsers)
      shape.Root.EraseFromParent();
    return true;
  }

  private static void RewriteBitFieldAccess(Access access, IrValue pointer, int bitOffset, BitField bitField) {
    var mask = (1 << bitField.Width) - 1;
    switch (access.Instruction) {
      case IrLoad load: {
        var block = load.Parent!;
        IrValue value = block.InsertBefore(new IrLoad(IrType.I8, pointer), load);
        if (bitOffset != 0)
          value = block.InsertBefore(new IrBinary(IrBinaryOp.LShr, value, new IrConstantInt(IrType.I8, bitOffset)), load);
        value = block.InsertBefore(new IrBinary(IrBinaryOp.And, value, new IrConstantInt(IrType.I8, mask)), load);
        if (bitField.Signed) {
          var signShift = 8 - bitField.Width;
          value = block.InsertBefore(new IrBinary(IrBinaryOp.Shl, value, new IrConstantInt(IrType.I8, signShift)), load);
          value = block.InsertBefore(new IrBinary(IrBinaryOp.AShr, value, new IrConstantInt(IrType.I8, signShift)), load);
        }
        if (load.Type.Bits > 8)
          value = block.InsertBefore(new IrCast(bitField.Signed ? IrCastOp.SExt : IrCastOp.ZExt, value, load.Type), load);
        else if (load.Type.Bits < 8)
          value = block.InsertBefore(new IrCast(IrCastOp.Trunc, value, load.Type), load);
        load.ReplaceAllUsesWith(value);
        load.EraseFromParent();
        break;
      }
      case IrStore store: {
        var block = store.Parent!;
        IrValue value = store.Value;
        if (value.Type.Bits > 8)
          value = block.InsertBefore(new IrCast(IrCastOp.Trunc, value, IrType.I8), store);
        else if (value.Type.Bits < 8)
          value = block.InsertBefore(new IrCast(IrCastOp.ZExt, value, IrType.I8), store);
        value = block.InsertBefore(new IrBinary(IrBinaryOp.And, value, new IrConstantInt(IrType.I8, mask)), store);
        if (bitOffset != 0)
          value = block.InsertBefore(new IrBinary(IrBinaryOp.Shl, value, new IrConstantInt(IrType.I8, bitOffset)), store);
        var old = block.InsertBefore(new IrLoad(IrType.I8, pointer), store);
        var fieldMask = mask << bitOffset;
        var preserved = block.InsertBefore(new IrBinary(IrBinaryOp.And, old,
          new IrConstantInt(IrType.I8, 0xff ^ fieldMask)), store);
        var merged = block.InsertBefore(new IrBinary(IrBinaryOp.Or, preserved, value), store);
        block.InsertBefore(new IrStore(merged, pointer), store);
        store.EraseFromParent();
        break;
      }
    }
  }

  private static int SubByteWidth(ValueRange range) {
    if (range.IsTop || range.IsEmpty)
      return 0;
    for (var bits = 1; bits < 8; ++bits)
      if (range.Lo < 0) {
        var limit = 1L << (bits - 1);
        if (range.Lo >= -limit && range.Hi < limit)
          return bits;
      } else if (range.Hi < 1L << bits)
        return bits;
    return 0;
  }

  private static IrType Narrowest(IrType original, ValueRange range) {
    if (range.IsTop || range.IsEmpty || !original.IsInteger)
      return original;
    foreach (var bits in new[] { 8, 16, 32 }) {
      if (bits >= original.Bits)
        break;
      var signed = range.Lo < 0;
      var candidate = IrType.Integer(bits, signed);
      var bounds = ValueRange.OfType(candidate);
      if (range.Lo >= bounds.Lo && range.Hi <= bounds.Hi)
        return candidate;
    }
    return original;
  }

  private static bool TryRecordShape(IrFunction fn, IrAlloca root, out RecordShape? shape) {
    shape = null;
    if (!ReferenceEquals(root.Parent, fn.Entry) || root.Allocated != IrType.I8 || root.Count < 4 || !PrivatePointerTree(root))
      return false;
    var accesses = CollectAccesses(fn, root);
    if (accesses is null || accesses.Count < 2)
      return false;
    long stride = 0;
    foreach (var access in accesses)
      foreach (var coefficient in access.Bytes.Terms.Values)
        if (coefficient != 0)
          stride = Gcd(stride, Math.Abs(coefficient));
    if (stride < 2 || stride > root.Count || root.Count % stride != 0)
      return false;
    var fields = new Dictionary<long, Field>();
    foreach (var access in accesses) {
      var offset = Mod(access.Bytes.Constant, stride);
      if (offset + access.BytesWide > stride)
        return false;
      foreach (var coefficient in access.Bytes.Terms.Values)
        if (coefficient % stride != 0)
          return false;
      if (!fields.TryGetValue(offset, out var field))
        fields[offset] = field = new Field(offset, access.ValueType, access.BytesWide, []);
      else if (field.Size != access.BytesWide || !field.Type.SameStorage(access.ValueType))
        return false;
      field.Accesses.Add(access);
    }
    var ordered = fields.Values.OrderBy(f => f.Offset).ToList();
    for (var i = 0; i < ordered.Count; ++i)
      for (var j = i + 1; j < ordered.Count; ++j)
        if (ordered[j].Offset < ordered[i].Offset + ordered[i].Size)
          return false; // overlapping fields are UNION/alias-visible storage, never layout-rewritten.
    if (accesses.Any(a => !CanBuildLinear(a.Bytes)))
      return false;
    shape = new RecordShape(root, stride, checked(root.Count / (int)stride), ordered);
    return true;
  }

  private static bool ExactFields(RecordShape shape) {
    long cursor = 0;
    foreach (var field in shape.Fields.OrderBy(f => f.Offset)) {
      if (field.Offset != cursor)
        return false;
      cursor += field.Size;
    }
    return cursor == shape.Stride;
  }

  private static List<Access>? CollectAccesses(IrFunction fn, IrAlloca root) {
    var result = new List<Access>();
    foreach (var instruction in fn.AllInstructions)
      switch (instruction) {
        case IrLoad load when TryPointerLinear(load.Pointer, root, out var bytes): {
          if (IrAliasAnalysis.StorageBytes(load.Type) is not { } width)
            return null;
          result.Add(new Access(load, load.Pointer, bytes!, load.Type, width));
          break;
        }
        case IrStore store when TryPointerLinear(store.Pointer, root, out var bytes): {
          if (IrAliasAnalysis.StorageBytes(store.Value.Type) is not { } width)
            return null;
          result.Add(new Access(store, store.Pointer, bytes!, store.Value.Type, width));
          break;
        }
      }
    return result;
  }

  private static bool PrivatePointerTree(IrValue root) {
    var seen = new HashSet<IrValue>(ReferenceEqualityComparer.Instance) { root };
    var queue = new Queue<IrValue>([root]);
    while (queue.Count > 0) {
      var pointer = queue.Dequeue();
      foreach (var user in pointer.Users)
        switch (user) {
          case IrGep gep when ReferenceEquals(gep.BasePtr, pointer):
            if (seen.Add(gep)) queue.Enqueue(gep);
            break;
          case IrLoad load when ReferenceEquals(load.Pointer, pointer):
            break;
          case IrStore store when ReferenceEquals(store.Pointer, pointer) && !ReferenceEquals(store.Value, pointer):
            break;
          default:
            return false;
        }
    }
    return true;
  }

  private static bool TryPointerLinear(IrValue pointer, IrAlloca root, out Linear? bytes) {
    bytes = new Linear();
    var current = pointer;
    while (current is IrGep gep) {
      if (!TryLinear(gep.ByteOffset, out var part))
        return false;
      if (gep.ElementType is { } element) {
        if (IrAliasAnalysis.StorageBytes(element) is not { } elementBytes || !part!.Scale(elementBytes))
          return false;
      }
      if (!bytes.Add(part!))
        return false;
      current = gep.BasePtr;
    }
    return ReferenceEquals(current, root);
  }

  private static bool TryLinear(IrValue value, out Linear? expression, int depth = 0) {
    expression = null;
    if (depth > 16 || !value.Type.IsInteger)
      return false;
    switch (value) {
      case IrConstantInt constant:
        expression = new Linear(constant.Value);
        return true;
      case IrBinary { Op: IrBinaryOp.Add or IrBinaryOp.Sub } binary:
        if (!TryLinear(binary.Lhs, out var left, depth + 1) || !TryLinear(binary.Rhs, out var right, depth + 1))
          return false;
        expression = left!.Clone();
        return expression.Add(right!, binary.Op == IrBinaryOp.Add ? 1 : -1);
      case IrBinary { Op: IrBinaryOp.Mul } binary when binary.Lhs is IrConstantInt lc:
        if (!TryLinear(binary.Rhs, out expression, depth + 1)) return false;
        return expression!.Scale(lc.Value);
      case IrBinary { Op: IrBinaryOp.Mul } binary when binary.Rhs is IrConstantInt rc:
        if (!TryLinear(binary.Lhs, out expression, depth + 1)) return false;
        return expression!.Scale(rc.Value);
      case IrCast { Op: IrCastOp.ZExt or IrCastOp.SExt } cast when cast.Type.Bits >= cast.Value.Type.Bits:
        return TryLinear(cast.Value, out expression, depth + 1);
      default:
        expression = new Linear();
        expression.Terms[value] = 1;
        return true;
    }
  }

  private static bool CanBuildLinear(Linear expression) {
    IrType? type = null;
    foreach (var term in expression.Terms.Keys) {
      if (!term.Type.IsInteger) return false;
      type ??= term.Type;
      if (!term.Type.SameStorage(type!)) return false;
    }
    return true;
  }

  private static IrValue? BuildLinear(IrBasicBlock block, IrInstruction before, Linear expression) {
    IrType? type = null;
    foreach (var term in expression.Terms.Keys) {
      if (!term.Type.IsInteger)
        return null;
      type ??= term.Type;
      if (!term.Type.SameStorage(type!))
        return null;
    }
    type ??= IrType.I32;
    IrValue? result = expression.Constant == 0 && expression.Terms.Count > 0
      ? null
      : new IrConstantInt(type, expression.Constant);
    foreach (var (term, coefficient) in expression.Terms) {
      IrValue value = term;
      if (coefficient != 1)
        value = block.InsertBefore(new IrBinary(IrBinaryOp.Mul, term, new IrConstantInt(type, coefficient)), before);
      if (result is null)
        result = value;
      else
        result = block.InsertBefore(new IrBinary(IrBinaryOp.Add, result, value), before);
    }
    return result ?? new IrConstantInt(type, 0);
  }

  private static bool RewriteAccess(Access access, IrAlloca root, Linear bytes) {
    var block = access.Instruction.Parent;
    if (block is null || BuildLinear(block, access.Instruction, bytes) is not { } offset)
      return false;
    var pointer = block.InsertBefore(new IrGep(root, offset), access.Instruction);
    SetPointer(access.Instruction, pointer);
    return true;
  }

  private static void SetPointer(IrInstruction instruction, IrValue pointer) {
    switch (instruction) {
      case IrLoad load: load.SetOperand(0, pointer); break;
      case IrStore store: store.SetOperand(1, pointer); break;
      default: throw new ArgumentException("memory access expected", nameof(instruction));
    }
  }

  private static void CleanupDeadGeps(IrAlloca root) {
    bool changed;
    do {
      changed = false;
      foreach (var gep in DescendantGeps(root).Where(g => g.HasNoUsers).ToList()) {
        gep.EraseFromParent();
        changed = true;
      }
    } while (changed);
  }

  private static IEnumerable<IrGep> DescendantGeps(IrValue root) {
    var seen = new HashSet<IrValue>(ReferenceEqualityComparer.Instance) { root };
    var queue = new Queue<IrValue>([root]);
    while (queue.Count > 0) {
      var pointer = queue.Dequeue();
      foreach (var gep in pointer.Users.OfType<IrGep>().Where(g => ReferenceEquals(g.BasePtr, pointer)))
        if (seen.Add(gep)) {
          yield return gep;
          queue.Enqueue(gep);
        }
    }
  }

  private static IrAlloca InsertAllocaAfter(IrAlloca anchor, IrType type, int count, string name) {
    var block = anchor.Parent ?? throw new InvalidOperationException("alloca has no parent");
    var at = block.Instructions.ToList().IndexOf(anchor);
    return block.InsertAt(at + 1, new IrAlloca(type) { Count = count, Name = name });
  }

  private static Linear? TryScaleCopy(Linear source, long factor) {
    var result = source.Clone();
    return result.Scale(factor) ? result : null;
  }

  private static long Gcd(long a, long b) {
    a = Math.Abs(a); b = Math.Abs(b);
    while (b != 0) (a, b) = (b, a % b);
    return a;
  }

  private static long Mod(long value, long modulus) {
    var result = value % modulus;
    return result < 0 ? result + modulus : result;
  }

  private static bool TryCommonTwoDimensionalShape(IReadOnlyList<Access> accesses, int elementBytes, int totalElements,
      out int columns, out int rows, out IrValue? rowTerm) {
    columns = rows = 0; rowTerm = null;
    long candidate = 0;
    IrValue? candidateTerm = null;
    foreach (var access in accesses) {
      var elements = access.Bytes.Clone();
      if (!elements.DivideExact(elementBytes) || elements.Terms.Count != 2 || elements.Constant != 0)
        return false;
      var stridedTerms = elements.Terms.Where(pair => Math.Abs(pair.Value) > 1).ToList();
      var unitTerms = elements.Terms.Where(pair => Math.Abs(pair.Value) == 1).ToList();
      if (stridedTerms.Count != 1 || unitTerms.Count != 1)
        return false;
      var strided = stridedTerms[0];
      var stride = Math.Abs(strided.Value);
      if (candidate == 0) { candidate = stride; candidateTerm = strided.Key; }
      else if (candidate != stride || !ReferenceEquals(candidateTerm, strided.Key))
        return false;
    }
    if (candidate < 2 || totalElements % candidate != 0)
      return false;
    columns = checked((int)candidate);
    rows = totalElements / columns;
    rowTerm = candidateTerm;
    return rows >= 2;
  }

  private static bool IsInnermostTraversal(CountedLoop loop, IReadOnlyList<CountedLoop> loops, IReadOnlyList<Access> accesses)
    => accesses.Any(a => a.Instruction.Parent is { } block && loop.Region.Contains(block))
       && !loops.Any(nested => !ReferenceEquals(nested, loop)
         && loop.Region.Contains(nested.Header)
         && accesses.Any(a => a.Instruction.Parent is { } block && nested.Region.Contains(block)));

  private static bool SameCounterSequence(CountedLoop first, CountedLoop second)
    => first.Counter.Type.SameStorage(second.Counter.Type)
       && TryCounterProgression(first, out var firstStart, out var firstStep)
       && TryCounterProgression(second, out var secondStart, out var secondStep)
       && firstStart == secondStart && firstStep == secondStep;

  private static bool TryCounterProgression(CountedLoop loop, out long start, out long step) {
    start = step = 0;
    if (loop.Counter.IncomingFrom(loop.Preheader) is not IrConstantInt initial
        || loop.Counter.IncomingFrom(loop.Latch) is not IrBinary { Op: IrBinaryOp.Add } next
        || !ReferenceEquals(next.Lhs, loop.Counter) || next.Rhs is not IrConstantInt increment)
      return false;
    start = CountedLoop.Truncate(loop.Counter.Type, initial.Value);
    step = CountedLoop.Truncate(loop.Counter.Type, increment.Value);
    return step != 0;
  }

  private static bool TransparentPath(IrBasicBlock from, IrBasicBlock to) {
    var seen = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance);
    for (var current = from; !ReferenceEquals(current, to);) {
      if (!seen.Add(current) || current.Terminator is not IrBr branch)
        return false;
      if (current.Instructions.Any(i => !ReferenceEquals(i, current.Terminator) && i is not IrPhi))
        return false;
      current = branch.Target;
    }
    return true;
  }

  private static bool SameIterationAddress(Access first, IrPhi firstCounter, Access second, IrPhi secondCounter, int elementBytes) {
    var a = first.Bytes.Clone();
    var b = second.Bytes.Clone();
    if (!a.DivideExact(elementBytes) || !b.DivideExact(elementBytes))
      return false;
    if (!a.Terms.Remove(firstCounter, out var ac) || !b.Terms.Remove(secondCounter, out var bc) || ac != bc)
      return false;
    return a.SameAs(b);
  }

  private static long? IndexOf(Access access, IrPhi counter, int elementBytes) {
    var index = access.Bytes.Clone();
    if (!index.DivideExact(elementBytes) || !index.Terms.Remove(counter, out var coefficient) || coefficient != 1 || index.Terms.Count != 0)
      return null;
    return index.Constant;
  }

  private static bool ConstantElement(Access access, int elementBytes, out long index) {
    var expression = access.Bytes.Clone();
    if (!expression.DivideExact(elementBytes) || expression.Terms.Count != 0) { index = 0; return false; }
    index = expression.Constant;
    return true;
  }

  private static bool TryClonePureValue(IrValue value, CountedLoop producer, CountedLoop consumer, IrInstruction before, out IrValue? clone) {
    var cache = new Dictionary<IrValue, IrValue>(ReferenceEqualityComparer.Instance) { [producer.Counter] = consumer.Counter };
    var regionInstructions = producer.Region.Concat(consumer.Region)
      .SelectMany(b => b.Instructions).ToList();
    var writes = regionInstructions.OfType<IrStore>().ToList();
    var hasOpaqueWrites = regionInstructions.Any(i => i switch {
      IrInlineAsm => true,
      IrCall { Callee: IrFunction callee } when callee.IsDeclaration && FunctionSummaries.IsPureExternal(callee.Name) => false,
      IrCall => true,
      _ => false,
    });
    return Clone(value, out clone, 0);

    bool Clone(IrValue current, out IrValue? result, int depth) {
      result = null;
      if (depth > 16)
        return false;
      if (cache.TryGetValue(current, out var known)) { result = known; return true; }
      if (current is IrConstant) { result = current; return true; }
      if (current is IrInstruction instruction && instruction.Parent is not { })
        return false;
      if (current is IrInstruction outside && !producer.Region.Contains(outside.Parent!)) {
        result = current; // producer operands defined outside the loop dominate its body; the transparent-gap proof keeps them valid here.
        return true;
      }
      var block = before.Parent!;
      switch (current) {
        case IrBinary binary:
          if (!Clone(binary.Lhs, out var l, depth + 1) || !Clone(binary.Rhs, out var r, depth + 1)) return false;
          result = block.InsertBefore(new IrBinary(binary.Op, l!, r!), before);
          break;
        case IrCast cast:
          if (!Clone(cast.Value, out var inner, depth + 1)) return false;
          result = block.InsertBefore(new IrCast(cast.Op, inner!, cast.Type), before);
          break;
        case IrLoad load:
          if (!ClonePointer(load.Pointer, out var pointer, depth + 1)) return false;
          if (hasOpaqueWrites || writes.Any(w => IrAliasAnalysis.MayAlias(pointer!, load.Type, w.Pointer, w.Value.Type))) return false;
          result = block.InsertBefore(new IrLoad(load.Type, pointer!), before);
          break;
        default:
          return false;
      }
      cache[current] = result!;
      return true;
    }

    bool ClonePointer(IrValue pointer, out IrValue? result, int depth) {
      result = null;
      if (depth > 16)
        return false;
      if (pointer is IrAlloca or IrGlobalVariable) { result = pointer; return true; }
      if (pointer is not IrGep gep || !ClonePointer(gep.BasePtr, out var basePtr, depth + 1) || !Clone(gep.ByteOffset, out var offset, depth + 1))
        return false;
      result = before.Parent!.InsertBefore(gep.ElementType is { } et
        ? new IrGep(basePtr!, offset!, et)
        : new IrGep(basePtr!, offset!), before);
      return true;
    }
  }
}