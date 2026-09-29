namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>The relationship between two memory locations.</summary>
public enum IrAliasResult {
  /// <summary>The locations are proven not to overlap.</summary>
  NoAlias,
  /// <summary>The locations may overlap, but the analysis cannot prove whether they do.</summary>
  MayAlias,
  /// <summary>The locations are proven to overlap, but start at different addresses.</summary>
  PartialAlias,
  /// <summary>The locations are proven to start at the same address.</summary>
  MustAlias,
}

/// <summary>
/// Basic, target-independent alias analysis for ordinary IR loads and stores.
///
/// <para>
/// A memory access is not just a pointer: its byte width is part of the question. Two two-byte
/// accesses at offsets zero and one overlap even though their start addresses differ, which is why
/// the old pointer-only tests in the memory optimizers were not sufficient. This analysis therefore
/// answers queries over <c>(pointer, access type)</c> pairs and reasons about half-open byte ranges
/// whenever both offsets and widths are known.
/// </para>
///
/// <para>
/// The compatibility query has a deliberately small built-in provenance model. Analysis-owned callers
/// may additionally supply <see cref="IrPointerIdentityAnalysis"/>, which centralizes pointer roots
/// across GEPs and pointer-preserving bitcasts while keeping BYREF arguments non-unique. Loaded
/// pointers, integer-to-pointer casts, explicit far pointers and other opaque roots remain conservative.
/// </para>
/// </summary>
public static class IrAliasAnalysis {

  private readonly record struct Address(IrValue Root, long? Offset, bool IsUniqueObject);

  /// <summary>Classifies whether two typed memory accesses overlap.</summary>
  public static IrAliasResult Alias(
      IrValue firstPointer,
      IrType firstAccessType,
      IrValue secondPointer,
      IrType secondAccessType,
      IrPointerIdentityAnalysis? identities = null) {
    ArgumentNullException.ThrowIfNull(firstPointer);
    ArgumentNullException.ThrowIfNull(firstAccessType);
    ArgumentNullException.ThrowIfNull(secondPointer);
    ArgumentNullException.ThrowIfNull(secondAccessType);

    if (ReferenceEquals(firstPointer, secondPointer))
      return IrAliasResult.MustAlias;

    var first = Decompose(firstPointer, identities);
    var second = Decompose(secondPointer, identities);
    if (!ReferenceEquals(first.Root, second.Root))
      return first.IsUniqueObject && second.IsUniqueObject
        ? IrAliasResult.NoAlias
        : IrAliasResult.MayAlias;

    if (first.Offset is not { } firstOffset || second.Offset is not { } secondOffset)
      return IrAliasResult.MayAlias;
    if (firstOffset == secondOffset)
      return IrAliasResult.MustAlias;

    var firstBytes = StorageBytes(firstAccessType);
    var secondBytes = StorageBytes(secondAccessType);
    if (firstBytes is not { } firstSize || secondBytes is not { } secondSize
        || !TryEnd(firstOffset, firstSize, out var firstEnd)
        || !TryEnd(secondOffset, secondSize, out var secondEnd))
      return IrAliasResult.MayAlias;

    return firstEnd <= secondOffset || secondEnd <= firstOffset
      ? IrAliasResult.NoAlias
      : IrAliasResult.PartialAlias;
  }

  /// <summary>True unless the two typed accesses are proven disjoint.</summary>
  public static bool MayAlias(
      IrValue firstPointer,
      IrType firstAccessType,
      IrValue secondPointer,
      IrType secondAccessType,
      IrPointerIdentityAnalysis? identities = null)
    => Alias(firstPointer, firstAccessType, secondPointer, secondAccessType, identities)
       != IrAliasResult.NoAlias;

  /// <summary>
  /// Whether <paramref name="later"/> completely covers every byte written by
  /// <paramref name="earlier"/>. Unknown offsets/widths deliberately answer false.
  /// </summary>
  public static bool CompletelyOverwrites(
      IrStore later,
      IrStore earlier,
      IrPointerIdentityAnalysis? identities = null) {
    ArgumentNullException.ThrowIfNull(later);
    ArgumentNullException.ThrowIfNull(earlier);

    var laterAddress = Decompose(later.Pointer, identities);
    var earlierAddress = Decompose(earlier.Pointer, identities);
    if (!ReferenceEquals(laterAddress.Root, earlierAddress.Root)
        || laterAddress.Offset is not { } laterOffset
        || earlierAddress.Offset is not { } earlierOffset
        || StorageBytes(later.Value.Type) is not { } laterBytes
        || StorageBytes(earlier.Value.Type) is not { } earlierBytes
        || !TryEnd(laterOffset, laterBytes, out var laterEnd)
        || !TryEnd(earlierOffset, earlierBytes, out var earlierEnd))
      return false;

    return laterOffset <= earlierOffset && laterEnd >= earlierEnd;
  }

  /// <summary>
  /// Returns the target-independent storage width of a scalar type, or null when the target decides
  /// it (currently pointers) or the type has no storage.
  /// </summary>
  public static int? StorageBytes(IrType type) {
    ArgumentNullException.ThrowIfNull(type);
    if (type.IsVoid || type.IsPointer || type.Bits <= 0)
      return null;
    try {
      return checked((type.Bits + 7) / 8);
    } catch (OverflowException) {
      return null;
    }
  }

  private static Address Decompose(IrValue pointer, IrPointerIdentityAnalysis? identities) {
    if (identities?.TryResolve(pointer) is { } identity)
      return new(identity.Root, identity.ByteOffset, identity.IsUniqueObject);

    var root = pointer;
    long offset = 0;
    var known = true;
    while (root is IrGep gep) {
      if (known && TryGepOffset(gep, out var displacement) && TryAdd(offset, displacement, out var sum))
        offset = sum;
      else
        known = false;
      root = gep.BasePtr;
    }
    return new(root, known ? offset : null, IsUniqueObject(root));
  }

  private static bool TryGepOffset(IrGep gep, out long offset) {
    offset = 0;
    if (gep.ByteOffset is not IrConstantInt index)
      return false;
    if (gep.ElementType is null) {
      offset = index.Value;
      return true;
    }
    if (StorageBytes(gep.ElementType) is not { } elementBytes)
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

  private static bool TryEnd(long start, int bytes, out long end) {
    try {
      end = checked(start + bytes);
      return true;
    } catch (OverflowException) {
      end = 0;
      return false;
    }
  }

  private static bool IsUniqueObject(IrValue value) => value is IrAlloca or IrGlobalVariable;
}
