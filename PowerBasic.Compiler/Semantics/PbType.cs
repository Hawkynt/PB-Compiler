namespace PowerBasic.Compiler.Semantics;

/// <summary>Discriminates the PB 3.5 scalar kinds.</summary>
public enum ScalarKind { Byte, Word, Dword, Integer, Long, Quad, Single, Double, Ext }

/// <summary>
/// A resolved PowerBASIC type. Sizes are the on-target (16-bit real mode) byte
/// sizes; UDTs are packed (PB 3.5 TYPEs have no padding).
/// </summary>
public abstract record PbType {
  /// <summary>Storage size in bytes on the DOS target.</summary>
  public abstract int Size { get; }

  public static readonly ScalarType Byte = new(ScalarKind.Byte, 1, false, false);
  public static readonly ScalarType Word = new(ScalarKind.Word, 2, false, false);
  public static readonly ScalarType Dword = new(ScalarKind.Dword, 4, false, false);
  public static readonly ScalarType Integer = new(ScalarKind.Integer, 2, true, false);
  public static readonly ScalarType Long = new(ScalarKind.Long, 4, true, false);
  public static readonly ScalarType Quad = new(ScalarKind.Quad, 8, true, false);
  public static readonly ScalarType Single = new(ScalarKind.Single, 4, true, true);
  public static readonly ScalarType Double = new(ScalarKind.Double, 8, true, true);
  public static readonly ScalarType Ext = new(ScalarKind.Ext, 10, true, true);
  public static readonly BcdType Fix = new(IsFixedPoint: true);
  public static readonly BcdType Bcd = new(IsFixedPoint: false);
  public static readonly StringType String = new();
  public static readonly AnyType Any = new();
}

/// <summary>Numeric scalar.</summary>
public sealed record ScalarType(ScalarKind Kind, int ByteSize, bool Signed, bool IsFloat) : PbType {
  public override int Size => this.ByteSize;
  public bool IsIntegral => !this.IsFloat;
}

/// <summary>
/// Dynamic string. Stored as a 2-byte handle into the runtime's string handle
/// table; the character data lives in the far string heap.
/// </summary>
public sealed record StringType : PbType {
  public override int Size => 2;
}

/// <summary>Fixed-length string (<c>STRING * n</c>), stored inline.</summary>
public sealed record FixedStringType(int Length) : PbType {
  public override int Size => this.Length;
}

/// <summary>FLEX string (PB 3.5 flexible structure); stored like a dynamic string handle.</summary>
public sealed record FlexType : PbType {
  public override int Size => 2;
}

/// <summary>
/// ASCIIZ * n (PB 3.5): NUL-terminated fixed buffer of n bytes; LEN() is the
/// character count before the NUL, SIZEOF() is n.
/// </summary>
public sealed record AsciizType(int Length) : PbType {
  public override int Size => this.Length;
}

/// <summary>
/// BCD numeric (baseline PB): FIX (<c>@</c>, 8 bytes fixed-point) or BCD
/// (<c>@@</c>, 10 bytes floating). Storage is supported; arithmetic comes with
/// a later wave and is diagnosed by the binder.
/// </summary>
public sealed record BcdType(bool IsFixedPoint) : PbType {
  public override int Size => this.IsFixedPoint ? 8 : 10;
}

/// <summary>Data pointer (PB 3.2): 32-bit seg:off pointer to <see cref="Target"/>; <c>@p</c> dereferences.</summary>
public sealed record PointerType(PbType Target) : PbType {
  public override int Size => 4;
}

/// <summary>
/// Microsoft Binary Format float (BASICA / GW-BASIC): the interpreters store
/// SINGLE / DOUBLE in MBF, not IEEE - a biased-128 exponent byte with the sign
/// folded into the mantissa's top bit. The value computes on the x87 as usual;
/// only the in-memory cell is MBF, with conversion on load/store. Single is
/// 4 bytes (precision-identical to IEEE single); Double is 8 bytes.
/// </summary>
public sealed record MbfType(bool IsDouble) : PbType {
  public override int Size => this.IsDouble ? 8 : 4;
}

/// <summary>
/// PB 3.6 typed procedure pointer / delegate (a "fat" closure value): an 8-byte
/// cell holding a far code pointer (offset, segment) followed by a far environment
/// pointer (offset, segment). A call through it coerces arguments to
/// <see cref="ParameterTypes"/> (BYVAL), passes the environment, and yields
/// <see cref="ReturnType"/> (null for a SUB). Assignable from a lambda (the env is
/// null for a non-capturing lambda, or points at the captured locals for a
/// capturing one) or CODEPTR32 of a matching procedure (null env). Storing one into
/// a 4-byte DWORD keeps just the code pointer (the CALL DWORD interop path).
/// </summary>
public sealed record ProcPtrType(IReadOnlyList<PbType> ParameterTypes, PbType? ReturnType) : PbType {
  public override int Size => 8;

  // the parameter list is a reference-typed member, so the record's synthesized
  // equality would compare it by reference - two structurally identical signatures
  // (a DECLARE and its definition, two DIMs of the same delegate) must compare equal
  public bool Equals(ProcPtrType? other)
    => other is not null
       && EqualityComparer<PbType?>.Default.Equals(this.ReturnType, other.ReturnType)
       && this.ParameterTypes.SequenceEqual(other.ParameterTypes);

  public override int GetHashCode() {
    var hash = new HashCode();
    hash.Add(this.ReturnType);
    foreach (var p in this.ParameterTypes)
      hash.Add(p);
    return hash.ToHashCode();
  }
}

/// <summary>One UDT/UNION field with its resolved offset.</summary>
public sealed record UdtField(string Name, PbType Type, int Offset, int ElementCount = 1) {
  public int TotalSize => this.Type.Size * this.ElementCount;
}

/// <summary>TYPE ... END TYPE (packed) or UNION ... END UNION (all fields at offset 0).</summary>
public sealed record UdtType(string Name, IReadOnlyList<UdtField> Fields, bool IsUnion) : PbType {
  public override int Size => this.IsUnion
    ? this.Fields.Max(f => f.TotalSize)
    : this.Fields.Sum(f => f.TotalSize);

  public UdtField? FindField(string name) => this.Fields.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Array of <see cref="Element"/>; static arrays have compile-time bounds, dynamic ones a descriptor.</summary>
public sealed record ArrayType(PbType Element, IReadOnlyList<(int Lower, int Upper)>? StaticBounds, int Rank) : PbType {
  public bool IsDynamic => this.StaticBounds == null;

  /// <summary>Static arrays: inline data size. Dynamic arrays: descriptor size (segment, offset, element size, rank, bounds).</summary>
  public override int Size => this.StaticBounds == null
    ? 8 + this.Rank * 4
    : this.Element.Size * this.ElementCount;

  public int ElementCount => this.StaticBounds?.Aggregate(1, (acc, b) => acc * (b.Upper - b.Lower + 1)) ?? 0;
}

/// <summary>Parameter-only wildcard (<c>AS ANY</c>).</summary>
public sealed record AnyType : PbType {
  public override int Size => 0;
}
