namespace PowerBasic.Compiler.Emit.Omf;

/// <summary>
/// Lowers a parsed <see cref="OmfModule"/> to a synthetic <see cref="PbuFile"/> so the
/// existing <see cref="Linker"/> lays it out and resolves it like any other unit
/// (docs/LINKER.md). Code-class segments are concatenated into the unit's code, data/
/// const into its data, BSS into its bss; PUBDEFs become exports, EXTDEFs imports, and
/// FIXUPPs are translated to the unit fixup kinds - external references become
/// import calls/offsets, intra-module segment references are pre-relocated by the
/// target segment's base so the linker's per-unit base add composes correctly.
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

    // 3. fixups. The tiny single-segment model the Linker hosts can express a
    //    relocation only at a CODE site (PbuFixup.Offset is code-base relative) and
    //    only for a 16-bit OFFSET location. Far (segment / seg:off) locations and
    //    relocations sitting in a data segment have no representation here, so we
    //    reject them with a clear diagnostic rather than emit a wrong image.
    foreach (var f in m.Fixups) {
      if (f.SegmentIndex < 1 || f.SegmentIndex > m.Segments.Count)
        continue; // dangling site segment: nothing to patch
      if (f.Location is OmfLocation.Base16 or OmfLocation.Pointer32)
        throw new OmfException($"module '{pbu.Name}' has a far (segment/pointer) fixup; the tiny single-segment model cannot host it");
      if (f.Location is not (OmfLocation.Offset16 or OmfLocation.LoaderOffset16))
        throw new OmfException($"module '{pbu.Name}' has an unsupported FIXUPP location ({f.Location})");
      if (!inCode[f.SegmentIndex]) {
        if (m.Segments[f.SegmentIndex - 1].IsBss) continue; // a fixup into BSS has no bytes to patch
        throw new OmfException($"module '{pbu.Name}' has a relocation inside a data segment; the linker can only patch code-segment sites");
      }
      var site = (uint)(codeBase[f.SegmentIndex] + f.DataOffset);
      if (f.TargetKind == OmfTargetKind.External) {
        var name = f.TargetIndex >= 1 && f.TargetIndex <= m.Externals.Count ? m.Externals[f.TargetIndex - 1] : null;
        if (name == null || !importIndex.TryGetValue(name, out var imp)) continue;
        pbu.Fixups.Add(new PbuFixup(site, f.SelfRelative ? PbuFixupKind.ImportCall : PbuFixupKind.ImportOffset, (ushort)imp));
      } else { // segment target inside this module (may be any code/data segment)
        var tgt = f.TargetIndex;
        if (tgt < 1 || tgt > m.Segments.Count) continue;
        if (m.Segments[tgt - 1].IsBss)
          throw new OmfException($"module '{pbu.Name}' relocates against a BSS segment, which has no fixed offset in this model");
        if (f.SelfRelative && tgt == f.SegmentIndex) continue; // same-segment self-relative: already correct
        // pre-add the target segment's intra-unit base so the linker's per-unit base add finishes it
        AddAtSite(code, (int)site, inCode[tgt] ? codeBase[tgt] : dataBase[tgt]);
        pbu.Fixups.Add(new PbuFixup(site, inCode[tgt] ? PbuFixupKind.NearCode : PbuFixupKind.DataOffset, 0));
      }
    }
    return pbu;
  }

  private static void AddAtSite(byte[] image, int site, int addend) {
    if (site + 1 >= image.Length) return;
    var v = (ushort)((image[site] | (image[site + 1] << 8)) + addend);
    image[site] = (byte)v;
    image[site + 1] = (byte)(v >> 8);
  }
}
