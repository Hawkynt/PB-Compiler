namespace PowerBasic.Compiler.Emit.Omf;

/// <summary>A segment defined by an OMF module (SEGDEF).</summary>
public sealed class OmfSegment {
  public required string Name { get; init; }
  public required string ClassName { get; init; }
  /// <summary>Enumerated bytes written into the segment (LEDATA/LIDATA), zero-extended to <see cref="Length"/>.</summary>
  public byte[] Data { get; set; } = [];
  public int Length { get; set; }
  /// <summary>True for a code-class segment (CODE / ends in CODE / _TEXT), else treated as data.</summary>
  public bool IsCode =>
    this.ClassName.Contains("CODE", StringComparison.OrdinalIgnoreCase)
    || this.Name.EndsWith("_TEXT", StringComparison.OrdinalIgnoreCase)
    || this.Name.Equals("CODE", StringComparison.OrdinalIgnoreCase);
  /// <summary>True for an uninitialised segment (BSS / STACK) - contributes size, no bytes.</summary>
  public bool IsBss =>
    this.ClassName.Contains("BSS", StringComparison.OrdinalIgnoreCase)
    || this.ClassName.Contains("STACK", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A public symbol the module exports (PUBDEF): segment-relative offset.</summary>
public sealed record OmfPublic(string Name, int SegmentIndex, int Offset);

/// <summary>How a fixup's target is named.</summary>
public enum OmfTargetKind { Segment, External }

/// <summary>
/// FIXUPP location type (the LOC field). Only <see cref="Offset16"/> maps onto our tiny
/// single-segment model; <see cref="Base16"/>/<see cref="Pointer32"/> are far (seg or
/// seg:off) and cannot be hosted, so the converter rejects them rather than miscompile.
/// </summary>
public enum OmfLocation { LoByte = 0, Offset16 = 1, Base16 = 2, Pointer32 = 3, HiByte = 4, LoaderOffset16 = 5, Other = 0xFF }

/// <summary>
/// A relocation (FIXUPP): patch the location at <see cref="DataOffset"/> inside segment
/// <see cref="SegmentIndex"/>. <see cref="Location"/> says what kind of location it is
/// (a 16-bit offset is the only one we relocate). <see cref="SelfRelative"/> = the
/// location is a self-relative displacement (near call/jmp); otherwise an absolute offset.
/// </summary>
public sealed record OmfFixup(int SegmentIndex, int DataOffset, bool SelfRelative, OmfTargetKind TargetKind, int TargetIndex, OmfLocation Location = OmfLocation.Offset16);

/// <summary>A parsed OMF object module (one .OBJ, or one member of a .LIB).</summary>
public sealed class OmfModule {
  public string Name { get; set; } = "";
  /// <summary>Segments in definition order; OMF segment indices are 1-based into this list.</summary>
  public List<OmfSegment> Segments { get; } = [];
  public List<OmfPublic> Publics { get; } = [];
  /// <summary>EXTDEF names; OMF external indices are 1-based into this list.</summary>
  public List<string> Externals { get; } = [];
  public List<OmfFixup> Fixups { get; } = [];
}
