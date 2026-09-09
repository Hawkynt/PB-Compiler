namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0285 — merges identical, contained, and prefix/suffix-overlapping read-only byte blobs.
///
/// <para>
/// A byte global is eligible only when its complete pointer-use tree is a chain of GEPs ending in
/// loads. Anything that can observe or leak the address — a call, comparison, cast, store, return,
/// phi, select, inline address use, etc. — keeps the blob private. This is intentionally stricter
/// than trying to infer what an arbitrary consumer might do with a pointer.
/// </para>
/// <para>
/// The IR deliberately carries no alignment fact stronger than one byte for these blobs and the LLVM
/// emitter emits generic loads/stores with <c>align 1</c>, so byte-offset sharing cannot weaken an
/// existing alignment guarantee. String literals remain O0011's responsibility: <c>.str*</c> globals
/// are excluded because their call-shaped consumers and the BASIC writer have a dedicated literal
/// representation. The special <c>.data</c> symbol is likewise retained because the direct/x86 bridge
/// recognizes that name explicitly.
/// </para>
/// </summary>
public static class ConstantDataMerging {

  /// <summary>Merges eligible byte globals to a fixpoint; returns the number of globals removed.</summary>
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);

    // Inline assembly is an opaque text blob and can name a global without appearing in its IR use
    // list. In that case the proof below is incomplete, so keep every blob private rather than merge
    // storage an asm statement may distinguish or modify.
    if (module.Functions.Any(function => function.HasInlineAsm))
      return 0;

    var removed = 0;
    while (BestPlan(module) is { } plan) {
      Apply(module, plan);
      ++removed;
    }
    return removed;
  }

  private static MergePlan? BestPlan(IrModule module) {
    var candidates = module.Globals
      .Where(IsCandidate)
      .OrderBy(global => global.Name, StringComparer.Ordinal)
      .ToArray();

    MergePlan? best = null;
    for (var i = 0; i < candidates.Length; ++i)
      for (var j = i + 1; j < candidates.Length; ++j)
        if (TryPlan(candidates[i], candidates[j], out var candidate)
            && (best is null || IsBetter(candidate, best)))
          best = candidate;
    return best;
  }

  private static bool IsCandidate(IrGlobalVariable global) {
    if (global.Bytes is not { Length: > 0 } || global.ValueType.Bits != 8 || global.HasNoUsers)
      return false;
    if (global.Name.StartsWith(".str", StringComparison.Ordinal) || global.Name == ".data")
      return false;
    return AddressIsReadOnly(global, []);
  }

  /// <summary>
  /// Proves both non-escape and read-only storage transitively. A derived pointer remains safe only
  /// while every reader is another GEP or a load through exactly that pointer.
  /// </summary>
  private static bool AddressIsReadOnly(IrValue pointer, HashSet<IrValue> seen) {
    if (!seen.Add(pointer))
      return true;

    foreach (var user in pointer.Users) {
      if (user.Parent is null)
        return false;
      switch (user) {
        case IrLoad load when ReferenceEquals(load.Pointer, pointer):
          break;
        case IrGep gep when ReferenceEquals(gep.BasePtr, pointer):
          if (!AddressIsReadOnly(gep, seen))
            return false;
          break;
        default:
          return false;
      }
    }
    return true;
  }

  private static bool TryPlan(IrGlobalVariable first, IrGlobalVariable second, out MergePlan plan) {
    var a = first.Bytes!;
    var b = second.Bytes!;
    var bInA = a.AsSpan().IndexOf(b);
    var aInB = b.AsSpan().IndexOf(a);

    // Equal payloads are contained both ways. Keep the lexicographically stable symbol so the result
    // does not depend on module insertion order.
    if (bInA >= 0 && aInB >= 0) {
      if (StringComparer.Ordinal.Compare(first.Name, second.Name) <= 0)
        plan = new(first, second, bInA, a, b.Length, false);
      else
        plan = new(second, first, aInB, b, a.Length, false);
      return true;
    }

    if (bInA >= 0) {
      plan = new(first, second, bInA, a, b.Length, false);
      return true;
    }
    if (aInB >= 0) {
      plan = new(second, first, aInB, b, a.Length, false);
      return true;
    }

    var ab = SuffixPrefixOverlap(a, b);
    var ba = SuffixPrefixOverlap(b, a);
    if (ab == 0 && ba == 0) {
      plan = null!;
      return false;
    }

    // Prefer the larger byte saving; equal overlaps use symbol order as a deterministic tie-breaker.
    var firstThenSecond = ab > ba
      || ab == ba && StringComparer.Ordinal.Compare(first.Name, second.Name) <= 0;
    if (firstThenSecond)
      plan = OverlapPlan(first, second, ab);
    else
      plan = OverlapPlan(second, first, ba);
    return true;
  }

  private static MergePlan OverlapPlan(IrGlobalVariable prefix, IrGlobalVariable suffix, int overlap) {
    var left = prefix.Bytes!;
    var right = suffix.Bytes!;
    var bytes = new byte[left.Length + right.Length - overlap];
    left.CopyTo(bytes, 0);
    right.AsSpan(overlap).CopyTo(bytes.AsSpan(left.Length));
    return new(prefix, suffix, left.Length - overlap, bytes, overlap, true);
  }

  private static int SuffixPrefixOverlap(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) {
    for (var length = Math.Min(left.Length, right.Length) - 1; length > 0; --length)
      if (left[^length..].SequenceEqual(right[..length]))
        return length;
    return 0;
  }

  private static bool IsBetter(MergePlan candidate, MergePlan current) {
    if (candidate.SavedBytes != current.SavedBytes)
      return candidate.SavedBytes > current.SavedBytes;
    if (candidate.RebuildBase != current.RebuildBase)
      return !candidate.RebuildBase; // containment costs no replacement storage object

    var order = StringComparer.Ordinal.Compare(candidate.Base.Name, current.Base.Name);
    if (order != 0)
      return order < 0;
    order = StringComparer.Ordinal.Compare(candidate.Other.Name, current.Other.Name);
    if (order != 0)
      return order < 0;
    return candidate.OtherOffset < current.OtherOffset;
  }

  private static void Apply(IrModule module, MergePlan plan) {
    var target = plan.Base;
    if (plan.RebuildBase) {
      target = new IrGlobalVariable(plan.Base.Name, plan.Base.ValueType) {
        Bytes = plan.Bytes,
        Count = plan.Bytes.Length,
        IsZeroInitialized = false,
      };
      RewriteUses(plan.Base, target, 0);
      module.RemoveGlobal(plan.Base);
      module.AddGlobal(target);
    }

    RewriteUses(plan.Other, target, plan.OtherOffset);
    module.RemoveGlobal(plan.Other);
  }

  private static void RewriteUses(IrGlobalVariable source, IrGlobalVariable target, int byteOffset) {
    if (byteOffset == 0) {
      source.ReplaceAllUsesWith(target);
      return;
    }

    foreach (var user in source.Users.ToArray()) {
      // Candidate formation proved every user is attached. Keep this as an invariant failure rather
      // than silently producing a detached GEP if a future caller violates that precondition.
      var block = user.Parent
        ?? throw new InvalidOperationException("constant-data merge encountered a detached global use");
      var adjusted = block.InsertBefore(
        new IrGep(target, new IrConstantInt(IrType.I32, byteOffset)), user);
      user.ReplaceOperand(source, adjusted);
    }
  }

  private sealed record MergePlan(
    IrGlobalVariable Base,
    IrGlobalVariable Other,
    int OtherOffset,
    byte[] Bytes,
    int SavedBytes,
    bool RebuildBase);
}
