namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>
/// A pointer's target-independent object identity. <see cref="ByteOffset"/> is null when the root is
/// known but the displacement is not; <see cref="IsUniqueObject"/> means distinct unique roots cannot
/// alias each other.
///
/// <para>
/// This is deliberately an IR object/provenance fact rather than target pointer analysis. Stack
/// allocations and globals are unique objects; formal pointer arguments retain their SSA root but are
/// not unique because two arguments may denote the same object. GEPs and pointer-preserving bitcasts
/// retain identity. Loaded pointers, far-pointer constructors, integer-to-pointer casts and other
/// opaque sources remain unknown.
/// </para>
/// </summary>
public readonly record struct IrPointerIdentity(
  IrValue Root,
  long? ByteOffset,
  bool IsUniqueObject);

/// <summary>
/// Shared function-local pointer-base/object-identity analysis. It contains only facts guaranteed by
/// the target-neutral IR and therefore safely feeds alias/MemorySSA clients without inventing
/// language-level noalias promises for BYREF parameters.
/// </summary>
public sealed class IrPointerIdentityAnalysis {

  private readonly Dictionary<IrValue, IrPointerIdentity?> _cache
    = new(ReferenceEqualityComparer.Instance);
  private readonly HashSet<IrValue> _active
    = new(ReferenceEqualityComparer.Instance);

  /// <summary>Resolves a pointer to its known root object and optional constant byte displacement.</summary>
  public IrPointerIdentity? TryResolve(IrValue value) {
    ArgumentNullException.ThrowIfNull(value);
    if (!value.Type.IsPointer)
      return null;
    if (this._cache.TryGetValue(value, out var cached))
      return cached;
    if (!this._active.Add(value))
      return null;

    try {
      var result = this.ResolveCore(value);
      this._cache[value] = result;
      return result;
    } finally {
      this._active.Remove(value);
    }
  }

  private IrPointerIdentity? ResolveCore(IrValue value) => value switch {
    IrAlloca => new(value, 0, IsUniqueObject: true),
    IrGlobalVariable => new(value, 0, IsUniqueObject: true),
    IrArgument => new(value, 0, IsUniqueObject: false),

    IrCast { Op: IrCastOp.BitCast } cast
      when cast.Value.Type.IsPointer && cast.Type.IsPointer
      => this.TryResolve(cast.Value),

    IrGep gep => this.ResolveGep(gep),

    IrSelect select when select.Type.IsPointer
      => this.Merge(this.TryResolve(select.IfTrue), this.TryResolve(select.IfFalse)),

    _ => null,
  };

  private IrPointerIdentity? ResolveGep(IrGep gep) {
    if (this.TryResolve(gep.BasePtr) is not { } basis)
      return null;

    if (!TryGepOffset(gep, out var displacement)
        || basis.ByteOffset is not { } baseOffset
        || !TryAdd(baseOffset, displacement, out var offset))
      return basis with { ByteOffset = null };

    return basis with { ByteOffset = offset };
  }

  private IrPointerIdentity? Merge(IrPointerIdentity? left, IrPointerIdentity? right) {
    if (left is not { } a || right is not { } b || !ReferenceEquals(a.Root, b.Root))
      return null;

    return new(
      a.Root,
      a.ByteOffset == b.ByteOffset ? a.ByteOffset : null,
      a.IsUniqueObject && b.IsUniqueObject);
  }

  private static bool TryGepOffset(IrGep gep, out long offset) {
    offset = 0;
    if (gep.ByteOffset is not IrConstantInt index)
      return false;
    if (gep.ElementType is null) {
      offset = index.Value;
      return true;
    }
    if (IrAliasAnalysis.StorageBytes(gep.ElementType) is not { } elementBytes)
      return false;
    try {
      offset = checked(index.Value * elementBytes);
      return true;
    } catch (OverflowException) {
      return false;
    }
  }

  private static bool TryAdd(long left, long right, out long result) {
    try {
      result = checked(left + right);
      return true;
    } catch (OverflowException) {
      result = 0;
      return false;
    }
  }
}
