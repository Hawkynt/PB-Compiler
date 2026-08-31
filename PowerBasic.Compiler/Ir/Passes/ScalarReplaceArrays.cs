namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0182 — small local array scalar replacement.
///
/// A tiny local array whose address never escapes and whose every subscript is a compile-time
/// constant is not really an array: it is N independent variables that happen to share a name. Split
/// into N allocas, <see cref="Mem2Reg"/> promotes each into SSA, and everything downstream — constant
/// propagation, value numbering, dead-store elimination — sees ordinary values instead of memory it
/// has to be careful about. Left as an array, a single store to <c>a(0)</c> makes every later read of
/// <c>a(1)</c> unanalysable, because nothing here proves the two do not alias.
///
/// <para>
/// The conditions are narrow and each is load-bearing. The address must not escape (a call receiving
/// it can do anything to it); every access must be at a constant, in-range, element-aligned offset
/// (a computed subscript could name any element, so splitting would lose the connection); every
/// memory access must have the element's storage type; and the array must be small, because the point
/// is to expose values, not to mint fifty variables.
/// </para>
///
/// <para>
/// The storage-type proof is what distinguishes an array from packed aggregate backing. A packed
/// <c>TYPE</c> is also lowered as <c>alloca i8, N</c>, but a field inside it may be read as <c>i16</c>
/// or <c>i32</c>. Treating that wider field as one BYTE array element would silently shrink its
/// storage under opaque pointers. Aggregate scalar replacement is a separate proof with byte-region
/// overlap rules; this pass handles actual homogeneous arrays only.
/// </para>
/// </summary>
public static class ScalarReplaceArrays {

  /// <summary>The most elements worth splitting; beyond this the variables cost more than they reveal.</summary>
  private const int _MAX_ELEMENTS = 8;

  public static int Run(IrFunction fn) {
    if (fn.HasErrorHandler)
      return 0;                                  // a fault can enter anywhere - see IrFunction
    var split = 0;
    foreach (var block in fn.Blocks.ToList())
      foreach (var instruction in block.Instructions.ToList())
        if (instruction is IrAlloca alloca && Splittable(alloca, out var stride) && Split(fn, alloca, stride))
          ++split;
    return split;
  }

  /// <summary>Whether this alloca is a small, non-escaping homogeneous array reached only at constant offsets.</summary>
  private static bool Splittable(IrAlloca alloca, out int stride) {
    stride = SizeOf(alloca.Allocated);
    if (alloca.Count is < 2 or > _MAX_ELEMENTS || stride == 0)
      return false;

    foreach (var user in alloca.Users)
      switch (user) {
        // element zero, reached through the array pointer itself
        case IrLoad or IrStore when AccessMatchesElement(user, alloca, alloca.Allocated):
          continue;
        case IrGep gep when Offset(gep, stride) is >= 0 && Offset(gep, stride) < alloca.Count:
          foreach (var indexed in gep.Users)
            if (!AccessMatchesElement(indexed, gep, alloca.Allocated))
              return false;
          continue;
        default:
          return false;                          // a call, a phi, a store OF the pointer - it escapes
      }
    return true;
  }

  /// <summary>
  /// True when the instruction uses <paramref name="pointer"/> as an address and accesses exactly the
  /// array element's storage shape. Signed and unsigned integer views are storage-compatible; a wider
  /// field access is not.
  /// </summary>
  private static bool AccessMatchesElement(IrInstruction instruction, IrValue pointer, IrType elementType) => instruction switch {
    IrLoad load => ReferenceEquals(load.Pointer, pointer) && load.Type.SameStorage(elementType),
    IrStore store => ReferenceEquals(store.Pointer, pointer)
      && !ReferenceEquals(store.Value, pointer)
      && store.Value.Type.SameStorage(elementType),
    _ => false,
  };

  /// <summary>The element index a GEP names, or -1 when it is not a constant aligned one.</summary>
  private static int Offset(IrGep gep, int stride) {
    if (gep.ByteOffset is not IrConstantInt constant)
      return -1;
    if (gep.ElementType is not null)
      return (int)constant.Value;                // already an element index
    return constant.Value % stride == 0 ? (int)(constant.Value / stride) : -1;
  }

  private static int SizeOf(IrType type) => type.Kind switch {
    IrTypeKind.Int or IrTypeKind.Float => Math.Max(1, type.Bits / 8),
    _ => 0,                                      // a pointer element's width is a target property
  };

  private static bool Split(IrFunction fn, IrAlloca alloca, int stride) {
    var entry = fn.Entry;
    if (entry is null)
      return false;

    // one slot per element, inserted where the original was so they dominate every use
    var at = -1;
    for (var i = 0; i < entry.Instructions.Count; ++i)
      if (ReferenceEquals(entry.Instructions[i], alloca)) {
        at = i;
        break;
      }
    if (at < 0)
      return false;
    var elements = new IrAlloca[alloca.Count];
    for (var i = 0; i < elements.Length; ++i)
      elements[i] = entry.InsertAt(at + i + 1,
        new IrAlloca(alloca.Allocated) { Name = (alloca.Name ?? "a") + "." + i });

    // every GEP becomes the element it always named; the array pointer itself is element zero
    foreach (var user in alloca.Users.ToList())
      if (user is IrGep gep) {
        gep.ReplaceAllUsesWith(elements[Offset(gep, stride)]);
        gep.Parent?.Remove(gep);
      }
    alloca.ReplaceAllUsesWith(elements[0]);
    entry.Remove(alloca);
    return true;
  }
}
