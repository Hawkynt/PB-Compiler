namespace PowerBasic.Compiler.Emit.Omf;

/// <summary>
/// Emits a <see cref="PbuFile"/> as a 16-bit Intel OMF object module (.OBJ) - the inverse of
/// <see cref="OmfReader"/>/<see cref="OmfToPbu"/> (docs/LINKER.md). This lets a genuine linker
/// (MS <c>LINK.EXE</c>) or C/asm object consume PB output, where today we only consume foreign
/// OMF. The unit's single code blob becomes one <c>_TEXT</c>/CODE segment and its data blob
/// (plus BSS) one <c>_DATA</c>/DATA segment; exports become PUBDEFs, imports EXTDEFs, and each
/// <see cref="PbuFixup"/> a FIXUPP whose located bytes keep their addend:
/// <list type="bullet">
///   <item><see cref="PbuFixupKind.NearCode"/>/<see cref="PbuFixupKind.DataOffset"/> - an
///   absolute 16-bit OFFSET (LOC=1) against the code / data SEGDEF.</item>
///   <item><see cref="PbuFixupKind.Segment"/> - a 16-bit BASE/segment word (LOC=2).</item>
///   <item><see cref="PbuFixupKind.ImportCall"/> - a self-relative OFFSET against the EXTDEF
///   (near call displacement); <see cref="PbuFixupKind.ImportOffset"/> the absolute variant.</item>
/// </list>
/// Because the unit is one code + one data segment, intra-unit segment bases are 0, so reading
/// the emitted object back with <see cref="OmfToPbu"/> reproduces the original unit (segment
/// fixup sites normalise to 0; BASIC-only export metadata - kind, signature hash - is not
/// representable in OMF and is dropped, as for any foreign object).
/// </summary>
public static class OmfWriter {

  // Classic LINK.EXE accepts at most 1024 enumerated-data bytes per LEDATA record; a larger
  // segment is split across several, each carrying its own fixups (offsets are LEDATA-relative).
  private const int MaxLedataBytes = 1024;

  public static byte[] WriteObject(PbuFile unit) {
    ArgumentNullException.ThrowIfNull(unit);
    var name = unit.Name.Length > 0 ? unit.Name : "PBU";
    var hasData = unit.Data.Length > 0 || unit.BssSize > 0;

    var o = new List<byte>();
    o.AddRange(Record(0x80, Str(name)));                                          // THEADR
    o.AddRange(Record(0x96, Str("_TEXT"), Str("CODE"), Str("_DATA"), Str("DATA"))); // LNAMES 1.._TEXT 2.CODE 3._DATA 4.DATA
    o.AddRange(Record(0x98, B(0x28), U16(unit.Code.Length), B(1), B(2), B(0)));   // SEGDEF seg1 _TEXT/CODE (byte align, public)
    if (hasData)
      o.AddRange(Record(0x98, B(0x28), U16(checked((int)(unit.Data.Length + unit.BssSize))), B(3), B(4), B(0))); // SEGDEF seg2 _DATA/DATA

    if (unit.Imports.Count > 0) {
      var ext = new List<byte>();
      foreach (var import in unit.Imports) { ext.AddRange(Str(import.Name)); ext.Add(0); /* type index */ }
      o.AddRange(Record(0x8C, ext.ToArray()));                                    // EXTDEF (index = order, 1-based)
    }

    if (unit.Exports.Count > 0) {
      var pub = new List<byte> { 0, 1 };                                          // group 0, segment 1 (code)
      foreach (var export in unit.Exports) {
        pub.AddRange(Str(export.Name));
        pub.AddRange(U16(checked((int)export.CodeOffset)));
        pub.Add(0);                                                               // type index
      }
      o.AddRange(Record(0x90, pub.ToArray()));                                    // PUBDEF
    }

    EmitSegment(o, segment: 1, unit.Code, [.. unit.Fixups.Where(f => !f.InData)]);
    if (unit.Data.Length > 0)
      EmitSegment(o, segment: 2, unit.Data, [.. unit.Fixups.Where(f => f.InData)]);

    o.AddRange(Record(0x8A, B(0)));                                               // MODEND (non-main, no start address)
    return o.ToArray();
  }

  /// <summary>Writes <paramref name="data"/> as LEDATA chunks (each followed by its FIXUPPs), so no fixup straddles a chunk and offsets stay LEDATA-relative.</summary>
  private static void EmitSegment(List<byte> o, int segment, byte[] data, IReadOnlyList<PbuFixup> fixups) {
    var ordered = fixups.OrderBy(f => f.Offset).ToList();
    for (var pos = 0; pos < data.Length;) {
      var len = Math.Min(MaxLedataBytes, data.Length - pos);
      // end the chunk before any 2-byte fixup that would straddle its tail
      foreach (var f in ordered)
        if (f.Offset > (uint)pos && f.Offset < (uint)(pos + len) && f.Offset + 2 > (uint)(pos + len))
          len = (int)(f.Offset - (uint)pos);

      o.AddRange(Record(0xA0, B((byte)segment), U16(pos), data[pos..(pos + len)])); // LEDATA

      var inChunk = ordered.Where(f => f.Offset >= (uint)pos && f.Offset + 2 <= (uint)(pos + len)).ToList();
      if (inChunk.Count > 0) {
        var fx = new List<byte>();
        foreach (var f in inChunk)
          fx.AddRange(FixupSubrecord(f, segment, (int)(f.Offset - (uint)pos)));
        o.AddRange(Record(0x9C, fx.ToArray()));                                    // FIXUPP
      }
      pos += len;
    }
  }

  /// <summary>One FIXUP subrecord: LOCAT (high byte first) + FIXDAT + frame/target indices, no displacement (addend stays in the located bytes).</summary>
  private static byte[] FixupSubrecord(PbuFixup f, int siteSegment, int siteRel) {
    var (loc, selfRel, external) = f.Kind switch {
      PbuFixupKind.NearCode => (1, false, false),
      PbuFixupKind.DataOffset => (1, false, false),
      PbuFixupKind.Segment => (2, false, false),
      PbuFixupKind.ImportCall => (1, true, true),
      PbuFixupKind.ImportOffset => (1, false, true),
      _ => throw new OmfException($"cannot emit fixup kind {f.Kind} to OMF"),
    };
    var targetSegment = f.Kind == PbuFixupKind.DataOffset ? 2 : 1;
    var hi = (byte)(0x80 | (selfRel ? 0 : 0x40) | (loc << 2) | ((siteRel >> 8) & 0x3));
    var lo = (byte)(siteRel & 0xFF);
    if (external) {
      // FIXDAT: frame=SEGDEF(method 0), P=1 (no displacement), target=EXTDEF(method 2)
      const byte fixdat = (0 << 4) | (1 << 2) | 2;
      return [hi, lo, fixdat, .. Idx(siteSegment), .. Idx(f.Target + 1)]; // frame = site segment, target = external
    }
    // FIXDAT: frame=SEGDEF(method 0), P=1, target=SEGDEF(method 0); frame and target are that segment
    const byte segFixdat = (0 << 4) | (1 << 2) | 0;
    return [hi, lo, segFixdat, .. Idx(targetSegment), .. Idx(targetSegment)];
  }

  // ---- OMF primitives -------------------------------------------------------

  private static byte[] Record(byte type, params byte[][] parts) {
    var body = parts.SelectMany(p => p).ToArray();
    var len = body.Length + 1; // body + trailing checksum byte (0 = "ignore")
    return [type, (byte)len, (byte)(len >> 8), .. body, 0];
  }

  private static byte[] Str(string s) {
    var b = System.Text.Encoding.ASCII.GetBytes(s);
    if (b.Length > byte.MaxValue) throw new OmfException($"OMF name too long: {s}");
    return [(byte)b.Length, .. b];
  }

  private static byte[] U16(int v) => [(byte)v, (byte)(v >> 8)];
  private static byte[] B(params byte[] v) => v;

  /// <summary>OMF index: one byte if &lt; 0x80, else a two-byte 15-bit big-endian-ish value.</summary>
  private static byte[] Idx(int v) => v < 0x80 ? [(byte)v] : [(byte)(0x80 | (v >> 8)), (byte)v];
}
