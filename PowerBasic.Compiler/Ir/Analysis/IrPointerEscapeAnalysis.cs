namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>
/// Conservative function-local pointer escape analysis.
///
/// <para>
/// A pointer remains non-escaping only while every use is an ordinary load/store through that pointer,
/// a GEP derived from it, or a pointer-preserving bitcast whose own uses are likewise non-escaping.
/// Passing the address to a call, returning it, storing the pointer value, merging it through phi/select,
/// converting it through an integer, binding it to inline assembly, or any unrecognized use is an escape.
/// </para>
///
/// <para>
/// This deliberately answers only the capture question. Whether the root is a compiler-private object is
/// policy owned by the consuming optimization; for example O0065 excludes source-variable allocas even
/// when their address is proven non-escaping.
/// </para>
/// </summary>
public sealed class IrPointerEscapeAnalysis {

  private readonly Dictionary<IrValue, bool> _doesNotEscape
    = new(ReferenceEqualityComparer.Instance);

  /// <summary>True when the complete derived-pointer use graph is confined to explicit memory accesses.</summary>
  public bool DoesNotEscape(IrValue pointer) {
    ArgumentNullException.ThrowIfNull(pointer);
    if (!pointer.Type.IsPointer)
      return false;
    return this.DoesNotEscape(pointer, new HashSet<IrValue>(ReferenceEqualityComparer.Instance));
  }

  private bool DoesNotEscape(IrValue pointer, HashSet<IrValue> active) {
    if (this._doesNotEscape.TryGetValue(pointer, out var cached))
      return cached;
    if (!active.Add(pointer))
      return true;

    var result = true;
    foreach (var user in pointer.Users) {
      switch (user) {
        case IrLoad load when ReferenceEquals(load.Pointer, pointer):
          break;

        case IrStore store when ReferenceEquals(store.Pointer, pointer)
                                && !ReferenceEquals(store.Value, pointer):
          break;

        case IrGep gep when ReferenceEquals(gep.BasePtr, pointer):
          if (!this.DoesNotEscape(gep, active))
            result = false;
          break;

        case IrCast { Op: IrCastOp.BitCast } cast
          when ReferenceEquals(cast.Value, pointer)
               && cast.Value.Type.IsPointer
               && cast.Type.IsPointer:
          if (!this.DoesNotEscape(cast, active))
            result = false;
          break;

        default:
          result = false;
          break;
      }

      if (!result)
        break;
    }

    active.Remove(pointer);
    this._doesNotEscape[pointer] = result;
    return result;
  }
}
