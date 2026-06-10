namespace PowerBasic.Compiler.Semantics;

/// <summary>Discriminates the PB 3.5 scalar kinds.</summary>
public enum ScalarKind { Byte, Word, Dword, Integer, Long, Single, Double, Ext }

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
  public static readonly ScalarType Single = new(ScalarKind.Single, 4, true, true);
  public static readonly ScalarType Double = new(ScalarKind.Double, 8, true, true);
  public static readonly ScalarType Ext = new(ScalarKind.Ext, 10, true, true);
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
