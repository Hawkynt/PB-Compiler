namespace PowerBasic.Compiler.Emit;

/// <summary>One linked artifact ready for the MZ writer.</summary>
public sealed record LinkedImage(byte[] Code, byte[] Data, uint BssSize, IReadOnlyList<int> SegmentRelocationSites, IReadOnlyDictionary<string, uint> ResolvedExports);

/// <summary>Raised when symbol resolution fails or signatures mismatch.</summary>
public sealed class LinkException(string message) : Exception(message);

/// <summary>
/// Resolves a main image's imports against explicitly linked units and
/// libraries (docs/FORMATS.md "Linking model"): units link unconditionally,
/// libraries contribute only units that satisfy unresolved imports; signature
/// hashes must match; everything is laid out into one combined code+data image.
/// </summary>
public sealed class Linker {

  private readonly List<PbuFile> _mandatoryUnits = [];
  private readonly List<PblFile> _libraries = [];
  private readonly List<Omf.OmfLibrary> _omfLibraries = [];

  public void AddUnit(PbuFile unit) => this._mandatoryUnits.Add(unit);
  public void AddLibrary(PblFile library) => this._libraries.Add(library);

  /// <summary>Adds a foreign OMF .LIB for lazy, dictionary-driven selective extraction (only referenced members are pulled).</summary>
  public void AddOmfLibrary(Omf.OmfLibrary library) => this._omfLibraries.Add(library);

  /// <summary>
  /// Links <paramref name="main"/> (a unit-shaped image whose exports are the
  /// program's own procedures) into one image. Layout: main code, then each
  /// pulled unit's code; data areas concatenated after all code in the same order.
  /// </summary>
  public LinkedImage Link(PbuFile main) {
    // 1. decide which units participate: all mandatory ones, plus library units
    //    pulled in while imports remain unresolved (transitively)
    var participating = new List<PbuFile> { main };
    participating.AddRange(this._mandatoryUnits);

    // BASIC symbols are case-insensitive; foreign (OMF) publics are case-sensitive.
    // Every export is indexed case-sensitively; only non-foreign (BASIC) ones are also
    // indexed case-insensitively. Resolution: exact case first, then BASIC case-fold,
    // then a leading-underscore try (so DECLARE ... CDECL finds the C public "_name").
    var bySensitive = new Dictionary<string, (PbuFile Unit, PbuExport Export)>(StringComparer.Ordinal);
    var byInsensitive = new Dictionary<string, (PbuFile Unit, PbuExport Export)>(StringComparer.OrdinalIgnoreCase);
    void Index(PbuFile unit) {
      foreach (var export in unit.Exports) {
        if (!bySensitive.TryAdd(export.Name, (unit, export)))
          throw new LinkException($"duplicate symbol {export.Name} (in {bySensitive[export.Name].Unit.Name} and {unit.Name})");
        if (!unit.Foreign && !byInsensitive.TryAdd(export.Name, (unit, export)))
          throw new LinkException($"duplicate symbol {export.Name} (in {byInsensitive[export.Name].Unit.Name} and {unit.Name})");
      }
    }
    foreach (var unit in participating)
      Index(unit);

    (PbuFile Unit, PbuExport Export)? Resolve(string name) {
      if (bySensitive.TryGetValue(name, out var s)) return s;
      if (byInsensitive.TryGetValue(name, out var i)) return i;
      if (!name.StartsWith('_') && bySensitive.TryGetValue("_" + name, out var u)) return u; // cdecl auto-decoration
      return null;
    }

    for (var grew = true; grew;) {
      grew = false;
      foreach (var import in participating.SelectMany(u => u.Imports).ToList()) {
        if (Resolve(import.Name) != null)
          continue;
        var name = import.Name;
        var provider = this._libraries.Select(l => l.FindExporter(name) ?? (name.StartsWith('_') ? null : l.FindExporter("_" + name))).FirstOrDefault(u => u != null);
        // foreign OMF .LIBs resolve lazily: convert only the member that defines the symbol
        if (provider == null)
          foreach (var omf in this._omfLibraries)
            if (omf.Provide(name) is { } pulled) { provider = pulled; break; }
        if (provider == null)
          continue; // reported below once the pull set is final
        participating.Add(provider);
        Index(provider);
        grew = true;
      }
    }

    // 2. verify all imports resolve with matching signatures
    foreach (var unit in participating)
      foreach (var import in unit.Imports) {
        if (Resolve(import.Name) is not { } found) {
          // a mangled C++ external the user has not ALIASed yet: surface the demangled
          // signature so they can write the matching DECLARE ... CDECL ALIAS "<symbol>".
          var hint = Omf.Demangle.Parse(import.Name) is { IsMangled: true } d
            ? $" (C++ {d.Scheme} symbol for {d.Pretty})"
            : "";
          throw new LinkException($"unresolved symbol {import.Name}{hint} (imported by {unit.Name})");
        }
        // hash 0 on either side = unchecked (runtime symbols and asm-level references)
        if (found.Export.SignatureHash != import.SignatureHash && found.Export.SignatureHash != 0 && import.SignatureHash != 0)
          throw new LinkException($"signature mismatch for {import.Name}: {unit.Name} expects a different parameter list than {found.Unit.Name} provides");
      }

    // 3. layout: code blocks back to back, then data blocks; BSS accumulates.
    // Blocks are kept word-aligned so the units' internal Align(2) data holds.
    var codeBase = new Dictionary<PbuFile, uint>(ReferenceEqualityComparer.Instance);
    var dataBase = new Dictionary<PbuFile, uint>(ReferenceEqualityComparer.Instance);
    var codeSize = 0u;
    foreach (var unit in participating) {
      codeSize = (codeSize + 1) & ~1u;
      codeBase[unit] = codeSize;
      codeSize += (uint)unit.Code.Length;
    }
    codeSize = (codeSize + 1) & ~1u; // the data area starts word-aligned behind the code
    var dataSize = 0u;
    var bssSize = 0u;
    foreach (var unit in participating) {
      dataSize = (dataSize + 1) & ~1u;
      dataBase[unit] = dataSize;
      dataSize += (uint)unit.Data.Length;
      bssSize += unit.BssSize;
    }

    var code = new byte[codeSize];
    var data = new byte[dataSize];
    foreach (var unit in participating) {
      unit.Code.CopyTo(code, codeBase[unit]);
      unit.Data.CopyTo(data, dataBase[unit]);
    }

    // 4. apply fixups. A site sits in the code image, or - for a foreign far/data
    //    initializer (PbuFixup.InData) - in the data image. Data offsets are relative to
    //    the data base which the caller appends right after code, so a data target (and an
    //    MZ relocation in the data image) gets the code length added.
    var segmentSites = new List<int>();
    foreach (var unit in participating)
      foreach (var fixup in unit.Fixups) {
        var inData = fixup.InData;
        var blob = inData ? data : code;
        var site = (int)((inData ? dataBase[unit] : codeBase[unit]) + fixup.Offset);
        // where this site lands in the concatenated [code .. data] image the MZ writer emits
        var imageOffset = inData ? (int)(codeSize + dataBase[unit] + fixup.Offset) : site;
        switch (fixup.Kind) {
          case PbuFixupKind.NearCode:
            Patch16(blob, site, (ushort)(Read16(blob, site) + codeBase[unit]));
            break;

          case PbuFixupKind.DataOffset:
            Patch16(blob, site, (ushort)(Read16(blob, site) + codeSize + dataBase[unit]));
            break;

          case PbuFixupKind.Segment:
            segmentSites.Add(imageOffset);
            break;

          case PbuFixupKind.ImportCall or PbuFixupKind.ImportOffset: {
            if (fixup.Target >= unit.Imports.Count)
              throw new LinkException($"fixup in {unit.Name} references import #{fixup.Target} of {unit.Imports.Count}");
            var import = unit.Imports[fixup.Target];
            var (providerUnit, export) = Resolve(import.Name)!.Value;
            var target = codeBase[providerUnit] + export.CodeOffset;
            if (fixup.Kind == PbuFixupKind.ImportOffset)
              Patch16(blob, site, (ushort)(Read16(blob, site) + target)); // site keeps its addend
            else
              // near-call displacement is relative to the byte after the 16-bit operand
              Patch16(blob, site, (ushort)(target - (uint)(imageOffset + 2)));
            break;
          }

          default:
            throw new LinkException($"unknown fixup kind {fixup.Kind} in {unit.Name}");
        }
      }

    // ResolvedExports holds every export at its final offset, keyed case-sensitively so
    // foreign case-only variants (_foo/_FOO) stay distinct. BASIC names are matched
    // case-insensitively during resolution (above); lookups here use the exact name.
    var resolved = bySensitive.ToDictionary(kv => kv.Key, kv => codeBase[kv.Value.Unit] + kv.Value.Export.CodeOffset, StringComparer.Ordinal);
    return new(code, data, bssSize, segmentSites, resolved);
  }

  private static ushort Read16(byte[] image, int site) => (ushort)(image[site] | (image[site + 1] << 8));

  private static void Patch16(byte[] image, int site, ushort value) {
    image[site] = (byte)value;
    image[site + 1] = (byte)(value >> 8);
  }
}
