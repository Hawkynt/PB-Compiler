namespace PowerBasic.Compiler.Emit.Omf;

/// <summary>
/// Lowers a parsed <see cref="OmfModule"/> to a synthetic <see cref="PbuFile"/> so the
/// existing <see cref="Linker"/> lays it out and resolves it like any other unit
/// (docs/LINKER.md). Code-class segments are concatenated into the unit's code, data/
/// const into its data, BSS into its bss; PUBDEFs become exports, EXTDEFs imports, and
/// FIXUPPs are translated to the unit fixup kinds - external references become
/// import calls/offsets, intra-module segment references are pre-relocated by the
/// target segment's base so the linker's per-unit base add composes correctly.
/// Far (segment / seg:off) and data-segment fixups are lowered too: because the whole
/// program lives in one combined segment loaded at a single paragraph, a far reference's
/// segment is just that load segment (an MZ relocation), which hosts compact/large-model
/// objects that still fit 64 KiB (the linker rejects larger images).
/// </summary>
public static class OmfToPbu {

  public static PbuFile Convert(OmfModule m) {
    // 1. lay this module's own segments into combined code / data / bss
    var codeBase = new int[m.Segments.Count + 1];
    var dataBase = new int[m.Segments.Count + 1];
    var inCode = new bool[m.Segments.Count + 1];
    var codeLen = 0; var dataLen = 0; uint bss = 0;
    for (var i = 0; i < m.Segments.Count; i++) {
      var s = m.Segments[i];
      if (s.IsBss) { bss += (uint)s.Length; continue; }
      if (s.IsCode) { codeBase[i + 1] = codeLen; inCode[i + 1] = true; codeLen += s.Length; }
      else { dataBase[i + 1] = dataLen; dataLen += s.Length; }
    }

    var code = new byte[codeLen];
    var data = new byte[dataLen];
    for (var i = 0; i < m.Segments.Count; i++) {
      var s = m.Segments[i];
      if (s.IsBss) continue;
      var dst = s.IsCode ? code : data;
      var at = s.IsCode ? codeBase[i + 1] : dataBase[i + 1];
      Array.Copy(s.Data, 0, dst, at, Math.Min(s.Data.Length, s.Length));
    }

    var pbu = new PbuFile { Name = m.Name.Length > 0 ? m.Name : "omf", Code = code, Data = data, BssSize = bss, Foreign = true };

    // 2. exports (publics) and imports (externals)
    foreach (var p in m.Publics) {
      var off = p.SegmentIndex >= 1 && inCode[p.SegmentIndex] ? codeBase[p.SegmentIndex] + p.Offset : p.Offset;
      pbu.Exports.Add(new PbuExport(p.Name, PbuExportKind.Function, 0, (uint)off));
    }
    // Only externals a FIXUPP actually targets impose a link dependency. Compilers
    // (notably Watcom) emit phantom EXTDEFs - memory-model markers such as
    // _small_code_ / _small_data_ that no fixup references - purely as a hint to
    // their own librarian about which runtime to pull. Importing those would
    // manufacture a dependency nothing can satisfy, so skip the unreferenced ones.
    var referenced = new HashSet<int>();
    foreach (var f in m.Fixups)
      if (f.TargetKind == OmfTargetKind.External && f.TargetIndex >= 1 && f.TargetIndex <= m.Externals.Count)
        referenced.Add(f.TargetIndex);
    var importIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    for (var e = 0; e < m.Externals.Count; e++) {
      if (!referenced.Contains(e + 1))
        continue; // unreferenced external - no relocation depends on it
      importIndex[m.Externals[e]] = pbu.Imports.Count;
      pbu.Imports.Add(new PbuImport(m.Externals[e], 0));
    }

    // 3. fixups. The whole program is hosted in ONE combined segment loaded at a single
    //    paragraph (code, then data, then BSS - at most 64 KiB, the linker enforces it).
    //    Within one segment every FAR reference's segment value is simply that load
    //    segment, so a Base16 (segment word) location becomes an MZ relocation (the site
    //    holds 0; the DOS loader adds the load segment) and a Pointer32 (seg:off) splits
    //    into an offset half plus that segment relocation. An offset half - like a near
    //    Offset16 - is the target's offset inside the combined segment. A fixup site that
    //    sits in a data segment is patched in the data image (PbuFixup.InData). This hosts
    //    compact/large-model objects (far data and far pointers) as long as the whole image
    //    still fits the 64 KiB segment; a genuinely larger multi-segment image is rejected
    //    later by the linker's size check.
    foreach (var f in m.Fixups) {
      if (f.SegmentIndex < 1 || f.SegmentIndex > m.Segments.Count)
        continue; // dangling site segment: nothing to patch
      if (m.Segments[f.SegmentIndex - 1].IsBss)
        continue; // a fixup into BSS has no bytes to patch
      var inData = !inCode[f.SegmentIndex];
      var blob = inData ? data : code;
      var unitOffset = (uint)((inData ? dataBase[f.SegmentIndex] : codeBase[f.SegmentIndex]) + f.DataOffset);
      var site = (int)unitOffset;
      switch (f.Location) {
        case OmfLocation.Offset16 or OmfLocation.LoaderOffset16:
          EmitOffset(f, blob, site, unitOffset, inData);
          break;
        case OmfLocation.Base16:
          EmitSegment(blob, site, unitOffset, inData);
          break;
        case OmfLocation.Pointer32: // seg:off - offset half at the site, segment half above it
          EmitOffset(f, blob, site, unitOffset, inData);
          EmitSegment(blob, site + 2, unitOffset + 2, inData);
          break;
        default:
          throw new OmfException($"module '{pbu.Name}' has an unsupported FIXUPP location ({f.Location})");
      }
    }
    return pbu;

    // -- lower one 16-bit offset location (near offset, or the offset half of a far ptr) --
    void EmitOffset(OmfFixup f, byte[] blob, int site, uint unitOffset, bool inData) {
      if (f.TargetKind == OmfTargetKind.External) {
        var name = f.TargetIndex >= 1 && f.TargetIndex <= m.Externals.Count ? m.Externals[f.TargetIndex - 1] : null;
        if (name == null || !importIndex.TryGetValue(name, out var imp)) return;
        // a near call carries no addend (the linker computes the displacement); an absolute
        // import offset keeps its located addend, plus any FIXUPP target displacement.
        if (!f.SelfRelative && f.Displacement != 0) AddAtSite(blob, site, f.Displacement);
        pbu.Fixups.Add(new PbuFixup(unitOffset, f.SelfRelative ? PbuFixupKind.ImportCall : PbuFixupKind.ImportOffset, (ushort)imp, inData));
      } else { // segment target inside this module (may be any code/data segment)
        var tgt = f.TargetIndex;
        if (tgt < 1 || tgt > m.Segments.Count) return;
        if (m.Segments[tgt - 1].IsBss)
          throw new OmfException($"module '{pbu.Name}' relocates against a BSS segment, which has no fixed offset in this model");
        if (f.SelfRelative && tgt == f.SegmentIndex && !inData) return; // same-segment self-relative: already correct
        // pre-add the target segment's intra-unit base (and any displacement) so the
        // linker's per-unit base add finishes it
        AddAtSite(blob, site, (inCode[tgt] ? codeBase[tgt] : dataBase[tgt]) + f.Displacement);
        pbu.Fixups.Add(new PbuFixup(unitOffset, inCode[tgt] ? PbuFixupKind.NearCode : PbuFixupKind.DataOffset, 0, inData));
      }
    }

    // -- lower a 16-bit segment word: one load segment, so zero it and let the loader add it --
    void EmitSegment(byte[] blob, int site, uint unitOffset, bool inData) {
      if (site >= 0 && site + 1 < blob.Length) { blob[site] = 0; blob[site + 1] = 0; }
      pbu.Fixups.Add(new PbuFixup(unitOffset, PbuFixupKind.Segment, 0, inData));
    }
  }

  private static void AddAtSite(byte[] image, int site, int addend) {
    if (site < 0 || site + 1 >= image.Length) return;
    var v = (ushort)((image[site] | (image[site + 1] << 8)) + addend);
    image[site] = (byte)v;
    image[site + 1] = (byte)(v >> 8);
  }
}
